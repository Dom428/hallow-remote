// Hallow Authenticator - the phone app. Your 2FA codes live on this phone only (encrypted in IndexedDB); Hallow Remote just delivers the
// page and, when you ask, keeps encrypted backups it can't read. Nothing secret goes to the PC unless you turn on the PC mirror.
import { codeFor, nowSeconds, normalize, parseOtpauth, toOtpauth, clearKeyCache, b64ToBytes, bytesToB64, base32Encode } from './otp.js';
import { Vault, idbStorage, Locker, WrongPasscode, readAnyBackup, backupKind, waitAfterFails, rand, backupKeyFrom, makeBackupWithKey, markBackedUp, backupStatus, autoBackupDue, STALE_DAYS } from './vault.js';
import { otpauthList, aegisExport, googleMigration, googleCan } from './exporters.js';
import { qrSvg } from './qrcode.js';
import { decodeImage } from './qr.js';
import { parseAny, transferIds, openTransfer, normalizeCode, mirrorKeyFrom, sealMirror } from './importers.js';

export const VERSION = '1.1';
const $ = id => document.getElementById(id);
const vault = new Vault(idbStorage());
let csrf = null, online = false, hello = null, installEvt = null, cfg = {}, storageState = { supported: false, persisted: false, asked: 0 };
const locker = new Locker(why => lockNow(why === 'idle' ? 'Locked after a few minutes without use.' : null));

// ---- tiny DOM helpers (text only - nothing from a QR code or a file is ever put in as HTML)
function h(tag, attrs, ...kids) {
  const e = document.createElement(tag);
  for (const k in attrs || {}) {
    const v = attrs[k]; if (v == null || v === false) continue;
    if (k.startsWith('on')) e.addEventListener(k.slice(2), v);
    else if (k === 'text') e.textContent = v;
    else if (k === 'cls') e.className = v;
    else e.setAttribute(k, v === true ? '' : v);
  }
  for (const c of kids.flat()) if (c != null && c !== false) e.append(c.nodeType ? c : document.createTextNode(String(c)));
  return e;
}
const svgNS = 'http://www.w3.org/2000/svg';
function svg(tag, attrs) { const e = document.createElementNS(svgNS, tag); for (const k in attrs) e.setAttribute(k, attrs[k]); return e; }
let toastT = 0;
function toast(msg, ms) { const t = $('toast'); t.textContent = msg; t.classList.add('show'); clearTimeout(toastT); toastT = setTimeout(() => t.classList.remove('show'), ms || 2600); }
function show(view) { for (const v of ['v_lock', 'v_main', 'v_sheet']) $(v).classList.toggle('hidden', v !== view); window.scrollTo(0, 0); }

