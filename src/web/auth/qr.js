// Hallow Authenticator - a small QR code reader, written for this app (no library, nothing downloaded), for phones whose browser has no
// BarcodeDetector (iPhones). It finds the three finder squares, the alignment square when there is one, undoes the perspective, reads the
// format bits, unmasks, de-interleaves, fixes errors with Reed-Solomon and decodes numeric, alphanumeric and byte segments (versions 1-40).
// The steps follow the QR standard (ISO/IEC 18004); the error correction is the textbook Berlekamp-Massey + Forney.

// ---- GF(256) with the QR polynomial x^8 + x^4 + x^3 + x^2 + 1
const EXP = new Uint8Array(512), LOG = new Uint8Array(256);
(() => { let x = 1; for (let i = 0; i < 255; i++) { EXP[i] = x; LOG[x] = i; x <<= 1; if (x & 256) x ^= 0x11d; } for (let i = 255; i < 512; i++) EXP[i] = EXP[i - 255]; })();
const gmul = (a, b) => (a && b ? EXP[LOG[a] + LOG[b]] : 0);
const gdiv = (a, b) => { if (!b) throw new Error('div0'); return a ? EXP[(LOG[a] + 255 - LOG[b]) % 255] : 0; };
const gpow = (x, p) => EXP[(((LOG[x] * p) % 255) + 255) % 255];
const ginv = x => EXP[255 - LOG[x]];
const pScale = (p, x) => p.map(c => gmul(c, x));
function pAdd(p, q) { const r = new Array(Math.max(p.length, q.length)).fill(0); for (let i = 0; i < p.length; i++) r[i + r.length - p.length] = p[i]; for (let i = 0; i < q.length; i++) r[i + r.length - q.length] ^= q[i]; return r; }
function pMul(p, q) { const r = new Array(p.length + q.length - 1).fill(0); for (let j = 0; j < q.length; j++) for (let i = 0; i < p.length; i++) r[i + j] ^= gmul(p[i], q[j]); return r; }
function pEval(p, x) { let y = p[0]; for (let i = 1; i < p.length; i++) y = gmul(y, x) ^ p[i]; return y; }
function pDiv(dividend, divisor) {
  const out = dividend.slice();
  for (let i = 0; i < dividend.length - (divisor.length - 1); i++) { const c = out[i]; if (c) for (let j = 1; j < divisor.length; j++) if (divisor[j]) out[i + j] ^= gmul(divisor[j], c); }
  const sep = out.length - (divisor.length - 1); return [out.slice(0, sep), out.slice(sep)];
}
// corrects msg (data + nsym check bytes, first = highest degree) in place; false if it can't
export function rsCorrect(msg, nsym) {
  const synd = [0]; let bad = false;
  for (let i = 0; i < nsym; i++) { const s = pEval(msg, gpow(2, i)); synd.push(s); if (s) bad = true; }
  if (!bad) return true;
  // Berlekamp-Massey: the error locator
  let loc = [1], old = [1];
  for (let i = 0; i < nsym; i++) {
    const K = i + 1; let delta = synd[K];
    for (let j = 1; j < loc.length; j++) delta ^= gmul(loc[loc.length - 1 - j], synd[K - j]);
    old = old.concat([0]);
    if (delta) { if (old.length > loc.length) { const nl = pScale(old, delta); old = pScale(loc, ginv(delta)); loc = nl; } loc = pAdd(loc, pScale(old, delta)); }
  }
  while (loc.length && loc[0] === 0) loc.shift();
  const errs = loc.length - 1; if (errs * 2 > nsym || errs < 1) return false;
  // Chien search
  const rev = loc.slice().reverse(), pos = [];
  for (let i = 0; i < msg.length; i++) if (pEval(rev, gpow(2, i)) === 0) pos.push(msg.length - 1 - i);
  if (pos.length !== errs) return false;
  // Forney
  const coef = pos.map(p => msg.length - 1 - p);
  let eloc = [1]; for (const c of coef) eloc = pMul(eloc, pAdd([1], [gpow(2, c), 0]));
  const sr = synd.slice().reverse();
  let ev = pDiv(pMul(sr, eloc), [1].concat(new Array(eloc.length).fill(0)))[1];
  ev = ev.slice().reverse();
  const X = coef.map(c => gpow(2, c));
  for (let i = 0; i < X.length; i++) {
    const xi = ginv(X[i]); let lp = 1;
    for (let j = 0; j < X.length; j++) if (j !== i) lp = gmul(lp, 1 ^ gmul(xi, X[j]));
    if (!lp) return false;
    const y = gmul(X[i], pEval(ev.slice().reverse(), xi));
    msg[pos[i]] ^= gdiv(y, lp);
  }
  for (let i = 0; i < nsym; i++) if (pEval(msg, gpow(2, i))) return false;
  return true;
}

