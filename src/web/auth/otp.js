// Hallow Authenticator - one-time codes: TOTP (RFC 6238), HOTP (RFC 4226), Steam Guard, and otpauth:// links.
// Pure functions on WebCrypto (works in the phone's browser and in Node 18+ for the tests). Nothing here stores or sends anything.
const subtle = globalThis.crypto.subtle;
const B32 = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
export const STEAM_ALPHABET = '23456789BCDFGHJKMNPQRTVWXY';

// base32 (RFC 4648) - spaces, dashes and padding are ignored, lower case is fine. null if it isn't base32.
export function base32Decode(text) {
  const clean = String(text || '').toUpperCase().replace(/[\s=-]/g, '');
  if (!clean.length) return null;
  const out = []; let buf = 0, bits = 0;
  for (const ch of clean) {
    const v = B32.indexOf(ch); if (v < 0) return null;
    buf = (buf << 5) | v; bits += 5;
    if (bits >= 8) { out.push((buf >> (bits - 8)) & 255); bits -= 8; }
    buf &= 0xffff;
  }
  return new Uint8Array(out);
}
export function base32Encode(bytes) {
  let out = '', buf = 0, bits = 0;
  for (const b of bytes) { buf = (buf << 8) | b; bits += 8; while (bits >= 5) { out += B32[(buf >> (bits - 5)) & 31]; bits -= 5; } buf &= 0xffff; }
  if (bits > 0) out += B32[(buf << (5 - bits)) & 31];
  return out;
}
export function b64ToBytes(s) {
  s = String(s || '').replace(/[\s]/g, '').replace(/-/g, '+').replace(/_/g, '/');
  while (s.length % 4) s += '=';
  const bin = atob(s); const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}
export function bytesToB64(bytes) { let s = ''; for (let i = 0; i < bytes.length; i++) s += String.fromCharCode(bytes[i]); return btoa(s); }

const HASH = { SHA1: 'SHA-1', SHA256: 'SHA-256', SHA512: 'SHA-512' };
const keyCache = new Map();   // secret+algo -> CryptoKey (only while the page is unlocked; cleared on lock)
export function clearKeyCache() { keyCache.clear(); }
async function hmacKey(secret, algo) {
  const id = algo + ':' + Array.prototype.join.call(secret, ',');
  let k = keyCache.get(id);
  if (!k) { k = await subtle.importKey('raw', secret, { name: 'HMAC', hash: HASH[algo] || 'SHA-1' }, false, ['sign']); if (keyCache.size < 500) keyCache.set(id, k); }
  return k;
}
function counterBytes(counter) {
  const b = new Uint8Array(8); let c = BigInt(counter);
  for (let i = 7; i >= 0; i--) { b[i] = Number(c & 255n); c >>= 8n; }
  return b;
}
// the 31-bit number from RFC 4226's dynamic truncation
async function truncated(secret, counter, algo) {
  const mac = new Uint8Array(await subtle.sign('HMAC', await hmacKey(secret, algo), counterBytes(counter)));
  const off = mac[mac.length - 1] & 15;
  return ((mac[off] & 127) << 24) | (mac[off + 1] << 16) | (mac[off + 2] << 8) | mac[off + 3];
}
export async function hotp(secret, counter, digits = 6, algo = 'SHA1') {
  const n = await truncated(secret, counter, algo);
  return String(n % 10 ** digits).padStart(digits, '0');
}
export async function totp(secret, unixSeconds, period = 30, digits = 6, algo = 'SHA1') {
  return hotp(secret, Math.floor(unixSeconds / period), digits, algo);
}
// Steam Guard: HMAC-SHA1 over the 30 s counter, then 5 characters from Steam's own alphabet
export async function steam(secret, unixSeconds) {
  let n = await truncated(secret, Math.floor(unixSeconds / 30), 'SHA1'), s = '';
  for (let i = 0; i < 5; i++) { s += STEAM_ALPHABET[n % STEAM_ALPHABET.length]; n = Math.floor(n / STEAM_ALPHABET.length); }
  return s;
}

// an entry's secret as bytes: base32 normally; a Steam shared_secret is base64 (as in a .maFile) - base32 is accepted too
export function secretBytes(e) {
  if (e.type === 'steam' && e.secretFormat === 'base64') return b64ToBytes(e.secret);
  return base32Decode(e.secret);
}
// the code for an entry now (or at a given time); HOTP uses its counter
export async function codeFor(e, unixSeconds) {
  const s = secretBytes(e); if (!s || !s.length) return null;
  if (e.type === 'steam') return steam(s, unixSeconds);
  if (e.type === 'hotp') return hotp(s, e.counter || 0, e.digits, e.algo);
  return totp(s, unixSeconds, e.period, e.digits, e.algo);
}
export function nowSeconds() { return Math.floor(Date.now() / 1000); }