// ---- accent (the Hallow accent from the PC, kept on the phone for offline use) - never anything secret in localStorage
function lum(hx) { const v = [1, 3, 5].map(i => { const c = parseInt(hx.substr(i, 2), 16) / 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); }); return 0.2126 * v[0] + 0.7152 * v[1] + 0.0722 * v[2]; }
const contrast = (a, b) => { const x = lum(a), y = lum(b); return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); };
function darken(hx) { let [r, g, b] = [1, 3, 5].map(i => parseInt(hx.substr(i, 2), 16)); let cur = hx; let n = 0; while ((contrast(cur, '#ffffff') < 4.5 || contrast(cur, '#f6f4fa') < 4.5) && n++ < 100) { r *= 0.96; g *= 0.96; b *= 0.96; cur = '#' + [r, g, b].map(v => Math.round(v).toString(16).padStart(2, '0')).join(''); } return cur; }
function applyAccent(hx) {
  if (!/^#[0-9a-f]{6}$/i.test(hx || '')) hx = '#f07820';
  const r = document.documentElement.style, light = matchMedia('(prefers-color-scheme: light)').matches;
  r.setProperty('--accent', hx); r.setProperty('--on-accent', contrast(hx, '#1a0e04') >= contrast(hx, '#ffffff') ? '#1a0e04' : '#ffffff');
  r.setProperty('--accent-text', light ? darken(hx) : hx);
}
try { applyAccent(localStorage.getItem('hallowAuthAccent')); } catch (x) { applyAccent(null); }
matchMedia('(prefers-color-scheme: light)').addEventListener('change', () => { try { applyAccent(localStorage.getItem('hallowAuthAccent')); } catch (x) { } });

// ---- the PC (Hallow Remote): optional. Everything works without it except backups to the PC, Hallow Keys transfers and the mirror.
async function connect() {
  try {
    const c = new AbortController(); const t = setTimeout(() => c.abort(), 5000);
    const r = await fetch('api/hello', { cache: 'no-store', signal: c.signal }); clearTimeout(t);
    if (!r.ok) throw new Error(r.status);
    hello = await r.json(); csrf = hello.csrf; online = true;
    if (hello.accent) { try { localStorage.setItem('hallowAuthAccent', hello.accent); } catch (x) { } applyAccent(hello.accent); }
  } catch (x) { online = false; }
  return online;
}
async function post(path, body, json) {
  if (!csrf && !(await connect())) throw new Error('Your PC isn\'t reachable right now (Hallow Remote and Tailscale need to be on)');
  const r = await fetch(path, { method: 'POST', cache: 'no-store', headers: { 'X-Hallow-Csrf': csrf, 'Content-Type': json ? 'application/json' : 'application/x-www-form-urlencoded' }, body });
  let d = null; try { d = await r.json(); } catch (x) { }
  if (r.status === 403 && d && /Reload/.test(d.msg || '')) { csrf = null; }
  if (!r.ok || !d || d.ok === false) throw new Error((d && d.msg) || ('The PC said ' + r.status));
  return d;
}
const form = o => Object.keys(o).map(k => encodeURIComponent(k) + '=' + encodeURIComponent(o[k])).join('&');

// ---- lock screen
async function showLock(msg) {
  show('v_lock'); const b = $('lockbody'); b.textContent = '';
  if (!(await vault.exists())) return setupScreen(b);
  const m = await vault.meta();
  const pass = h('input', { cls: 'in', type: 'password', id: 'passcode', autocomplete: 'current-password', placeholder: 'Authenticator passcode', 'aria-label': 'Authenticator passcode', enterkeyhint: 'go' });
  const status = h('div', { cls: 'sub small', id: 'lockmsg', text: msg || '' });
  const go = h('button', { cls: 'btn go wide', id: 'b_unlock', text: 'Unlock' });
  const tryUnlock = async () => {
    const mm = await vault.meta(); const wait = waitAfterFails(mm.fails || 0) * 1000 - (Date.now() - (mm.lastFail || 0));
    if (wait > 0) { status.textContent = 'Too many wrong tries - wait ' + Math.ceil(wait / 1000) + ' s.'; return; }
    if (!pass.value) return; go.disabled = true; status.textContent = 'Unlocking...';
    try { await vault.unlock(pass.value); pass.value = ''; await unlocked(); }
    catch (x) { status.textContent = x instanceof WrongPasscode ? 'Wrong passcode.' : x.message; status.className = 'sub small bad'; go.disabled = false; pass.select(); }
  };
  go.addEventListener('click', tryUnlock); pass.addEventListener('keydown', e => { if (e.key === 'Enter') tryUnlock(); });
  b.append(h('div', { cls: 'card' }, pass, go, m.prf ? h('button', { cls: 'btn wide', id: 'b_bio', text: 'Unlock with fingerprint / face', onclick: () => bioUnlock(status) }) : null, status));
  if (m.prf && msg == null) setTimeout(() => bioUnlock(status), 300); else setTimeout(() => pass.focus(), 100);
}
function setupScreen(b) {
  const p1 = h('input', { cls: 'in', type: 'password', id: 'new1', autocomplete: 'new-password', placeholder: 'New passcode (8+ characters is best)' });
  const p2 = h('input', { cls: 'in', type: 'password', id: 'new2', autocomplete: 'new-password', placeholder: 'The same again' });
  const st = h('div', { cls: 'sub small' });
  const go = h('button', { cls: 'btn go wide', id: 'b_create', text: 'Create my authenticator' });
  go.addEventListener('click', async () => {
    if (p1.value.length < 6) { st.textContent = 'Use at least 6 characters - a longer passphrase is much stronger.'; return; }
    if (p1.value !== p2.value) { st.textContent = 'The two passcodes don\'t match.'; return; }
    go.disabled = true; st.textContent = 'Setting up (a second or two)...';
    try { await vault.create(p1.value); p1.value = p2.value = ''; await askPersist(); await unlocked(); toast('Ready - add your first account with +'); }
    catch (x) { st.textContent = x.message; go.disabled = false; }
  });
  b.append(h('div', { cls: 'card' },
    h('p', { text: 'Your 2FA codes are kept on this phone only, encrypted with a passcode you choose now. It\'s separate from Hallow Keys\' master password.' }),
    h('p', { cls: 'sub small', text: 'There is no way to recover it if you forget it - keep an encrypted backup (Settings) so a new phone can get your accounts back.' }),
    p1, h('div', { cls: 'f' }), p2, go, st));
  if (isIos() && !standalone()) b.append(h('div', { cls: 'banner', text: 'On iPhone: first tap Share, then Add to Home Screen, and set up the app from the Home Screen icon - Safari and the Home Screen app keep separate data.' }));
}
async function unlocked() {
  const s = cfg = await vault.settings(); locker.set(s.lockMinutes, s.lockHidden); locker.arm();
  if (!storageState.persisted) askPersist();
  show('v_main'); renderList(); renderInstall();
  setTimeout(() => maybeAutoBackup(false), 1500);
}
// 1.1: ask the browser to keep this site's data (IndexedDB) even when the phone runs low on space - at setup and on every start until
// it says yes. It doesn't stop "Clear browsing data" for this site (Settings explains that).
async function askPersist() {
  try {
    if (!(navigator.storage && navigator.storage.persist)) return storageState;
    storageState.supported = true;
    storageState.persisted = await navigator.storage.persisted();
    if (!storageState.persisted) { storageState.asked++; storageState.persisted = await navigator.storage.persist(); }
  } catch (x) { }
  return storageState;
}
function lockNow(msg) { vault.lock(); clearKeyCache(); locker.disarm(); $('list').textContent = ''; $('sheet').textContent = ''; stopCamera(); showLock(msg || null); }

// ---- fingerprint / face: a passkey on this phone with the PRF extension (Android Chrome, iPhone iOS 18+). The passcode always works too.
async function prfSupported() {
  if (!window.PublicKeyCredential || !navigator.credentials) return false;
  try { if (PublicKeyCredential.getClientCapabilities) { const c = await PublicKeyCredential.getClientCapabilities(); if (c && c['extension:prf'] === false) return false; } } catch (x) { }
  try { return await PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable(); } catch (x) { return false; }
}
async function prfGet(credId, salt) {
  const a = await navigator.credentials.get({ publicKey: { challenge: rand(32), rpId: location.hostname, allowCredentials: [{ type: 'public-key', id: credId }], userVerification: 'required', timeout: 60000, extensions: { prf: { eval: { first: salt } } } } });
  const r = a.getClientExtensionResults(); const out = r && r.prf && r.prf.results && r.prf.results.first;
  if (!out) throw new Error('This phone\'s passkeys can\'t do fingerprint unlock for web apps (no PRF)');
  return new Uint8Array(out);
}
async function bioUnlock(status) {
  try { const m = await vault.meta(); const out = await prfGet(b64ToBytes(m.prf.credId), b64ToBytes(m.prf.salt)); await vault.unlockWithPrf(out); out.fill(0); await unlocked(); }
  catch (x) { if (status) status.textContent = x.name === 'NotAllowedError' ? 'Cancelled - use your passcode.' : x.message; }
}
async function enrollBio() {
  const salt = rand(32);
  const cred = await navigator.credentials.create({ publicKey: {
    rp: { name: 'Hallow Authenticator', id: location.hostname }, user: { id: rand(16), name: 'Hallow Authenticator', displayName: 'Hallow Authenticator (unlock)' },
    challenge: rand(32), pubKeyCredParams: [{ type: 'public-key', alg: -7 }, { type: 'public-key', alg: -257 }], timeout: 60000,
    authenticatorSelection: { authenticatorAttachment: 'platform', residentKey: 'required', userVerification: 'required' },
    extensions: { prf: { eval: { first: salt } } } } });
  const r = cred.getClientExtensionResults();
  if (!r || !r.prf || r.prf.enabled === false) throw new Error('This phone made the passkey but can\'t do PRF, so fingerprint unlock isn\'t possible here - your passcode keeps working.');
  let out = r.prf.results && r.prf.results.first ? new Uint8Array(r.prf.results.first) : await prfGet(new Uint8Array(cred.rawId), salt);
  await vault.setPrf(new Uint8Array(cred.rawId), salt, out); out.fill(0);
}

// ---- the list
let ticker = 0, filter = '';
const codeCache = new Map();
function tileColor(s) { let x = 0; for (const c of s) x = (x * 31 + c.charCodeAt(0)) >>> 0; return 'hsl(' + (x % 360) + ',52%,38%)'; }
function sorted() {
  const f = filter.toLowerCase();
  return vault.data.entries.filter(e => !f || (e.issuer + ' ' + e.label).toLowerCase().includes(f))
    .sort((a, b) => (b.fav ? 1 : 0) - (a.fav ? 1 : 0) || (a.issuer || a.label).localeCompare(b.issuer || b.label, undefined, { sensitivity: 'base' }) || a.label.localeCompare(b.label));
}
const spaced = c => (c.length === 6 ? c.slice(0, 3) + ' ' + c.slice(3) : c.length === 8 ? c.slice(0, 4) + ' ' + c.slice(4) : c);
// 1.1: "Hide codes until tapped" - a tap shows the code for 20 s (and copies it)
const revealed = new Map(), REVEAL_MS = 20000;
const isHidden = e => cfg.hideCodes && !((revealed.get(e.id) || 0) > Date.now());
// 1.1: the backup reminders above the list
function renderBackupBar() {
  const b = $('backupbar'); if (!b) return; b.textContent = '';
  if (!vault.unlocked || !vault.data.entries.length) return;
  const st = backupStatus(vault.data, Date.now()), go = h('button', { cls: 'btn go', id: 'b_backupnow', text: 'Back up', onclick: () => backupSheet(false, () => { show('v_main'); renderList(); }) });
  if (st.never) b.append(h('div', { cls: 'banner warnb', id: 'bb_never' }, h('b', { text: 'No backup yet. ' }), h('span', { text: 'If this phone is lost, reset or its browser data is cleared, these codes are gone. Make an encrypted backup (a file, or your PC).' }), go));
  else if (st.notIn > 0) b.append(h('div', { cls: 'banner', id: 'bb_notin' }, h('span', { text: st.notIn + (st.notIn === 1 ? ' account is' : ' accounts are') + ' not in your latest backup' + (cfg.autoBackup ? ' (the automatic backup to your PC sends at most one a day).' : '.') + ' ' }), go));
  else if (st.stale) b.append(h('div', { cls: 'banner soft', id: 'bb_stale' }, h('span', { text: 'Your last backup was ' + st.days + ' days ago. A fresh one only takes a moment. ' }), go));
}
function renderList() {
  const list = $('list'); list.textContent = '';
  renderBackupBar();
  if (!vault.unlocked) return;
  const items = sorted();
  if (!vault.data.entries.length) list.append(h('div', { cls: 'card empty' }, h('p', { text: 'No accounts yet.' }), h('button', { cls: 'btn go', text: 'Add an account', onclick: addSheet }), h('p', { cls: 'sub small', text: 'Moving from Microsoft Authenticator? ' }, h('button', { cls: 'btn', text: 'How to move', onclick: msGuide }))));
  for (const e of items) {
    const code = h('div', { cls: 'code', 'data-code': '' }), next = h('div', { cls: 'next' });
    const fg = svg('circle', { cx: 17, cy: 17, r: 14, class: 'fg', 'stroke-dasharray': '87.96', transform: 'rotate(-90 17 17)' });
    const ring = svg('svg', { viewBox: '0 0 34 34', class: 'ring', 'aria-hidden': 'true' }); ring.append(svg('circle', { cx: 17, cy: 17, r: 14, class: 'bgc' }), fg);
    const secs = svg('text', { x: 17, y: 21, 'text-anchor': 'middle' }); ring.append(secs);
    const name = e.issuer || e.label;
    const card = h('button', { cls: 'acc', 'data-id': e.id, 'aria-label': 'Copy the code for ' + name },
      h('div', { cls: 'tile', text: (name[0] || '?').toUpperCase() }),
      h('div', { cls: 'mid' }, h('div', { cls: 'nm', text: name }), e.issuer && e.label ? h('div', { cls: 'lb', text: e.label }) : null, code, next),
      h('div', { cls: 'side' }, e.type === 'hotp' ? h('span', { cls: 'more', role: 'button', text: 'Next', onclick: ev => { ev.stopPropagation(); hotpNext(e); } }) : ring,
        h('span', { cls: 'star' + (e.fav ? ' on' : ''), role: 'button', 'aria-label': e.fav ? 'Unpin' : 'Pin to the top', text: e.fav ? '★' : '☆', onclick: async ev => { ev.stopPropagation(); e.fav = !e.fav; await saveAll(); renderList(); } }),
        h('span', { cls: 'more', role: 'button', 'aria-label': 'Edit ' + name, text: '⋯', onclick: ev => { ev.stopPropagation(); editSheet(e); } })));
    card.querySelector('.tile').style.background = tileColor(name);
    card.addEventListener('click', () => { if (cfg.hideCodes) { revealed.set(e.id, Date.now() + REVEAL_MS); tick(true); } copyCode(e, code.dataset.code); });
    if (e.notes) card.querySelector('.mid').append(h('div', { cls: 'lb notesmark', text: 'Has backup codes / notes' }));
    card._u = { e, code, next, fg, secs };
    list.append(card);
  }
  tick(true);
}
async function tick(force) {
  if (!vault.unlocked) return;
  const now = Date.now() / 1000, s = cfg;
  for (const card of $('list').children) {
    const u = card._u; if (!u) continue; const e = u.e;
    const hid = isHidden(e); card.classList.toggle('masked', hid);
    if (e.type === 'hotp') { const c = e.lastCode || ''; u.code.textContent = c ? (hid ? '••• •••' : spaced(c)) : 'Tap Next for a code'; u.code.dataset.code = c; u.next.textContent = 'Counter ' + (e.counter || 0); continue; }
    const period = e.period || 30, left = period - (now % period), ctr = Math.floor(now / period);
    if (force || u.ctr !== ctr || u.hid !== hid) {
      u.ctr = ctr; u.hid = hid;
      const c = await codeFor(e, Math.floor(now)); u.code.dataset.code = c || '';
      u.code.textContent = hid ? (c && c.length === 5 ? '•••••' : '••• •••') : c ? spaced(c) : '------';
      if (s.showNext && !hid) { const n = await codeFor(e, Math.floor(now) + period); u.next.textContent = n ? 'Next ' + spaced(n) : ''; } else u.next.textContent = hid ? 'Tap to show' : '';
    }
    u.code.classList.toggle('soon', left <= 5);
    u.fg.setAttribute('stroke-dashoffset', String(87.96 * (1 - left / period)));
    u.secs.textContent = String(Math.ceil(left));
  }
}
async function copyCode(e, c) {
  if (!c) { if (e.type === 'hotp') hotpNext(e); return; }
  try { await navigator.clipboard.writeText(c); toast('Copied ' + (e.issuer || e.label) + ' code'); } catch (x) { toast('Couldn\'t copy - the code is ' + spaced(c)); }
}
async function hotpNext(e) { e.counter = (e.counter || 0) + 1; e.lastCode = await codeFor(Object.assign({}, e, { counter: e.counter - 1 }), 0); await saveAll(); renderList(); }
async function saveAll() { await vault.save(); pushMirror(false); maybeAutoBackup(false); }

// ---- 1.1: automatic backup to the PC (opt-in; switched on after your first backup): when something isn't in the latest backup and the
// PC is reachable, the already-encrypted vault goes to Hallow Remote - at most once a day. Remote keeps the last 10, unopened.
let autoBusy = false;
async function autoBackupText() {
  if (cfg.autoMode !== 'passcode' && vault.data.backupKey) return makeBackupWithKey(vault.data.entries, vault.data.backupKey);
  return vault.passcodeBackup();
}
async function maybeAutoBackup(force) {
  if (autoBusy || !vault.unlocked) return null;
  const now = Date.now(), a = vault.data.auto || {};
  if (!force && (!autoBackupDue(vault.data, cfg, now) || (a.lastTry && now - Date.parse(a.lastTry) < 10 * 60000))) return null;
  autoBusy = true;
  try {
    const sent = vault.data.entries.map(e => Object.assign({}, e));
    const text = await autoBackupText(), r = await post('api/autobackup', text, true);
    vault.data.auto = { last: new Date().toISOString(), lastTry: new Date().toISOString(), state: 'ok', name: r.name, mode: backupKind(text) };
    markBackedUp(vault.data, 'auto', Date.now(), sent);
    await vault.save(); if (!$('v_main').classList.contains('hidden')) renderBackupBar();
    return r.name;
  } catch (x) {
    if (vault.unlocked) { vault.data.auto = Object.assign({}, a, { lastTry: new Date().toISOString(), state: 'error', msg: /Not found/.test(x.message) ? 'Update Hallow Remote on your PC (1.21 or newer keeps automatic backups)' : x.message }); try { await vault.save(); } catch (y) { } }
    if (force) throw x; return null;
  } finally { autoBusy = false; }
}

// ---- sheets
let backFn = null;
function sheet(title, back) { show('v_sheet'); $('sheettitle').textContent = title; const s = $('sheet'); s.textContent = ''; backFn = back || (() => { show('v_main'); renderList(); }); return s; }
function back() { stopCamera(); (backFn || (() => show('v_main')))(); }
function opt(icon, title, sub, fn, id) { return h('button', { cls: 'opt', onclick: fn, id }, h('i', { text: icon }), h('div', {}, h('b', { text: title }), h('span', { text: sub }))); }

function addSheet() {
  const s = sheet('Add an account');
  s.append(
    opt('▣', 'Scan a QR code', 'The code a website shows when you set up an authenticator app (or Google Authenticator\'s export)', () => scanSheet(), 'o_scan'),
    opt('⌨', 'Type a setup key', 'When the site shows a key like "JBSW Y3DP ..." instead', () => manualSheet(), 'o_manual'),
    opt('\u{1F5BC}', 'From a picture', 'A screenshot or photo of a QR code', () => pickImage(), 'o_image'),
    opt('\u{1F511}', 'From Hallow Keys', 'Move or copy the 2FA codes from Hallow Keys on your PC (a code it shows)', () => keysSheet(), 'o_keys'),
    opt('⇩', 'Import a file or links', 'Aegis, 2FAS, otpauth:// links, or a Google Authenticator export code as text', () => importSheet(), 'o_import'),
    opt('↺', 'Restore a backup', 'A Hallow Authenticator backup file, from this phone or your PC', () => restoreSheet(), 'o_restore'),
    opt('ℹ', 'Moving from Microsoft Authenticator', 'It can\'t export - here\'s how to move each account', () => msGuide(), 'o_ms'));
}

// adds entries (skipping ones already here); returns how many were added
async function addEntries(list) {
  let added = 0, dup = 0;
  for (const e0 of list) {
    const e = Object.assign({}, e0); if (normalize(e)) continue;
    if (vault.data.entries.some(x => x.secret === e.secret && x.issuer === e.issuer && x.label === e.label && x.type === e.type)) { dup++; continue; }
    e.id = bytesToB64(rand(9)).replace(/[+/=]/g, ''); e.added = new Date().toISOString(); e.fav = !!e.fav; delete e.lastCode;
    vault.data.entries.push(e); added++;
  }
  if (added) await saveAll();
  return { added, dup };
}
function confirmSheet(title, entries, skipped, onDone) {
  const s = sheet(title, () => addSheet());
  const boxes = entries.map(e => h('input', { type: 'checkbox', checked: true }));
  s.append(h('div', { cls: 'card' }, h('p', { text: entries.length + (entries.length === 1 ? ' account' : ' accounts') + ' found:' }),
    entries.map((e, i) => h('label', { cls: 'pick' }, boxes[i], h('div', {}, h('b', { text: e.issuer || e.label }), ' ', h('span', { cls: 'sub small', text: (e.issuer ? e.label + ' - ' : '') + (e.type === 'steam' ? 'Steam Guard' : e.type.toUpperCase() + (e.digits !== 6 ? ', ' + e.digits + ' digits' : '') + (e.type === 'totp' && e.period !== 30 ? ', ' + e.period + ' s' : '')) })))),
    skipped && skipped.length ? h('p', { cls: 'sub small warn', text: 'Not added: ' + skipped.join('; ') }) : null));
  s.append(h('button', { cls: 'btn go wide', id: 'b_addall', text: 'Add to this phone', onclick: async ev => {
    ev.target.disabled = true; const r = await addEntries(entries.filter((e, i) => boxes[i].checked));
    toast(r.added + ' added' + (r.dup ? ', ' + r.dup + ' already here' : '')); if (onDone) await onDone(r); show('v_main'); renderList();
  } }));
}
function manualSheet() {
  const s = sheet('Type a setup key', () => addSheet());
  const f = id => h('input', { cls: 'in', id, autocomplete: 'off', autocapitalize: 'off', spellcheck: 'false' });
  const iss = f('m_issuer'), acc = f('m_label'), key = f('m_secret');
  const type = h('select', { cls: 'in', id: 'm_type' }, h('option', { value: 'totp', text: 'Time-based (most sites)' }), h('option', { value: 'hotp', text: 'Counter-based (HOTP)' }), h('option', { value: 'steam', text: 'Steam Guard (shared secret)' }));
  const algo = h('select', { cls: 'in', id: 'm_algo' }, ['SHA1', 'SHA256', 'SHA512'].map(a => h('option', { value: a, text: a })));
  const digits = h('select', { cls: 'in', id: 'm_digits' }, [6, 8].map(a => h('option', { value: a, text: a + ' digits' })));
  const period = h('select', { cls: 'in', id: 'm_period' }, [30, 60].map(a => h('option', { value: a, text: a + ' seconds' })));
  const steamNote = h('p', { cls: 'sub small hidden', text: 'Steam Guard: paste the shared_secret (base64, from a Steam Desktop Authenticator .maFile or similar) if you already have it. Steam only gives it out through its own mobile authenticator setup - this app does not log in to Steam for you. Without it, keep Steam Guard in the Steam app.' });
  const adv = h('div', {}, h('label', { cls: 'f', text: 'Algorithm' }), algo, h('label', { cls: 'f', text: 'Digits' }), digits, h('label', { cls: 'f', text: 'New code every' }), period);
  type.addEventListener('change', () => { steamNote.classList.toggle('hidden', type.value !== 'steam'); adv.classList.toggle('hidden', type.value === 'steam'); });
  const st = h('div', { cls: 'sub small bad' });
  s.append(h('div', { cls: 'card' }, h('label', { cls: 'f', text: 'Site or app (e.g. GitHub)' }), iss, h('label', { cls: 'f', text: 'Your account (e.g. you@example.com)' }), acc,
    h('label', { cls: 'f', text: 'Setup key' }), key, h('label', { cls: 'f', text: 'Kind' }), type, steamNote, adv, st,
    h('button', { cls: 'btn go wide', id: 'm_add', text: 'Add', onclick: async () => {
      let v = key.value.trim();
      if (/^otpauth:/i.test(v)) { const r = parseOtpauth(v); if (r.error) { st.textContent = r.error; return; } return confirmSheet('Add this account?', [r.entry]); }
      const e = { type: type.value, issuer: iss.value, label: acc.value, secret: v, algo: algo.value, digits: +digits.value, period: +period.value, source: 'manual' };
      if (e.type === 'steam' && (/[a-z0189+/]/.test(v) || /^[A-Za-z0-9+/]{27}=$/.test(v))) e.secretFormat = 'base64';
      const err = normalize(e); if (err) { st.textContent = err; return; }
      const r = await addEntries([e]); toast(r.added ? 'Added ' + (e.issuer || e.label) : 'That account is already here'); show('v_main'); renderList();
    } })));
}

// ---- QR codes: the camera (BarcodeDetector on Android Chrome, else this app's own reader) or a picture
let camStream = null, camTimer = 0, google = null;
function stopCamera() { clearTimeout(camTimer); camTimer = 0; if (camStream) { camStream.getTracks().forEach(t => t.stop()); camStream = null; } }
let detector;
async function getDetector() {
  if (detector !== undefined) return detector;
  detector = null;
  try { if ('BarcodeDetector' in window && (await BarcodeDetector.getSupportedFormats()).includes('qr_code')) detector = new BarcodeDetector({ formats: ['qr_code'] }); } catch (x) { }
  return detector;
}
const canvas = document.createElement('canvas');
async function decodeSource(src, w, h0) {
  const d = await getDetector();
  if (d) { try { const r = await d.detect(src); if (r && r.length) return r[0].rawValue; } catch (x) { } }
  for (const max of [1000, 640, 1600]) {
    const sc = Math.min(1, max / Math.max(w, h0)); canvas.width = Math.round(w * sc); canvas.height = Math.round(h0 * sc);
    const ctx = canvas.getContext('2d', { willReadFrequently: true }); ctx.drawImage(src, 0, 0, canvas.width, canvas.height);
    const t = decodeImage(ctx.getImageData(0, 0, canvas.width, canvas.height)); if (t != null) return t;
    if (sc === 1) break;
  }
  return null;
}
function scanSheet(onText, title) {
  const s = sheet(title || 'Scan a QR code', () => addSheet());
  const video = h('video', { playsinline: true, muted: true, autoplay: true }); video.muted = true;
  const st = h('p', { cls: 'sub', id: 'scanmsg', text: 'Starting the camera...' });
  s.append(h('div', { cls: 'cam' }, video, h('div', { cls: 'frame' })), st, h('button', { cls: 'btn wide', text: 'Use a picture instead', onclick: () => { stopCamera(); pickImage(onText); } }));
  google = null;
  (async () => {
    try { camStream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: { ideal: 'environment' } }, audio: false }); }
    catch (x) { st.textContent = 'The camera isn\'t available (' + (x.name === 'NotAllowedError' ? 'permission was refused - allow it in the browser\'s site settings' : x.message) + '). Use a picture instead.'; return; }
    video.srcObject = camStream; try { await video.play(); } catch (x) { }
    st.textContent = 'Point the camera at the QR code.';
    const loop = async () => {
      if (!camStream) return;
      if (video.videoWidth) { const t = await decodeSource(video, video.videoWidth, video.videoHeight); if (t != null && camStream) { if (await handleQr(t, onText, st) === 'done') { stopCamera(); return; } } }
      camTimer = setTimeout(loop, 180);
    };
    loop();
  })();
}
function pickImage(onText) {
  const inp = h('input', { type: 'file', accept: 'image/*' });
  inp.addEventListener('change', async () => {
    const f = inp.files && inp.files[0]; if (!f) return;
    try { const bmp = await createImageBitmap(f); const t = await decodeSource(bmp, bmp.width, bmp.height); if (t == null) { toast('No QR code found in that picture - try a sharper one'); return; } await handleQr(t, onText, null); }
    catch (x) { toast('Couldn\'t read that picture: ' + x.message); }
  });
  inp.click();
}
// returns 'done' when scanning should stop
async function handleQr(text, onText, st) {
  if (onText) { onText(text); return 'done'; }
  let r; try { r = await parseAny(text); } catch (x) { if (st) st.textContent = x.message; else toast(x.message); return 'more'; }
  if (r.kind === 'google') {
    if (!google || google.id !== r.batch.id) google = { id: r.batch.id, size: r.batch.size, got: new Set(), entries: [], skipped: [] };
    if (!google.got.has(r.batch.index)) { google.got.add(r.batch.index); google.entries.push(...r.entries); google.skipped.push(...r.skipped); }
    if (google.got.size < google.size) { const m = 'Google Authenticator export: ' + google.got.size + ' of ' + google.size + ' codes scanned - show the next one.'; if (st) st.textContent = m; else toast(m); return 'more'; }
    const g = google; google = null; confirmSheet('From Google Authenticator', g.entries, g.skipped); return 'done';
  }
  if (r.kind === 'keys') { keysSheet(r.code); return 'done'; }
  if (r.kind === 'mirror') { toast('That\'s a PC mirror code - open Settings > PC mirror to use it'); return 'more'; }
  if (r.entries && r.entries.length) { confirmSheet(r.entries.length === 1 ? 'Add this account?' : 'Add these accounts?', r.entries, r.skipped); return 'done'; }
  if (st) st.textContent = 'That QR code isn\'t an authenticator code.'; return 'more';
}

