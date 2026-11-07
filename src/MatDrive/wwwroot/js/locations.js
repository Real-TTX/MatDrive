/* MatDrive – a storage location (Pages/Locations/Index: /locations/{id}/{folder}) and its text editor (Pages/Locations/Edit): the explorer of the drives for a
   folder of the server or a share of another machine. Uploads (files and whole folders, by button and by drag & drop, each file streamed in one request, with
   progress and one question when names are taken), the menu of a row (also the right mouse button), the actions for the selection, the preview (with the
   previous and next file), moving and copying inside the location and to another one, copying to My Drive or a shared drive and from the drives into the
   folder, unzip, the properties with the rwx permissions, and the search below the folder (sent by the server as it is found, with a button to stop).
   Every change goes to /live/locations (business layer); the list is drawn by the server and loaded again after a change. */
import { t, el, icon, request, toast, dialog, token, stamp, lang, uid, busy } from './util.js';

const root = document.getElementById('md-loc-explorer');
const editor = document.getElementById('md-loc-editor');

const ctx = root ? {
  id: Number(root.dataset.location),
  path: root.dataset.path || '/',
  name: root.dataset.locationName || '',
  canEdit: root.dataset.canEdit === 'true',
} : null;

if (root) {
  // the menu "New" of the page (drive.js) does what was chosen in this folder; what a location cannot hold (documents of MatOffice) opens My Drive
  window.mdExplorer = { canAdd: ctx.canEdit, create };
  flashAfterReload();
  wireRows();
  wireBulk();
  wireDrop();
  wireKeys();
  wireTop();
}

if (editor) {
  wireEditor();
}

// ---- addresses and paths --------------------------------------------------------------------------------------------------------

const q = encodeURIComponent;
const live = suffix => `/live/locations/${ctx.id}/${suffix}`;

/** "/a" + "b" = "/a/b"; the top is "/". */
function join(folder, name) { return (folder === '/' ? '' : folder.replace(/\/+$/, '')) + '/' + name; }

function parentOf(path) { const i = path.lastIndexOf('/'); return i <= 0 ? '/' : path.slice(0, i); }

/** The page of a folder: every segment encoded (/locations/3/My%20photos/2026). */
function browseUrl(id, path) {
  const segments = (path || '').split('/').filter(Boolean).map(q);
  return segments.length ? `/locations/${id}/${segments.join('/')}` : `/locations/${id}`;
}

function contentUrl(path, download) { return `/location-files/${ctx.id}/content?path=${q(path)}${download ? '&download=true' : ''}`; }

function editUrl(path) { return `/locations/edit/${ctx.id}?path=${q(path)}`; }

/** 1.5 MB, like the server writes it (Ui.Bytes). */
function bytes(n) {
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = Number(n) || 0;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return unit === 0 ? `${value} B` : `${value.toLocaleString(lang(), { minimumFractionDigits: value < 10 ? 1 : 0, maximumFractionDigits: value < 10 ? 1 : 0 })} ${units[unit]}`;
}

// ---- helpers ----------------------------------------------------------------------------------------------------------------------

function flash(message, tone = 'success') {
  try { sessionStorage.setItem('md.flash', JSON.stringify({ message, tone })); } catch { /* private mode */ }
}

function flashAfterReload() {
  try {
    const f = JSON.parse(sessionStorage.getItem('md.flash') || 'null');
    sessionStorage.removeItem('md.flash');
    if (f) setTimeout(() => toast(f.message, f.tone), 100);
  } catch { /* nothing */ }
}

function reload(message, tone) {
  if (message) flash(message, tone);
  location.reload();
}

function problem(e) {
  if (e.status === 0) return t('Locations.Offline');
  return e.body?.error || (e.status === 404 ? t('Locations.NotAllowed') : t('Locations.Failed'));
}

/** A change: the request, then the page again with a message (or the error as a toast); busyLabel shows the panel of util.busy while it runs. */
async function act(url, body, done, busyLabel) {
  const stop = busyLabel ? busy(busyLabel) : null;
  try {
    const r = await request('POST', url, body);
    stop?.();
    reload(done ? done(r) : null);
  } catch (e) {
    stop?.();
    toast(problem(e), 'danger');
  }
}

function rowOf(node) { return node.closest('[data-entry]'); }

function itemOf(row) {
  return {
    path: row.dataset.path, kind: row.dataset.kind, name: row.dataset.name, category: row.dataset.category, preview: row.dataset.preview,
    text: row.dataset.text === '1', size: Number(row.dataset.size || 0), row,
  };
}

function selectedPaths() {
  return [...new Set([...root.querySelectorAll('[data-select-row]:checked')].map(c => c.value))];
}

/** The entries of the page (once: the table and the tiles show the same). */
function items() {
  const seen = new Set();
  return [...root.querySelectorAll('[data-view="list"] [data-entry]')].map(itemOf).filter(i => !seen.has(i.path) && seen.add(i.path));
}

function itemByPath(path) {
  const row = root.querySelector(`[data-view="list"] [data-entry][data-path="${CSS.escape(path)}"]`);
  return row ? itemOf(row) : null;
}

// ---- "New" ------------------------------------------------------------------------------------------------------------------------

async function create(what) {
  if (!ctx.canEdit || !['folder', 'files', 'dir'].includes(what)) {
    location.assign('/drive?new=' + q(what));
    return;
  }

  if (what === 'folder') {
    const name = await window.MatUi.prompt(t('Locations.NewFolder.Label'), { title: t('Locations.NewFolder'), value: t('Locations.NewFolder.Default'), ok: t('Locations.Create') });
    if (!name) return;
    await act(live('folders'), { path: ctx.path, name }, r => t('Locations.FolderCreated', r.name));
    return;
  }

  const input = el('input', { type: 'file', multiple: true, hidden: true });
  if (what === 'dir') { input.webkitdirectory = true; input.setAttribute('webkitdirectory', ''); }
  input.addEventListener('change', () => {
    const files = [...input.files].map(f => ({ file: f, path: f.webkitRelativePath || f.name }));
    input.remove();
    if (files.length) upload(files, ctx.path);
  });
  document.body.append(input);
  input.click();
}

