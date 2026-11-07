/* MatDrive – the text editor: loads the text, keeps Tab as a tab, shows line and column, saves with Ctrl+S or the button (with the ETag it started from:
   a file changed meanwhile is not overwritten), warns before leaving with unsaved changes. */
import { t, request, toast } from './util.js';

const root = document.getElementById('md-editor');
if (root) {
  const area = root.querySelector('textarea');
  const save = root.querySelector('[data-editor-save]');
  const state = root.querySelector('[data-editor-state]');
  const pos = root.querySelector('[data-editor-pos]');
  const wrap = root.querySelector('[data-editor-wrap]');
  const id = root.dataset.id;
  const canEdit = root.dataset.canEdit === 'true';
  let etag = null;
  let saved = '';

  const dirty = () => area.value !== saved;
  const paint = () => {
    if (save) save.disabled = !dirty();
    state.textContent = dirty() ? t('Editor.Unsaved') : etag ? t('Editor.Saved') : '';
  };
  const where = () => {
    const before = area.value.slice(0, area.selectionStart);
    const line = before.split('\n').length;
    const col = area.selectionStart - before.lastIndexOf('\n');
    pos.textContent = t('Editor.Position', line, col);
  };

  try { wrap.checked = localStorage.getItem('md.editor.wrap') === '1'; } catch { /* private mode */ }
  const applyWrap = () => { area.style.whiteSpace = wrap.checked ? 'pre-wrap' : 'pre'; try { localStorage.setItem('md.editor.wrap', wrap.checked ? '1' : '0'); } catch { /* nothing */ } };
  wrap.addEventListener('change', applyWrap);
  applyWrap();

  request('GET', '/live/drive/text/' + id).then(r => {
    area.value = r.text;
    saved = r.text;
    etag = r.etag;
    paint();
    where();
    if (canEdit) area.focus();
  }).catch(e => { state.textContent = e.body?.error || t('Drive.Failed'); });

  async function store() {
    if (!canEdit || !dirty()) return;
    const text = area.value;
    state.textContent = t('Editor.Saving');
    try {
      const r = await request('PUT', '/live/drive/text/' + id, { text, etag });
      etag = r.etag;
      saved = text;
      paint();
      toast(t('Editor.Saved'), 'success');
    } catch (e) {
      state.textContent = e.body?.error || t('Drive.Failed');
      toast(e.status === 409 ? t('Editor.Conflict') : (e.body?.error || t('Drive.Failed')), 'danger');
    }
  }

  area.addEventListener('input', paint);
  area.addEventListener('keyup', where);
  area.addEventListener('click', where);
  area.addEventListener('keydown', e => {
    if ((e.ctrlKey || e.metaKey) && (e.key === 's' || e.key === 'S')) { e.preventDefault(); store(); return; }
    if (e.key === 'Tab' && !e.ctrlKey && !e.altKey && canEdit) {
      e.preventDefault();
      const s = area.selectionStart;
      area.setRangeText('\t', s, area.selectionEnd, 'end');
      paint();
    }
  });
  save?.addEventListener('click', store);
  window.addEventListener('beforeunload', e => { if (dirty()) { e.preventDefault(); e.returnValue = ''; } });
}
