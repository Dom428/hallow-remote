// Hallow Authenticator - bringing accounts in: Google Authenticator's export QR codes (otpauth-migration://), lists of otpauth:// links,
// Aegis (plain or encrypted JSON), 2FAS (plain or encrypted), and Hallow Keys (a one-time, end-to-end encrypted transfer through Hallow
// Remote). Also the PC mirror's sealing. Everything here runs on the phone.
import { base32Encode, base32Decode, b64ToBytes, bytesToB64, normalize, parseOtpauth, parseOtpauthList } from './otp.js';
import { scrypt } from './scrypt.js';
const subtle = globalThis.crypto.subtle;
const enc = new TextEncoder(), dec = new TextDecoder();
const hex = b => Array.from(b, x => x.toString(16).padStart(2, '0')).join('');
const unhex = s => new Uint8Array((String(s).match(/../g) || []).map(h => parseInt(h, 16)));

// ---- a tiny protobuf reader (varints and length-delimited fields are all Google's export uses)
function pbRead(bytes) {
  let pos = 0; const fields = [];
  const varint = () => { let r = 0n, s = 0n; for (;;) { if (pos >= bytes.length) throw new Error('cut short'); const b = bytes[pos++]; r |= BigInt(b & 127) << s; if (!(b & 128)) return r; s += 7n; if (s > 70n) throw new Error('bad varint'); } };
  while (pos < bytes.length) {
    const key = Number(varint()), f = key >>> 3, t = key & 7;
    if (t === 0) fields.push({ f, v: varint() });
    else if (t === 2) { const n = Number(varint()); if (pos + n > bytes.length) throw new Error('cut short'); fields.push({ f, b: bytes.slice(pos, pos + n) }); pos += n; }
    else if (t === 1) pos += 8; else if (t === 5) pos += 4; else throw new Error('unknown wire type');
  }
  return fields;
}
// "otpauth-migration://offline?data=..." -> { entries, batch: {index, size, id} }
export function parseGoogleMigration(uri) {
  let u; try { u = new URL(String(uri).trim()); } catch (x) { throw new Error('That isn\'t a Google Authenticator export code'); }
  if (u.protocol !== 'otpauth-migration:') throw new Error('That isn\'t a Google Authenticator export code');
  const data = u.searchParams.get('data'); if (!data) throw new Error('The export code has no data in it');
  let fields; try { fields = pbRead(b64ToBytes(data)); } catch (x) { throw new Error('The export code is damaged (' + x.message + ')'); }
  const entries = [], skipped = []; const batch = { index: 0, size: 1, id: 0 };
  for (const fl of fields) {
    if (fl.f === 1 && fl.b) {
      const p = { secret: null, name: '', issuer: '', algo: 0, digits: 0, type: 0, counter: 0 };
      for (const x of pbRead(fl.b)) {
        if (x.f === 1) p.secret = x.b; else if (x.f === 2) p.name = dec.decode(x.b); else if (x.f === 3) p.issuer = dec.decode(x.b);
        else if (x.f === 4) p.algo = Number(x.v); else if (x.f === 5) p.digits = Number(x.v); else if (x.f === 6) p.type = Number(x.v); else if (x.f === 7) p.counter = Number(x.v);
      }
      let label = p.name, issuer = p.issuer; const c = label.indexOf(':');
      if (c >= 0) { if (!issuer) issuer = label.slice(0, c).trim(); label = label.slice(c + 1).trim(); }
      if (p.algo === 4) { skipped.push((issuer || label) + ' (MD5 codes aren\'t supported)'); continue; }
      const e = { type: p.type === 1 ? 'hotp' : 'totp', issuer, label, secret: p.secret ? base32Encode(p.secret) : '', algo: ['SHA1', 'SHA1', 'SHA256', 'SHA512'][p.algo] || 'SHA1', digits: p.digits === 2 ? 8 : 6, period: 30, counter: p.counter, source: 'google' };
      const err = normalize(e); if (err) skipped.push((issuer || label) + ' (' + err + ')'); else entries.push(e);
    } else if (fl.f === 3) batch.size = Number(fl.v); else if (fl.f === 4) batch.index = Number(fl.v); else if (fl.f === 5) batch.id = Number(fl.v);
  }
  return { entries, skipped, batch };
}

