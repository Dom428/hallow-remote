// Hallow Remote - a small service worker (HTTPS only): the page opens like an app, also without a connection.
// The page is fetched fresh when the PC answers; the cache is only used when it doesn't. Only the shell is cached - this page,
// the manifest and the icons - plus (v1.10) Hallow Keys' phone copy, which is encrypted with your master password.
// Never cached: anything else under api/ (state, actions, clips, files, screen peeks, share links...).
const CACHE = 'hallow-remote-v1';
const SHELL = ['', 'manifest.json', 'icon.png', 'app-192.png', 'app-512.png', 'app-mask-192.png', 'app-mask-512.png', 'apple-touch-icon.png'];
self.addEventListener('install', e => { e.waitUntil(caches.open(CACHE).then(c => c.addAll(SHELL.map(f => './' + f)).catch(() => { }))); self.skipWaiting(); });
self.addEventListener('activate', e => { e.waitUntil(self.clients.claim()); });
self.addEventListener('fetch', e => {
  const u = new URL(e.request.url);
  if (e.request.method !== 'GET' || u.origin !== location.origin) return;
  const scope = new URL(self.registration.scope).pathname;
  const rel = u.pathname.startsWith(scope) ? u.pathname.slice(scope.length) : null;
  if (rel === null || !(SHELL.includes(rel) || rel === 'api/keys')) return;   // everything else: always live, never stored
  e.respondWith(fetch(e.request).then(r => { if (r.ok) { const c = r.clone(); caches.open(CACHE).then(x => x.put(rel === '' ? './' : e.request, c)); } return r; })
    .catch(() => caches.match(rel === '' ? './' : e.request)));
});
// v1.13: tapping one of the page's notifications brings the page back
self.addEventListener('notificationclick', e => {
  e.notification.close();
  e.waitUntil(self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(list => {
    for (const c of list) if (c.url.startsWith(self.registration.scope) && 'focus' in c) return c.focus();
    return self.clients.openWindow ? self.clients.openWindow(self.registration.scope) : null;
  }));
});