function importSheet() {
  const s = sheet('Import', () => addSheet());
  const ta = h('textarea', { cls: 'in', id: 'imp_text', placeholder: 'Paste otpauth:// links (one per line), a Google export code, or Aegis / 2FAS JSON', spellcheck: 'false' });
  const st = h('div', { cls: 'sub small bad' });
  const run = async (text) => {
    st.textContent = '';
    try {
      let r = await parseAny(text);
      if (r.needsPassword) { const pw = await askPassword('This ' + (r.kind === 'aegis' ? 'Aegis' : '2FAS') + ' export is encrypted. Its password:'); if (pw == null) return; st.textContent = 'Opening...'; r = await parseAny(text, pw); st.textContent = ''; }
      if (r.kind === 'backup') return restoreText(text);
      if (r.kind === 'keys') return keysSheet(r.code);
      if (!r.entries || !r.entries.length) { st.textContent = 'No accounts found in that.' + (r.skipped && r.skipped.length ? ' ' + r.skipped.join('; ') : ''); return; }
      confirmSheet('Import', r.entries, r.skipped);
    } catch (x) { st.textContent = x.message; }
  };
  const file = h('input', { type: 'file', accept: '.json,.txt,application/json,text/plain', id: 'imp_file', cls: 'hidden' });
  file.addEventListener('change', async () => { const f = file.files && file.files[0]; if (f) run(await f.text()); });
  s.append(h('div', { cls: 'card' }, ta, st, h('div', { cls: 'row' }, h('button', { cls: 'btn go', id: 'imp_go', text: 'Import text', onclick: () => run(ta.value) }), h('button', { cls: 'btn', text: 'Choose a file', onclick: () => file.click() })), file),
    h('div', { cls: 'card sub small' }, h('p', { text: 'Google Authenticator: on the old phone, menu > Transfer accounts > Export, then scan each QR code it shows with Scan a QR code (it counts "1 of 3"...).' }),
      h('p', { text: 'Aegis: Settings > Import & Export > Export (JSON, encrypted or not). 2FAS: Settings > Backup > Export. Files stay on this phone.' })));
}
function askPassword(msg) {
  return new Promise(res => {
    const s = sheet('Password', () => { res(null); addSheet(); });
    const p = h('input', { cls: 'in', type: 'password', id: 'ask_pw', autocomplete: 'off' });
    s.append(h('div', { cls: 'card' }, h('p', { text: msg }), p, h('button', { cls: 'btn go wide', id: 'ask_ok', text: 'OK', onclick: () => res(p.value) })));
    p.focus();
  });
}

