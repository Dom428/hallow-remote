// Hallow Authenticator - the encrypted store on the phone.
//   - A random 256-bit data key (DEK) encrypts the accounts with AES-256-GCM.
//   - The DEK is kept only wrapped: by a key from your Authenticator passcode (PBKDF2-SHA256, 600,000 rounds, random salt) and, if you
//     turn it on, by a key from your fingerprint / face through a passkey's PRF extension (WebAuthn). Nothing readable is ever stored.
//   - Storage is IndexedDB in the browser; the tests pass a Map instead.
//   - Encrypted backups use their own password (same PBKDF2 + AES-GCM), so the file can live anywhere - the PC can't read it.
import { bytesToB64, b64ToBytes } from './otp.js';
const subtle = globalThis.crypto.subtle;
const enc = new TextEncoder(), dec = new TextDecoder();
export const ITERATIONS = 600000;
export const BACKUP_FORMAT = 'hallow-authenticator-backup';
export function rand(n) { return globalThis.crypto.getRandomValues(new Uint8Array(n)); }

// ---- storage: IndexedDB (one object store of records) or anything with get / set / del
export function idbStorage(name = 'hallow-authenticator') {
  let dbp = null;
  const db = () => dbp || (dbp = new Promise((res, rej) => {
    const r = indexedDB.open(name, 1);
    r.onupgradeneeded = () => r.result.createObjectStore('kv');
    r.onsuccess = () => res(r.result); r.onerror = () => rej(r.error);
  }));
  const tx = async (mode, fn) => { const d = await db(); return new Promise((res, rej) => { const t = d.transaction('kv', mode); const st = t.objectStore('kv'); const q = fn(st); t.oncomplete = () => res(q && q.result); t.onerror = () => rej(t.error); t.onabort = () => rej(t.error); }); };
  return {
    get: k => tx('readonly', s => s.get(k)),
    set: (k, v) => tx('readwrite', s => s.put(v, k)),
    del: k => tx('readwrite', s => s.delete(k)),
  };
}
export function memoryStorage() {
  const m = new Map();
  return { get: async k => (m.has(k) ? structuredClone(m.get(k)) : undefined), set: async (k, v) => { m.set(k, structuredClone(v)); }, del: async k => { m.delete(k); }, raw: m };
}

