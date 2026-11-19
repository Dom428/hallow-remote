// Hallow Remote v1.10 - a small service worker (HTTPS only) so the page and Hallow Keys' encrypted phone copy open without a connection.
// The page is fetched fresh when the PC answers; the cache is only used when it doesn't. Only this page, its icon and the encrypted copy are cached.
const CACHE = 'hallow-remote-v1';
self.addEventListener('install', e => { e.waitUntil(caches.open(CACHE).then(c => c.addAll(['./', 'icon.png', 'manifest.json']).catch(() => { }))); self.skipWaiting(); });
self.addEventListener('activate', e => { e.waitUntil(self.clients.claim()); });
self.addEventListener('fetch', e => {
  const u = new URL(e.request.url);
  if (e.request.method !== 'GET' || u.origin !== location.origin) return;
  const scope = new URL(self.registration.scope).pathname;
  const rel = u.pathname.startsWith(scope) ? u.pathname.slice(scope.length) : null;
  if (rel === null || !['', 'icon.png', 'manifest.json', 'api/keys'].includes(rel)) return;   // everything else: always live
  e.respondWith(fetch(e.request).then(r => { if (r.ok) { const c = r.clone(); caches.open(CACHE).then(x => x.put(e.request, c)); } return r; }).catch(() => caches.match(e.request)));
});