// ---- uploads ----------------------------------------------------------------------------------------------------------------------

let panel = null;

function uploadPanel() {
  if (panel) return panel;
  const list = el('ul', { class: 'md-uploads__list' });
  const title = el('strong', { class: 'md-uploads__title' });
  const close = el('button', { type: 'button', class: 'mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm', 'aria-label': t('Locations.Close'), onclick: () => { panel.node.hidden = true; } }, icon('x'));
  const node = el('section', { class: 'md-uploads', role: 'status', 'aria-live': 'polite' }, el('div', { class: 'md-uploads__head' }, title, close), list);
  document.body.append(node);
  panel = { node, list, title };
  return panel;
}

/**
 * Uploads files ({ file, path }; path = the relative path of a file of a folder) into a folder of the location, three at a time. Each file is one request
 * whose body is the file itself: the server streams it to the location (never held in memory). Names that are taken are asked about once, before the first byte.
 */
async function upload(entries, folder) {
  let existing = [];
  try {
    existing = (await request('POST', live('exists'), { folder, paths: entries.map(e => e.path) })).existing || [];
  } catch (e) {
    toast(problem(e), 'danger');
    return;
  }

  let conflict = 'ask';
  if (existing.length) {
    const choice = await askConflict(existing);
    if (!choice || choice === 'cancel') return;
    if (choice === 'skip') {
      const taken = new Set(existing);
      entries = entries.filter(e => !taken.has(e.path));
      if (!entries.length) return;
    } else {
      conflict = choice;
    }
  }

  const p = uploadPanel();
  p.node.hidden = false;
  let done = 0;
  let failed = 0;
  const total = entries.length;
  const paint = () => { p.title.textContent = done + failed < total ? t('Locations.Uploading', done + failed, total) : failed ? t('Locations.UploadedWithErrors', done, failed) : t('Locations.Uploaded', done); };
  paint();
  const rows = entries.map(entry => {
    const bar = el('progress', { max: 100, value: 0, class: 'md-uploads__bar' });
    const state = el('span', { class: 'md-uploads__state' });
    const li = el('li', {}, el('span', { class: 'md-uploads__name', text: entry.path }), bar, state);
    p.list.prepend(li);
    return { entry, bar, state, li };
  });

  const queue = [...rows];
  const worker = async () => {
    while (queue.length) {
      const r = queue.shift();
      try {
        await uploadOne(r.entry, folder, conflict, pct => { r.bar.value = pct; });
        r.bar.value = 100;
        r.state.replaceChildren(icon('check'));
        done++;
      } catch (e) {
        failed++;
        r.li.classList.add('is-failed');
        r.state.textContent = problem(e);
      }
      paint();
    }
  };
  await Promise.all([worker(), worker(), worker()]);
  if (!failed) reload(t('Locations.Uploaded', done));
  else if (done) setTimeout(() => reload(), 2500);
}

/** Names that are taken: replace them, keep both ("name (2).ext") or skip them. */
function askConflict(existing) {
  const shown = existing.slice(0, 5);
  const body = el('div', { class: 'md-loc-conflict' },
    el('p', { text: existing.length === 1 ? t('Locations.Conflict.One', existing[0]) : t('Locations.Conflict.Many', existing.length) }),
    existing.length > 1 ? el('ul', { class: 'md-loc-conflict__list' }, ...shown.map(name => el('li', { class: 'mat-mono', text: name })),
      existing.length > shown.length ? el('li', { class: 'md-muted', text: t('Locations.Conflict.More', existing.length - shown.length) }) : null) : null);
  return dialog({
    title: t('Locations.Conflict.Title'), icon: 'alert', body, size: 'sm',
    buttons: [
      { label: t('Locations.Conflict.Replace'), value: 'replace', kind: 'primary' },
      { label: t('Locations.Conflict.KeepBoth'), value: 'keep' },
      { label: t('Locations.Conflict.Skip'), value: 'skip' },
      { label: t('Locations.Cancel'), value: 'cancel' },
    ],
  });
}

function uploadOne({ file, path }, folder, conflict, progress) {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('PUT', live(`file?folder=${q(folder)}&path=${q(path)}&conflict=${conflict}`));
    xhr.setRequestHeader('RequestVerificationToken', token());
    xhr.setRequestHeader('Content-Type', 'application/octet-stream');
    xhr.upload.onprogress = e => { if (e.lengthComputable) progress(Math.round(e.loaded / e.total * 100)); };
    xhr.onload = () => {
      let body = null;
      try { body = JSON.parse(xhr.responseText); } catch { body = null; }
      if (xhr.status >= 200 && xhr.status < 300) resolve(body);
      else { const err = new Error('upload'); err.status = xhr.status; err.body = body; reject(err); }
    };
    xhr.onerror = () => { const err = new Error('network'); err.status = 0; reject(err); };
    xhr.send(file);
  });
}

// ---- drag & drop ------------------------------------------------------------------------------------------------------------------