// ---- crypto helpers
export async function passKey(passcode, salt, iterations = ITERATIONS) {
  const base = await subtle.importKey('raw', enc.encode(passcode), 'PBKDF2', false, ['deriveKey']);
  return subtle.deriveKey({ name: 'PBKDF2', salt, iterations, hash: 'SHA-256' }, base, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
}
export async function hkdfKey(ikm, info) {
  const base = await subtle.importKey('raw', ikm, 'HKDF', false, ['deriveKey']);
  return subtle.deriveKey({ name: 'HKDF', hash: 'SHA-256', salt: new Uint8Array(32), info: enc.encode(info) }, base, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
}
export async function seal(key, plain, aad) {
  const iv = rand(12);
  const p = { name: 'AES-GCM', iv }; if (aad) p.additionalData = enc.encode(aad);
  const ct = new Uint8Array(await subtle.encrypt(p, key, typeof plain === 'string' ? enc.encode(plain) : plain));
  return { iv: bytesToB64(iv), ct: bytesToB64(ct) };
}
// throws if the key is wrong or the data was changed (AES-GCM's tag)
export async function open(key, box, aad) {
  const p = { name: 'AES-GCM', iv: b64ToBytes(box.iv) }; if (aad) p.additionalData = enc.encode(aad);
  return new Uint8Array(await subtle.decrypt(p, key, b64ToBytes(box.ct)));
}
async function rawAes(bytes) { return subtle.importKey('raw', bytes, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']); }

export class WrongPasscode extends Error { constructor(m) { super(m || 'Wrong passcode'); this.name = 'WrongPasscode'; } }

// ---- the vault
export class Vault {
  constructor(storage, opts = {}) { this.st = storage; this.iterations = opts.iterations || ITERATIONS; this.dek = null; this.dekRaw = null; this.data = null; }
  async meta() { return (await this.st.get('meta')) || null; }
  async exists() { return !!(await this.meta()); }
  get unlocked() { return !!this.dek; }
  // first run: a new vault under this passcode
  async create(passcode, settings) {
    if (await this.exists()) throw new Error('There is already a vault on this phone');
    const salt = rand(16), raw = rand(32);
    const kek = await passKey(passcode, salt, this.iterations);
    const meta = { v: 1, kdf: 'PBKDF2-SHA256', iter: this.iterations, salt: bytesToB64(salt), wrap: await seal(kek, raw, 'HallowAuthDEK1'), prf: null, created: new Date().toISOString(), settings: Object.assign(defaultSettings(), settings || {}), fails: 0 };
    await this.st.set('meta', meta);
    this.dekRaw = raw; this.dek = await rawAes(raw);
    this.data = { v: 1, entries: [], mirror: null, updated: new Date().toISOString() };
    await this.save();
  }
  async unlock(passcode) {
    const m = await this.meta(); if (!m) throw new Error('No vault yet');
    const kek = await passKey(passcode, b64ToBytes(m.salt), m.iter);
    let raw; try { raw = await open(kek, m.wrap, 'HallowAuthDEK1'); } catch (x) { m.fails = (m.fails || 0) + 1; m.lastFail = Date.now(); await this.st.set('meta', m); throw new WrongPasscode(); }
    if (m.fails) { m.fails = 0; await this.st.set('meta', m); }
    await this._load(raw);
  }
  // unlocking with the 32 bytes a passkey's PRF gave (see prf.js in app.js)
  async unlockWithPrf(prfOutput) {
    const m = await this.meta(); if (!m || !m.prf) throw new Error('Fingerprint / face unlock isn\'t set up');
    const k = await hkdfKey(prfOutput, 'HallowAuthPRF1');
    let raw; try { raw = await open(k, m.prf.wrap, 'HallowAuthDEK1'); } catch (x) { throw new WrongPasscode('That passkey didn\'t unlock it - use your passcode'); }
    await this._load(raw);
  }
  async _load(raw) {
    const dek = await rawAes(raw);
    const box = await this.st.get('vault');
    const data = box ? JSON.parse(dec.decode(await open(dek, box, 'HallowAuthVault1'))) : { v: 1, entries: [], mirror: null };
    this.dekRaw = raw; this.dek = dek; this.data = data;
    if (!Array.isArray(data.entries)) data.entries = [];
  }
  lock() { if (this.dekRaw) this.dekRaw.fill(0); this.dekRaw = null; this.dek = null; this.data = null; }
  async save() {
    if (!this.dek) throw new Error('Locked');
    this.data.updated = new Date().toISOString();
    await this.st.set('vault', await seal(this.dek, JSON.stringify(this.data), 'HallowAuthVault1'));
  }
  async changePasscode(oldPass, newPass) {
    const m = await this.meta();
    const raw = await open(await passKey(oldPass, b64ToBytes(m.salt), m.iter), m.wrap, 'HallowAuthDEK1').catch(() => { throw new WrongPasscode(); });
    const salt = rand(16); m.salt = bytesToB64(salt); m.iter = this.iterations;
    m.wrap = await seal(await passKey(newPass, salt, m.iter), raw, 'HallowAuthDEK1');
    await this.st.set('meta', m);
  }
  async setPrf(credId, prfSalt, prfOutput) {
    if (!this.dekRaw) throw new Error('Locked');
    const m = await this.meta();
    m.prf = { credId: bytesToB64(credId), salt: bytesToB64(prfSalt), wrap: await seal(await hkdfKey(prfOutput, 'HallowAuthPRF1'), this.dekRaw, 'HallowAuthDEK1') };
    await this.st.set('meta', m);
  }
  // 1.1: is this the passcode? (asked again before showing every secret at once - the plain export and the transfer QR codes)
  async checkPasscode(passcode) { const m = await this.meta(); try { await open(await passKey(passcode, b64ToBytes(m.salt), m.iter), m.wrap, 'HallowAuthDEK1'); return true; } catch (x) { return false; } }
  // 1.1: an automatic backup locked by the passcode: the passcode's wrap of the data key (as stored on the phone) and the accounts sealed
  // again with the data key. It opens only with the passcode the phone had when it was made.
  async passcodeBackup() {
    if (!this.dek) throw new Error('Locked');
    const m = await this.meta(), box = await seal(this.dek, JSON.stringify({ app: 'Hallow Authenticator', v: 1, entries: this.data.entries }), VAULT_BACKUP_FORMAT);
    return JSON.stringify({ format: VAULT_BACKUP_FORMAT, v: 1, kdf: m.kdf || 'PBKDF2-SHA256', iter: m.iter, salt: m.salt, wiv: m.wrap.iv, wct: m.wrap.ct, iv: box.iv, ct: box.ct, made: new Date().toISOString() });
  }
  async clearPrf() { const m = await this.meta(); m.prf = null; await this.st.set('meta', m); }
  async settings() { const m = await this.meta(); return Object.assign(defaultSettings(), (m && m.settings) || {}); }
  async setSettings(s) { const m = await this.meta(); m.settings = Object.assign(defaultSettings(), m.settings || {}, s); await this.st.set('meta', m); return m.settings; }
  // everything gone from this phone (the user typed ERASE first)
  async erase() { this.lock(); await this.st.del('vault'); await this.st.del('meta'); }
  // what's stored, for the tests: nothing readable may be in here
  async storedText() { return JSON.stringify([await this.st.get('meta'), await this.st.get('vault')]); }
}
// 1.1: hideCodes; autoBackup null = not decided yet (switched on after the first backup); autoMode 'backup' (backup password) or 'passcode'
export function defaultSettings() { return { lockMinutes: 2, lockHidden: true, showNext: true, sort: 'manual', hideCodes: false, autoBackup: null, autoMode: 'backup' }; }
// a short wait after wrong passcodes (on top of PBKDF2's own cost): 5 free tries, then 30 s, 1 min, 2 min ... up to 15 min
export function waitAfterFails(fails) { return fails < 5 ? 0 : Math.min(15 * 60, 30 * 2 ** (fails - 5)); }

// ---- encrypted backup file (its own password)
export async function makeBackup(data, password, iterations = ITERATIONS) {
  const salt = rand(16);
  const box = await seal(await passKey(password, salt, iterations), JSON.stringify({ app: 'Hallow Authenticator', v: 1, entries: data.entries }), BACKUP_FORMAT);
  return JSON.stringify({ format: BACKUP_FORMAT, v: 1, kdf: 'PBKDF2-SHA256', iter: iterations, salt: bytesToB64(salt), iv: box.iv, ct: box.ct, made: new Date().toISOString() });
}
export async function readBackup(text, password) {
  let f; try { f = JSON.parse(text); } catch (x) { throw new Error('That isn\'t a Hallow Authenticator backup'); }
  if (!f || f.format !== BACKUP_FORMAT || !f.salt || !f.ct) throw new Error('That isn\'t a Hallow Authenticator backup');
  if (!(f.iter >= 100000 && f.iter <= 10000000)) throw new Error('This backup has odd settings - it may be damaged');
  let plain; try { plain = await open(await passKey(password, b64ToBytes(f.salt), f.iter), f, BACKUP_FORMAT); } catch (x) { throw new WrongPasscode('Wrong backup password (or the file is damaged)'); }
  const d = JSON.parse(dec.decode(plain)); if (!Array.isArray(d.entries)) throw new Error('The backup has no accounts');
  return d.entries;
}

// ---- 1.1: backups that need no password typed: the key from a backup password is kept (inside the encrypted vault) after a backup,
// so automatic backups can be locked with the same password - the same file format, so Restore opens them with that password.
export async function backupKeyFrom(password, iterations = ITERATIONS) {
  const salt = rand(16), base = await subtle.importKey('raw', enc.encode(password), 'PBKDF2', false, ['deriveBits']);
  const bits = new Uint8Array(await subtle.deriveBits({ name: 'PBKDF2', salt, iterations, hash: 'SHA-256' }, base, 256));
  return { salt: bytesToB64(salt), iter: iterations, key: bytesToB64(bits) };
}
export async function makeBackupWithKey(entries, bk) {
  const raw = b64ToBytes(bk.key), key = await rawAes(raw); raw.fill(0);
  const box = await seal(key, JSON.stringify({ app: 'Hallow Authenticator', v: 1, entries }), BACKUP_FORMAT);
  return JSON.stringify({ format: BACKUP_FORMAT, v: 1, kdf: 'PBKDF2-SHA256', iter: bk.iter, salt: bk.salt, iv: box.iv, ct: box.ct, made: new Date().toISOString() });
}
export const VAULT_BACKUP_FORMAT = 'hallow-authenticator-vault';
export async function readVaultBackup(text, passcode) {
  let f; try { f = JSON.parse(text); } catch (x) { throw new Error('That isn\'t a Hallow Authenticator backup'); }
  if (!f || f.format !== VAULT_BACKUP_FORMAT || !f.salt || !f.wct || !f.ct) throw new Error('That isn\'t a Hallow Authenticator backup');
  if (!(f.iter >= 100000 && f.iter <= 10000000)) throw new Error('This backup has odd settings - it may be damaged');
  let raw; try { raw = await open(await passKey(passcode, b64ToBytes(f.salt), f.iter), { iv: f.wiv, ct: f.wct }, 'HallowAuthDEK1'); } catch (x) { throw new WrongPasscode('Wrong passcode for this backup (it needs the passcode the phone had when it was made)'); }
  const dek = await rawAes(raw); raw.fill(0);
  let plain; try { plain = await open(dek, f, VAULT_BACKUP_FORMAT); } catch (x) { throw new Error('The backup is damaged'); }
  const d = JSON.parse(dec.decode(plain)); if (!Array.isArray(d.entries)) throw new Error('The backup has no accounts');
  return d.entries;
}
// either kind: one locked by a backup password, or by the passcode
export function backupKind(text) { try { const f = JSON.parse(text); return f && f.format === VAULT_BACKUP_FORMAT ? 'passcode' : f && f.format === BACKUP_FORMAT ? 'password' : null; } catch (x) { return null; } }
export async function readAnyBackup(text, secret) { return backupKind(text) === 'passcode' ? readVaultBackup(text, secret) : readBackup(text, secret); }

// ---- 1.1: what the latest backup holds (kept inside the encrypted vault): a short fingerprint per account, to say "N accounts not in
// your latest backup" after you add or change some, and when it was made (a gentle reminder after 14 days).
export function entryPrint(e) {
  const s = JSON.stringify([e.type, e.issuer, e.label, e.secret, e.algo, e.digits, e.period, e.notes || '']);
  let h = 0x811c9dc5; for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 0x01000193) >>> 0; }
  return h.toString(16).padStart(8, '0');
}
export function markBackedUp(data, kind, when, entries) {
  const prints = {}; for (const e of entries || data.entries) prints[e.id] = entryPrint(e);
  data.backup = { at: new Date(when || Date.now()).toISOString(), kind, prints };
}
export const STALE_DAYS = 14;
export function backupStatus(data, now) {
  const b = data && data.backup, n = ((data && data.entries) || []).length;
  if (!b || !b.at) return { never: true, notIn: n, stale: false, days: null, at: null };
  const notIn = data.entries.filter(e => b.prints[e.id] !== entryPrint(e)).length;
  const days = Math.floor(((now || Date.now()) - Date.parse(b.at)) / 86400000);
  return { never: false, notIn, stale: days > STALE_DAYS, days, at: b.at, kind: b.kind };
}
// automatic backup to the PC: only after changes (something not in the latest backup), at most once a day
export const AUTO_GAP_MS = 24 * 3600 * 1000;
export function autoBackupDue(data, settings, now) {
  if (!settings || settings.autoBackup !== true || !data || !data.entries.length) return false;
  const st = backupStatus(data, now); if (!st.never && st.notIn === 0) return false;
  const last = data.auto && data.auto.last ? Date.parse(data.auto.last) : 0;
  return (now || Date.now()) - last >= AUTO_GAP_MS;
}

// ---- auto-lock: after N idle minutes, and (optionally) as soon as the page is hidden. Clock and timers can be passed in (tests).
export class Locker {
  constructor(onLock, opts = {}) { this.onLock = onLock; this.now = opts.now || (() => Date.now()); this.minutes = 2; this.hidden = true; this.last = this.now(); this.armed = false; }
  set(minutes, lockHidden) { this.minutes = minutes; this.hidden = lockHidden; }
  arm() { this.armed = true; this.last = this.now(); }
  disarm() { this.armed = false; }
  touch() { this.last = this.now(); }
  // called every second by the page
  tick() { if (this.armed && this.now() - this.last >= this.minutes * 60000) { this.armed = false; this.onLock('idle'); return true; } return false; }
  visibility(state) { if (this.armed && state === 'hidden' && this.hidden) { this.armed = false; this.onLock('hidden'); return true; } return false; }
}