// ---- Aegis (https://github.com/beemdevelopment/Aegis/blob/master/docs/vault.md)
function aegisEntries(db) {
  const entries = [], skipped = [];
  for (const a of (db && db.entries) || []) {
    const i = a.info || {}, t = String(a.type || '').toLowerCase();
    if (!['totp', 'hotp', 'steam'].includes(t)) { skipped.push((a.issuer || a.name || '?') + ' (' + t + ' codes aren\'t supported)'); continue; }
    const e = { type: t, issuer: a.issuer || '', label: a.name || '', secret: i.secret || '', algo: i.algo || 'SHA1', digits: i.digits || 6, period: i.period || 30, counter: i.counter || 0, fav: !!a.favorite, source: 'aegis' };
    if (a.note) e.notes = String(a.note).slice(0, 4000);   // 1.1: Aegis' note (this app's per-account notes)
    const err = normalize(e); if (err) skipped.push((e.issuer || e.label) + ' (' + err + ')'); else entries.push(e);
  }
  return { entries, skipped };
}
export function isAegis(j) { return j && j.header && j.db !== undefined && typeof j.version === 'number'; }
export function aegisEncrypted(j) { return isAegis(j) && typeof j.db === 'string'; }
export async function parseAegis(j, password) {
  if (!aegisEncrypted(j)) return aegisEntries(j.db);
  const slots = ((j.header && j.header.slots) || []).filter(s => s.type === 1);
  if (!slots.length) throw new Error('This Aegis file has no password slot (only biometric ones) - export it again with a password');
  let master = null;
  for (const s of slots) {
    try {
      const k = await scrypt(password, unhex(s.salt), s.n, s.r, s.p, 32);
      const key = await subtle.importKey('raw', k, 'AES-GCM', false, ['decrypt']);
      const ct = new Uint8Array([...unhex(s.key), ...unhex(s.key_params.tag)]);
      master = new Uint8Array(await subtle.decrypt({ name: 'AES-GCM', iv: unhex(s.key_params.nonce) }, key, ct)); break;
    } catch (x) { }
  }
  if (!master) throw new Error('Wrong Aegis password');
  const mk = await subtle.importKey('raw', master, 'AES-GCM', false, ['decrypt']);
  const body = new Uint8Array([...b64ToBytes(j.db), ...unhex(j.header.params.tag)]);
  const db = JSON.parse(dec.decode(await subtle.decrypt({ name: 'AES-GCM', iv: unhex(j.header.params.nonce) }, mk, body)));
  return aegisEntries(db);
}

// ---- 2FAS ("services"; the encrypted kind is "servicesEncrypted": base64 ciphertext : salt : iv, PBKDF2-SHA256 10,000 rounds, AES-GCM)
export function is2fas(j) { return j && (Array.isArray(j.services) || typeof j.servicesEncrypted === 'string') && j.schemaVersion !== undefined; }
function twofasEntries(list) {
  const entries = [], skipped = [];
  for (const s of list || []) {
    const o = s.otp || {}, t = String(o.tokenType || 'TOTP').toLowerCase();
    if (!['totp', 'hotp', 'steam'].includes(t)) { skipped.push((s.name || '?') + ' (' + t + ' codes aren\'t supported)'); continue; }
    const e = { type: t, issuer: o.issuer || s.name || '', label: o.account || o.label || '', secret: s.secret || '', algo: o.algorithm || 'SHA1', digits: o.digits || 6, period: o.period || 30, counter: o.counter || 0, source: '2fas' };
    const err = normalize(e); if (err) skipped.push((e.issuer || e.label) + ' (' + err + ')'); else entries.push(e);
  }
  return { entries, skipped };
}
export async function parse2fas(j, password) {
  if (!j.servicesEncrypted || (Array.isArray(j.services) && j.services.length)) return twofasEntries(j.services);
  const [c, s, iv] = j.servicesEncrypted.split(':').map(b64ToBytes);
  if (!c || !s || !iv) throw new Error('This 2FAS file looks damaged');
  const base = await subtle.importKey('raw', enc.encode(password), 'PBKDF2', false, ['deriveKey']);
  const key = await subtle.deriveKey({ name: 'PBKDF2', salt: s, iterations: 10000, hash: 'SHA-256' }, base, { name: 'AES-GCM', length: 256 }, false, ['decrypt']);
  let plain; try { plain = await subtle.decrypt({ name: 'AES-GCM', iv }, key, c); } catch (x) { throw new Error('Wrong 2FAS password'); }
  return twofasEntries(JSON.parse(dec.decode(plain)));
}

