/* MatDrive – the page of a shared link: the text of a text file is loaded as text (never as HTML), and a folder link with the right to add sends the chosen files
   at once and shows the folder again. */
(function () {
  'use strict';

  var pre = document.querySelector('[data-text-src]');
  if (pre) {
    fetch(pre.getAttribute('data-text-src'), { credentials: 'same-origin' })
      .then(function (r) { return r.ok ? r.text() : Promise.reject(r.status); })
      .then(function (text) { pre.textContent = text; })
      .catch(function () { pre.textContent = '–'; });
  }

  var form = document.querySelector('[data-link-upload]');
  if (form) {
    var input = form.querySelector('[data-link-upload-input]');
    input.addEventListener('change', function () {
      if (!input.files.length) { return; }
      var data = new FormData();
      Array.prototype.forEach.call(input.files, function (f) { data.append('files', f, f.name); });
      var token = form.querySelector('input[name="__RequestVerificationToken"]').value;
      form.classList.add('is-busy');
      fetch(form.getAttribute('action'), { method: 'POST', body: data, headers: { RequestVerificationToken: token }, credentials: 'same-origin' })
        .then(function (r) { return r.json(); })
        .then(function (results) {
          var failed = results.filter(function (x) { return x.error; });
          if (failed.length && window.MatUi) { window.MatUi.toast(failed.map(function (x) { return x.name + ': ' + x.error; }).join('\n'), { tone: 'danger' }); }
          setTimeout(function () { location.reload(); }, failed.length ? 2500 : 0);
        })
        .catch(function () { form.classList.remove('is-busy'); if (window.MatUi) { window.MatUi.toast('Upload failed', { tone: 'danger' }); } });
    });
  }
})();