// ---- from Hallow Keys: a one-time code shown on the PC. The PC part is sealed with it; Remote only relays the sealed blob.
function keysSheet(code) {
  const s = sheet('From Hallow Keys', () => addSheet());
  const inp = h('input', { cls: 'in mono', id: 'k_code', placeholder: 'ABCD-EFGH-...', autocomplete: 'off', autocapitalize: 'characters', spellcheck: 'false', value: code ? code.replace(/(.{4})(?!$)/g, '$1-') : '' });
  const st = h('div', { cls: 'sub small' });
  const go = async () => {
    const c = normalizeCode(inp.value); if (c.length !== 32) { st.textContent = 'The code has 32 letters and numbers (dashes don\'t matter).'; return; }
    st.textContent = 'Fetching from your PC...';
    try {
      const ids = await transferIds(c);
      const blob = await post('api/xfer', form({ id: ids.id }));
      const r = await openTransfer(blob.blob, ids);
      confirmSheet(r.mode === 'move' ? 'Move from Hallow Keys' : 'Copy from Hallow Keys', r.entries, r.skipped, async res => {
        try { await post('api/xfer-done', form({ id: ids.id, proof: ids.proof })); toast(r.mode === 'move' ? 'Moved - Hallow Keys stops filling these codes' : 'Copied from Hallow Keys', 3500); }
        catch (x) { toast('Added here, but the PC didn\'t hear back (' + x.message + ') - Hallow Keys keeps its copy', 4000); }
      });
    } catch (x) { st.textContent = x.message; st.className = 'sub small bad'; }
  };
  s.append(h('div', { cls: 'card' }, h('p', { text: 'In Hallow Keys on your PC: Settings > Hallow Authenticator > Send my 2FA codes. Scan the QR code it shows, or type its code here.' }),
    inp, st, h('div', { cls: 'row' }, h('button', { cls: 'btn go', id: 'k_go', text: 'Get my codes', onclick: go }), h('button', { cls: 'btn', text: 'Scan', onclick: () => scanSheet(t => keysSheet(normalizeCode(t)), 'Scan Hallow Keys\' code') }))),
    h('p', { cls: 'sub small', text: 'End to end encrypted: the code never leaves the PC screen and this phone; Hallow Remote only passes on the sealed data, once, within 10 minutes.' }));
  if (code) go();
}

