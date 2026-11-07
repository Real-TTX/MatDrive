/* MatDrive – the pages of an item: a new version from a file of the device (the tab "Versions"), with progress. */
import { t, toast, token } from './util.js';

document.addEventListener('click', e => {
  const button = e.target.closest('[data-upload-version]');
  if (!button) return;
  const input = document.createElement('input');
  input.type = 'file';
  input.hidden = true;
  input.addEventListener('change', () => {
    const file = input.files[0];
    input.remove();
    if (file) send(button, file);
  });
  document.body.append(input);
  input.click();
});

function send(button, file) {
  const label = button.querySelector('span');
  const before = label.textContent;
  button.disabled = true;
  const xhr = new XMLHttpRequest();
  xhr.open('PUT', '/live/drive/version/' + button.dataset.uploadVersion);
  xhr.setRequestHeader('RequestVerificationToken', token());
  xhr.setRequestHeader('Content-Type', 'application/octet-stream');
  xhr.upload.onprogress = ev => { if (ev.lengthComputable) label.textContent = t('Drive.UploadingPercent', Math.round(ev.loaded / ev.total * 100)); };
  xhr.onload = () => {
    if (xhr.status >= 200 && xhr.status < 300) {
      try { sessionStorage.setItem('md.flash', JSON.stringify({ message: t('Drive.VersionUploaded'), tone: 'success' })); } catch { /* private mode */ }
      location.reload();
      return;
    }
    let error = null;
    try { error = JSON.parse(xhr.responseText).error; } catch { error = null; }
    toast(error || t('Drive.Failed'), 'danger');
    label.textContent = before;
    button.disabled = false;
  };
  xhr.onerror = () => { toast(t('Drive.Offline'), 'danger'); label.textContent = before; button.disabled = false; };
  xhr.send(file);
}

try {
  const f = JSON.parse(sessionStorage.getItem('md.flash') || 'null');
  sessionStorage.removeItem('md.flash');
  if (f) setTimeout(() => toast(f.message, f.tone), 100);
} catch { /* nothing */ }