// ---- tables (by version 1-40; index 0 unused). L, M, Q, H
const ECC_PER_BLOCK = [
  [-1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
  [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28],
  [-1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
  [-1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30]];
const BLOCKS = [
  [-1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25],
  [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49],
  [-1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68],
  [-1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81]];
export function rawModules(ver) { let r = (16 * ver + 128) * ver + 64; if (ver >= 2) { const n = Math.floor(ver / 7) + 2; r -= (25 * n - 10) * n - 55; if (ver >= 7) r -= 36; } return r; }
export function alignPositions(ver) {
  if (ver === 1) return [];
  const n = Math.floor(ver / 7) + 2, size = ver * 4 + 17, step = Math.floor((ver * 8 + n * 3 + 5) / (n * 4 - 4)) * 2;
  const r = [6]; for (let i = n - 1, pos = size - 7; r.length < n; i--, pos -= step) r.splice(1, 0, pos);
  return r;
}
// format bits: ecc level index (L=0 M=1 Q=2 H=3) and mask -> the 15 bits (with BCH and the 0x5412 mask)
const ECL_BITS = [1, 0, 3, 2];
export function formatBits(ecl, mask) { const data = (ECL_BITS[ecl] << 3) | mask; let rem = data; for (let i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >>> 9) * 0x537); return ((data << 10) | rem) ^ 0x5412; }
const FORMATS = []; for (let e = 0; e < 4; e++) for (let m = 0; m < 8; m++) FORMATS.push([formatBits(e, m), e, m]);
const popcount = x => { let c = 0; while (x) { c += x & 1; x >>>= 1; } return c; };

export function functionMap(ver) {
  const size = ver * 4 + 17, f = Array.from({ length: size }, () => new Uint8Array(size));
  const set = (x, y) => { if (x >= 0 && y >= 0 && x < size && y < size) f[y][x] = 1; };
  for (let i = 0; i < size; i++) { set(6, i); set(i, 6); }
  for (const [cx, cy] of [[3, 3], [size - 4, 3], [3, size - 4]]) for (let dy = -4; dy <= 4; dy++) for (let dx = -4; dx <= 4; dx++) set(cx + dx, cy + dy);
  const al = alignPositions(ver), n = al.length;
  for (let i = 0; i < n; i++) for (let j = 0; j < n; j++) { if ((i === 0 && j === 0) || (i === 0 && j === n - 1) || (i === n - 1 && j === 0)) continue; for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) set(al[i] + dx, al[j] + dy); }
  for (let i = 0; i <= 8; i++) { set(8, i); set(i, 8); }
  for (let i = 0; i < 8; i++) set(size - 1 - i, 8);
  for (let i = 0; i < 8; i++) set(8, size - 1 - i);
  if (ver >= 7) for (let i = 0; i < 18; i++) { const a = size - 11 + (i % 3), b = Math.floor(i / 3); set(a, b); set(b, a); }
  return f;
}
export function maskBit(mask, x, y) {
  switch (mask) {
    case 0: return (x + y) % 2 === 0; case 1: return y % 2 === 0; case 2: return x % 3 === 0; case 3: return (x + y) % 3 === 0;
    case 4: return (Math.floor(x / 3) + Math.floor(y / 2)) % 2 === 0; case 5: return (x * y) % 2 + (x * y) % 3 === 0;
    case 6: return ((x * y) % 2 + (x * y) % 3) % 2 === 0; default: return ((x + y) % 2 + (x * y) % 3) % 2 === 0;
  }
}

