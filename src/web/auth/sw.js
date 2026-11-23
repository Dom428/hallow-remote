// Hallow Authenticator - the service worker: the app's own files are kept on the phone, so it opens with the PC off or no internet.
// Cache first (fast, and no waiting on a PC that's asleep); a new version of this file brings new files. api/ is never cached.
const VERSION = '1.1.0';
const CACHE = 'hallow-auth-' + VERSION;
const FILES = ['./', 'index.html', 'app.css', 'app.js', 'otp.js', 'vault.js', 'qr.js', 'importers.js', 'scrypt.js', 'exporters.js', 'qrcode.js', 'manifest.json', 'icon-192.png', 'icon-512.png', 'icon-mask-512.png', 'apple-touch-icon.png'];
self.addEventListener('install', e => { e.waitUntil(caches.open(CACHE).then(c => c.addAll(FILES)).then(() => self.skipWaiting())); });
self.addEventListener('activate', e => { e.waitUntil(caches.keys().then(ks => Promise.all(ks.filter(k => k.startsWith('hallow-auth-') && k !== CACHE).map(k => caches.delete(k)))).then(() => self.clients.claim())); });
self.addEventListener('fetch', e => {
  const u = new URL(e.request.url);
  if (e.request.method !== 'GET' || u.origin !== location.origin) return;
  const scope = new URL(self.registration.scope).pathname;
  if (!u.pathname.startsWith(scope)) return;
  const rel = u.pathname.slice(scope.length);
  if (rel.startsWith('api/')) return;   // always live, never stored
  const key = rel === '' || e.request.mode === 'navigate' ? './' : rel;
  if (!FILES.includes(key)) return;
  e.respondWith(caches.open(CACHE).then(c => c.match(key).then(hit => hit || fetch(e.request).then(r => { if (r.ok) c.put(key, r.clone()); return r; }))));
});
