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
  async clearPrf() { const m = await this.meta(); m.prf = null; await this.st.set('meta', m); }
  async settings() { const m = await this.meta(); return Object.assign(defaultSettings(), (m && m.settings) || {}); }
  async setSettings(s) { const m = await this.meta(); m.settings = Object.assign(defaultSettings(), m.settings || {}, s); await this.st.set('meta', m); return m.settings; }
  // everything gone from this phone (the user typed ERASE first)
  async erase() { this.lock(); await this.st.del('vault'); await this.st.del('meta'); }
  // what's stored, for the tests: nothing readable may be in here
  async storedText() { return JSON.stringify([await this.st.get('meta'), await this.st.get('vault')]); }
}
export function defaultSettings() { return { lockMinutes: 2, lockHidden: true, showNext: true, sort: 'manual' }; }
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