// ---- from a grid of modules (grid[y][x] true = dark) to the text
export function decodeGrid(grid) {
  const size = grid.length, ver = (size - 17) / 4;
  if (!Number.isInteger(ver) || ver < 1 || ver > 40) return null;
  const g = (x, y) => (grid[y][x] ? 1 : 0);
  // format: both copies, nearest valid pattern
  let a = 0, b = 0;
  for (let i = 0; i <= 5; i++) a |= g(8, i) << i;
  a |= g(8, 7) << 6; a |= g(8, 8) << 7; a |= g(7, 8) << 8;
  for (let i = 9; i < 15; i++) a |= g(14 - i, 8) << i;
  for (let i = 0; i < 8; i++) b |= g(size - 1 - i, 8) << i;
  for (let i = 8; i < 15; i++) b |= g(8, size - 15 + i) << i;
  let best = null, bd = 99;
  for (const [bits, e, m] of FORMATS) { const d = Math.min(popcount(bits ^ a), popcount(bits ^ b)); if (d < bd) { bd = d; best = [e, m]; } }
  if (bd > 3) return null;
  const [ecl, mask] = best, fn = functionMap(ver);
  // codewords in the zigzag order
  const total = Math.floor(rawModules(ver) / 8), cw = new Uint8Array(total); let i = 0;
  for (let right = size - 1; right >= 1; right -= 2) {
    if (right === 6) right = 5;
    for (let vert = 0; vert < size; vert++) for (let j = 0; j < 2; j++) {
      const x = right - j, up = ((right + 1) & 2) === 0, y = up ? size - 1 - vert : vert;
      if (fn[y][x] || i >= total * 8) continue;
      if (g(x, y) ^ (maskBit(mask, x, y) ? 1 : 0)) cw[i >>> 3] |= 0x80 >> (i & 7);
      i++;
    }
  }
  // de-interleave, correct each block
  const nb = BLOCKS[ecl][ver], ecc = ECC_PER_BLOCK[ecl][ver], numShort = nb - total % nb, shortLen = Math.floor(total / nb);
  const blocks = Array.from({ length: nb }, () => []); let k = 0;
  for (let p = 0; p <= shortLen; p++) for (let j = 0; j < nb; j++) { if (p === shortLen - ecc && j < numShort) continue; blocks[j].push(cw[k++]); }
  // (short blocks have shortLen codewords, long ones shortLen + 1; the ecc part is always the last `ecc`)
  const data = [];
  for (let j = 0; j < nb; j++) {
    const blk = blocks[j];
    if (!rsCorrect(blk, ecc)) return null;
    for (let q = 0; q < blk.length - ecc; q++) data.push(blk[q]);
  }
  return parseSegments(data, ver);
}
const ALNUM = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:';
function parseSegments(data, ver) {
  let pos = 0; const len = data.length * 8;
  const read = n => { let v = 0; for (let q = 0; q < n; q++) { v = (v << 1) | ((data[pos >>> 3] >>> (7 - (pos & 7))) & 1); pos++; } return v; };
  const cb = (m) => { const t = ver <= 9 ? 0 : ver <= 26 ? 1 : 2; return { 1: [10, 12, 14], 2: [9, 11, 13], 4: [8, 16, 16], 8: [8, 10, 12] }[m][t]; };
  const parts = [], bytes = [];
  const flushBytes = () => { if (bytes.length) { parts.push(decodeBytes(bytes)); bytes.length = 0; } };
  while (pos + 4 <= len) {
    const mode = read(4);
    if (mode === 0) break;
    if (mode === 7) { const f = read(8); if ((f & 0x80) === 0) { } else if ((f & 0xc0) === 0x80) read(8); else read(16); continue; }
    if (mode === 3) { read(16); continue; }
    if (mode === 5) continue;
    if (mode === 9) { read(8); continue; }
    if (mode !== 1 && mode !== 2 && mode !== 4) break;   // kanji and others: not used by authenticator QR codes
    let n = read(cb(mode));
    if (mode === 4) { if (pos + n * 8 > len) return null; for (let q = 0; q < n; q++) bytes.push(read(8)); continue; }
    flushBytes(); let s = '';
    if (mode === 1) { while (n >= 3) { const v = read(10); s += String(v).padStart(3, '0'); n -= 3; } if (n === 2) s += String(read(7)).padStart(2, '0'); else if (n === 1) s += String(read(4)); }
    else { while (n >= 2) { const v = read(11); s += ALNUM[Math.floor(v / 45)] + ALNUM[v % 45]; n -= 2; } if (n === 1) s += ALNUM[read(6)]; }
    if (pos > len) return null;
    parts.push(s);
  }
  flushBytes();
  return parts.join('');
}
function decodeBytes(b) {
  const u = new Uint8Array(b);
  try { return new TextDecoder('utf-8', { fatal: true }).decode(u); } catch (x) { let s = ''; for (const c of u) s += String.fromCharCode(c); return s; }
}