function wireDrop() {
  let depth = 0;
  let dragged = null;
  const overlay = el('div', { class: 'md-drop', hidden: true }, icon('upload'), el('span', { text: t('Locations.DropHere') }));
  root.append(overlay);
  const isFiles = e => [...(e.dataTransfer?.types || [])].includes('Files');
  const clear = except => document.querySelectorAll('.md-drop-target').forEach(n => { if (n !== except) n.classList.remove('md-drop-target'); });
  const moveInto = async folder => {
    const paths = (dragged || []).filter(p => p !== folder && parentOf(p) !== folder);
    dragged = null;
    if (paths.length) await act(live('transfer'), { paths, targetId: ctx.id, targetPath: folder, move: true }, r => t('Locations.Moved', r.count), t('Locations.Busy.Moving'));
  };

  root.addEventListener('dragenter', e => {
    if (!isFiles(e) || !ctx.canEdit) return;
    depth++;
    overlay.hidden = false;
  });
  root.addEventListener('dragleave', e => {
    if (!isFiles(e)) return;
    depth = Math.max(0, depth - 1);
    if (!depth) overlay.hidden = true;
  });
  root.addEventListener('dragover', e => {
    if (isFiles(e) && ctx.canEdit) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; }
    const target = e.target.closest('[data-drop-folder]');
    clear(target);
    if (target && ((isFiles(e) && ctx.canEdit) || dragged)) { e.preventDefault(); target.classList.add('md-drop-target'); }
  });
  root.addEventListener('drop', async e => {
    overlay.hidden = true;
    depth = 0;
    const target = e.target.closest('[data-drop-folder]');
    clear(null);
    if (isFiles(e)) {
      if (!ctx.canEdit) return;
      e.preventDefault();
      const entries = await filesOf(e.dataTransfer);
      if (entries.length) upload(entries, target ? target.dataset.dropFolder : ctx.path);
      return;
    }

    if (dragged && target) {
      e.preventDefault();
      await moveInto(target.dataset.dropFolder);
    }
  });

  // moving entries by dragging them onto a folder of the list or of the path above
  root.addEventListener('dragstart', e => {
    const row = e.target.closest?.('[data-entry]');
    if (!row || !ctx.canEdit) return;
    const sel = selectedPaths();
    dragged = sel.includes(row.dataset.path) ? sel : [row.dataset.path];
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('text/plain', row.dataset.name);
  });
  root.addEventListener('dragend', () => { dragged = null; clear(null); });
  document.querySelectorAll('.mat-crumbs a').forEach(a => {
    const m = (a.getAttribute('href') || '').match(/^\/locations\/(\d+)(\/[^?#]*)?$/);
    if (!m || Number(m[1]) !== ctx.id) return;
    const folder = '/' + (m[2] || '').split('/').filter(Boolean).map(decodeURIComponent).join('/');
    a.addEventListener('dragover', e => { if (dragged) { e.preventDefault(); a.classList.add('md-drop-target'); } });
    a.addEventListener('dragleave', () => a.classList.remove('md-drop-target'));
    a.addEventListener('drop', async e => {
      if (!dragged) return;
      e.preventDefault();
      a.classList.remove('md-drop-target');
      await moveInto(folder);
    });
  });
}

/** The files of a drop, folders included (with their relative paths). */
async function filesOf(transfer) {
  const out = [];
  const entries = [...transfer.items].map(i => i.webkitGetAsEntry?.()).filter(Boolean);
  if (!entries.length) return [...transfer.files].map(f => ({ file: f, path: f.name }));
  const walk = async (entry, prefix) => {
    if (entry.isFile) {
      const file = await new Promise((res, rej) => entry.file(res, rej));
      out.push({ file, path: prefix + file.name });
    } else if (entry.isDirectory) {
      const reader = entry.createReader();
      for (;;) {
        const batch = await new Promise((res, rej) => reader.readEntries(res, rej));
        if (!batch.length) break;
        for (const child of batch) await walk(child, prefix + entry.name + '/');
      }
    }
  };
  for (const entry of entries) await walk(entry, '');
  return out;
}

// ---- rows: open, menu -------------------------------------------------------------------------------------------------------------

function wireRows() {
  root.addEventListener('click', e => {
    const open = e.target.closest('[data-open]');
    if (open && !e.ctrlKey && !e.metaKey && !e.shiftKey) {
      const row = rowOf(open);
      if (row && row.dataset.kind === 'file') {
        e.preventDefault();
        preview(itemOf(row));
        return;
      }
    }

    const menu = e.target.closest('[data-row-menu]');
    if (menu) { e.preventDefault(); rowMenu(itemOf(rowOf(menu)), menu); }
  });

  root.addEventListener('contextmenu', e => {
    const row = e.target.closest('[data-entry]');
    if (!row || e.target.closest('input')) return;
    e.preventDefault();
    rowMenu(itemOf(row), null, e.clientX, e.clientY);
  });

  root.addEventListener('dblclick', e => {
    const row = e.target.closest('[data-entry]');
    if (!row || e.target.closest('input, button, a')) return;
    const item = itemOf(row);
    if (item.kind === 'folder') location.assign(browseUrl(ctx.id, item.path));
    else preview(item);
  });
}

let menuNode = null;

function closeMenu() {
  if (!menuNode) return;
  menuNode.remove();
  menuNode = null;
  document.removeEventListener('pointerdown', outside, true);
  document.removeEventListener('keydown', escape, true);
}

function outside(e) { if (menuNode && !menuNode.contains(e.target)) closeMenu(); }

function escape(e) { if (e.key === 'Escape') closeMenu(); }

function rowMenu(item, anchor, x, y) {
  closeMenu();
  const zip = item.kind === 'file' && item.name.toLowerCase().endsWith('.zip');
  const entries = [
    item.kind === 'folder' ? ['folder-open', t('Locations.Open'), () => location.assign(browseUrl(ctx.id, item.path))] : ['eye', t('Locations.Preview'), () => preview(item)],
    item.text ? ['file-text', t('Locations.EditText'), () => location.assign(editUrl(item.path))] : null,
    ['download', t('Locations.Download'), () => download([item.path])],
    '-',
    ctx.canEdit ? ['edit', t('Locations.Rename'), () => rename(item)] : null,
    ctx.canEdit ? ['folder-move', t('Locations.MoveTo'), () => pick('move', [item.path])] : null,
    ['copy', t('Locations.CopyTo'), () => pick('copy', [item.path])],
    ['hard-drive', t('Locations.ToDrive'), () => toDrive([item.path])],
    ctx.canEdit && zip ? ['archive', t('Locations.Unzip'), () => act(live('unzip'), { path: item.path }, r => t('Locations.Unzipped', r.name), t('Locations.Busy.Unzipping'))] : null,
    '-',
    ['info', t('Locations.Properties'), () => properties(item.path)],
    ctx.canEdit ? '-' : null,
    ctx.canEdit ? ['trash', t('Locations.Delete'), () => remove([item.path]), true] : null,
  ];

  const list = el('div', { class: 'mat-menu md-menu', role: 'menu', 'aria-label': item.name });
  let sep = false;
  for (const entry of entries) {
    if (!entry) continue;
    if (entry === '-') { sep = list.childElementCount > 0; continue; }
    if (sep) { list.append(el('div', { class: 'mat-menu__sep', role: 'separator' })); sep = false; }
    const [ic, label, run, danger] = entry;
    list.append(el('button', { type: 'button', role: 'menuitem', class: 'mat-menu__item' + (danger ? ' mat-menu__item--danger' : ''), onclick: () => { closeMenu(); run(); } }, icon(ic), el('span', { text: label })));
  }

  document.body.append(list);
  const r = anchor ? anchor.getBoundingClientRect() : { left: x, right: x, top: y, bottom: y };
  const w = list.offsetWidth;
  const h = list.offsetHeight;
  list.style.position = 'fixed';
  list.style.left = Math.max(8, Math.min((anchor ? r.right - w : r.left), window.innerWidth - w - 8)) + 'px';
  list.style.top = Math.max(8, r.bottom + h + 8 > window.innerHeight ? r.top - h - 4 : r.bottom + 4) + 'px';
  list.style.zIndex = '1000';
  menuNode = list;
  setTimeout(() => { document.addEventListener('pointerdown', outside, true); document.addEventListener('keydown', escape, true); }, 0);
  list.querySelector('button')?.focus();
}

// ---- actions ----------------------------------------------------------------------------------------------------------------------

function wireBulk() {
  root.addEventListener('click', e => {
    const b = e.target.closest('[data-act]');
    if (!b) return;
    e.preventDefault();
    const paths = selectedPaths();
    if (!paths.length) return;
    switch (b.dataset.act) {
      case 'download': download(paths); break;
      case 'move': pick('move', paths); break;
      case 'copy': pick('copy', paths); break;
      case 'to-drive': toDrive(paths); break;
      case 'delete': remove(paths); break;
    }
  });

  root.querySelector('[data-loc-from-drive]')?.addEventListener('click', e => { e.preventDefault(); fromDrive(); });
}

function wireTop() {
  document.querySelector('[data-loc-search]')?.addEventListener('click', e => { e.preventDefault(); deepSearch(); });
  document.querySelector('[data-loc-props]')?.addEventListener('click', e => { e.preventDefault(); properties(ctx.path); });
}

/** One file as it is; several entries or a folder as a zip file made while it downloads. */
function download(paths) {
  if (paths.length === 1) {
    const item = itemByPath(paths[0]);
    if (item?.kind === 'file') { location.assign(contentUrl(item.path, true)); return; }
  }

  // everything of the folder: the folder itself as one zip
  const all = items();
  const list = all.length > 1 && paths.length === all.length ? [ctx.path] : paths;
  const url = `/location-files/${ctx.id}/zip?` + list.map(p => 'path=' + q(p)).join('&');
  if (url.length > 7000) { toast(t('Locations.ZipTooMany'), 'danger'); return; }
  location.assign(url);
}

async function rename(item) {
  const name = await window.MatUi.prompt(t('Locations.Rename.Label'), { title: t('Locations.Rename'), value: item.name, ok: t('Locations.Rename') });
  if (!name || name === item.name) return;
  await act(live('rename'), { path: item.path, name }, r => t('Locations.Renamed', r.name));
}

async function remove(paths) {
  if (!ctx.canEdit || !paths.length) return;
  const one = paths.length === 1 ? itemByPath(paths[0]) : null;
  const message = one ? t(one.kind === 'folder' ? 'Locations.Delete.ConfirmFolder' : 'Locations.Delete.ConfirmOne', one.name) : t('Locations.Delete.ConfirmMany', paths.length);
  if (!await window.MatUi.confirm(message, { danger: true, ok: t('Locations.Delete') })) return;
  await act(live('delete'), { paths }, r => t('Locations.Deleted', r.count), t('Locations.Busy.Deleting'));
}

// ---- pickers: a folder of a location, of a drive ----------------------------------------------------------------------------------

/** The common frame of the pickers: a select of the places (locations or drives), the path as crumbs, the list of the folder. */
function picker({ places, placeLabel, current, start, rootIcon, load, row }) {
  const state = { place: current, path: start };
  const crumbs = el('nav', { class: 'md-picker__crumbs', 'aria-label': t('Locations.Picker.Where') });
  const list = el('ul', { class: 'md-picker__list', 'aria-label': t('Locations.Picker.Folders') });
  const select = el('select', { class: 'mat-select', id: uid('pick'), 'aria-label': placeLabel }, ...places.map(p => el('option', { value: p.id, text: p.name })));
  select.value = String(state.place.id);
  select.addEventListener('change', () => { state.place = places.find(p => String(p.id) === select.value); state.path = []; draw(); });

  async function draw() {
    crumbs.replaceChildren(
      el('button', { type: 'button', class: 'md-picker__crumb', onclick: () => { state.path = []; draw(); } }, icon(rootIcon), el('span', { text: state.place.name })),
      ...state.path.flatMap((p, i) => [el('span', { class: 'md-picker__sep', text: '›' }),
        el('button', { type: 'button', class: 'md-picker__crumb', onclick: () => { state.path = state.path.slice(0, i + 1); draw(); } }, el('span', { text: p.name }))]));
    list.replaceChildren(el('li', { class: 'md-muted', text: t('Locations.Loading') }));
    try {
      const entries = await load(state);
      const rows = entries.map(e => row(e, () => { state.path.push(e); draw(); }, state)).filter(Boolean);
      list.replaceChildren(...(rows.length ? rows : [el('li', { class: 'md-muted', text: t('Locations.Picker.NoFolders') })]));
      window.MatUi.refresh(list);
    } catch (e) {
      list.replaceChildren(el('li', { class: 'md-muted', text: problem(e) }));
    }
  }

  draw();
  const body = el('div', { class: 'md-picker' },
    places.length > 1 ? el('div', { class: 'mat-field' }, el('label', { class: 'mat-label', for: select.id, text: placeLabel }), select) : null, crumbs, list);
  return { state, body, draw };
}

function folderRow(entry, open) {
  const folderIcon = icon('folder');
  if (entry.colour) folderIcon.setAttribute('data-colour', entry.colour); // a folder of a drive may have a colour
  return el('li', {}, el('button', { type: 'button', class: 'md-picker__item', onclick: open }, folderIcon, el('span', { text: entry.name }), icon('chevron-right', 'md-picker__chev')));
}

/** Move or copy entries into a folder of this or another location the person may change. */
async function pick(action, paths) {
  let targets;
  try { targets = (await request('GET', '/live/locations/targets')).filter(l => l.canEdit); } catch (e) { toast(problem(e), 'danger'); return; }
  if (!targets.length) { toast(t('Locations.Picker.NoTargets'), 'danger'); return; }
  const here = targets.find(l => l.id === ctx.id);
  const start = here ? ctx.path.split('/').filter(Boolean).reduce((acc, name) => [...acc, { name, path: join(acc.length ? acc[acc.length - 1].path : '/', name) }], []) : [];
  const { state, body } = picker({
    places: targets, placeLabel: t('Locations.Picker.Location'), current: here || targets[0], start, rootIcon: 'server',
    load: s => request('GET', `/live/locations/${s.place.id}/folders?path=${q(s.path.length ? s.path[s.path.length - 1].path : '/')}`),
    // a folder that is moved or copied is not a place to put it
    row: (f, open, s) => (s.place.id === ctx.id && paths.includes(f.path) ? null : folderRow(f, open)),
  });
  const choice = await dialog({
    title: t(action === 'move' ? 'Locations.MoveTo' : 'Locations.CopyTo'), icon: action === 'move' ? 'folder-move' : 'copy', body, size: 'md',
    buttons: [{ label: t(action === 'move' ? 'Locations.MoveHere' : 'Locations.CopyHere'), value: 'ok', kind: 'primary' }, { label: t('Locations.Cancel'), value: 'cancel' }],
  });
  if (choice !== 'ok') return;
  const targetPath = state.path.length ? state.path[state.path.length - 1].path : '/';
  await act(live('transfer'), { paths, targetId: state.place.id, targetPath, move: action === 'move' },
    r => t(action === 'move' ? 'Locations.Moved' : 'Locations.Copied', r.count), t(action === 'move' ? 'Locations.Busy.Moving' : 'Locations.Busy.Copying'));
}

/** Copy entries into My Drive or a shared drive (a folder of it); the quota of the drive counts. */
async function toDrive(paths) {
  let drives;
  try { drives = (await request('GET', '/live/drive/drives')).filter(d => d.canEdit); } catch (e) { toast(problem(e), 'danger'); return; }
  if (!drives.length) { toast(t('Locations.NotAllowed'), 'danger'); return; }
  const { state, body } = picker({
    places: drives, placeLabel: t('Locations.Picker.Drive'), current: drives[0], start: [], rootIcon: 'hard-drive',
    load: s => request('GET', `/live/drive/tree?driveId=${s.place.id}${s.path.length ? '&parentId=' + s.path[s.path.length - 1].id : ''}`),
    row: (f, open) => folderRow(f, open),
  });
  const choice = await dialog({
    title: t('Locations.ToDrive'), icon: 'hard-drive', body, size: 'md',
    buttons: [{ label: t('Locations.CopyHere'), value: 'ok', kind: 'primary' }, { label: t('Locations.Cancel'), value: 'cancel' }],
  });
  if (choice !== 'ok') return;
  const where = [state.place.name, ...state.path.map(p => p.name)].join(' › ');
  const stop = busy(t('Locations.Busy.Copying'));
  try {
    const r = await request('POST', live('to-drive'), { paths, driveId: state.place.id, parentId: state.path.length ? state.path[state.path.length - 1].id : null });
    stop();
    toast(t('Locations.CopiedToDrive', r.count, where), 'success');
  } catch (e) {
    stop();
    toast(problem(e), 'danger');
  }
}

/** Copy files and folders of the drives into this folder: chosen one level at a time (the choice stays while browsing). */
async function fromDrive() {
  let drives;
  try { drives = await request('GET', '/live/drive/drives'); } catch (e) { toast(problem(e), 'danger'); return; }
  const chosen = new Map();
  const count = el('p', { class: 'md-loc-chosen', role: 'status', 'aria-live': 'polite' });
  const paint = () => { count.textContent = t('Locations.FromDrive.Chosen', chosen.size); };
  paint();
  const { body } = picker({
    places: drives, placeLabel: t('Locations.Picker.Drive'), current: drives[0], start: [], rootIcon: 'hard-drive',
    load: s => request('GET', `/live/locations/drive-items?driveId=${s.place.id}${s.path.length ? '&parentId=' + s.path[s.path.length - 1].id : ''}`),
    row: (item, open) => {
      const box = el('input', { type: 'checkbox', class: 'mat-check__input', checked: chosen.has(item.id), 'aria-label': t('Locations.SelectNamed', item.name) });
      box.addEventListener('change', () => { if (box.checked) chosen.set(item.id, item.name); else chosen.delete(item.id); paint(); });
      const fileIcon = el('span', { class: 'md-ficon md-ficon--' + (item.folder ? 'folder' : item.category) }, icon(item.folder ? 'folder' : iconOf(item.category)));
      if (item.folder && item.colour) fileIcon.firstChild.setAttribute('data-colour', item.colour);
      return el('li', { class: 'md-loc-pick' }, el('label', { class: 'md-loc-pick__check' }, box),
        item.folder
          ? el('button', { type: 'button', class: 'md-picker__item', onclick: open }, fileIcon, el('span', { text: item.name }), icon('chevron-right', 'md-picker__chev'))
          : el('span', { class: 'md-loc-pick__file' }, fileIcon, el('span', { text: item.name }), el('small', { class: 'md-muted', text: bytes(item.size) })));
    },
  });
  body.append(count);
  const choice = await dialog({
    title: t('Locations.FromDrive'), icon: 'hard-drive', body, size: 'md',
    buttons: [{ label: t('Locations.CopyHere'), value: 'ok', kind: 'primary' }, { label: t('Locations.Cancel'), value: 'cancel' }],
    validate: (form, value) => { if (value === 'ok' && !chosen.size) { count.textContent = t('Locations.FromDrive.None'); return false; } return true; },
  });
  if (choice !== 'ok') return;
  await act(live('from-drive'), { ids: [...chosen.keys()], path: ctx.path }, r => t('Locations.CopiedFromDrive', r.count), t('Locations.Busy.Copying'));
}

// ---- properties -------------------------------------------------------------------------------------------------------------------

/** Name, type, place, size, date, the kind of the location; owner, group and the nine rwx boxes where the protocol knows them (changeable by an editor). */
async function properties(path) {
  let p;
  try { p = await request('GET', live(`properties?path=${q(path)}`)); } catch (e) { toast(problem(e), 'danger'); return; }
  const facts = el('dl', { class: 'md-dl md-loc-facts' });
  const add = (label, value) => { if (value !== null && value !== undefined && value !== '') facts.append(el('dt', { text: label }), el('dd', {}, el('span', { text: value }))); };
  const title = p.name || ctx.name;
  add(t('Locations.Props.Name'), title);
  add(t('Locations.Props.Type'), p.folder ? t('Locations.Props.Folder') : `${t('Locations.Props.File')} · ${p.mime}`);
  add(t('Locations.Props.Where'), p.where);
  if (!p.folder) add(t('Locations.Props.Size'), t('Locations.Props.Bytes', bytes(p.size), Number(p.size).toLocaleString(lang())));
  if (p.items !== null && p.items !== undefined) add(t('Locations.Props.Items'), String(p.items));
  if (p.modified) add(t('Locations.Props.Modified'), stamp(p.modified));
  add(t('Locations.Props.Kind'), p.kind);
  if (p.link) add(t('Locations.Props.Link'), t('Locations.Props.LinkText'));
  if (p.hidden) add(t('Locations.Props.Hidden'), t('Locations.Props.HiddenText'));

  let grid = null;
  if (p.permissions) {
    if (p.permissions.owner || p.permissions.group) add(t('Locations.Props.Owner'), [p.permissions.owner, p.permissions.group].filter(Boolean).join(' · '));
    grid = permissionGrid(p.permissions.mode, p.canChange);
  }

  const canSave = grid && p.canChange;
  const choice = await dialog({
    title, icon: p.folder ? 'folder' : 'file', body: el('div', { class: 'md-loc-props' }, facts, grid?.node), size: 'md',
    buttons: canSave ? [{ label: t('Locations.Save'), value: 'save', kind: 'primary', icon: 'save' }, { label: t('Locations.Close'), value: 'cancel' }]
      : [{ label: t('Locations.Close'), value: 'ok', kind: 'primary' }],
  });
  if (choice !== 'save' || grid.mode() === p.permissions.mode) return;
  try {
    const r = await request('POST', live('permissions'), { path, mode: grid.mode() });
    toast(t('Locations.Permissions.Saved', r.octal, r.symbols), 'success');
  } catch (e) {
    toast(problem(e), 'danger');
  }
}

/** Nine boxes like chmod (owner, group, others × read, write, execute) and the number to read along (755 · rwxr-xr-x). */
function permissionGrid(mode, canChange) {
  const who = [t('Locations.Perm.Owner'), t('Locations.Perm.Group'), t('Locations.Perm.Others')];
  const what = [t('Locations.Perm.Read'), t('Locations.Perm.Write'), t('Locations.Perm.Execute')];
  const boxes = [];
  const symbols = m => [...'rwxrwxrwx'].map((c, i) => ((m & (1 << (8 - i))) ? c : '-')).join('');
  const table = el('table', { class: 'md-perm' },
    el('caption', { class: 'mat-sr-only', text: t('Locations.Perm.Title') }),
    el('thead', {}, el('tr', {}, el('th', { scope: 'col' }, el('span', { class: 'mat-sr-only', text: t('Locations.Perm.Who') })), ...what.map(w => el('th', { scope: 'col', text: w })))),
    el('tbody', {}, ...who.map((label, r) => el('tr', {}, el('th', { scope: 'row', text: label }), ...what.map((w, c) => {
      const bit = 8 - (r * 3 + c);
      const box = el('input', { type: 'checkbox', class: 'mat-check__input', checked: (mode & (1 << bit)) !== 0, disabled: !canChange, 'aria-label': `${label}: ${w}` });
      box.dataset.bit = String(bit);
      boxes.push(box);
      return el('td', { 'data-label': w }, box);
    })))));
  const value = el('p', { class: 'md-perm__mode mat-mono' });
  const current = () => boxes.reduce((m, b) => (b.checked ? m | (1 << Number(b.dataset.bit)) : m), 0);
  const paint = () => { const m = current(); value.textContent = `${m.toString(8).padStart(3, '0')} · ${symbols(m)}`; };
  boxes.forEach(b => b.addEventListener('change', paint));
  paint();
  const node = el('section', { class: 'md-perm-box', 'aria-label': t('Locations.Perm.Title') }, el('h3', { class: 'md-perm__title', text: t('Locations.Perm.Title') }), table, value);
  return { node, mode: current };
}

// ---- the search below the folder --------------------------------------------------------------------------------------------------

/** Names (and on request the text of text files) in this folder and every folder below it; the hits come while the server looks. */
async function deepSearch() {
  const field = uid('loc-q');
  const input = el('input', { class: 'mat-input', type: 'search', id: field, name: 'q', autocomplete: 'off', enterkeyhint: 'search', minlength: 2, placeholder: t('Locations.Search.Placeholder') });
  const content = el('input', { type: 'checkbox', class: 'mat-check__input', name: 'content' });
  const hidden = el('input', { type: 'checkbox', class: 'mat-check__input', name: 'hidden' });
  const status = el('p', { class: 'md-loc-search__status md-muted', role: 'status', 'aria-live': 'polite', text: t('Locations.Search.Where', ctx.name + (ctx.path === '/' ? '' : ctx.path)) });
  const stop = el('button', { type: 'button', class: 'mat-btn mat-btn--secondary mat-btn--sm', hidden: true }, icon('stop'), el('span', { text: t('Locations.Search.Stop') }));
  const list = el('ul', { class: 'md-loc-search__list', 'aria-label': t('Locations.Search.Hits') });
  const body = el('div', { class: 'md-loc-search' },
    el('div', { class: 'mat-field' }, el('label', { class: 'mat-label', for: field, text: t('Locations.Search.Label') }), input),
    el('div', { class: 'md-loc-search__options' },
      el('label', { class: 'mat-check' }, content, el('span', { class: 'mat-check__text', text: t('Locations.Search.Content') })),
      el('label', { class: 'mat-check' }, hidden, el('span', { class: 'mat-check__text', text: t('Locations.Search.Hidden') }))),
    el('div', { class: 'md-loc-search__bar' }, status, stop), list);

  let controller = null;
  let hits = 0;
  stop.addEventListener('click', () => controller?.abort());

  const hitRow = hit => {
    const parent = hit.folder || '/';
    const open = el('button', { type: 'button', class: 'md-loc-hit__open' },
      el('span', { class: 'md-ficon md-ficon--' + hit.category }, icon(hit.isFolder ? 'folder' : iconOf(hit.category))),
      el('span', { class: 'md-loc-hit__text' }, el('span', { class: 'md-loc-hit__name', text: hit.name }), el('small', { class: 'md-muted mat-mono', text: parent }),
        hit.snippet ? el('small', { class: 'md-loc-hit__snippet', text: hit.snippet }) : null));
    open.addEventListener('click', () => {
      if (hit.isFolder) { location.assign(browseUrl(ctx.id, hit.path)); return; }
      preview({ path: hit.path, kind: 'file', name: hit.name, category: hit.category, preview: hit.preview, text: hit.text, size: hit.size }, []);
    });
    const show = el('a', { class: 'mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm', href: browseUrl(ctx.id, parent) + '?q=' + q(hit.name), 'aria-label': t('Locations.Search.ShowInFolder', hit.name), 'data-tip': t('Locations.Search.ShowInFolder', hit.name) }, icon('folder-open'));
    return el('li', { class: 'md-loc-hit' }, open, show);
  };

  const handle = ev => {
    if (ev.type === 'progress') {
      status.textContent = t('Locations.Search.Progress', ev.hits, ev.folders, ev.files, ev.current || '/');
    } else if (ev.type === 'hit' && ev.hit) {
      hits = ev.hits;
      list.append(hitRow(ev.hit));
    } else if (ev.type === 'done') {
      status.textContent = ev.error || (ev.truncated ? t('Locations.Search.Truncated', ev.hits) : ev.hits ? t('Locations.Search.Done', ev.hits, ev.folders, ev.files) : t('Locations.Search.Nothing', ev.folders, ev.files));
    }
  };

  async function run() {
    const text = input.value.trim();
    if (text.length < 2) { status.textContent = t('Locations.Search.TooShort'); input.focus(); return; }
    controller?.abort();
    const mine = new AbortController();
    controller = mine;
    hits = 0;
    list.replaceChildren();
    stop.hidden = false;
    status.textContent = t('Locations.Search.Running');
    const url = live(`search?path=${q(ctx.path)}&q=${q(text)}${content.checked ? '&content=true' : ''}${hidden.checked ? '&hidden=true' : ''}`);
    try {
      const response = await fetch(url, { headers: { Accept: 'application/x-ndjson' }, credentials: 'same-origin', cache: 'no-store', signal: mine.signal });
      if (!response.ok) { const err = new Error('HTTP ' + response.status); err.status = response.status; throw err; }
      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let buffer = '';
      for (;;) {
        const { value, done } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });
        let nl;
        while ((nl = buffer.indexOf('\n')) >= 0) {
          const line = buffer.slice(0, nl).trim();
          buffer = buffer.slice(nl + 1);
          if (line) handle(JSON.parse(line));
        }
      }
    } catch (e) {
      if (e.name === 'AbortError') { if (controller === mine) status.textContent = t('Locations.Search.Stopped', hits); }
      else status.textContent = e.status !== undefined ? problem(e) : t('Locations.Failed');
    } finally {
      if (controller === mine) stop.hidden = true;
    }
  }

  await dialog({
    title: t('Locations.Search.Title'), icon: 'search', body, size: 'lg',
    buttons: [{ label: t('Locations.Search.Go'), value: 'search', kind: 'primary', icon: 'search' }, { label: t('Locations.Close'), value: 'cancel' }],
    onOpen: () => input.focus(),
    validate: (form, value) => { if (value === 'search') { run(); return false; } return true; },
  });
  controller?.abort();
}