// ---- edit one account
function editSheet(e) {
  const s = sheet(e.issuer || e.label);
  const iss = h('input', { cls: 'in', id: 'e_issuer', value: e.issuer }), lab = h('input', { cls: 'in', id: 'e_label', value: e.label });
  const keyBox = h('div', { cls: 'hidden' });
  let armed = false;
  const del = h('button', { cls: 'btn danger wide', id: 'e_delete', text: 'Delete this account', onclick: async () => {
    if (!armed) { armed = true; del.textContent = 'Tap again to delete (turn off 2FA on the site first, or you may be locked out)'; return; }
    vault.data.entries = vault.data.entries.filter(x => x !== e); await saveAll(); toast('Deleted'); show('v_main'); renderList();
  } });
  // 1.1: the site's own backup / recovery codes and notes, encrypted with the rest (hidden until you ask)
  const notes = h('textarea', { cls: 'in', id: 'e_notes', placeholder: 'This site\'s backup / recovery codes, or a note (kept encrypted on this phone, and in your backups)', spellcheck: 'false', maxlength: '4000' }); notes.value = e.notes || '';
  const notesBox = h('div', { cls: 'hidden', id: 'e_notesbox' }, notes, h('p', { cls: 'sub small', text: 'Most sites give you a few one-time backup codes when you turn on 2FA - keep them here so a lost phone isn\'t a locked account. They go into your backups and the Aegis export, never to the PC mirror.' }));
  const qrBox = h('div', { cls: 'hidden', id: 'e_qrbox' });
  s.append(h('div', { cls: 'card' }, h('label', { cls: 'f', text: 'Site or app' }), iss, h('label', { cls: 'f', text: 'Account' }), lab,
    h('div', { cls: 'kv' }, h('span', { text: 'Kind' }), e.type === 'steam' ? 'Steam Guard' : e.type.toUpperCase() + ', ' + e.algo + ', ' + e.digits + ' digits' + (e.type === 'totp' ? ', ' + e.period + ' s' : '')),
    h('div', { cls: 'kv' }, h('span', { text: 'Added' }), new Date(e.added || Date.now()).toLocaleDateString()),
    h('button', { cls: 'btn wide', id: 'e_notesbtn', text: e.notes ? 'Show backup codes / notes' : 'Add backup codes / notes', onclick: ev => { notesBox.classList.remove('hidden'); ev.target.classList.add('hidden'); notes.focus(); } }), notesBox,
    h('button', { cls: 'btn go wide', id: 'e_save', text: 'Save', onclick: async () => { e.issuer = iss.value.trim().slice(0, 80); e.label = lab.value.trim().slice(0, 120); if (!e.issuer && !e.label) e.label = 'Account'; const nv = notes.value.slice(0, 4000); if (nv.trim()) e.notes = nv; else delete e.notes; await saveAll(); toast('Saved'); show('v_main'); renderList(); } })),
    h('div', { cls: 'card' }, h('button', { cls: 'btn wide', id: 'e_qr', text: 'Show a QR code to move it to another app', onclick: () => { qrBox.classList.toggle('hidden'); if (!qrBox.firstChild) qrBox.append(h('p', { cls: 'sub small warn', text: 'Anyone who scans or photographs this can make your codes. Scan it with the other app now, check it shows the same code, then hide it.' }), h('div', { cls: 'qrwrap', id: 'e_qrcode' }, qrSvg(toOtpauth(e), 'M'))); } }), qrBox,
      h('button', { cls: 'btn wide', text: 'Show the setup key', onclick: () => { keyBox.classList.toggle('hidden'); } }), keyBox), del);
  keyBox.append(h('p', { cls: 'sub small warn', text: 'Anyone who sees this can make your codes. Only use it to add the account to another authenticator.' }), h('p', { cls: 'mono', text: e.secret }), h('p', { cls: 'mono small sub', text: toOtpauth(e) }));
}

