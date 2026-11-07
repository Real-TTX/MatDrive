/* MatDrive service worker: the app as a PWA – small on purpose.
   - Static files (toolkit, styles, scripts, icons): from the cache first, refreshed in the background.
   - Pages of the drive come from the network; a copy of the last folders opened is kept, so without a network the last state of a folder is still shown
     (read-only in effect: every change needs the network). Other pages without a network: the offline page (offline.html) with a button to try again.
   - File content, uploads, the APIs, WebDAV and every change (PUT, POST, DELETE) are never touched. */
const VERSION = 'matdrive-v1';
const PAGES = 'md-pages';
const SHELL = [
  '/offline.html', '/js/offline.js', '/matui/matui.css', '/matui/matui.js', '/css/app.css', '/favicon.svg', '/img/icon-192.png', '/img/icon-512.png',
  '/img/logo/matdrive-logo.svg', '/img/logo/matdrive-logo-dark.svg', '/manifest.webmanifest', '/js/util.js', '/js/drive.js', '/js/explorer.js',
];
const MAX_PAGES = 30;

self.addEventListener('install', event => {
  event.waitUntil(caches.open(VERSION).then(cache => cache.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', event => {
  event.waitUntil(caches.keys()
    .then(keys => Promise.all(keys.filter(k => k !== VERSION && k !== PAGES).map(k => caches.delete(k))))
    .then(() => self.clients.claim()));
});

// signing out clears what was kept for this person (sent by pwa.js)
self.addEventListener('message', event => {
  if (event.data === 'md-clear') {
    event.waitUntil(caches.delete(PAGES));
  }
});

function isStatic(url) {
  return /^\/(matui|css|js|img)\//.test(url.pathname) || url.pathname === '/favicon.svg' || url.pathname === '/manifest.webmanifest' || url.pathname === '/offline.html';
}

function isDrivePage(url) {
  return url.pathname === '/drive' || /^\/drive\/(folder|shared-drive)\/\d+$/.test(url.pathname) || /^\/drive\/(shared|recent|starred)$/.test(url.pathname);
}

function trim(cache, max) {
  return cache.keys().then(keys => (keys.length > max ? cache.delete(keys[0]) : undefined));
}

function networkFirst(request, cacheName, max, fallback) {
  return fetch(request).then(response => {
    if (response.ok) {
      const copy = response.clone();
      caches.open(cacheName).then(cache => cache.put(request, copy).then(() => trim(cache, max)));
    }
    return response;
  }).catch(() => caches.open(cacheName).then(cache => cache.match(request)).then(hit => hit || (fallback ? caches.match(fallback) : Response.error())));
}

self.addEventListener('fetch', event => {
  const request = event.request;
  if (request.method !== 'GET') return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin || url.pathname === '/sw.js' || url.pathname === '/health') return;
  if (/^\/(api|live|files|dav|l|office)\//.test(url.pathname)) return;

  if (request.mode === 'navigate') {
    event.respondWith(isDrivePage(url) ? networkFirst(request, PAGES, MAX_PAGES, '/offline.html') : fetch(request).catch(() => caches.match('/offline.html')));
    return;
  }

  // scripts and styles without a version in the address: from the network while there is one, so a new build is used at once
  if (isStatic(url) && !url.search.includes('v=') && /^\/(js|css)\//.test(url.pathname)) { event.respondWith(networkFirst(request, VERSION, 500)); return; }

  if (isStatic(url)) {
    event.respondWith(caches.open(VERSION).then(cache => cache.match(request).then(hit => {
      const fresh = fetch(request).then(response => { if (response.ok) cache.put(request, response.clone()); return response; }).catch(() => hit);
      return hit || fresh;
    })));
  }
});