/** The icon of a kind of file (as FileTypes.IconOf on the server). */
function iconOf(category) {
  return { folder: 'folder', document: 'file-doc', spreadsheet: 'file-sheet', presentation: 'file-slides', pdf: 'file-pdf', image: 'image', video: 'file-video', audio: 'file-audio', archive: 'archive', text: 'file-text', code: 'code' }[category] || 'file';
}

// ---- preview ----------------------------------------------------------------------------------------------------------------------

/** A file shown in the page (image, video, audio, PDF, text); the arrows go to the previous and next file of the folder. */
function preview(item, among) {
  const all = among && among.length ? among : (among ? [item] : items().filter(i => i.kind === 'file'));
  let index = Math.max(0, all.findIndex(i => i.path === item.path));
  if (!all.length) all.push(item);
  const stage = el('div', { class: 'md-preview__stage' });
  const name = el('h2', { class: 'md-preview__name' });
  const tools = el('div', { class: 'md-preview__tools' });
  const prev = el('button', { type: 'button', class: 'md-preview__nav md-preview__nav--prev', 'aria-label': t('Locations.Previous') }, icon('chevron-left'));
  const next = el('button', { type: 'button', class: 'md-preview__nav md-preview__nav--next', 'aria-label': t('Locations.Next') }, icon('chevron-right'));
  const close = el('button', { type: 'button', class: 'mat-btn mat-btn--ghost mat-btn--icon md-preview__close', 'aria-label': t('Locations.Close') }, icon('x'));
  const box = el('dialog', { class: 'md-preview', 'aria-label': t('Locations.Preview') }, el('header', { class: 'md-preview__head' }, name, tools, close), stage, prev, next);
  document.body.append(box);

  const show = () => {
    const it = all[index];
    name.textContent = it.name;
    prev.hidden = index <= 0;
    next.hidden = index >= all.length - 1;
    // replaceChildren writes a null as the text "null": only real nodes go in
    tools.replaceChildren(...[
      it.text ? el('a', { class: 'mat-btn mat-btn--secondary mat-btn--sm', href: editUrl(it.path) }, icon('file-text'), el('span', { text: t('Locations.EditText') })) : null,
      el('a', { class: 'mat-btn mat-btn--secondary mat-btn--icon mat-btn--sm', href: contentUrl(it.path, true), 'aria-label': t('Locations.Download'), 'data-tip': t('Locations.Download') }, icon('download')),
    ].filter(Boolean));
    const src = contentUrl(it.path, false);
    stage.replaceChildren();
    switch (it.preview) {
      case 'image': stage.append(el('img', { src, alt: it.name, class: 'md-preview__image' })); break;
      case 'video': stage.append(el('video', { src, controls: true, autoplay: true, class: 'md-preview__media' })); break;
      case 'audio': stage.append(el('div', { class: 'md-preview__audio' }, icon('file-audio', 'md-preview__big'), el('audio', { src, controls: true, autoplay: true }))); break;
      case 'pdf': stage.append(el('iframe', { src, title: it.name, class: 'md-preview__pdf' })); break;
      case 'text': {
        const pre = el('pre', { class: 'md-preview__text' }, el('code', { text: t('Locations.Loading') }));
        stage.append(pre);
        request('GET', live(`text?path=${q(it.path)}`)).then(r => { pre.firstChild.textContent = r.text; }).catch(e => { pre.firstChild.textContent = problem(e); });
        break;
      }
      default:
        stage.append(el('div', { class: 'md-preview__none' }, el('span', { class: 'md-ficon md-ficon--' + it.category }, icon(iconOf(it.category), 'md-preview__big')),
          el('p', { text: t('Locations.Preview.None') }),
          el('div', { class: 'mat-btn-row mat-btn-row--plain md-center' },
            el('a', { class: 'mat-btn mat-btn--primary', href: contentUrl(it.path, true) }, icon('download'), el('span', { text: t('Locations.Download') })))));
    }
  };

  prev.addEventListener('click', () => { if (index > 0) { index--; show(); } });
  next.addEventListener('click', () => { if (index < all.length - 1) { index++; show(); } });
  close.addEventListener('click', () => box.close());
  box.addEventListener('keydown', e => {
    if (e.key === 'ArrowLeft' && index > 0) { index--; show(); }
    if (e.key === 'ArrowRight' && index < all.length - 1) { index++; show(); }
  });
  box.addEventListener('close', () => box.remove());
  show();
  box.showModal();
}