// ---- any text or file the user gives: otpauth links, a Google export code, Aegis / 2FAS JSON. needsPassword = ask, then call again.
export async function parseAny(text, password) {
  const t = String(text || '').trim();
  if (/^otpauth-migration:/i.test(t)) { const r = parseGoogleMigration(t); return { kind: 'google', entries: r.entries, skipped: r.skipped, batch: r.batch }; }
  if (t.startsWith('{')) {
    let j; try { j = JSON.parse(t); } catch (x) { throw new Error('That file isn\'t valid JSON'); }
    if (isAegis(j)) { if (aegisEncrypted(j) && password == null) return { kind: 'aegis', needsPassword: true }; const r = await parseAegis(j, password); return { kind: 'aegis', entries: r.entries, skipped: r.skipped }; }
    if (is2fas(j)) { if (j.servicesEncrypted && !(j.services && j.services.length) && password == null) return { kind: '2fas', needsPassword: true }; const r = await parse2fas(j, password); return { kind: '2fas', entries: r.entries, skipped: r.skipped }; }
    if (j.format === 'hallow-authenticator-backup' || j.format === 'hallow-authenticator-vault') return { kind: 'backup' };   // 1.1: also the automatic backups locked by the passcode
    throw new Error('That JSON isn\'t an export this app knows (Aegis, 2FAS or a Hallow Authenticator backup)');
  }
  if (/otpauth:\/\//i.test(t)) { const r = parseOtpauthList(t); return { kind: 'links', entries: r.entries, skipped: r.errors }; }
  if (/^HALLOWAUTH1:/i.test(t)) return { kind: 'keys', code: normalizeCode(t) };
  if (/^HALLOWMIRROR1:/i.test(t)) return { kind: 'mirror', key: t.slice(14) };
  throw new Error('Nothing this app can use was found in that');
}

// ---- Hallow Keys transfer: a 32-letter code (160 bits) shown on the PC. Remote only ever sees the lookup id and the sealed blob.
export function normalizeCode(s) { return String(s || '').toUpperCase().replace(/^HALLOWAUTH1:/, '').replace(/[^A-Z2-7]/g, ''); }
async function sha(s) { return new Uint8Array(await subtle.digest('SHA-256', enc.encode(s))); }
export async function transferIds(code) {
  code = normalizeCode(code); if (code.length !== 32) throw new Error('The code from Hallow Keys has 32 letters and numbers');
  const id = hex(await sha('HallowAuthXferId' + code)).slice(0, 32);
  const key = await subtle.importKey('raw', await sha('HallowAuthXferKey' + code), 'AES-GCM', false, ['encrypt', 'decrypt']);
  const ackKey = await subtle.importKey('raw', await sha('HallowAuthXferAck' + code), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  const proof = hex(new Uint8Array(await subtle.sign('HMAC', ackKey, enc.encode(id))));
  return { id, key, proof };
}
export async function openTransfer(blob, ids) {
  const b = typeof blob === 'string' ? JSON.parse(blob) : blob;
  let plain; try { plain = await subtle.decrypt({ name: 'AES-GCM', iv: b64ToBytes(b.iv), additionalData: enc.encode('HallowAuthXfer1:' + ids.id) }, ids.key, b64ToBytes(b.ct)); } catch (x) { throw new Error('That code doesn\'t open this transfer - check it on the PC'); }
  const p = JSON.parse(dec.decode(plain)); const entries = [], skipped = [];
  for (const x of p.entries || []) {
    let e = null;
    if (/^otpauth:/i.test(x.uri || '')) { const r = parseOtpauth(x.uri); if (r.entry) e = r.entry; else { skipped.push((x.title || '?') + ' (' + r.error + ')'); continue; } }
    else { e = { type: 'totp', secret: x.uri || '' }; const err = normalize(e); if (err) { skipped.push((x.title || '?') + ' (' + err + ')'); continue; } }
    if (!e.issuer) e.issuer = x.title || ''; if (!e.label || e.label === 'Account') e.label = x.username || e.label;
    e.source = 'keys'; entries.push(e);
  }
  return { mode: p.mode === 'move' ? 'move' : 'copy', entries, skipped };
}
// the test side / Hallow Keys side of the same thing (Keys does this in C#; kept here so the tests can check both ends agree)
export async function sealTransfer(payload, ids) {
  const iv = globalThis.crypto.getRandomValues(new Uint8Array(12));
  const ct = new Uint8Array(await subtle.encrypt({ name: 'AES-GCM', iv, additionalData: enc.encode('HallowAuthXfer1:' + ids.id) }, ids.key, enc.encode(JSON.stringify(payload))));
  return { v: 1, iv: bytesToB64(iv), ct: bytesToB64(ct) };
}

// ---- PC mirror (off unless you turn it on): a read-only copy of the accounts you pick, sealed with a key Hallow Keys gave the phone
export function mirrorKeyFrom(text) { const b = base32Decode(String(text || '').replace(/^HALLOWMIRROR1:/i, '')); if (!b || b.length !== 32) throw new Error('That isn\'t a Hallow Keys mirror code'); return b; }
export async function sealMirror(keyBytes, entries) {
  const key = await subtle.importKey('raw', keyBytes, 'AES-GCM', false, ['encrypt']);
  const list = entries.filter(e => e.type !== 'hotp').map(e => ({ type: e.type, issuer: e.issuer, label: e.label, secret: e.secretFormat === 'base64' ? base32Encode(b64ToBytes(e.secret)) : e.secret, algo: e.algo, digits: e.digits, period: e.period }));
  const iv = globalThis.crypto.getRandomValues(new Uint8Array(12));
  const ct = new Uint8Array(await subtle.encrypt({ name: 'AES-GCM', iv, additionalData: enc.encode('HallowAuthMirror1') }, key, enc.encode(JSON.stringify({ made: new Date().toISOString(), entries: list }))));
  return JSON.stringify({ v: 1, iv: bytesToB64(iv), ct: bytesToB64(ct) });
}
export { hex, unhex };
