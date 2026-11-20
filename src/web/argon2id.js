/*
 * HKArgon2 - pure JavaScript Argon2id (RFC 9106, version 0x13) with BLAKE2b (RFC 7693).
 * No dependencies, no WebAssembly, no BigInt. Classic script; also loads in Node via require().
 *
 *   HKArgon2.argon2id(password, salt, memoryKiB, passes, lanes, tagLength,
 *                     secret?, ad?, onProgress?) -> Promise<Uint8Array>
 *   HKArgon2.blake2b(data, outLen?, key?) -> Uint8Array
 *
 * 64-bit words are stored as (lo, hi) pairs of uint32 in Uint32Arrays (little-endian order:
 * word k occupies indices 2k (low half) and 2k+1 (high half)).
 * Lanes are computed sequentially on one thread, segment by segment, which honours the
 * RFC's slice synchronisation points. Work is chunked and the function yields to the event
 * loop roughly every YIELD_MS milliseconds so a page stays responsive and can show progress.
 * The permutation P() below is fully unrolled (all 16 words in locals); it was emitted by a
 * dev-time generator and is plain static code (no eval / new Function at runtime).
 * BlaMka multiply 2*lo(x)*lo(y) is computed exactly with 16-bit partial products.
 */
var HKArgon2 = (function () {
  'use strict';

  var TWO32 = 4294967296;

  /* ------------------------------------------------------------------ */
  /* BLAKE2b (RFC 7693)                                                  */
  /* ------------------------------------------------------------------ */

  var B2_IV = new Uint32Array([
    0xf3bcc908, 0x6a09e667, 0x84caa73b, 0xbb67ae85,
    0xfe94f82b, 0x3c6ef372, 0x5f1d36f1, 0xa54ff53a,
    0xade682d1, 0x510e527f, 0x2b3e6c1f, 0x9b05688c,
    0xfb41bd6b, 0x1f83d9ab, 0x137e2179, 0x5be0cd19
  ]);

  var SIGMA = [
    0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
    14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3,
    11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4,
    7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8,
    9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13,
    2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9,
    12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11,
    13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10,
    6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5,
    10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0,
    0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
    14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3
  ];
  // pre-doubled (uint32 indices)
  var SIGMA2 = new Uint8Array(SIGMA.length);
  for (var si = 0; si < SIGMA.length; si++) SIGMA2[si] = SIGMA[si] * 2;

  var b2v = new Uint32Array(32);
  var b2m = new Uint32Array(32);

  // a,b,c,d,x,y are uint32 indices (already doubled)
  function b2G(v, m, a, b, c, d, x, y) {
    var lo, hi, xl, xh;
    // a = a + b + m[x]
    lo = v[a] + v[b]; hi = v[a + 1] + v[b + 1]; if (lo >= TWO32) hi++;
    lo = (lo >>> 0) + m[x]; hi = hi + m[x + 1]; if (lo >= TWO32) hi++;
    v[a] = lo; v[a + 1] = hi;
    // d = (d ^ a) >>> 32
    xl = v[d] ^ v[a]; xh = v[d + 1] ^ v[a + 1];
    v[d] = xh; v[d + 1] = xl;
    // c = c + d
    lo = v[c] + v[d]; hi = v[c + 1] + v[d + 1]; if (lo >= TWO32) hi++;
    v[c] = lo; v[c + 1] = hi;
    // b = (b ^ c) >>> 24
    xl = v[b] ^ v[c]; xh = v[b + 1] ^ v[c + 1];
    v[b] = (xl >>> 24) | (xh << 8); v[b + 1] = (xh >>> 24) | (xl << 8);
    // a = a + b + m[y]
    lo = v[a] + v[b]; hi = v[a + 1] + v[b + 1]; if (lo >= TWO32) hi++;
    lo = (lo >>> 0) + m[y]; hi = hi + m[y + 1]; if (lo >= TWO32) hi++;
    v[a] = lo; v[a + 1] = hi;
    // d = (d ^ a) >>> 16
    xl = v[d] ^ v[a]; xh = v[d + 1] ^ v[a + 1];
    v[d] = (xl >>> 16) | (xh << 16); v[d + 1] = (xh >>> 16) | (xl << 16);
    // c = c + d
    lo = v[c] + v[d]; hi = v[c + 1] + v[d + 1]; if (lo >= TWO32) hi++;
    v[c] = lo; v[c + 1] = hi;
    // b = (b ^ c) >>> 63  (== rotl 1)
    xl = v[b] ^ v[c]; xh = v[b + 1] ^ v[c + 1];
    v[b] = (xl << 1) | (xh >>> 31); v[b + 1] = (xh << 1) | (xl >>> 31);
  }

  function b2Compress(h, buf, off, tLo, tHi, last) {
    var v = b2v, m = b2m, i;
    for (i = 0; i < 16; i++) { v[i] = h[i]; v[i + 16] = B2_IV[i]; }
    v[24] ^= tLo; v[25] ^= tHi;
    if (last) { v[28] = ~v[28]; v[29] = ~v[29]; }
    for (i = 0; i < 32; i++) {
      var o = off + 4 * i;
      m[i] = buf[o] | (buf[o + 1] << 8) | (buf[o + 2] << 16) | (buf[o + 3] << 24);
    }
    for (var r = 0; r < 12; r++) {
      var s = r * 16;
      b2G(v, m, 0, 8, 16, 24, SIGMA2[s], SIGMA2[s + 1]);
      b2G(v, m, 2, 10, 18, 26, SIGMA2[s + 2], SIGMA2[s + 3]);
      b2G(v, m, 4, 12, 20, 28, SIGMA2[s + 4], SIGMA2[s + 5]);
      b2G(v, m, 6, 14, 22, 30, SIGMA2[s + 6], SIGMA2[s + 7]);
      b2G(v, m, 0, 10, 20, 30, SIGMA2[s + 8], SIGMA2[s + 9]);
      b2G(v, m, 2, 12, 22, 24, SIGMA2[s + 10], SIGMA2[s + 11]);
      b2G(v, m, 4, 14, 16, 26, SIGMA2[s + 12], SIGMA2[s + 13]);
      b2G(v, m, 6, 8, 18, 28, SIGMA2[s + 14], SIGMA2[s + 15]);
    }
    for (i = 0; i < 16; i++) h[i] ^= v[i] ^ v[i + 16];
  }

  /**
   * BLAKE2b digest. data: Uint8Array, outLen: 1..64 (default 64), key: optional Uint8Array (0..64 bytes).
   */
  function blake2b(data, outLen, key) {
    if (outLen === undefined || outLen === null) outLen = 64;
    if (!(data instanceof Uint8Array)) throw new TypeError('blake2b: data must be a Uint8Array');
    if (!(outLen >= 1 && outLen <= 64) || (outLen | 0) !== outLen) throw new RangeError('blake2b: outLen must be 1..64');
    var keyLen = 0;
    if (key !== undefined && key !== null) {
      if (!(key instanceof Uint8Array)) throw new TypeError('blake2b: key must be a Uint8Array');
      if (key.length > 64) throw new RangeError('blake2b: key must be at most 64 bytes');
      keyLen = key.length;
    }
    var h = new Uint32Array(16);
    h.set(B2_IV);
    h[0] ^= 0x01010000 ^ (keyLen << 8) ^ outLen;

    var input = data;
    if (keyLen > 0) {
      input = new Uint8Array(128 + data.length);
      input.set(key, 0);
      input.set(data, 128);
    }
    var n = input.length;
    var nBlocks = n === 0 ? 1 : Math.ceil(n / 128);
    var last = new Uint8Array(128);
    for (var b = 0; b < nBlocks; b++) {
      var isLast = b === nBlocks - 1;
      var count = isLast ? n : (b + 1) * 128;
      var tLo = count >>> 0, tHi = Math.floor(count / TWO32) >>> 0;
      if (isLast) {
        last.fill(0);
        last.set(input.subarray(b * 128, n), 0);
        b2Compress(h, last, 0, tLo, tHi, true);
      } else {
        b2Compress(h, input, b * 128, tLo, tHi, false);
      }
    }
    var out = new Uint8Array(outLen);
    for (var i = 0; i < outLen; i++) out[i] = h[i >> 2] >>> (8 * (i & 3));
    if (keyLen > 0) input.fill(0);
    last.fill(0); h.fill(0); b2v.fill(0); b2m.fill(0);
    return out;
  }

  /* ------------------------------------------------------------------ */
  /* Argon2 helpers                                                      */
  /* ------------------------------------------------------------------ */

  function le32(n) {
    return new Uint8Array([n & 0xff, (n >>> 8) & 0xff, (n >>> 16) & 0xff, (n >>> 24) & 0xff]);
  }

  function concat(parts) {
    var len = 0, i;
    for (i = 0; i < parts.length; i++) len += parts[i].length;
    var out = new Uint8Array(len), o = 0;
    for (i = 0; i < parts.length; i++) { out.set(parts[i], o); o += parts[i].length; }
    return out;
  }

  // Variable-length hash H' (RFC 9106 section 3.3)
  function hprime(T, A) {
    var inp = concat([le32(T), A]);
    var out;
    if (T <= 64) {
      out = blake2b(inp, T);
    } else {
      out = new Uint8Array(T);
      var r = Math.ceil(T / 32) - 2;
      var V = blake2b(inp, 64), W;
      out.set(V.subarray(0, 32), 0);
      for (var i = 1; i < r; i++) {
        W = blake2b(V, 64); V.fill(0); V = W;
        out.set(V.subarray(0, 32), i * 32);
      }
      W = blake2b(V, T - 32 * r); V.fill(0);
      out.set(W, 32 * r); W.fill(0);
    }
    inp.fill(0);
    return out;
  }

  // High 32 bits of the unsigned 64-bit product a*b (a, b uint32).
  function mulhi(a, b) {
    var al = a & 0xffff, ah = a >>> 16, bl = b & 0xffff, bh = b >>> 16;
    var ll = al * bl, p1 = al * bh, p2 = ah * bl;
    var t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    return (ah * bh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16)) >>> 0;
  }

  /* ------------------------------------------------------------------ */
  /* Compression function G (RFC 9106 section 3.5/3.6)                   */
  /* ------------------------------------------------------------------ */

  var R = new Uint32Array(256);   // working copy (gets permuted)
  var Tb = new Uint32Array(256);  // R (xor old block) saved for feed-forward

  // Permutation P on 16 64-bit words at uint32 indices i0..i15 (generated: all words held in locals)
  function P(v, i0, i1, i2, i3, i4, i5, i6, i7, i8, i9, i10, i11, i12, i13, i14, i15) {
    var w0l = v[i0], w0h = v[i0 + 1],
      w1l = v[i1], w1h = v[i1 + 1],
      w2l = v[i2], w2h = v[i2 + 1],
      w3l = v[i3], w3h = v[i3 + 1],
      w4l = v[i4], w4h = v[i4 + 1],
      w5l = v[i5], w5h = v[i5 + 1],
      w6l = v[i6], w6h = v[i6 + 1],
      w7l = v[i7], w7h = v[i7 + 1],
      w8l = v[i8], w8h = v[i8 + 1],
      w9l = v[i9], w9h = v[i9 + 1],
      w10l = v[i10], w10h = v[i10 + 1],
      w11l = v[i11], w11h = v[i11 + 1],
      w12l = v[i12], w12h = v[i12 + 1],
      w13l = v[i13], w13h = v[i13 + 1],
      w14l = v[i14], w14h = v[i14 + 1],
      w15l = v[i15], w15h = v[i15 + 1];
    var xl, xh, yl, yh, ll, p1, p2, t, ml, mh, s, nl;
    // GB(w0, w4, w8, w12)
    xl = w0l & 0xffff; xh = w0l >>> 16; yl = w4l & 0xffff; yh = w4l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w0l + w4l + ml; nl = s >>> 0;
    w0h = (w0h + w4h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w0l = nl;
    xl = w12l ^ w0l; w12l = (w12h ^ w0h) >>> 0; w12h = xl >>> 0;
    xl = w8l & 0xffff; xh = w8l >>> 16; yl = w12l & 0xffff; yh = w12l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w8l + w12l + ml; nl = s >>> 0;
    w8h = (w8h + w12h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w8l = nl;
    xl = w4l ^ w8l; xh = w4h ^ w8h;
    w4l = ((xl >>> 24) | (xh << 8)) >>> 0; w4h = ((xh >>> 24) | (xl << 8)) >>> 0;
    xl = w0l & 0xffff; xh = w0l >>> 16; yl = w4l & 0xffff; yh = w4l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w0l + w4l + ml; nl = s >>> 0;
    w0h = (w0h + w4h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w0l = nl;
    xl = w12l ^ w0l; xh = w12h ^ w0h;
    w12l = ((xl >>> 16) | (xh << 16)) >>> 0; w12h = ((xh >>> 16) | (xl << 16)) >>> 0;
    xl = w8l & 0xffff; xh = w8l >>> 16; yl = w12l & 0xffff; yh = w12l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w8l + w12l + ml; nl = s >>> 0;
    w8h = (w8h + w12h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w8l = nl;
    xl = w4l ^ w8l; xh = w4h ^ w8h;
    w4l = ((xl << 1) | (xh >>> 31)) >>> 0; w4h = ((xh << 1) | (xl >>> 31)) >>> 0;
    // GB(w1, w5, w9, w13)
    xl = w1l & 0xffff; xh = w1l >>> 16; yl = w5l & 0xffff; yh = w5l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w1l + w5l + ml; nl = s >>> 0;
    w1h = (w1h + w5h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w1l = nl;
    xl = w13l ^ w1l; w13l = (w13h ^ w1h) >>> 0; w13h = xl >>> 0;
    xl = w9l & 0xffff; xh = w9l >>> 16; yl = w13l & 0xffff; yh = w13l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w9l + w13l + ml; nl = s >>> 0;
    w9h = (w9h + w13h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w9l = nl;
    xl = w5l ^ w9l; xh = w5h ^ w9h;
    w5l = ((xl >>> 24) | (xh << 8)) >>> 0; w5h = ((xh >>> 24) | (xl << 8)) >>> 0;
    xl = w1l & 0xffff; xh = w1l >>> 16; yl = w5l & 0xffff; yh = w5l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w1l + w5l + ml; nl = s >>> 0;
    w1h = (w1h + w5h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w1l = nl;
    xl = w13l ^ w1l; xh = w13h ^ w1h;
    w13l = ((xl >>> 16) | (xh << 16)) >>> 0; w13h = ((xh >>> 16) | (xl << 16)) >>> 0;
    xl = w9l & 0xffff; xh = w9l >>> 16; yl = w13l & 0xffff; yh = w13l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w9l + w13l + ml; nl = s >>> 0;
    w9h = (w9h + w13h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w9l = nl;
    xl = w5l ^ w9l; xh = w5h ^ w9h;
    w5l = ((xl << 1) | (xh >>> 31)) >>> 0; w5h = ((xh << 1) | (xl >>> 31)) >>> 0;
    // GB(w2, w6, w10, w14)
    xl = w2l & 0xffff; xh = w2l >>> 16; yl = w6l & 0xffff; yh = w6l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w2l + w6l + ml; nl = s >>> 0;
    w2h = (w2h + w6h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w2l = nl;
    xl = w14l ^ w2l; w14l = (w14h ^ w2h) >>> 0; w14h = xl >>> 0;
    xl = w10l & 0xffff; xh = w10l >>> 16; yl = w14l & 0xffff; yh = w14l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w10l + w14l + ml; nl = s >>> 0;
    w10h = (w10h + w14h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w10l = nl;
    xl = w6l ^ w10l; xh = w6h ^ w10h;
    w6l = ((xl >>> 24) | (xh << 8)) >>> 0; w6h = ((xh >>> 24) | (xl << 8)) >>> 0;
    xl = w2l & 0xffff; xh = w2l >>> 16; yl = w6l & 0xffff; yh = w6l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w2l + w6l + ml; nl = s >>> 0;
    w2h = (w2h + w6h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w2l = nl;
    xl = w14l ^ w2l; xh = w14h ^ w2h;
    w14l = ((xl >>> 16) | (xh << 16)) >>> 0; w14h = ((xh >>> 16) | (xl << 16)) >>> 0;
    xl = w10l & 0xffff; xh = w10l >>> 16; yl = w14l & 0xffff; yh = w14l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w10l + w14l + ml; nl = s >>> 0;
    w10h = (w10h + w14h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w10l = nl;
    xl = w6l ^ w10l; xh = w6h ^ w10h;
    w6l = ((xl << 1) | (xh >>> 31)) >>> 0; w6h = ((xh << 1) | (xl >>> 31)) >>> 0;
    // GB(w3, w7, w11, w15)
    xl = w3l & 0xffff; xh = w3l >>> 16; yl = w7l & 0xffff; yh = w7l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w3l + w7l + ml; nl = s >>> 0;
    w3h = (w3h + w7h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w3l = nl;
    xl = w15l ^ w3l; w15l = (w15h ^ w3h) >>> 0; w15h = xl >>> 0;
    xl = w11l & 0xffff; xh = w11l >>> 16; yl = w15l & 0xffff; yh = w15l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w11l + w15l + ml; nl = s >>> 0;
    w11h = (w11h + w15h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w11l = nl;
    xl = w7l ^ w11l; xh = w7h ^ w11h;
    w7l = ((xl >>> 24) | (xh << 8)) >>> 0; w7h = ((xh >>> 24) | (xl << 8)) >>> 0;
    xl = w3l & 0xffff; xh = w3l >>> 16; yl = w7l & 0xffff; yh = w7l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w3l + w7l + ml; nl = s >>> 0;
    w3h = (w3h + w7h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w3l = nl;
    xl = w15l ^ w3l; xh = w15h ^ w3h;
    w15l = ((xl >>> 16) | (xh << 16)) >>> 0; w15h = ((xh >>> 16) | (xl << 16)) >>> 0;
    xl = w11l & 0xffff; xh = w11l >>> 16; yl = w15l & 0xffff; yh = w15l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w11l + w15l + ml; nl = s >>> 0;
    w11h = (w11h + w15h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w11l = nl;
    xl = w7l ^ w11l; xh = w7h ^ w11h;
    w7l = ((xl << 1) | (xh >>> 31)) >>> 0; w7h = ((xh << 1) | (xl >>> 31)) >>> 0;
    // GB(w0, w5, w10, w15)
    xl = w0l & 0xffff; xh = w0l >>> 16; yl = w5l & 0xffff; yh = w5l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w0l + w5l + ml; nl = s >>> 0;
    w0h = (w0h + w5h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w0l = nl;
    xl = w15l ^ w0l; w15l = (w15h ^ w0h) >>> 0; w15h = xl >>> 0;
    xl = w10l & 0xffff; xh = w10l >>> 16; yl = w15l & 0xffff; yh = w15l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w10l + w15l + ml; nl = s >>> 0;
    w10h = (w10h + w15h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w10l = nl;
    xl = w5l ^ w10l; xh = w5h ^ w10h;
    w5l = ((xl >>> 24) | (xh << 8)) >>> 0; w5h = ((xh >>> 24) | (xl << 8)) >>> 0;
    xl = w0l & 0xffff; xh = w0l >>> 16; yl = w5l & 0xffff; yh = w5l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w0l + w5l + ml; nl = s >>> 0;
    w0h = (w0h + w5h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w0l = nl;
    xl = w15l ^ w0l; xh = w15h ^ w0h;
    w15l = ((xl >>> 16) | (xh << 16)) >>> 0; w15h = ((xh >>> 16) | (xl << 16)) >>> 0;
    xl = w10l & 0xffff; xh = w10l >>> 16; yl = w15l & 0xffff; yh = w15l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w10l + w15l + ml; nl = s >>> 0;
    w10h = (w10h + w15h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w10l = nl;
    xl = w5l ^ w10l; xh = w5h ^ w10h;
    w5l = ((xl << 1) | (xh >>> 31)) >>> 0; w5h = ((xh << 1) | (xl >>> 31)) >>> 0;
    // GB(w1, w6, w11, w12)
    xl = w1l & 0xffff; xh = w1l >>> 16; yl = w6l & 0xffff; yh = w6l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w1l + w6l + ml; nl = s >>> 0;
    w1h = (w1h + w6h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w1l = nl;
    xl = w12l ^ w1l; w12l = (w12h ^ w1h) >>> 0; w12h = xl >>> 0;
    xl = w11l & 0xffff; xh = w11l >>> 16; yl = w12l & 0xffff; yh = w12l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w11l + w12l + ml; nl = s >>> 0;
    w11h = (w11h + w12h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w11l = nl;
    xl = w6l ^ w11l; xh = w6h ^ w11h;
    w6l = ((xl >>> 24) | (xh << 8)) >>> 0; w6h = ((xh >>> 24) | (xl << 8)) >>> 0;
    xl = w1l & 0xffff; xh = w1l >>> 16; yl = w6l & 0xffff; yh = w6l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w1l + w6l + ml; nl = s >>> 0;
    w1h = (w1h + w6h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w1l = nl;
    xl = w12l ^ w1l; xh = w12h ^ w1h;
    w12l = ((xl >>> 16) | (xh << 16)) >>> 0; w12h = ((xh >>> 16) | (xl << 16)) >>> 0;
    xl = w11l & 0xffff; xh = w11l >>> 16; yl = w12l & 0xffff; yh = w12l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w11l + w12l + ml; nl = s >>> 0;
    w11h = (w11h + w12h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w11l = nl;
    xl = w6l ^ w11l; xh = w6h ^ w11h;
    w6l = ((xl << 1) | (xh >>> 31)) >>> 0; w6h = ((xh << 1) | (xl >>> 31)) >>> 0;
    // GB(w2, w7, w8, w13)
    xl = w2l & 0xffff; xh = w2l >>> 16; yl = w7l & 0xffff; yh = w7l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w2l + w7l + ml; nl = s >>> 0;
    w2h = (w2h + w7h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w2l = nl;
    xl = w13l ^ w2l; w13l = (w13h ^ w2h) >>> 0; w13h = xl >>> 0;
    xl = w8l & 0xffff; xh = w8l >>> 16; yl = w13l & 0xffff; yh = w13l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w8l + w13l + ml; nl = s >>> 0;
    w8h = (w8h + w13h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w8l = nl;
    xl = w7l ^ w8l; xh = w7h ^ w8h;
    w7l = ((xl >>> 24) | (xh << 8)) >>> 0; w7h = ((xh >>> 24) | (xl << 8)) >>> 0;
    xl = w2l & 0xffff; xh = w2l >>> 16; yl = w7l & 0xffff; yh = w7l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w2l + w7l + ml; nl = s >>> 0;
    w2h = (w2h + w7h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w2l = nl;
    xl = w13l ^ w2l; xh = w13h ^ w2h;
    w13l = ((xl >>> 16) | (xh << 16)) >>> 0; w13h = ((xh >>> 16) | (xl << 16)) >>> 0;
    xl = w8l & 0xffff; xh = w8l >>> 16; yl = w13l & 0xffff; yh = w13l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w8l + w13l + ml; nl = s >>> 0;
    w8h = (w8h + w13h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w8l = nl;
    xl = w7l ^ w8l; xh = w7h ^ w8h;
    w7l = ((xl << 1) | (xh >>> 31)) >>> 0; w7h = ((xh << 1) | (xl >>> 31)) >>> 0;
    // GB(w3, w4, w9, w14)
    xl = w3l & 0xffff; xh = w3l >>> 16; yl = w4l & 0xffff; yh = w4l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w3l + w4l + ml; nl = s >>> 0;
    w3h = (w3h + w4h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w3l = nl;
    xl = w14l ^ w3l; w14l = (w14h ^ w3h) >>> 0; w14h = xl >>> 0;
    xl = w9l & 0xffff; xh = w9l >>> 16; yl = w14l & 0xffff; yh = w14l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w9l + w14l + ml; nl = s >>> 0;
    w9h = (w9h + w14h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w9l = nl;
    xl = w4l ^ w9l; xh = w4h ^ w9h;
    w4l = ((xl >>> 24) | (xh << 8)) >>> 0; w4h = ((xh >>> 24) | (xl << 8)) >>> 0;
    xl = w3l & 0xffff; xh = w3l >>> 16; yl = w4l & 0xffff; yh = w4l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w3l + w4l + ml; nl = s >>> 0;
    w3h = (w3h + w4h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w3l = nl;
    xl = w14l ^ w3l; xh = w14h ^ w3h;
    w14l = ((xl >>> 16) | (xh << 16)) >>> 0; w14h = ((xh >>> 16) | (xl << 16)) >>> 0;
    xl = w9l & 0xffff; xh = w9l >>> 16; yl = w14l & 0xffff; yh = w14l >>> 16;
    ll = xl * yl; p1 = xl * yh; p2 = xh * yl;
    t = (ll >>> 16) + (p1 & 0xffff) + (p2 & 0xffff);
    mh = xh * yh + (p1 >>> 16) + (p2 >>> 16) + (t >>> 16);
    ml = (t << 16) | (ll & 0xffff);
    mh = (mh << 1) | (ml >>> 31); ml = (ml << 1) >>> 0;
    s = w9l + w14l + ml; nl = s >>> 0;
    w9h = (w9h + w14h + (mh >>> 0) + ((s / 4294967296) | 0)) >>> 0; w9l = nl;
    xl = w4l ^ w9l; xh = w4h ^ w9h;
    w4l = ((xl << 1) | (xh >>> 31)) >>> 0; w4h = ((xh << 1) | (xl >>> 31)) >>> 0;
    v[i0] = w0l; v[i0 + 1] = w0h;
    v[i1] = w1l; v[i1 + 1] = w1h;
    v[i2] = w2l; v[i2 + 1] = w2h;
    v[i3] = w3l; v[i3 + 1] = w3h;
    v[i4] = w4l; v[i4 + 1] = w4h;
    v[i5] = w5l; v[i5 + 1] = w5h;
    v[i6] = w6l; v[i6 + 1] = w6h;
    v[i7] = w7l; v[i7 + 1] = w7h;
    v[i8] = w8l; v[i8 + 1] = w8h;
    v[i9] = w9l; v[i9 + 1] = w9h;
    v[i10] = w10l; v[i10 + 1] = w10h;
    v[i11] = w11l; v[i11 + 1] = w11h;
    v[i12] = w12l; v[i12 + 1] = w12h;
    v[i13] = w13l; v[i13 + 1] = w13h;
    v[i14] = w14l; v[i14 + 1] = w14h;
    v[i15] = w15l; v[i15 + 1] = w15h;
  }

  // next = G(prev, ref) [xor next if withXor]. Offsets are uint32 indices into the arrays.
  function fillBlock(pa, po, ra, ro, na, no, withXor) {
    var k, r;
    if (withXor) {
      for (k = 0; k < 256; k++) { r = pa[po + k] ^ ra[ro + k]; R[k] = r; Tb[k] = r ^ na[no + k]; }
    } else {
      for (k = 0; k < 256; k++) { r = pa[po + k] ^ ra[ro + k]; R[k] = r; Tb[k] = r; }
    }
    var b;
    // rows: 8 rows of 16 consecutive 64-bit words
    for (k = 0; k < 8; k++) {
      b = 32 * k;
      P(R, b, b + 2, b + 4, b + 6, b + 8, b + 10, b + 12, b + 14,
        b + 16, b + 18, b + 20, b + 22, b + 24, b + 26, b + 28, b + 30);
    }
    // columns: words (2k, 2k+1, 2k+16, 2k+17, ..., 2k+112, 2k+113)
    for (k = 0; k < 8; k++) {
      b = 4 * k;
      P(R, b, b + 2, b + 32, b + 34, b + 64, b + 66, b + 96, b + 98,
        b + 128, b + 130, b + 160, b + 162, b + 192, b + 194, b + 224, b + 226);
    }
    for (k = 0; k < 256; k++) na[no + k] = Tb[k] ^ R[k];
  }

  /* ------------------------------------------------------------------ */
  /* Event-loop yielding                                                 */
  /* ------------------------------------------------------------------ */

  var YIELD_MS = 30;   // max time between yields
  var CHUNK = 32;      // blocks computed between clock checks

  var yieldNow = null;
  function getYield() {
    if (yieldNow) return yieldNow;
    if (typeof setImmediate === 'function') {
      yieldNow = function () { return new Promise(function (res) { setImmediate(res); }); };
    } else if (typeof MessageChannel === 'function') {
      var ch = new MessageChannel(), queue = [];
      ch.port1.onmessage = function () { var f = queue.shift(); if (f) f(); };
      yieldNow = function () { return new Promise(function (res) { queue.push(res); ch.port2.postMessage(0); }); };
    } else {
      yieldNow = function () { return new Promise(function (res) { setTimeout(res, 0); }); };
    }
    return yieldNow;
  }
  var now = (typeof performance !== 'undefined' && performance && typeof performance.now === 'function')
    ? function () { return performance.now(); }
    : function () { return Date.now(); };

  /* ------------------------------------------------------------------ */
  /* Argon2id                                                            */
  /* ------------------------------------------------------------------ */

  function isU32Int(x) { return typeof x === 'number' && Math.floor(x) === x && x >= 0 && x <= 0xffffffff; }

  /**
   * Argon2id (type 2, version 0x13). Returns Promise<Uint8Array> of tagLength bytes.
   * Throws synchronously on invalid parameters.
   */
  function argon2id(password, salt, memoryKiB, passes, lanes, tagLength, secret, ad, onProgress) {
    if (!(password instanceof Uint8Array)) throw new TypeError('argon2id: password must be a Uint8Array');
    if (!(salt instanceof Uint8Array)) throw new TypeError('argon2id: salt must be a Uint8Array');
    if (secret === undefined || secret === null) secret = new Uint8Array(0);
    else if (!(secret instanceof Uint8Array)) throw new TypeError('argon2id: secret must be a Uint8Array');
    if (ad === undefined || ad === null) ad = new Uint8Array(0);
    else if (!(ad instanceof Uint8Array)) throw new TypeError('argon2id: ad must be a Uint8Array');
    if (onProgress !== undefined && onProgress !== null && typeof onProgress !== 'function')
      throw new TypeError('argon2id: onProgress must be a function');
    if (!isU32Int(lanes) || lanes < 1 || lanes > 16) throw new RangeError('argon2id: lanes must be an integer 1..16');
    if (!isU32Int(memoryKiB) || memoryKiB < 8 * lanes) throw new RangeError('argon2id: memoryKiB must be an integer >= 8*lanes');
    if (!isU32Int(passes) || passes < 1) throw new RangeError('argon2id: passes must be an integer >= 1');
    if (!isU32Int(tagLength) || tagLength < 4) throw new RangeError('argon2id: tagLength must be an integer >= 4');
    return run(password, salt, memoryKiB, passes, lanes, tagLength, secret, ad, onProgress || null);
  }

  async function run(password, salt, memoryKiB, passes, lanes, tagLength, secret, ad, onProgress) {
    var yieldFn = getYield();
    var TYPE = 2, VERSION = 0x13;
    var mPrime = 4 * lanes * Math.floor(memoryKiB / (4 * lanes));
    var q = mPrime / lanes;          // lane length (columns)
    var segLen = q / 4;              // segment length

    // H0
    var h0in = concat([le32(lanes), le32(tagLength), le32(memoryKiB), le32(passes), le32(VERSION), le32(TYPE),
      le32(password.length), password, le32(salt.length), salt, le32(secret.length), secret, le32(ad.length), ad]);
    var H0 = blake2b(h0in, 64);
    h0in.fill(0);

    var mem = new Uint32Array(mPrime * 256);
    var seed = new Uint8Array(72);
    seed.set(H0, 0);
    var lane, j, k, blk;
    for (lane = 0; lane < lanes; lane++) {
      for (j = 0; j < 2; j++) {
        seed.set(le32(j), 64);
        seed.set(le32(lane), 68);
        blk = hprime(1024, seed);
        var base = (lane * q + j) * 256;
        for (k = 0; k < 256; k++) {
          mem[base + k] = blk[4 * k] | (blk[4 * k + 1] << 8) | (blk[4 * k + 2] << 16) | (blk[4 * k + 3] << 24);
        }
        blk.fill(0);
      }
    }
    seed.fill(0); H0.fill(0);

    var zero = new Uint32Array(256);
    var input = new Uint32Array(256);
    var addr = new Uint32Array(256);

    function nextAddresses() {
      input[12]++;  // counter (64-bit word 6, low half)
      fillBlock(zero, 0, input, 0, addr, 0, false);
      fillBlock(zero, 0, addr, 0, addr, 0, false);
    }

    // Compute blocks [start, end) of segment (pass, slice, lane).
    function fillSegment(pass, slice, lane, start, end, init) {
      var dataIndep = pass === 0 && slice < 2;   // Argon2id: data-independent in first half of pass 0
      if (init && dataIndep) {
        input.fill(0);
        input[0] = pass; input[2] = lane; input[4] = slice;
        input[6] = mPrime; input[8] = passes; input[10] = TYPE;
        if (pass === 0 && slice === 0) nextAddresses();
      }
      var laneBase = lane * q;
      var startPos = (pass !== 0 && slice !== 3) ? (slice + 1) * segLen : 0;
      for (var i = start; i < end; i++) {
        var curr = laneBase + slice * segLen + i;
        var prev = (slice === 0 && i === 0) ? curr + q - 1 : curr - 1;
        var j1, j2;
        if (dataIndep) {
          var ai = i & 127;
          if (ai === 0) nextAddresses();
          j1 = addr[2 * ai]; j2 = addr[2 * ai + 1];
        } else {
          j1 = mem[prev * 256]; j2 = mem[prev * 256 + 1];
        }
        var refLane = (pass === 0 && slice === 0) ? lane : j2 % lanes;
        var same = refLane === lane;
        var area;
        if (pass === 0) {
          if (slice === 0) area = i - 1;
          else area = same ? slice * segLen + i - 1 : slice * segLen + (i === 0 ? -1 : 0);
        } else {
          area = same ? q - segLen + i - 1 : q - segLen + (i === 0 ? -1 : 0);
        }
        var x = mulhi(j1, j1);
        var rel = area - 1 - mulhi(area, x);
        var refIdx = (startPos + rel) % q;
        fillBlock(mem, prev * 256, mem, (refLane * q + refIdx) * 256, mem, curr * 256, pass !== 0);
      }
    }

    var total = passes * (mPrime - 2 * lanes);
    var done = 0;
    var last = now();
    try {
      for (var pass = 0; pass < passes; pass++) {
        for (var slice = 0; slice < 4; slice++) {
          for (lane = 0; lane < lanes; lane++) {
            var s0 = (pass === 0 && slice === 0) ? 2 : 0;
            for (var s = s0; s < segLen;) {
              var e = s + CHUNK; if (e > segLen) e = segLen;
              fillSegment(pass, slice, lane, s, e, s === s0);
              done += e - s; s = e;
              if (now() - last >= YIELD_MS) {
                if (onProgress) onProgress(done / total);
                await yieldFn();
                last = now();
              }
            }
          }
        }
      }

      // Final block C = XOR of last column; tag = H'^T(C)
      var C = new Uint32Array(256);
      for (lane = 0; lane < lanes; lane++) {
        var lb = (lane * q + q - 1) * 256;
        for (k = 0; k < 256; k++) C[k] ^= mem[lb + k];
      }
      var cb = new Uint8Array(1024);
      for (k = 0; k < 256; k++) {
        var w = C[k];
        cb[4 * k] = w; cb[4 * k + 1] = w >>> 8; cb[4 * k + 2] = w >>> 16; cb[4 * k + 3] = w >>> 24;
      }
      var tag = hprime(tagLength, cb);
      C.fill(0); cb.fill(0);
      if (onProgress) onProgress(1);
      return tag;
    } finally {
      mem.fill(0); input.fill(0); addr.fill(0); R.fill(0); Tb.fill(0);
    }
  }

  return { argon2id: argon2id, blake2b: blake2b };
})();
if (typeof module !== 'undefined') module.exports = HKArgon2;