// ---- keys -------------------------------------------------------------------------------------------------------------------------

function wireKeys() {
  document.addEventListener('keydown', e => {
    if (e.target.closest('input, textarea, select, [contenteditable], dialog')) return;
    const paths = selectedPaths();
    if (e.key === 'Delete' && paths.length && ctx.canEdit) {
      e.preventDefault();
      remove(paths);
    } else if (e.key === 'F2' && paths.length === 1 && ctx.canEdit) {
      e.preventDefault();
      const item = itemByPath(paths[0]);
      if (item) rename(item);
    }
  });
}

// ---- the text editor (Pages/Locations/Edit) ---------------------------------------------------------------------------------------

/** Keeps Tab as a tab, shows line and column, saves with Ctrl+S or the button (with the ETag it started from: a file changed meanwhile is not overwritten). */
function wireEditor() {
  const area = editor.querySelector('textarea');
  const save = editor.querySelector('[data-editor-save]');
  const state = editor.querySelector('[data-editor-state]');
  const pos = editor.querySelector('[data-editor-pos]');
  const wrap = editor.querySelector('[data-editor-wrap]');
  const id = Number(editor.dataset.location);
  const path = editor.dataset.path;
  const canEdit = editor.dataset.canEdit === 'true';
  let etag = editor.dataset.etag;
  let saved = area.value;

  const dirty = () => area.value !== saved;
  const paint = () => {
    if (save) save.disabled = !dirty();
    state.textContent = dirty() ? t('Locations.Editor.Unsaved') : '';
  };
  const where = () => {
    const before = area.value.slice(0, area.selectionStart);
    const line = before.split('\n').length;
    const col = area.selectionStart - before.lastIndexOf('\n');
    pos.textContent = t('Locations.Editor.Position', line, col);
  };

  try { wrap.checked = localStorage.getItem('md.editor.wrap') === '1'; } catch { /* private mode */ }
  const applyWrap = () => { area.style.whiteSpace = wrap.checked ? 'pre-wrap' : 'pre'; try { localStorage.setItem('md.editor.wrap', wrap.checked ? '1' : '0'); } catch { /* nothing */ } };
  wrap.addEventListener('change', applyWrap);
  applyWrap();
  paint();
  where();
  if (canEdit) area.focus();

  async function store() {
    if (!canEdit || !dirty()) return;
    const text = area.value;
    state.textContent = t('Locations.Editor.Saving');
    try {
      const r = await request('PUT', `/live/locations/${id}/text`, { path, text, etag });
      etag = r.etag;
      saved = text;
      paint();
      state.textContent = t('Locations.Editor.Saved');
      toast(t('Locations.Editor.Saved'), 'success');
    } catch (e) {
      state.textContent = e.status === 409 ? t('Locations.Editor.Conflict') : problem(e);
      toast(e.status === 409 ? t('Locations.Editor.Conflict') : problem(e), 'danger');
    }
  }

  area.addEventListener('input', paint);
  area.addEventListener('keyup', where);
  area.addEventListener('click', where);
  area.addEventListener('keydown', e => {
    if ((e.ctrlKey || e.metaKey) && (e.key === 's' || e.key === 'S')) { e.preventDefault(); store(); return; }
    if (e.key === 'Tab' && !e.ctrlKey && !e.altKey && !e.shiftKey && canEdit) {
      e.preventDefault();
      area.setRangeText('\t', area.selectionStart, area.selectionEnd, 'end');
      paint();
    }
  });
  save?.addEventListener('click', store);
  window.addEventListener('beforeunload', e => { if (dirty()) { e.preventDefault(); e.returnValue = ''; } });
}
