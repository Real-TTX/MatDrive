/* MatDrive without a network – small on purpose.
   - A page of the app shows "Offline" in the top bar while the device has no network (a click tries again).
   - The offline page (offline.html, shown by the service worker when a page cannot be loaded) tries again by itself as soon as the network is back, and by a button.
   - The pages of the drive come from the service worker while offline (the last copy of a folder). */
(function () {
  'use strict';

  var root = document.documentElement;
  var TEXTS = (function () { try { return JSON.parse(document.getElementById('md-texts').textContent); } catch (e) { return {}; } })();
  function t(key) { return TEXTS[key] || key; }

  // the top bar of an app page
  var mark = document.getElementById('md-offline');
  function badge() {
    if (!mark) { return; }
    var off = navigator.onLine === false;
    mark.hidden = !off;
    if (off) { mark.querySelector('[data-offline-text]').textContent = t('Js.Offline.Badge'); mark.title = t('Js.Offline.Now'); }
  }
  if (mark) {
    mark.addEventListener('click', function () { if (navigator.onLine === false) { badge(); } else { location.reload(); } });
    window.addEventListener('online', function () { badge(); });
    window.addEventListener('offline', badge);
    badge();
  }

  // the offline page
  var page = document.getElementById('md-offline-page');
  if (page) {
    var lang = (root.getAttribute('lang') || '').slice(0, 2);
    var stored = '';
    try { stored = localStorage.getItem('md-lang') || ''; } catch (e) { stored = ''; }
    root.lang = stored || ((navigator.language || 'en').slice(0, 2) === 'de' ? 'de' : lang || 'en');
    var retry = document.getElementById('md-retry');
    var status = document.getElementById('md-retry-status');
    var go = function () { location.assign(document.referrer && document.referrer.indexOf(location.origin) === 0 ? document.referrer : '/drive'); };
    retry.addEventListener('click', function () {
      status.hidden = false;
      fetch('/health', { cache: 'no-store' }).then(function (r) { if (r.ok) { go(); } else { status.hidden = false; } }).catch(function () { status.hidden = false; });
    });
    window.addEventListener('online', go);
  }

  // remember the language of the person for the offline page (it cannot ask the server)
  var lang2 = root.getAttribute('lang');
  if (lang2 && document.getElementById('md-texts')) { try { localStorage.setItem('md-lang', lang2.slice(0, 2)); } catch (e) { /* private mode */ } }
})();