// ---- image -> dark / light pixels (a local threshold, so shadows and glare on a phone screen don't matter much)
export function binarize(img, invert) {
  const { data, width: w, height: h } = img, lum = new Uint8Array(w * h);
  for (let i = 0, p = 0; i < w * h; i++, p += 4) lum[i] = (data[p] * 77 + data[p + 1] * 150 + data[p + 2] * 29) >> 8;
  const B = 8, bw = Math.ceil(w / B), bh = Math.ceil(h / B), black = new Float32Array(bw * bh);
  for (let by = 0; by < bh; by++) for (let bx = 0; bx < bw; bx++) {
    let sum = 0, mn = 255, mx = 0, n = 0;
    for (let y = by * B; y < Math.min(h, by * B + B); y++) for (let x = bx * B; x < Math.min(w, bx * B + B); x++) { const v = lum[y * w + x]; sum += v; n++; if (v < mn) mn = v; if (v > mx) mx = v; }
    let avg = sum / n;
    if (mx - mn <= 24) { avg = mn / 2; if (by > 0 && bx > 0) { const nb = (black[(by - 1) * bw + bx] + 2 * black[by * bw + bx - 1] + black[(by - 1) * bw + bx - 1]) / 4; if (mn < nb) avg = nb; } }
    black[by * bw + bx] = avg;
  }
  const out = new Uint8Array(w * h);
  for (let by = 0; by < bh; by++) for (let bx = 0; bx < bw; bx++) {
    let s = 0, n = 0;
    for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) { const yy = by + dy, xx = bx + dx; if (yy >= 0 && xx >= 0 && yy < bh && xx < bw) { s += black[yy * bw + xx]; n++; } }
    const t = s / n;
    for (let y = by * B; y < Math.min(h, by * B + B); y++) for (let x = bx * B; x < Math.min(w, bx * B + B); x++) { const d = lum[y * w + x] <= t ? 1 : 0; out[y * w + x] = invert ? 1 - d : d; }
  }
  return { bits: out, w, h };
}