// ---- settings
async function settingsSheet() {
  const s = sheet('Settings'); const st = await vault.settings(), m = await vault.meta();
  const mins = h('select', { cls: 'in', id: 's_lockmin' }, [1, 2, 5, 10, 30].map(n => h('option', { value: n, text: n + (n === 1 ? ' minute' : ' minutes'), selected: n === st.lockMinutes })));
  mins.addEventListener('change', async () => { const v = await vault.setSettings({ lockMinutes: +mins.value }); locker.set(v.lockMinutes, v.lockHidden); });
  const hid = h('input', { type: 'checkbox', id: 's_hidden', checked: st.lockHidden }); hid.addEventListener('change', async () => { const v = await vault.setSettings({ lockHidden: hid.checked }); locker.set(v.lockMinutes, v.lockHidden); });
  const nxt = h('input', { type: 'checkbox', id: 's_next', checked: st.showNext }); nxt.addEventListener('change', async () => { cfg = await vault.setSettings({ showNext: nxt.checked }); });
  const hideC = h('input', { type: 'checkbox', id: 's_hidecodes', checked: !!st.hideCodes }); hideC.addEventListener('change', async () => { cfg = await vault.setSettings({ hideCodes: hideC.checked }); revealed.clear(); });
  s.append(h('h3', { text: 'Locking and privacy' }), h('div', { cls: 'card' }, h('label', { cls: 'f', text: 'Lock after this long without use' }), mins,
    h('label', { cls: 'sw' }, h('span', { text: 'Lock as soon as I switch away from the app' }), hid), h('label', { cls: 'sw' }, h('span', { text: 'Show the next code' }), nxt),
    h('label', { cls: 'sw' }, h('span', {}, h('span', { text: 'Hide codes until tapped' }), h('br'), h('span', { cls: 'sub small', text: 'For a screen others can see: a tap shows the code for 20 seconds (and copies it).' })), hideC)));
  // 1.1: storage that the browser doesn't clear by itself
  await askPersist();
  s.append(h('h3', { text: 'Storage on this phone' }), h('div', { cls: 'card', id: 's_storage' },
    h('div', { cls: 'kv' }, h('span', { text: 'Protected from automatic clearing' }), h('b', { id: 's_persist', cls: storageState.persisted ? 'good' : 'warn', text: !storageState.supported ? 'not supported here' : storageState.persisted ? 'yes' : 'no' })),
    h('p', { cls: 'sub small', text: storageState.persisted ? 'The browser has promised not to clear this app\'s data on its own, for example when the phone runs low on space.' : 'The browser hasn\'t promised to keep this app\'s data yet - it may clear it when the phone runs low on space. Installing the app (Add to Home screen) and using it often usually makes it say yes; the app asks again each time it starts.' }),
    h('p', { cls: 'sub small warn', text: 'Even when protected: Chrome\'s "Clear browsing data" (cookies and site data) for this site, clearing Chrome\'s storage in Android settings, uninstalling the app or resetting the phone still wipes your accounts. Keep a backup.' }),
    storageState.persisted || !storageState.supported ? null : h('button', { cls: 'btn wide', id: 's_persistask', text: 'Ask the browser again', onclick: async () => { await askPersist(); toast(storageState.persisted ? 'Protected from automatic clearing' : 'The browser said not yet - it decides by itself'); settingsSheet(); } })));
  // fingerprint
  const bio = h('div', { cls: 'card', id: 's_bio' });
  if (m.prf) bio.append(h('p', { text: 'Fingerprint / face unlock is on.' }), h('button', { cls: 'btn wide', text: 'Turn it off', onclick: async () => { await vault.clearPrf(); toast('Fingerprint unlock is off'); settingsSheet(); } }));
  else if (await prfSupported()) bio.append(h('p', { cls: 'sub small', text: 'Unlock with your fingerprint or face instead of typing the passcode. It makes a passkey for this app on the phone (Google Password Manager on Android, iCloud Keychain on iPhone); the passcode keeps working.' }),
    h('button', { cls: 'btn go wide', id: 's_bioon', text: 'Turn on fingerprint / face unlock', onclick: async ev => { ev.target.disabled = true; try { await enrollBio(); toast('Fingerprint unlock is on'); settingsSheet(); } catch (x) { toast(x.name === 'NotAllowedError' ? 'Cancelled' : x.message, 5000); ev.target.disabled = false; } } }));
  else bio.append(h('p', { cls: 'sub small', text: 'Fingerprint / face unlock needs a phone browser with passkeys and the PRF extension (Chrome on Android, Safari on iOS 18+). Your passcode works everywhere.' }));
  s.append(h('h3', { text: 'Fingerprint / face' }), bio);
  // backups
  const bst = backupStatus(vault.data, Date.now()), au = vault.data.auto || {};
  s.append(h('h3', { text: 'Backup' }), h('div', { cls: 'card', id: 's_backupcard' },
    h('div', { cls: 'kv' }, h('span', { text: 'Latest backup' }), h('b', { id: 's_lastbackup', cls: bst.never ? 'warn' : bst.stale ? 'warn' : '', text: bst.never ? 'none yet' : new Date(bst.at).toLocaleString() + ' (' + ({ file: 'file', pc: 'PC', auto: 'automatic, PC' }[bst.kind] || bst.kind) + ')' })),
    bst.never ? null : h('div', { cls: 'kv' }, h('span', { text: 'Not in it' }), h('span', { id: 's_notin', text: bst.notIn ? bst.notIn + (bst.notIn === 1 ? ' account' : ' accounts') : 'nothing - all backed up' })),
    h('p', { cls: 'sub small', text: 'An encrypted file with all your accounts (and their backup codes / notes), locked with a backup password you choose (not your passcode). Keep it somewhere safe - with it, a new phone gets everything back.' }),
    h('button', { cls: 'btn go wide', id: 's_backup', text: 'Save a backup file', onclick: () => backupSheet(false) }),
    h('button', { cls: 'btn wide', id: 's_backuppc', text: 'Back up to my PC', onclick: () => backupSheet(true) }),
    h('button', { cls: 'btn wide', id: 's_restore', text: 'Restore a backup', onclick: () => restoreSheet() }),
    h('p', { cls: 'sub small', text: 'On the PC the file stays encrypted (Hallow Remote can\'t open it) in %APPDATA%\\HallowAuthenticator\\backups - copy it to your NAS if you like.' })));
  // 1.1: automatic backup to the PC
  const autoOn = h('input', { type: 'checkbox', id: 's_auto', checked: st.autoBackup === true });
  autoOn.addEventListener('change', async () => { cfg = await vault.setSettings({ autoBackup: autoOn.checked }); toast(autoOn.checked ? 'Automatic backup to your PC is on' : 'Automatic backup is off'); if (autoOn.checked) maybeAutoBackup(false); });
  const mode = h('select', { cls: 'in', id: 's_automode' }, h('option', { value: 'backup', text: 'My backup password (from my latest backup)', selected: st.autoMode !== 'passcode', disabled: !vault.data.backupKey }), h('option', { value: 'passcode', text: 'My Authenticator passcode', selected: st.autoMode === 'passcode' || !vault.data.backupKey }));
  mode.addEventListener('change', async () => { cfg = await vault.setSettings({ autoMode: mode.value }); });
  s.append(h('h3', { text: 'Automatic backup to my PC' }), h('div', { cls: 'card', id: 's_autocard' },
    h('label', { cls: 'sw' }, h('span', { text: 'Back up to my PC by itself' }), autoOn),
    h('p', { cls: 'sub small', text: 'After you add or change accounts, when your PC is reachable (Hallow Remote and Tailscale on), the app sends its already-encrypted accounts to Hallow Remote - at most once a day. The PC keeps the last 10 and can\'t open them. ' + (st.autoBackup === null ? 'It switches on by itself after your first backup.' : '') }),
    h('label', { cls: 'f', text: 'Locked with' }), mode,
    h('p', { cls: 'sub small', text: 'Restoring needs that password - or, for the passcode, the passcode this phone had when the backup was made.' }),
    h('div', { cls: 'kv' }, h('span', { text: 'Last automatic backup' }), h('span', { id: 's_lastauto', text: au.last ? new Date(au.last).toLocaleString() : 'not yet' })),
    au.state === 'error' ? h('p', { cls: 'sub small warn', id: 's_autoerr', text: 'Last try didn\'t reach the PC: ' + (au.msg || '') }) : null,
    h('button', { cls: 'btn wide', id: 's_autonow', text: 'Send one to my PC now', onclick: async ev => { ev.target.disabled = true; try { const n = await maybeAutoBackup(true); toast('Saved on your PC: ' + n); } catch (x) { toast(x.message, 4500); } settingsSheet(); } })));
  // 1.1: never locked in
  s.append(h('h3', { text: 'Move to another app' }), h('div', { cls: 'card' },
    h('p', { cls: 'sub small', text: 'Your accounts are yours: export them for Aegis, 2FAS, Google Authenticator or any app that reads otpauth:// links. To move a single account, open it (⋯) and show its QR code.' }),
    h('button', { cls: 'btn wide', id: 's_export', text: 'Export / move my accounts', onclick: exportSheet })));
  // mirror
  s.append(h('h3', { text: 'PC mirror (off unless you turn it on)' }), mirrorCard());
  // passcode, help, erase
  s.append(h('h3', { text: 'More' }), h('div', { cls: 'card' },
    h('button', { cls: 'btn wide', text: 'Change passcode', onclick: changePassSheet }),
    h('button', { cls: 'btn wide', text: 'Moving from Microsoft Authenticator', onclick: msGuide }),
    h('button', { cls: 'btn wide', text: 'Steam Guard codes', onclick: steamHelp }),
    h('button', { cls: 'btn wide', text: 'What works offline', onclick: offlineHelp }),
    h('button', { cls: 'btn danger wide', id: 's_erase', text: 'Erase everything on this phone', onclick: eraseSheet }),
    h('p', { cls: 'sub small', text: 'Hallow Authenticator ' + VERSION + (online ? ' - connected to your PC' : ' - your PC isn\'t reachable right now (codes still work)') }), installHint()));
}
function installHint() {
  if (standalone()) return null;
  if (installEvt) return h('button', { cls: 'btn go wide', text: 'Install the app', onclick: doInstall });
  return h('p', { cls: 'sub small', text: isIos() ? 'Install: Share > Add to Home Screen (then use it from there).' : 'Install: Chrome menu (three dots) > Add to Home screen / Install app.' });
}
function renderInstall() {
  const b = $('installbar'); b.textContent = '';
  if (standalone()) return;
  if (installEvt) b.append(h('div', { cls: 'banner' }, h('span', { text: 'Install Hallow Authenticator so it opens like an app - and without your PC. ' }), h('button', { cls: 'btn go', id: 'b_install', text: 'Install', onclick: doInstall })));
}
async function doInstall() { if (!installEvt) return; installEvt.prompt(); try { await installEvt.userChoice; } catch (x) { } installEvt = null; renderInstall(); }
const isIos = () => /iPhone|iPad|iPod/.test(navigator.userAgent);
const standalone = () => matchMedia('(display-mode: standalone)').matches || navigator.standalone === true;

