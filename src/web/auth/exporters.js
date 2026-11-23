// Hallow Authenticator 1.1 - the escape hatch: your accounts can always leave for another app.
//   - a plain list of otpauth:// links (readable by anyone - the app warns and asks for the passcode first)
//   - an Aegis vault file (https://github.com/beemdevelopment/Aegis/blob/master/docs/vault.md): scrypt (N 2^15, r 8, p 1) password slot
//     wrapping a random master key, the entries sealed with AES-256-GCM - Aegis, 2FAS, Ente Auth, Proton Pass... import it
//   - Google Authenticator transfer codes (otpauth-migration://offline?data=<protobuf>), several QR codes when there are many accounts
// Everything here runs on the phone; nothing is sent anywhere.
import { toOtpauth, base32Decode, base32Encode, b64ToBytes, bytesToB64 } from './otp.js';
import { scrypt } from './scrypt.js';
const subtle = globalThis.crypto.subtle;
const enc = new TextEncoder();
const hex = b => Array.from(b, x => x.toString(16).padStart(2, '0')).join('');
const rand = n => globalThis.crypto.getRandomValues(new Uint8Array(n));
const uuid = () => { const b = rand(16); b[6] = (b[6] & 15) | 64; b[8] = (b[8] & 63) | 128; const h = hex(b); return h.slice(0, 8) + '-' + h.slice(8, 12) + '-' + h.slice(12, 16) + '-' + h.slice(16, 20) + '-' + h.slice(20); };
const b32 = e => (e.secretFormat === 'base64' ? base32Encode(b64ToBytes(e.secret)) : e.secret);

// ---- plain otpauth:// list (one per line)
export function otpauthList(entries) { return entries.map(toOtpauth).join('\n') + '\n'; }

// ---- Aegis
export function aegisDb(entries) {
  return {
    version: 3, groups: [],
    entries: entries.map(e => ({
      type: e.type, uuid: uuid(), name: e.label || '', issuer: e.issuer || '', note: e.notes || '', favorite: !!e.fav, icon: null, groups: [],
      info: e.type === 'hotp' ? { secret: b32(e), algo: e.algo, digits: e.digits, counter: e.counter || 0 } : { secret: b32(e), algo: e.type === 'steam' ? 'SHA1' : e.algo, digits: e.type === 'steam' ? 5 : e.digits, period: e.period || 30 },
    })),
  };
}
// an encrypted Aegis vault file (JSON text). n can be lowered for the tests only.
export async function aegisExport(entries, password, n = 32768) {
  if (!password) throw new Error('A password is needed');
  const master = rand(32), salt = rand(32), slotNonce = rand(12), dbNonce = rand(12);
  const k = await scrypt(password, salt, n, 8, 1, 32);
  const kk = await subtle.importKey('raw', k, 'AES-GCM', false, ['encrypt']); k.fill(0);
  const wrapped = new Uint8Array(await subtle.encrypt({ name: 'AES-GCM', iv: slotNonce }, kk, master));
  const mk = await subtle.importKey('raw', master, 'AES-GCM', false, ['encrypt']); master.fill(0);
  const sealed = new Uint8Array(await subtle.encrypt({ name: 'AES-GCM', iv: dbNonce }, mk, enc.encode(JSON.stringify(aegisDb(entries)))));
  const ct = sealed.slice(0, sealed.length - 16), tag = sealed.slice(sealed.length - 16);
  return JSON.stringify({
    version: 1,
    header: {
      slots: [{ type: 1, uuid: uuid(), key: hex(wrapped.slice(0, 32)), key_params: { nonce: hex(slotNonce), tag: hex(wrapped.slice(32)) }, n, r: 8, p: 1, salt: hex(salt), repaired: true, is_backup: false }],
      params: { nonce: hex(dbNonce), tag: hex(tag) },
    },
    db: bytesToB64(ct),
  }, null, 4);
}

// ---- Google Authenticator transfer codes
function pbVarint(n) { const o = []; let v = BigInt(n); do { let b = Number(v & 127n); v >>= 7n; if (v) b |= 128; o.push(b); } while (v); return o; }
const pbBytes = (f, b) => [...pbVarint((f << 3) | 2), ...pbVarint(b.length), ...b];
const pbInt = (f, v) => [...pbVarint(f << 3), ...pbVarint(v)];
// which accounts Google Authenticator can take: 30-second TOTP or HOTP, 6 or 8 digits (no Steam, no 60 s periods, no 7 digits)
export function googleCan(e) { return (e.type === 'totp' && (e.period || 30) === 30 || e.type === 'hotp') && (e.digits === 6 || e.digits === 8); }
export function googleMigration(entries, perCode = 8, batchId) {
  const ok = entries.filter(googleCan), skipped = entries.filter(e => !googleCan(e)).map(e => (e.issuer || e.label) + (e.type === 'steam' ? ' (Steam Guard)' : ' (' + e.period + ' s / ' + e.digits + ' digits)'));
  const id = batchId == null ? (rand(4).reduce((a, b) => a * 256 + b, 0) & 0x7fffffff) : batchId;
  const chunks = []; for (let i = 0; i < ok.length; i += perCode) chunks.push(ok.slice(i, i + perCode));
  const uris = chunks.map((list, idx) => {
    const body = [];
    for (const e of list) {
      const p = [...pbBytes(1, [...base32Decode(b32(e))]), ...pbBytes(2, [...enc.encode(e.issuer ? e.issuer + ':' + e.label : e.label)]), ...pbBytes(3, [...enc.encode(e.issuer || '')]),
        ...pbInt(4, { SHA1: 1, SHA256: 2, SHA512: 3 }[e.algo] || 1), ...pbInt(5, e.digits === 8 ? 2 : 1), ...pbInt(6, e.type === 'hotp' ? 1 : 2)];
      if (e.type === 'hotp') p.push(...pbInt(7, e.counter || 0));
      body.push(...pbBytes(1, p));
    }
    body.push(...pbInt(2, 1), ...pbInt(3, chunks.length), ...pbInt(4, idx), ...pbInt(5, id));
    return 'otpauth-migration://offline?data=' + encodeURIComponent(bytesToB64(new Uint8Array(body)));
  });
  return { uris, count: ok.length, skipped };
}
