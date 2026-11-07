/* MatDrive service worker: the app as a PWA – small on purpose.
   - Static files (toolkit, styles, the scripts of the editors, icons): from the cache first, refreshed in the background.
   - Pages come from the network. Without one: the last copy of an editor page you opened, else the offline page (offline.html) with a button to try again.
   - The content of a document (GET /live/documents/{id}) comes from the network and a copy is kept: without a network the last copy is shown
     (changes made then wait in the browser and are sent by the editor when the network is back).
   - The APIs, every change (PUT, POST, DELETE), the files of connected apps (/live/host) and everything else of /live are never touched. */
const VERSION = 'matdrive-v1';
const PAGES = 'md-pages';
const DOCUMENTS = 'md-documents';
const SHELL = [
  '/offline.html', '/js/offline.js', '/matui/matui.css', '/matui/matui.js', '/css/app.css', '/css/editor.css', '/favicon.svg', '/img/icon-192.png', '/img/icon-512.png',
  '/img/logo/matdrive-logo.svg', '/img/logo/matdrive-logo-dark.svg', '/manifest.webmanifest',
  '/js/editor/frame.js', '/js/editor/session.js', '/js/editor/util.js',
];
const MAX_PAGES = 12;
const MAX_DOCUMENTS = 30;

self.addEventListener('install', event => {
  event.waitUntil(caches.open(VERSION).then(cache => cache.addAll(SHELL)).then(() => self.skipWaiting()));
});

// an older version of the shell is gone (the copies of pages and documents stay: they are the person's)
self.addEventListener('activate', event => {
  event.waitUntil(caches.keys()
    .then(keys => Promise.all(keys.filter(k => k !== VERSION && k !== PAGES && k !== DOCUMENTS).map(k => caches.delete(k))))
    .then(() => self.clients.claim()));
});

// signing out clears what was kept for this person (sent by pwa.js)
self.addEventListener('message', event => {
  if (event.data === 'md-clear') {
    event.waitUntil(Promise.all([caches.delete(PAGES), caches.delete(DOCUMENTS)]));
  }
});

function isStatic(url) {
  return /^\/(matui|css|js|img)\//.test(url.pathname) || url.pathname === '/favicon.svg' || url.pathname === '/manifest.webmanifest' || url.pathname === '/offline.html';
}

function isEditorPage(url) {
  return /^\/(docs|sheets|slides|base)\/\d+$/.test(url.pathname) || /^\/s\/[0-9a-f-]{36}$/i.test(url.pathname);
}

function isDocumentData(url) {
  return /^\/live\/documents\/\d+$/.test(url.pathname);
}

function trim(cache, max) {
  return cache.keys().then(keys => (keys.length > max ? cache.delete(keys[0]) : undefined));
}

/** From the network (a copy is kept), without one the last copy. */
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
  if (url.origin !== self.location.origin || url.pathname === '/sw.js' || url.pathname === '/health' || url.pathname.startsWith('/api/')) return;

  if (isDocumentData(url) && !url.search) { event.respondWith(networkFirst(request, DOCUMENTS, MAX_DOCUMENTS)); return; }
  if (url.pathname.startsWith('/live/')) return;

  if (request.mode === 'navigate') {
    event.respondWith(isEditorPage(url) ? networkFirst(request, PAGES, MAX_PAGES, '/offline.html') : fetch(request).catch(() => caches.match('/offline.html')));
    return;
  }

  // the modules of the editors are loaded without a version in the address: from the network while there is one, so a new build is used at once
  if (isStatic(url) && !url.search.includes('v=') && /^\/(js|css)\//.test(url.pathname)) { event.respondWith(networkFirst(request, VERSION, 500)); return; }

  if (isStatic(url)) {
    event.respondWith(caches.open(VERSION).then(cache => cache.match(request).then(hit => {
      const fresh = fetch(request).then(response => { if (response.ok) cache.put(request, response.clone()); return response; }).catch(() => hit);
      return hit || fresh;
    })));
  }
});