// ---- finder patterns (1:1:3:1:1)
function ratioOk(c, tol) {
  let t = 0; for (const v of c) { if (!v) return false; t += v; } if (t < 7) return false;
  const m = t / 7, v = m * tol;
  return Math.abs(m - c[0]) < v && Math.abs(m - c[1]) < v && Math.abs(3 * m - c[2]) < 3 * v && Math.abs(m - c[3]) < v && Math.abs(m - c[4]) < v;
}
function findFinders(B) {
  const { bits, w, h } = B, at = (x, y) => bits[y * w + x];
  const cands = [];
  const crossV = (cx, cy, maxC, tot) => {
    const c = [0, 0, 0, 0, 0]; let y = cy;
    while (y >= 0 && at(cx, y)) { c[2]++; y--; } if (y < 0) return NaN;
    while (y >= 0 && !at(cx, y) && c[1] <= maxC) { c[1]++; y--; } if (y < 0 || c[1] > maxC) return NaN;
    while (y >= 0 && at(cx, y) && c[0] <= maxC) { c[0]++; y--; } if (c[0] > maxC) return NaN;
    y = cy + 1;
    while (y < h && at(cx, y)) { c[2]++; y++; } if (y === h) return NaN;
    while (y < h && !at(cx, y) && c[3] < maxC) { c[3]++; y++; } if (y === h || c[3] >= maxC) return NaN;
    while (y < h && at(cx, y) && c[4] < maxC) { c[4]++; y++; } if (c[4] >= maxC) return NaN;
    const t = c[0] + c[1] + c[2] + c[3] + c[4]; if (5 * Math.abs(t - tot) >= 2 * tot) return NaN;
    return ratioOk(c, 0.7) ? y - c[4] - c[3] - c[2] / 2 : NaN;
  };
  const crossH = (cx, cy, maxC, tot) => {
    const c = [0, 0, 0, 0, 0]; let x = cx;
    while (x >= 0 && at(x, cy)) { c[2]++; x--; } if (x < 0) return NaN;
    while (x >= 0 && !at(x, cy) && c[1] <= maxC) { c[1]++; x--; } if (x < 0 || c[1] > maxC) return NaN;
    while (x >= 0 && at(x, cy) && c[0] <= maxC) { c[0]++; x--; } if (c[0] > maxC) return NaN;
    x = cx + 1;
    while (x < w && at(x, cy)) { c[2]++; x++; } if (x === w) return NaN;
    while (x < w && !at(x, cy) && c[3] < maxC) { c[3]++; x++; } if (x === w || c[3] >= maxC) return NaN;
    while (x < w && at(x, cy) && c[4] < maxC) { c[4]++; x++; } if (c[4] >= maxC) return NaN;
    const t = c[0] + c[1] + c[2] + c[3] + c[4]; if (5 * Math.abs(t - tot) >= tot) return NaN;
    return ratioOk(c, 0.7) ? { x: x - c[4] - c[3] - c[2] / 2, size: t / 7 } : NaN;
  };
  const found = (c, y, xEnd) => {
    const tot = c[0] + c[1] + c[2] + c[3] + c[4];
    let cx = xEnd - c[4] - c[3] - c[2] / 2;
    const cy = crossV(Math.floor(cx), y, c[2], tot); if (isNaN(cy)) return false;
    const hr = crossH(Math.floor(cx), Math.floor(cy), c[2], tot); if (!hr || isNaN(hr.x)) return false;
    cx = hr.x; const ms = hr.size;
    for (const p of cands) if (Math.abs(cy - p.y) <= ms && Math.abs(cx - p.x) <= ms && (Math.abs(ms - p.size) <= 1 || Math.abs(ms - p.size) <= ms)) {
      const n = p.n; p.x = (p.x * n + cx) / (n + 1); p.y = (p.y * n + cy) / (n + 1); p.size = (p.size * n + ms) / (n + 1); p.n++; return true;
    }
    cands.push({ x: cx, y: cy, size: ms, n: 1 }); return true;
  };
  for (let y = 0; y < h; y++) {
    const c = [0, 0, 0, 0, 0]; let s = 0;
    for (let x = 0; x < w; x++) {
      if (at(x, y)) { if (s & 1) s++; c[s]++; }
      else if ((s & 1) === 0) {
        if (s === 4) {
          if (ratioOk(c, 0.7) && found(c, y, x)) { c.fill(0); s = 0; }
          else { c[0] = c[2]; c[1] = c[3]; c[2] = c[4]; c[3] = 1; c[4] = 0; s = 3; }
        } else c[++s]++;
      } else c[s]++;
    }
    if (s === 4 && ratioOk(c, 0.7)) found(c, y, w);
  }
  return cands;
}
const dist = (a, b) => Math.hypot(a.x - b.x, a.y - b.y);
// the most likely three finders, best first: a right isosceles triangle with similar module sizes
function pickTriples(cands) {
  const c = cands.filter(p => p.n >= 2).sort((a, b) => b.n - a.n).slice(0, 12);
  const list = c.length >= 3 ? c : cands.slice().sort((a, b) => b.n - a.n).slice(0, 12);
  const out = [];
  for (let i = 0; i < list.length; i++) for (let j = i + 1; j < list.length; j++) for (let k = j + 1; k < list.length; k++) {
    const P = [list[i], list[j], list[k]];
    const d = [dist(P[1], P[2]), dist(P[0], P[2]), dist(P[0], P[1])];
    const hi = d.indexOf(Math.max(...d)); const tl = P[hi], o = P.filter((_, q) => q !== hi);
    const l1 = dist(tl, o[0]), l2 = dist(tl, o[1]), hyp = d[hi];
    const ms = (P[0].size + P[1].size + P[2].size) / 3;
    if (l1 < ms * 14 || l2 < ms * 14) continue;   // too close to be one code (at least 21 modules wide)
    const score = Math.abs(l1 - l2) / Math.max(l1, l2) + Math.abs(hyp - Math.hypot(l1, l2)) / hyp + (Math.max(P[0].size, P[1].size, P[2].size) - Math.min(P[0].size, P[1].size, P[2].size)) / ms;
    let tr = o[0], bl = o[1];
    if ((tr.x - tl.x) * (bl.y - tl.y) - (tr.y - tl.y) * (bl.x - tl.x) < 0) { tr = o[1]; bl = o[0]; }
    out.push({ tl, tr, bl, ms, score });
  }
  return out.sort((a, b) => a.score - b.score).slice(0, 4);
}
// the alignment square near where it should be (1:1:1, dark centre)
function findAlignment(B, ex, ey, ms, allowance) {
  const { bits, w, h } = B, at = (x, y) => bits[y * w + x];
  const x0 = Math.max(0, Math.floor(ex - allowance)), x1 = Math.min(w - 1, Math.ceil(ex + allowance)), y0 = Math.max(0, Math.floor(ey - allowance)), y1 = Math.min(h - 1, Math.ceil(ey + allowance));
  const ok3 = c => { const v = ms / 1.6; return Math.abs(ms - c[0]) < v && Math.abs(ms - c[1]) < v && Math.abs(ms - c[2]) < v; };
  let best = null;
  const crossV = (cx, cy, maxC, tot) => {
    const c = [0, 0, 0]; let y = cy;
    while (y >= y0 && at(cx, y) && c[1] <= maxC) { c[1]++; y--; } if (y < y0 || c[1] > maxC) return NaN;
    while (y >= y0 && !at(cx, y) && c[0] <= maxC) { c[0]++; y--; } if (c[0] > maxC) return NaN;
    y = cy + 1;
    while (y <= y1 && at(cx, y) && c[1] <= maxC) { c[1]++; y++; } if (y > y1 || c[1] > maxC) return NaN;
    while (y <= y1 && !at(cx, y) && c[2] <= maxC) { c[2]++; y++; } if (c[2] > maxC) return NaN;
    if (5 * Math.abs(c[0] + c[1] + c[2] - tot) >= 2 * tot) return NaN;
    return ok3(c) ? y - c[2] - c[1] / 2 : NaN;
  };
  for (let y = y0; y <= y1; y++) {
    const c = [0, 0, 0]; let s = 0, x = x0;
    while (x <= x1 && !at(x, y)) x++;
    for (; x <= x1; x++) {
      if (at(x, y)) {
        if (s === 1) c[1]++;
        else if (s === 2) {
          if (ok3(c)) {
            const cx = x - c[2] - c[1] / 2, cy = crossV(Math.floor(cx), y, 2 * c[1], c[0] + c[1] + c[2]);
            if (!isNaN(cy)) { const d = Math.hypot(cx - ex, cy - ey); if (!best || d < best.d) best = { x: cx, y: cy, d }; }
          }
          c[0] = c[2]; c[1] = 1; c[2] = 0; s = 1;
        } else c[++s]++;
      } else { if (s === 1) s++; c[s]++; }
    }
  }
  return best;
}
// a perspective transform from module space to the image (unit square in the middle)
function squareToQuad(x0, y0, x1, y1, x2, y2, x3, y3) {
  const dx3 = x0 - x1 + x2 - x3, dy3 = y0 - y1 + y2 - y3;
  if (Math.abs(dx3) < 1e-9 && Math.abs(dy3) < 1e-9) return [x1 - x0, x3 - x0, x0, y1 - y0, y3 - y0, y0, 0, 0, 1];
  const dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2, den = dx1 * dy2 - dx2 * dy1;
  const g = (dx3 * dy2 - dx2 * dy3) / den, hh = (dx1 * dy3 - dx3 * dy1) / den;
  return [x1 - x0 + g * x1, x3 - x0 + hh * x3, x0, y1 - y0 + g * y1, y3 - y0 + hh * y3, y0, g, hh, 1];
}
function adj(m) { const [a, b, c, d, e, f, g, h, i] = m; return [e * i - f * h, c * h - b * i, b * f - c * e, f * g - d * i, a * i - c * g, c * d - a * f, d * h - e * g, b * g - a * h, a * e - b * d]; }
function mul(m, n) { const r = new Array(9); for (let i = 0; i < 3; i++) for (let j = 0; j < 3; j++) r[i * 3 + j] = m[i * 3] * n[j] + m[i * 3 + 1] * n[3 + j] + m[i * 3 + 2] * n[6 + j]; return r; }
function quadToQuad(src, dst) { return mul(squareToQuad(...dst), adj(squareToQuad(...src))); }
function sampleGrid(B, T, dim) {
  const { bits, w, h } = B, grid = [];
  for (let y = 0; y < dim; y++) {
    const row = []; for (let x = 0; x < dim; x++) {
      const u = x + 0.5, v = y + 0.5, d = T[6] * u + T[7] * v + T[8];
      const px = Math.round((T[0] * u + T[1] * v + T[2]) / d - 0.5), py = Math.round((T[3] * u + T[4] * v + T[5]) / d - 0.5);
      if (px < 0 || py < 0 || px >= w || py >= h) return null;
      row.push(bits[py * w + px] === 1);
    }
    grid.push(row);
  }
  return grid;
}
function tryTriple(B, t) {
  const { tl, tr, bl } = t;
  const ms = t.ms, est = Math.round((dist(tl, tr) / ms + dist(tl, bl) / ms) / 2) + 7;
  let base = est; switch (base & 3) { case 0: base++; break; case 2: base--; break; case 3: base += 2; break; }
  for (const dim of [base, base + 4, base - 4, base + 8, base - 8]) {
    if (dim < 21 || dim > 177) continue;
    const ver = (dim - 17) / 4;
    const est = { x: tr.x - tl.x + bl.x, y: tr.y - tl.y + bl.y }, corners = [];
    if (ver >= 2) {
      const corr = 1 - 3 / (dim - 7), ex = tl.x + corr * (est.x - tl.x), ey = tl.y + corr * (est.y - tl.y);
      for (const f of [4, 8, 16]) { const a = findAlignment(B, ex, ey, ms, f * ms); if (a) { corners.push([a, dim - 6.5]); break; } }
    }
    corners.push([est, dim - 3.5]);   // no alignment square found (or it misled): a parallelogram
    for (const [br, at] of corners) {
      const T = quadToQuad([3.5, 3.5, dim - 3.5, 3.5, at, at, 3.5, dim - 3.5], [tl.x, tl.y, tr.x, tr.y, br.x, br.y, bl.x, bl.y]);
      const grid = sampleGrid(B, T, dim); if (!grid) continue;
      const text = decodeGrid(grid); if (text != null) return text;
    }
  }
  return null;
}
// ImageData-like {data, width, height} -> the text in the QR code, or null
export function decodeImage(img) {
  for (const inv of [false, true]) {
    const B = binarize(img, inv);
    const triples = pickTriples(findFinders(B));
    for (const t of triples) { const r = tryTriple(B, t); if (r != null) return r; }
  }
  return null;
}