function changePassSheet() {
  const s = sheet('Change passcode', settingsSheet);
  const a = h('input', { cls: 'in', type: 'password', placeholder: 'Current passcode', autocomplete: 'current-password' }), b = h('input', { cls: 'in', type: 'password', placeholder: 'New passcode', autocomplete: 'new-password' }), c = h('input', { cls: 'in', type: 'password', placeholder: 'New passcode again', autocomplete: 'new-password' });
  const st = h('div', { cls: 'sub small bad' });
  s.append(h('div', { cls: 'card' }, a, b, c, st, h('button', { cls: 'btn go wide', text: 'Change', onclick: async () => {
    if (b.value.length < 6) { st.textContent = 'At least 6 characters.'; return; } if (b.value !== c.value) { st.textContent = 'The new ones don\'t match.'; return; }
    try { await vault.changePasscode(a.value, b.value); toast('Passcode changed'); settingsSheet(); } catch (x) { st.textContent = x.message; }
  } })));
}
function eraseSheet() {
  const s = sheet('Erase everything', settingsSheet); const inp = h('input', { cls: 'in', id: 'erase_txt', placeholder: 'Type ERASE' });
  s.append(h('div', { cls: 'card' }, h('p', { text: 'This deletes every account from this phone. Without a backup you will need to set up 2FA again on every site (or use their recovery codes).' }), inp,
    h('button', { cls: 'btn danger wide', id: 'erase_go', text: 'Erase', onclick: async () => { if (inp.value !== 'ERASE') { toast('Type ERASE first'); return; } await vault.erase(); locker.disarm(); showLock(''); } })));
}
// 1.1: a file to keep (share sheet on phones, a download elsewhere). The developer tests set hallowAuth.testFiles to catch it instead.
async function saveFile(name, text, type) {
  if (Array.isArray(window.hallowAuth.testFiles)) { window.hallowAuth.testFiles.push({ name, text }); return; }
  const file = new File([text], name, { type: type || 'application/json' });
  if (navigator.canShare && navigator.canShare({ files: [file] })) { try { await navigator.share({ files: [file], title: name }); return; } catch (x) { if (x.name === 'AbortError') throw x; } }
  const u = URL.createObjectURL(file); const l = h('a', { href: u, download: name }); document.body.append(l); l.click(); l.remove(); setTimeout(() => URL.revokeObjectURL(u), 10000);
}
function backupSheet(toPc, backTo) {
  const s = sheet(toPc ? 'Back up to my PC' : 'Save a backup file', backTo || settingsSheet);
  if (backTo) s.append(h('div', { cls: 'row' }, h('button', { cls: 'btn', id: 'bk_topc', text: toPc ? 'Save a file instead' : 'Send to my PC instead', onclick: () => backupSheet(!toPc, backTo) })));
  const a = h('input', { cls: 'in', type: 'password', id: 'bk_pw1', placeholder: 'Backup password', autocomplete: 'new-password' }), b = h('input', { cls: 'in', type: 'password', id: 'bk_pw2', placeholder: 'The same again', autocomplete: 'new-password' });
  const st = h('div', { cls: 'sub small' });
  s.append(h('div', { cls: 'card' }, h('p', { cls: 'sub small', text: 'Choose a strong backup password and write it down somewhere safe - nobody can open the backup without it, including you.' }), a, b, st,
    h('button', { cls: 'btn go wide', id: 'bk_go', text: toPc ? 'Send the encrypted backup to my PC' : 'Save the encrypted file', onclick: async ev => {
      if (a.value.length < 8) { st.textContent = 'Use at least 8 characters.'; return; } if (a.value !== b.value) { st.textContent = 'They don\'t match.'; return; }
      ev.target.disabled = true; st.textContent = 'Encrypting...';
      try {
        const bk = await backupKeyFrom(a.value), sent = vault.data.entries.map(e => Object.assign({}, e)), text = await makeBackupWithKey(sent, bk); a.value = b.value = '';
        let done;
        if (toPc) { const r = await post('api/backup', text, true); done = 'Saved on your PC as ' + r.name + ' (encrypted).'; }
        else {
          const name = 'hallow-authenticator-' + new Date().toISOString().slice(0, 10) + '.hallowauth';
          await saveFile(name, text);
          done = 'Backup ready: ' + name + '. Keep it off this phone too (your PC, a NAS, cloud storage).';
        }
        // 1.1: remember what's in it (for the reminders) and the key from this password (for automatic backups) - inside the vault
        markBackedUp(vault.data, toPc ? 'pc' : 'file', Date.now(), sent); vault.data.backupKey = bk;
        if (cfg.autoBackup === null) { cfg = await vault.setSettings({ autoBackup: true }); done += ' Automatic backup to your PC is now on (Settings) - it uses this backup password.'; }
        await vault.save(); st.textContent = done;
      } catch (x) { st.textContent = x.name === 'AbortError' ? 'Cancelled - nothing was saved.' : x.message; }
      ev.target.disabled = false;
    } })));
}
async function restoreText(text) {
  const kind = backupKind(text);
  const pw = await askPassword(kind === 'passcode' ? 'This automatic backup is locked with the Authenticator passcode this phone had when it was made. That passcode:' : 'The backup password:'); if (pw == null) return;
  try { const entries = await readAnyBackup(text, pw); confirmSheet('Restore', entries, []); } catch (x) { toast(x.message, 4000); restoreSheet(); }
}
async function restoreSheet() {
  const s = sheet('Restore a backup', () => addSheet());
  const file = h('input', { type: 'file', accept: '.hallowauth,.json,application/json', cls: 'hidden', id: 'rs_file' });
  file.addEventListener('change', async () => { const f = file.files && file.files[0]; if (f) restoreText(await f.text()); });
  const pcList = h('div', { id: 'rs_pc', cls: 'sub small', text: 'Looking on your PC...' });
  s.append(h('div', { cls: 'card' }, h('button', { cls: 'btn go wide', text: 'Choose a backup file on this phone', onclick: () => file.click() }), file), h('h3', { text: 'Backups on your PC' }), h('div', { cls: 'card' }, pcList),
    h('p', { cls: 'sub small', text: 'Restoring adds the accounts from the backup; ones already here are skipped.' }));
  try {
    if (!(await connect())) throw new Error('offline');
    const r = await (await fetch('api/backups', { cache: 'no-store' })).json(); pcList.textContent = '';
    if (!r.items || !r.items.length) pcList.textContent = 'None yet.';
    for (const it of r.items || []) pcList.append(h('button', { cls: 'btn wide', text: it.made + ' (' + Math.max(1, Math.round(it.size / 1024)) + ' KB)', onclick: async () => { const t = await (await fetch('api/backups/' + encodeURIComponent(it.name), { cache: 'no-store' })).text(); restoreText(t); } }));
    // 1.1: the automatic ones (Hallow Remote 1.21+)
    const ra = await fetch('api/autobackups', { cache: 'no-store' }); const ja = ra.ok ? await ra.json() : null;
    if (ja && ja.items && ja.items.length) {
      pcList.append(h('p', { cls: 'sub small', text: 'Automatic backups (newest first):' }));
      for (const it of ja.items) pcList.append(h('button', { cls: 'btn wide auto', text: 'Automatic - ' + it.made + ' (' + Math.max(1, Math.round(it.size / 1024)) + ' KB)', onclick: async () => { const t = await (await fetch('api/autobackups/' + encodeURIComponent(it.name), { cache: 'no-store' })).text(); restoreText(t); } }));
    }
  } catch (x) { if (!pcList.querySelector('button')) pcList.textContent = 'Your PC isn\'t reachable right now.'; }
}

// ---- 1.1: the escape hatch - export to standard formats so you can always move to another app
function exportSheet() {
  const s = sheet('Move to another app', settingsSheet), list = vault.data.entries, today = new Date().toISOString().slice(0, 10);
  const pass = (id) => h('input', { cls: 'in', type: 'password', id, placeholder: 'Your Authenticator passcode', autocomplete: 'current-password' });
  const confirmPass = async (inp, st) => { if (!inp.value) { st.textContent = 'Type your passcode first.'; return false; } st.textContent = 'Checking...'; const ok = await vault.checkPasscode(inp.value); inp.value = ''; st.textContent = ok ? '' : 'Wrong passcode.'; return ok; };
  // Aegis
  const a1 = h('input', { cls: 'in', type: 'password', id: 'x_apw1', placeholder: 'A password for the export file', autocomplete: 'new-password' }), a2 = h('input', { cls: 'in', type: 'password', id: 'x_apw2', placeholder: 'The same again', autocomplete: 'new-password' }), ast = h('div', { cls: 'sub small', id: 'x_ast' });
  s.append(h('h3', { text: 'Encrypted file for Aegis and others' }), h('div', { cls: 'card' },
    h('p', { cls: 'sub small', text: 'An Aegis vault file (encrypted with a password you choose: scrypt + AES-256-GCM). Aegis imports it directly; 2FAS, Ente Auth, Proton Pass and Bitwarden Authenticator import Aegis files too. Backup codes / notes go in as Aegis notes.' }),
    a1, a2, ast, h('button', { cls: 'btn go wide', id: 'x_aegis', text: 'Make the Aegis file (' + list.length + (list.length === 1 ? ' account)' : ' accounts)'), onclick: async ev => {
      if (a1.value.length < 8) { ast.textContent = 'Use at least 8 characters.'; return; } if (a1.value !== a2.value) { ast.textContent = 'They don\'t match.'; return; }
      ev.target.disabled = true; ast.textContent = 'Encrypting (a few seconds)...';
      try { const text = await aegisExport(list, a1.value, window.hallowAuth.testAegisN || 32768); a1.value = a2.value = ''; const name = 'aegis-export-hallow-' + today + '.json'; await saveFile(name, text); ast.textContent = 'Ready: ' + name + '. In Aegis: Settings > Import & Export > Import from file > Aegis. Delete the file when you\'re done.'; }
      catch (x) { ast.textContent = x.name === 'AbortError' ? 'Cancelled.' : x.message; }
      ev.target.disabled = false;
    } })));
  // Google Authenticator transfer QR codes
  const gp = pass('x_gpass'), gst = h('div', { cls: 'sub small', id: 'x_gst' }), gbox = h('div', { id: 'x_gbox' });
  const gcount = list.filter(googleCan).length;
  s.append(h('h3', { text: 'Transfer QR codes (Google Authenticator style)' }), h('div', { cls: 'card' },
    h('p', { cls: 'sub small', text: 'The same kind of codes Google Authenticator\'s "Transfer accounts" shows. Scan them with Google Authenticator, Aegis, 2FAS or another phone running Hallow Authenticator (Microsoft Authenticator can\'t import them). ' + gcount + ' of ' + list.length + ' accounts fit (Google\'s format has no Steam Guard and only 30-second codes).' }),
    h('p', { cls: 'sub small warn', text: 'Anyone who scans or photographs these codes gets all those accounts. Show them only to your own phone, somewhere private.' }),
    gp, gst, h('button', { cls: 'btn wide', id: 'x_google', text: 'Show the transfer QR codes', onclick: async () => {
      if (!(await confirmPass(gp, gst))) return;
      const g = googleMigration(list); gbox.textContent = ''; let i = 0;
      if (!g.uris.length) { gst.textContent = 'None of your accounts fit Google\'s format.'; return; }
      const qr = h('div', { cls: 'qrwrap', id: 'x_gqr' }), cap = h('p', { cls: 'sub', id: 'x_gcap' });
      const draw = () => { qr.textContent = ''; qr.append(qrSvg(g.uris[i], 'M')); cap.textContent = 'Code ' + (i + 1) + ' of ' + g.uris.length + (g.skipped.length ? ' - not included: ' + g.skipped.join(', ') : ''); };
      gbox.append(qr, cap, h('div', { cls: 'row' }, h('button', { cls: 'btn', id: 'x_gprev', text: 'Previous', onclick: () => { if (i > 0) { i--; draw(); } } }), h('button', { cls: 'btn go', id: 'x_gnext', text: 'Next', onclick: () => { if (i < g.uris.length - 1) { i++; draw(); } } })),
        h('button', { cls: 'btn wide', text: 'Hide the codes', onclick: () => { gbox.textContent = ''; } }));
      draw();
    } }), gbox));
  // plain otpauth list
  const pp = pass('x_ppass'), pst = h('div', { cls: 'sub small', id: 'x_pst' }), pbox = h('div', { id: 'x_pbox' });
  s.append(h('h3', { text: 'Plain list of otpauth:// links' }), h('div', { cls: 'card warnc' },
    h('p', { cls: 'warn', text: 'NOT encrypted. Everyone and every app that can read this file or text can make all your 2FA codes - and with your passwords, sign in as you.' }),
    h('p', { cls: 'sub small', text: 'Only for an app that imports nothing else. Don\'t email it, don\'t put it in cloud storage or a chat, and delete it as soon as the other app has imported it. The encrypted Aegis file above is almost always the better choice.' }),
    pp, pst, h('button', { cls: 'btn danger wide', id: 'x_plain', text: 'I understand - make the plain list', onclick: async () => {
      if (!(await confirmPass(pp, pst))) return;
      const text = otpauthList(list); pbox.textContent = '';
      const ta = h('textarea', { cls: 'in', id: 'x_plaintext', readonly: true, spellcheck: 'false' }); ta.value = text;
      pbox.append(ta, h('div', { cls: 'row' }, h('button', { cls: 'btn', id: 'x_plainsave', text: 'Save as a text file', onclick: async () => { try { await saveFile('otpauth-links-' + today + '.txt', text, 'text/plain'); toast('Saved - delete it after the import'); } catch (x) { } } }),
        h('button', { cls: 'btn', text: 'Hide it', onclick: () => { pbox.textContent = ''; } })));
    } }), pbox));
}

