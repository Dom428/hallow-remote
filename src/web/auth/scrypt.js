// scrypt (RFC 7914) on top of WebCrypto's PBKDF2-SHA256 - only for opening encrypted Aegis exports.
const subtle = globalThis.crypto.subtle;
async function pbkdf2(pw, salt, bytes) {
  const k = await subtle.importKey('raw', pw, 'PBKDF2', false, ['deriveBits']);
  return new Uint8Array(await subtle.deriveBits({ name: 'PBKDF2', salt, iterations: 1, hash: 'SHA-256' }, k, bytes * 8));
}
function salsa8(B, x) {
  for (let i = 0; i < 16; i++) x[i] = B[i];
  const R = (a, b) => (a << b) | (a >>> (32 - b));
  for (let i = 0; i < 8; i += 2) {
    x[4] ^= R(x[0] + x[12], 7); x[8] ^= R(x[4] + x[0], 9); x[12] ^= R(x[8] + x[4], 13); x[0] ^= R(x[12] + x[8], 18);
    x[9] ^= R(x[5] + x[1], 7); x[13] ^= R(x[9] + x[5], 9); x[1] ^= R(x[13] + x[9], 13); x[5] ^= R(x[1] + x[13], 18);
    x[14] ^= R(x[10] + x[6], 7); x[2] ^= R(x[14] + x[10], 9); x[6] ^= R(x[2] + x[14], 13); x[10] ^= R(x[6] + x[2], 18);
    x[3] ^= R(x[15] + x[11], 7); x[7] ^= R(x[3] + x[15], 9); x[11] ^= R(x[7] + x[3], 13); x[15] ^= R(x[11] + x[7], 18);
    x[1] ^= R(x[0] + x[3], 7); x[2] ^= R(x[1] + x[0], 9); x[3] ^= R(x[2] + x[1], 13); x[0] ^= R(x[3] + x[2], 18);
    x[6] ^= R(x[5] + x[4], 7); x[7] ^= R(x[6] + x[5], 9); x[4] ^= R(x[7] + x[6], 13); x[5] ^= R(x[4] + x[7], 18);
    x[11] ^= R(x[10] + x[9], 7); x[8] ^= R(x[11] + x[10], 9); x[9] ^= R(x[8] + x[11], 13); x[10] ^= R(x[9] + x[8], 18);
    x[12] ^= R(x[15] + x[14], 7); x[13] ^= R(x[12] + x[15], 9); x[14] ^= R(x[13] + x[12], 13); x[15] ^= R(x[14] + x[13], 18);
  }
  for (let i = 0; i < 16; i++) B[i] = (B[i] + x[i]) | 0;
}
function blockMix(B, Y, r, tmp) {
  const X = B.slice((2 * r - 1) * 16, 2 * r * 16);
  for (let i = 0; i < 2 * r; i++) {
    for (let j = 0; j < 16; j++) X[j] ^= B[i * 16 + j];
    salsa8(X, tmp);
    const dst = (i % 2 === 0 ? i / 2 : r + (i - 1) / 2) * 16;
    for (let j = 0; j < 16; j++) Y[dst + j] = X[j];
  }
  B.set(Y);
}
export async function scrypt(password, salt, N, r, p, dkLen) {
  if (N < 2 || (N & (N - 1)) || N > 1 << 20 || r < 1 || p < 1 || r * p > 256) throw new Error('scrypt settings out of range');
  const pw = typeof password === 'string' ? new TextEncoder().encode(password) : password;
  const B = await pbkdf2(pw, salt, p * 128 * r);
  const words = 32 * r, V = new Int32Array(words * N), X = new Int32Array(words), Y = new Int32Array(words), tmp = new Int32Array(16);
  const dv = new DataView(B.buffer);
  for (let q = 0; q < p; q++) {
    const off = q * 128 * r;
    for (let i = 0; i < words; i++) X[i] = dv.getInt32(off + i * 4, true);
    for (let i = 0; i < N; i++) { V.set(X, i * words); blockMix(X, Y, r, tmp); }
    for (let i = 0; i < N; i++) { const j = X[(2 * r - 1) * 16] & (N - 1); for (let k = 0; k < words; k++) X[k] ^= V[j * words + k]; blockMix(X, Y, r, tmp); }
    for (let i = 0; i < words; i++) dv.setInt32(off + i * 4, X[i], true);
  }
  return pbkdf2(pw, B, dkLen);
}