// checks an entry and fills in the defaults; returns an error message or null
export function normalize(e) {
  e.type = (e.type || 'totp').toLowerCase();
  if (!['totp', 'hotp', 'steam'].includes(e.type)) return 'Only time-based (TOTP), counter-based (HOTP) and Steam Guard codes are supported';
  e.issuer = String(e.issuer || '').trim().slice(0, 80); e.label = String(e.label || '').trim().slice(0, 120);
  e.algo = String(e.algo || 'SHA1').toUpperCase().replace('-', '');
  if (e.type === 'steam') { e.algo = 'SHA1'; e.digits = 5; e.period = 30; if (!e.issuer) e.issuer = 'Steam'; }
  else {
    if (!HASH[e.algo]) return 'Unknown algorithm ' + e.algo + ' (SHA1, SHA256 or SHA512)';
    e.digits = +e.digits || 6; if (![6, 7, 8].includes(e.digits)) return 'Codes must be 6, 7 or 8 digits';
    e.period = +e.period || 30; if (e.period < 10 || e.period > 300) return 'The period must be 10 to 300 seconds';
  }
  e.counter = Math.max(0, Math.floor(+e.counter || 0));
  e.secret = String(e.secret || '').trim();
  if (e.secretFormat === 'base64') { try { if (!b64ToBytes(e.secret).length) return 'The secret is empty'; } catch (x) { return 'That Steam secret isn\'t valid base64'; } }
  else {
    e.secret = e.secret.toUpperCase().replace(/[\s=-]/g, ''); e.secretFormat = 'base32';
    const b = base32Decode(e.secret); if (!b) return 'The setup key has letters that aren\'t in a setup key (A-Z and 2-7)';
    if (b.length < 5) return 'That setup key is too short';
  }
  if (!e.issuer && !e.label) e.label = 'Account';
  return null;
}

// "otpauth://totp/Issuer:me@x.com?secret=...&issuer=...&algorithm=SHA256&digits=8&period=60" (also hotp, and steam as Aegis writes it)
export function parseOtpauth(uri) {
  let u; try { u = new URL(String(uri).trim()); } catch (x) { return { error: 'That isn\'t an otpauth:// link' }; }
  if (u.protocol !== 'otpauth:') return { error: 'That isn\'t an otpauth:// link' };
  const type = (u.hostname || u.host || '').toLowerCase();
  let label = (u.pathname || '').replace(/^\/+/, ''); try { label = decodeURIComponent(label); } catch (x) { }
  const p = u.searchParams; let issuer = p.get('issuer') || '', account = label;
  const c = label.indexOf(':');
  if (c >= 0) { const li = label.slice(0, c).trim(); account = label.slice(c + 1).trim(); if (!issuer) issuer = li; }
  const e = { type, issuer, label: account, secret: p.get('secret') || '', algo: p.get('algorithm') || 'SHA1', digits: p.get('digits') || 6, period: p.get('period') || 30, counter: p.get('counter') || 0 };
  if (type === 'totp' && ((p.get('encoder') || '').toLowerCase() === 'steam' || /^steam$/i.test(issuer) && p.get('digits') === '5')) e.type = 'steam';
  if (type === 'steam') e.type = 'steam';
  if (!e.secret) return { error: 'The link has no secret in it' };
  const err = normalize(e); if (err) return { error: err };
  return { entry: e };
}
export function toOtpauth(e) {
  const lab = encodeURIComponent(e.issuer ? e.issuer + ':' + e.label : e.label).replace(/%3A/g, ':');
  const t = e.type === 'steam' ? 'totp' : e.type;
  let q = 'secret=' + (e.secretFormat === 'base64' ? base32Encode(b64ToBytes(e.secret)) : e.secret) + (e.issuer ? '&issuer=' + encodeURIComponent(e.issuer) : '');
  if (e.type === 'steam') q += '&digits=5&encoder=steam';
  else { if (e.algo !== 'SHA1') q += '&algorithm=' + e.algo; if (e.digits !== 6) q += '&digits=' + e.digits; if (e.type === 'totp' && e.period !== 30) q += '&period=' + e.period; if (e.type === 'hotp') q += '&counter=' + e.counter; }
  return 'otpauth://' + t + '/' + lab + '?' + q;
}
// a list of links (one per line, or anything with otpauth:// links in it) -> entries and errors
export function parseOtpauthList(text) {
  const out = [], errors = [];
  const found = String(text || '').match(/otpauth:\/\/[^\s"'<>,]+/gi) || [];
  for (const l of found) { const r = parseOtpauth(l); if (r.entry) out.push(r.entry); else errors.push(r.error); }
  return { entries: out, errors };
}