// ---- PC mirror: a read-only, encrypted copy of the accounts you pick, for Hallow Keys on your PC (convenience over strength)
function mirrorCard() {
  const c = h('div', { cls: 'card', id: 's_mirror' }), mi = vault.data.mirror;
  c.append(h('p', { cls: 'sub small', text: 'Off by default. When on, the accounts you pick are sent (encrypted with a key only Hallow Keys has) to your PC, where Hallow Keys shows them behind its master password or Windows Hello. Handy at the desk - but then the PC holds both your passwords and those codes, which is weaker 2FA. Leave it off for important accounts.' }));
  if (!mi) { c.append(h('button', { cls: 'btn wide', id: 'm_pair', text: 'Turn on: scan Hallow Keys\' mirror code', onclick: () => scanSheet(async t => { try { const k = mirrorKeyFrom(t); vault.data.mirror = { key: bytesToB64(k), ids: [], last: null, state: 'new' }; await vault.save(); mirrorPickSheet(); } catch (x) { toast(x.message); settingsSheet(); } }, 'Scan the mirror code') })); return c; }
  c.append(h('div', { cls: 'kv' }, h('span', { text: 'Accounts on the PC' }), String(mi.ids.length)), h('div', { cls: 'kv' }, h('span', { text: 'Last sent' }), mi.last ? new Date(mi.last).toLocaleString() : 'not yet'), mi.state === 'error' ? h('p', { cls: 'sub small warn', text: 'The last send didn\'t reach the PC - it tries again after each change.' }) : null,
    h('button', { cls: 'btn wide', id: 'm_pick', text: 'Choose accounts', onclick: mirrorPickSheet }), h('button', { cls: 'btn wide', text: 'Send now', onclick: async () => { await pushMirror(true); settingsSheet(); } }),
    h('button', { cls: 'btn danger wide', id: 'm_off', text: 'Turn off and delete the PC copy', onclick: async () => { try { await post('api/mirror-off', ''); } catch (x) { toast('The PC isn\'t reachable - the copy there is deleted next time; turn it off in Hallow Keys too', 4500); } vault.data.mirror = null; await vault.save(); settingsSheet(); } }));
  return c;
}
function mirrorPickSheet() {
  const s = sheet('PC mirror: accounts', settingsSheet), mi = vault.data.mirror;
  const list = vault.data.entries.filter(e => e.type !== 'hotp'), boxes = list.map(e => h('input', { type: 'checkbox', checked: mi.ids.includes(e.id) }));
  s.append(h('div', { cls: 'card' }, list.length ? list.map((e, i) => h('label', { cls: 'pick' }, boxes[i], h('span', { text: (e.issuer || e.label) + (e.issuer ? ' - ' + e.label : '') }))) : h('p', { cls: 'sub', text: 'No accounts to mirror yet (counter-based ones never are).' })),
    h('button', { cls: 'btn go wide', id: 'm_save', text: 'Save and send', onclick: async () => { mi.ids = list.filter((e, i) => boxes[i].checked).map(e => e.id); await vault.save(); await pushMirror(true); settingsSheet(); } }));
}
async function pushMirror(loud) {
  const mi = vault.data && vault.data.mirror; if (!mi) return;
  try { const chosen = vault.data.entries.filter(e => mi.ids.includes(e.id)); await post('api/mirror', await sealMirror(b64ToBytes(mi.key), chosen), true); mi.last = new Date().toISOString(); mi.state = 'ok'; if (loud) toast('Sent to your PC (' + chosen.length + ')'); }
  catch (x) { mi.state = 'error'; if (loud) toast(x.message, 4000); }
  try { await vault.save(); } catch (x) { }
}

// ---- help pages
function page(title, paras, backTo) { const s = sheet(title, backTo || (() => { if (vault.unlocked) { show('v_main'); renderList(); } else showLock(); })); s.append(h('div', { cls: 'card' }, paras)); }
function msGuide() {
  page('Moving from Microsoft Authenticator', [
    h('p', { text: 'Microsoft Authenticator can\'t export your accounts, so each one is moved by setting it up again. Keep Microsoft Authenticator until you\'ve done them all.' }),
    h('p', {}, h('b', { text: 'For each account:' })),
    h('ol', {}, h('li', { text: 'On a computer (or this phone\'s browser), sign in to the site and open its security / two-step verification settings.' }),
      h('li', { text: 'Choose to add (or change) an authenticator app. It shows a QR code.' }),
      h('li', { text: 'In Hallow Authenticator: + > Scan a QR code, and scan it. Type the 6-digit code it shows back into the site to confirm.' }),
      h('li', { text: 'Only then remove Microsoft Authenticator from that account (or delete the account in Microsoft Authenticator).' })),
    h('p', {}, h('b', { text: 'Your Microsoft account itself' }), ' (Outlook, Xbox, OneDrive): account.microsoft.com > Security > Manage how I sign in > Add a new way to sign in > "Use an app" > "Set up a different authenticator app", then scan. It then uses normal 6-digit codes.'),
    h('p', {}, h('b', { text: 'What can\'t move: ' }), 'Microsoft\'s "Approve sign-in?" push prompts and passwordless phone sign-in only work in Microsoft Authenticator. With Hallow you type the 6-digit code instead. Work or school accounts may require Microsoft Authenticator - ask your IT team.'),
    h('p', {}, h('b', { text: 'Tip: ' }), 'save each site\'s recovery codes while you\'re in its settings, and make a Hallow backup when you\'re done.')]);
}
function steamHelp() {
  page('Steam Guard codes', [
    h('p', { text: 'Hallow Authenticator can make Steam Guard\'s 5-character codes - but only if you already have your account\'s shared secret (for example from a Steam Desktop Authenticator .maFile you made yourself).' }),
    h('p', { text: 'Steam gives the secret out only during its own mobile authenticator setup, and this app never logs in to Steam or automates that. If you don\'t have the secret, keep using the Steam app for Steam Guard. Trade confirmations also stay in the Steam app.' }),
    h('p', { text: 'To add it: + > Type a setup key > Kind: Steam Guard, and paste the shared_secret.' })], settingsSheet);
}
function offlineHelp() {
  page('What works offline', [
    h('p', { text: 'Once installed and opened once, the app and your accounts are on this phone: codes, copying, search, editing, scanning new QR codes and saving a backup file all work with the PC off, Tailscale off or no internet.' }),
    h('p', { text: 'Needs your PC (Hallow Remote over Tailscale): backing up to the PC (by hand or automatically), restoring from the PC, receiving codes from Hallow Keys, the PC mirror, and app updates. Codes depend on the phone\'s clock - keep automatic time on.' })], settingsSheet);
}

// ---- start
function wire() {
  $('b_add').addEventListener('click', addSheet); $('b_set').addEventListener('click', settingsSheet); $('b_lock').addEventListener('click', () => lockNow('Locked.'));
  $('b_back').addEventListener('click', back);
  $('search').addEventListener('input', ev => { filter = ev.target.value; renderList(); });
  for (const ev of ['pointerdown', 'keydown', 'scroll', 'touchstart']) addEventListener(ev, () => locker.touch(), { passive: true, capture: true });
  document.addEventListener('visibilitychange', () => { if (locker.visibility(document.visibilityState) === false && document.visibilityState === 'visible' && vault.unlocked) tick(true); });
  addEventListener('pagehide', () => { if (vault.unlocked && locker.hidden) lockNow(null); });
  addEventListener('beforeinstallprompt', e => { e.preventDefault(); installEvt = e; if (vault.unlocked) renderInstall(); });
  ticker = setInterval(() => { locker.tick(); if (vault.unlocked && !$('v_main').classList.contains('hidden')) tick(false); }, 1000);
}
async function start() {
  wire();
  if ('serviceWorker' in navigator) navigator.serviceWorker.register('sw.js', { scope: './' }).catch(() => { });
  if (!window.isSecureContext || !globalThis.crypto || !crypto.subtle) { show('v_lock'); $('lockbody').append(h('div', { cls: 'card' }, h('p', { text: 'Hallow Authenticator needs a secure (https) link. Turn on HTTPS in Hallow Remote on your PC, then open the https link.' }))); return; }
  connect();
  if (await vault.exists()) askPersist();   // 1.1: every start until the browser says yes (a new phone asks at setup)
  await showLock();
}
window.hallowAuth = { vault, version: VERSION, decodeImage, storage: storageState, refresh: () => { if (vault.unlocked) renderList(); }, autoBackup: maybeAutoBackup };   // for the developer tests in a test browser (no secrets: the vault object is the same one the page uses)
start();
