/* MatDrive – the explorer (Pages/Drive/Index): uploads (files and whole folders, by button and by drag & drop, large files in pieces, with progress), the menu of a
   row (also the right mouse button), the actions for the selection, the preview of files (with the previous and next file), moving and copying with a folder
   picker (also copying into a folder of a storage location), renaming, new folders and new documents of MatOffice. Every change goes to /live/drive (business
   layer; a copy to a storage location to /live/locations); the list is drawn by the server and loaded again after a change. */
import { t, el, icon, request, toast, dialog, token, busy } from './util.js';

const root = document.getElementById('md-explorer');
const SMALL = 8 * 1024 * 1024;
const PIECE = 8 * 1024 * 1024;

const ctx = root ? {
  drive: Number(root.dataset.drive),
  folder: root.dataset.folder ? Number(root.dataset.folder) : null,
  mode: root.dataset.mode,
  canAdd: root.dataset.canAdd === 'true',
  toLocation: root.dataset.toLocation === 'true',
  office: JSON.parse(root.dataset.office || '[]'),
} : null;

if (root) {
  window.mdExplorer = { canAdd: ctx.canAdd, create };
  flashAfterReload();
  wireRows();
  wireBulk();
  wireDrop();
  wireKeys();
  const wanted = new URLSearchParams(location.search).get('new');
  if (wanted) {
    history.replaceState(null, '', location.pathname);
    if (ctx.canAdd) setTimeout(() => create(wanted), 50);
  }
}

// ---- helpers ------------------------------------------------------------------------------------------------------------------

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
  if (e.status === 0) return t('Drive.Offline');
  return e.body?.error || (e.status === 404 ? t('Drive.NotAllowed') : t('Drive.Failed'));
}

async function act(url, body, done) {
  try {
    const r = await request('POST', url, body);
    reload(done ? done(r) : null);
  } catch (e) {
    toast(problem(e), 'danger');
  }
}

function rowOf(node) { return node.closest('[data-node]'); }

function itemOf(row) {
  return {
    id: Number(row.dataset.node), kind: row.dataset.kind, name: row.dataset.name, category: row.dataset.category, preview: row.dataset.preview,
    office: row.dataset.office === '1', starred: row.dataset.starred === '1', text: row.dataset.text === '1', row,
  };
}

function selectedIds() {
  return [...new Set([...root.querySelectorAll('[data-select-row]:checked')].map(c => Number(c.value)))];
}

function items() {
  const seen = new Set();
  return [...root.querySelectorAll('[data-view="list"] [data-node], .md-dropzone-host:not([data-view]) [data-node]')].map(itemOf).filter(i => !seen.has(i.id) && seen.add(i.id));
}

// ---- "New" ----------------------------------------------------------------------------------------------------------------------

async function create(what) {
  if (what === 'folder') {
    const name = await window.MatUi.prompt(t('Drive.NewFolder.Label'), { title: t('Drive.NewFolder'), value: t('Drive.NewFolder.Default'), ok: t('Drive.Create') });
    if (!name) return;
    await act('/live/drive/folders', { driveId: ctx.drive, parentId: ctx.folder, name }, r => t('Drive.Created', r.name));
  } else if (what === 'files' || what === 'dir') {
    const input = el('input', { type: 'file', multiple: true, hidden: true });
    if (what === 'dir') { input.webkitdirectory = true; input.setAttribute('webkitdirectory', ''); }
    input.addEventListener('change', () => {
      const files = [...input.files].map(f => ({ file: f, path: f.webkitRelativePath || f.name }));
      input.remove();
      if (files.length) upload(files, ctx.folder);
    });
    document.body.append(input);
    input.click();
  } else if (what.startsWith('office:')) {
    try {
      const r = await request('POST', '/live/drive/office/new', { kind: what.slice(7), driveId: ctx.drive, parentId: ctx.folder });
      window.open(r.open, '_blank', 'noopener');
      reload(t('Drive.Created', r.name));
    } catch (e) {
      toast(problem(e), 'danger');
    }
  }
}

// ---- uploads ------------------------------------------------------------------------------------------------------------------

let panel = null;

function uploadPanel() {
  if (panel) return panel;
  const list = el('ul', { class: 'md-uploads__list' });
  const title = el('strong', { class: 'md-uploads__title' });
  const close = el('button', { type: 'button', class: 'mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm', 'aria-label': t('Drive.Close'), onclick: () => { panel.node.hidden = true; } }, icon('x'));
  const node = el('section', { class: 'md-uploads', role: 'status', 'aria-live': 'polite' }, el('div', { class: 'md-uploads__head' }, title, close), list);
  document.body.append(node);
  panel = { node, list, title };
  return panel;
}

/** Uploads files ({ file, path }) into a folder: three at a time, small ones in one request, large ones in pieces. */
async function upload(entries, folderId) {
  const p = uploadPanel();
  p.node.hidden = false;
  let done = 0;
  let failed = 0;
  const total = entries.length;
  const paint = () => { p.title.textContent = done + failed < total ? t('Drive.Uploading', done + failed, total) : failed ? t('Drive.UploadedWithErrors', done, failed) : t('Drive.Uploaded', done); };
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
        await uploadOne(r.entry, folderId, pct => { r.bar.value = pct; });
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
  if (!failed) reload(t('Drive.Uploaded', done));
  else if (done) setTimeout(() => reload(), 2500);
}

function uploadOne({ file, path }, folderId, progress) {
  return file.size <= SMALL ? uploadSmall(file, path, folderId, progress) : uploadPieces(file, path, folderId, progress);
}

function uploadSmall(file, path, folderId, progress) {
  return new Promise((resolve, reject) => {
    const form = new FormData();
    form.append('driveId', ctx.drive);
    if (folderId) form.append('parentId', folderId);
    form.append('path', path);
    form.append('file', file, file.name);
    const xhr = new XMLHttpRequest();
    xhr.open('POST', '/live/drive/upload');
    xhr.setRequestHeader('RequestVerificationToken', token());
    xhr.upload.onprogress = e => { if (e.lengthComputable) progress(Math.round(e.loaded / e.total * 100)); };
    xhr.onload = () => {
      let body = null;
      try { body = JSON.parse(xhr.responseText); } catch { body = null; }
      if (xhr.status >= 200 && xhr.status < 300 && Array.isArray(body) && !body.some(b => b.error)) resolve(body);
      else { const err = new Error('upload'); err.status = xhr.status; err.body = { error: Array.isArray(body) ? body.find(b => b.error)?.error : body?.error }; reject(err); }
    };
    xhr.onerror = () => { const err = new Error('network'); err.status = 0; reject(err); };
    xhr.send(form);
  });
}

async function uploadPieces(file, path, folderId, progress) {
  const start = await request('POST', '/live/drive/uploads', { driveId: ctx.drive, parentId: folderId, path, size: file.size });
  let offset = 0;
  while (offset < file.size) {
    const piece = file.slice(offset, offset + PIECE);
    let tries = 0;
    for (;;) {
      try {
        const r = await request('PUT', `/live/drive/uploads/${start.token}?offset=${offset}`, piece);
        offset = r.received;
        break;
      } catch (e) {
        if (++tries >= 4 || (e.status && e.status < 500)) throw e;
        await new Promise(res => setTimeout(res, 1500 * tries));
      }
    }
    progress(Math.round(offset / file.size * 100));
  }
  return request('POST', `/live/drive/uploads/${start.token}/finish`);
}

// ---- drag & drop ----------------------------------------------------------------------------------------------------------------

function wireDrop() {
  let depth = 0;
  const overlay = el('div', { class: 'md-drop', hidden: true }, icon('upload'), el('span', { text: t('Drive.DropHere') }));
  root.append(overlay);
  const isFiles = e => [...(e.dataTransfer?.types || [])].includes('Files');

  root.addEventListener('dragenter', e => {
    if (!isFiles(e) || !ctx.canAdd) return;
    depth++;
    overlay.hidden = false;
  });
  root.addEventListener('dragleave', e => {
    if (!isFiles(e)) return;
    depth = Math.max(0, depth - 1);
    if (!depth) overlay.hidden = true;
  });
  root.addEventListener('dragover', e => {
    if (isFiles(e) && ctx.canAdd) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; }
    const target = e.target.closest('[data-drop-folder]');
    document.querySelectorAll('.md-drop-target').forEach(n => { if (n !== target) n.classList.remove('md-drop-target'); });
    if (target && (isFiles(e) || dragged)) { e.preventDefault(); target.classList.add('md-drop-target'); }
  });
  root.addEventListener('drop', async e => {
    overlay.hidden = true;
    depth = 0;
    const target = e.target.closest('[data-drop-folder]');
    document.querySelectorAll('.md-drop-target').forEach(n => n.classList.remove('md-drop-target'));
    if (isFiles(e)) {
      if (!ctx.canAdd && !target) return;
      e.preventDefault();
      const entries = await filesOf(e.dataTransfer);
      if (entries.length) upload(entries, target ? Number(target.dataset.dropFolder) : ctx.folder);
      return;
    }

    if (dragged && target) {
      e.preventDefault();
      const ids = dragged.filter(id => id !== Number(target.dataset.dropFolder));
      dragged = null;
      if (ids.length) await act('/live/drive/move', { ids, driveId: ctx.drive, parentId: Number(target.dataset.dropFolder) }, r => t('Drive.Moved', r.count));
    }
  });

  // moving items by dragging them onto a folder of the list or of the path above
  let dragged = null;
  root.addEventListener('dragstart', e => {
    const row = e.target.closest?.('[data-node]');
    if (!row) return;
    const id = Number(row.dataset.node);
    const sel = selectedIds();
    dragged = sel.includes(id) ? sel : [id];
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('text/plain', row.dataset.name);
  });
  root.addEventListener('dragend', () => { dragged = null; });
  document.querySelectorAll('.mat-crumbs a').forEach(a => {
    const m = a.getAttribute('href').match(/^\/drive(?:\/folder\/(\d+)|\/shared-drive\/(\d+))?$/);
    if (!m) return;
    a.addEventListener('dragover', e => { if (dragged) { e.preventDefault(); a.classList.add('md-drop-target'); } });
    a.addEventListener('dragleave', () => a.classList.remove('md-drop-target'));
    a.addEventListener('drop', async e => {
      if (!dragged) return;
      e.preventDefault();
      a.classList.remove('md-drop-target');
      const ids = dragged;
      dragged = null;
      await act('/live/drive/move', { ids, driveId: m[2] ? Number(m[2]) : ctx.drive, parentId: m[1] ? Number(m[1]) : null }, r => t('Drive.Moved', r.count));
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

// ---- rows: open, menu ---------------------------------------------------------------------------------------------------------

function wireRows() {
  root.addEventListener('click', e => {
    const open = e.target.closest('[data-open]');
    if (open && !e.ctrlKey && !e.metaKey && !e.shiftKey) {
      const row = rowOf(open);
      if (row && row.dataset.kind === 'file' && ctx.mode !== 'trash') {
        e.preventDefault();
        openFile(itemOf(row));
        return;
      }
    }

    const menu = e.target.closest('[data-row-menu]');
    if (menu) { e.preventDefault(); rowMenu(itemOf(rowOf(menu)), menu); return; }

    const rowAct = e.target.closest('[data-row-act]');
    if (rowAct) { e.preventDefault(); bulk(rowAct.dataset.rowAct, [Number(rowOf(rowAct).dataset.node)]); }
  });

  root.addEventListener('contextmenu', e => {
    const row = e.target.closest('[data-node]');
    if (!row || e.target.closest('input, a[href^="/office"]')) return;
    e.preventDefault();
    rowMenu(itemOf(row), null, e.clientX, e.clientY);
  });

  root.addEventListener('dblclick', e => {
    const row = e.target.closest('[data-node]');
    if (!row || e.target.closest('input, button, a')) return;
    const item = itemOf(row);
    if (item.kind === 'folder') location.assign('/drive/folder/' + item.id);
    else openFile(item);
  });
}

/** Opens a file the way it is best shown: MatOffice for office files, else the preview. */
function openFile(item) {
  if (item.office) { window.open('/office/open/' + item.id, '_blank', 'noopener'); return; }
  preview(item);
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
  const trash = ctx.mode === 'trash';
  const entries = trash ? [
    ['history', t('Drive.Restore'), () => bulk('restore', [item.id])],
    '-',
    ['trash', t('Drive.DeleteForever'), () => bulk('delete', [item.id]), true],
  ] : [
    item.kind === 'folder' ? ['folder-open', t('Drive.Open'), () => location.assign('/drive/folder/' + item.id)] : ['eye', t('Drive.Preview'), () => preview(item)],
    item.office ? ['external', t('Drive.OpenInOffice'), () => window.open('/office/open/' + item.id, '_blank', 'noopener')] : null,
    item.text ? ['edit', t('Drive.EditText'), () => location.assign('/drive/edit/' + item.id)] : null,
    ['download', t('Drive.Download'), () => download([item.id], item)],
    '-',
    ['share', t('Drive.Share'), () => location.assign(`/drive/item/${item.id}/sharing`)],
    ['link', t('Drive.GetLink'), () => location.assign(`/drive/item/${item.id}/link`)],
    '-',
    ['edit', t('Drive.Rename'), () => rename(item)],
    ['folder-move', t('Drive.MoveTo'), () => pick('move', [item.id])],
    ['copy', t('Drive.CopyTo'), () => pick('copy', [item.id])],
    ctx.toLocation ? ['server', t('Drive.CopyToLocation'), () => pick('location', [item.id])] : null,
    [item.starred ? 'star-filled' : 'star', t(item.starred ? 'Drive.Unstar' : 'Drive.Star'), () => act('/live/drive/star', { id: item.id, starred: !item.starred })],
    item.name.toLowerCase().endsWith('.zip') ? ['archive', t('Drive.Unzip'), () => act('/live/drive/unzip', { id: item.id }, r => t('Drive.Unzipped', r.name))] : null,
    '-',
    ['info', t('Drive.Details'), () => location.assign('/drive/item/' + item.id)],
    item.kind === 'file' ? ['history', t('Drive.Versions'), () => location.assign(`/drive/item/${item.id}/versions`)] : null,
    '-',
    ['trash', t('Drive.MoveToTrash'), () => bulk('trash', [item.id]), true],
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

// ---- actions --------------------------------------------------------------------------------------------------------------------

function wireBulk() {
  root.addEventListener('click', e => {
    const b = e.target.closest('[data-act]');
    if (!b) return;
    e.preventDefault();
    if (b.dataset.act === 'empty-trash') { emptyTrash(); return; }
    const ids = selectedIds();
    if (ids.length) bulk(b.dataset.act, ids);
  });
}

async function bulk(action, ids) {
  switch (action) {
    case 'download': return download(ids);
    case 'move': return pick('move', ids);
    case 'copy': return pick('copy', ids);
    case 'to-location': return pick('location', ids);
    case 'star':
      for (const id of ids) { try { await request('POST', '/live/drive/star', { id, starred: true }); } catch { /* the others still */ } }
      return reload(t('Drive.StarredCount', ids.length));
    case 'trash':
      return act('/live/drive/trash', { ids }, r => t('Drive.Trashed', r.count));
    case 'restore':
      return act('/live/drive/restore', { ids }, r => t('Drive.Restored', r.count));
    case 'delete':
      if (!await window.MatUi.confirm(t('Drive.DeleteForever.Confirm', ids.length), { danger: true, ok: t('Drive.DeleteForever') })) return;
      return act('/live/drive/delete', { ids }, () => t('Drive.Deleted'));
  }
}

async function emptyTrash() {
  const ids = items().map(i => i.id);
  if (!await window.MatUi.confirm(t('Drive.EmptyTrash.Confirm'), { danger: true, ok: t('Drive.EmptyTrash') })) return;
  await act('/live/drive/delete', { ids }, () => t('Drive.Deleted'));
}

function download(ids, item) {
  if (ids.length === 1 && item?.kind === 'file') location.assign(`/files/${ids[0]}/content?download=true`);
  else if (ids.length === 1 && !item) {
    const row = root.querySelector(`[data-node="${ids[0]}"]`);
    location.assign(row?.dataset.kind === 'file' ? `/files/${ids[0]}/content?download=true` : `/files/zip?ids=${ids.join(',')}`);
  } else location.assign(`/files/zip?ids=${ids.join(',')}`);
}

async function rename(item) {
  const name = await window.MatUi.prompt(t('Drive.Rename.Label'), { title: t('Drive.Rename'), value: item.name, ok: t('Drive.Rename') });
  if (!name || name === item.name) return;
  await act('/live/drive/rename', { id: item.id, name }, r => t('Drive.Renamed', r.name));
}

// ---- the folder picker (move / copy in the drives, copy to a storage location) --------------------------------------------------

/**
 * Where the picker puts things: a folder of a drive (by id) or of a storage location the person may change (by path, the business layer of the locations).
 * Each source lists its places, the folders below a folder (null = the top) and makes a new folder there.
 */
const sources = {
  drive: {
    label: 'Drive.Picker.Drive', icon: 'hard-drive',
    places: () => request('GET', '/live/drive/drives'),
    folders: (place, at) => request('GET', `/live/drive/tree?driveId=${place.id}${at ? '&parentId=' + at.id : ''}`),
    create: (place, at, name) => request('POST', '/live/drive/folders', { driveId: place.id, parentId: at ? at.id : null, name }),
  },
  location: {
    label: 'Drive.Picker.Location', icon: 'server',
    places: async () => (await request('GET', '/live/locations/targets')).filter(l => l.canEdit),
    folders: (place, at) => request('GET', `/live/locations/${place.id}/folders?path=${encodeURIComponent(at ? at.path : '/')}`),
    create: (place, at, name) => request('POST', `/live/locations/${place.id}/folders`, { path: at ? at.path : '/', name }),
  },
};

/** Move or copy items into a folder of a drive ('move', 'copy'), or copy them into a folder of a storage location ('location'). */
async function pick(action, ids) {
  const toLocation = action === 'location';
  const source = toLocation ? sources.location : sources.drive;
  let places;
  try { places = await source.places(); } catch (e) { toast(problem(e), 'danger'); return; }
  if (!places.length) { toast(t(toLocation ? 'Drive.Picker.NoLocations' : 'Drive.NotAllowed'), 'danger'); return; }
  const state = { place: (!toLocation && places.find(p => p.id === ctx.drive)) || places[0], path: [] };
  const at = () => (state.path.length ? state.path[state.path.length - 1] : null);
  const crumbs = el('nav', { class: 'md-picker__crumbs', 'aria-label': t('Drive.Picker.Where') });
  const list = el('ul', { class: 'md-picker__list', role: 'listbox', 'aria-label': t('Drive.Picker.Folders') });
  const select = el('select', { class: 'mat-input', 'aria-label': t(source.label) }, ...places.map(p => el('option', { value: p.id, text: p.name })));
  select.value = state.place.id;
  select.addEventListener('change', () => { state.place = places.find(p => p.id === Number(select.value)); state.path = []; draw(); });

  async function draw() {
    crumbs.replaceChildren(
      el('button', { type: 'button', class: 'md-picker__crumb', onclick: () => { state.path = []; draw(); } }, icon(source.icon), el('span', { text: state.place.name })),
      ...state.path.flatMap((p, i) => [el('span', { class: 'md-picker__sep', text: '›' }), el('button', { type: 'button', class: 'md-picker__crumb', onclick: () => { state.path = state.path.slice(0, i + 1); draw(); } }, el('span', { text: p.name }))]));
    list.replaceChildren(el('li', { class: 'md-muted', text: t('Drive.Loading') }));
    try {
      const folders = await source.folders(state.place, at());
      // a folder that is moved or copied inside the drives is not a place to put it
      const usable = toLocation ? folders : folders.filter(f => !ids.includes(f.id));
      list.replaceChildren(...(usable.length ? usable.map(f => {
        const folderIcon = icon('folder');
        if (f.colour) folderIcon.setAttribute('data-colour', f.colour);
        return el('li', {}, el('button', { type: 'button', class: 'md-picker__item', onclick: () => { state.path.push(f); draw(); } }, folderIcon, el('span', { text: f.name }), icon('chevron-right', 'md-picker__chev')));
      }) : [el('li', { class: 'md-muted', text: t('Drive.Picker.NoFolders') })]));
      window.MatUi.refresh(list);
    } catch (e) {
      list.replaceChildren(el('li', { class: 'md-muted', text: problem(e) }));
    }
  }

  // a new folder where the picker stands, entered at once
  const create = async () => {
    const name = await window.MatUi.prompt(t('Drive.NewFolder.Label'), { title: t('Drive.NewFolder'), value: t('Drive.NewFolder.Default'), ok: t('Drive.Create') });
    if (!name) return;
    try {
      state.path.push(await source.create(state.place, at(), name));
      draw();
    } catch (e) {
      toast(problem(e), 'danger');
    }
  };

  draw();
  const body = el('div', { class: 'md-picker' }, places.length > 1 ? el('div', { class: 'mat-field' }, el('label', { class: 'mat-label', text: t(source.label) }), select) : null, crumbs, list,
    el('button', { type: 'button', class: 'mat-btn mat-btn--secondary mat-btn--sm md-picker__new', onclick: create }, icon('folder-plus'), el('span', { text: t('Drive.NewFolder') })));
  const choice = await dialog({
    title: t(toLocation ? 'Drive.CopyToLocation' : action === 'move' ? 'Drive.MoveTo' : 'Drive.CopyTo'), icon: toLocation ? 'server' : action === 'move' ? 'folder-move' : 'copy', body, size: 'md',
    buttons: [{ label: t(action === 'move' ? 'Drive.MoveHere' : 'Drive.CopyHere'), value: 'ok', kind: 'primary' }, { label: t('Drive.Cancel'), value: 'cancel' }],
  });
  if (choice !== 'ok') return;
  if (toLocation) { await copyToLocation(ids, state.place, at(), [state.place.name, ...state.path.map(p => p.name)].join(' › ')); return; }
  await act('/live/drive/' + action, { ids, driveId: state.place.id, parentId: at()?.id ?? null }, r => t(action === 'move' ? 'Drive.Moved' : 'Drive.Copied', r.count));
}

/** Copies items (files and folders with everything in them) into a folder of a storage location; the list of the drive stays as it is. */
async function copyToLocation(ids, place, folder, where) {
  const stop = busy(t('Drive.Busy.Copying'));
  try {
    const r = await request('POST', `/live/locations/${place.id}/from-drive`, { ids, path: folder ? folder.path : '/' });
    stop();
    toast(t('Drive.CopiedToLocation', r.count, where), 'success');
  } catch (e) {
    stop();
    // "not found" here is the location that may not be changed (read-only meanwhile, or the right is gone); the server words its own errors
    toast(e.status === 404 ? t('Drive.ToLocation.Refused') : problem(e), 'danger');
  }
}

// ---- preview --------------------------------------------------------------------------------------------------------------------

async function preview(item) {
  const all = items().filter(i => i.kind === 'file');
  let index = Math.max(0, all.findIndex(i => i.id === item.id));
  const stage = el('div', { class: 'md-preview__stage' });
  const name = el('h2', { class: 'md-preview__name' });
  const tools = el('div', { class: 'md-preview__tools' });
  const prev = el('button', { type: 'button', class: 'md-preview__nav md-preview__nav--prev', 'aria-label': t('Drive.Previous') }, icon('chevron-left'));
  const next = el('button', { type: 'button', class: 'md-preview__nav md-preview__nav--next', 'aria-label': t('Drive.Next') }, icon('chevron-right'));
  const close = el('button', { type: 'button', class: 'mat-btn mat-btn--ghost mat-btn--icon md-preview__close', 'aria-label': t('Drive.Close') }, icon('x'));
  const box = el('dialog', { class: 'md-preview', 'aria-label': t('Drive.Preview') }, el('header', { class: 'md-preview__head' }, name, tools, close), stage, prev, next);
  document.body.append(box);

  const show = () => {
    const it = all[index];
    name.textContent = it.name;
    prev.hidden = index <= 0;
    next.hidden = index >= all.length - 1;
    // replaceChildren turns null into the text "null": only the buttons that apply go in
    tools.replaceChildren(...[
      it.office ? el('a', { class: 'mat-btn mat-btn--primary mat-btn--sm', href: '/office/open/' + it.id, target: '_blank', rel: 'noopener' }, icon('external'), el('span', { text: t('Drive.OpenInOffice') })) : null,
      it.text ? el('a', { class: 'mat-btn mat-btn--secondary mat-btn--sm', href: '/drive/edit/' + it.id }, icon('edit'), el('span', { text: t('Drive.EditText') })) : null,
      el('a', { class: 'mat-btn mat-btn--secondary mat-btn--icon mat-btn--sm', href: `/files/${it.id}/content?download=true`, 'aria-label': t('Drive.Download'), 'data-tip': t('Drive.Download') }, icon('download')),
      el('a', { class: 'mat-btn mat-btn--secondary mat-btn--icon mat-btn--sm', href: '/drive/item/' + it.id, 'aria-label': t('Drive.Details'), 'data-tip': t('Drive.Details') }, icon('info')),
    ].filter(Boolean));
    const src = `/files/${it.id}/content`;
    stage.replaceChildren();
    switch (it.preview) {
      case 'image': stage.append(el('img', { src, alt: it.name, class: 'md-preview__image' })); break;
      case 'video': stage.append(el('video', { src, controls: true, autoplay: true, class: 'md-preview__media' })); break;
      case 'audio': stage.append(el('div', { class: 'md-preview__audio' }, icon('file-audio', 'md-preview__big'), el('audio', { src, controls: true, autoplay: true }))); break;
      case 'pdf': stage.append(el('iframe', { src, title: it.name, class: 'md-preview__pdf' })); break;
      case 'text': {
        const pre = el('pre', { class: 'md-preview__text' }, el('code', { text: t('Drive.Loading') }));
        stage.append(pre);
        request('GET', '/live/drive/text/' + it.id).then(r => { pre.firstChild.textContent = r.text; }).catch(e => { pre.firstChild.textContent = problem(e); });
        break;
      }
      default:
        stage.append(el('div', { class: 'md-preview__none' }, el('span', { class: 'md-ficon md-ficon--' + it.category }, icon('file', 'md-preview__big')),
          el('p', { text: t(it.office ? 'Drive.Preview.Office' : 'Drive.Preview.None') }),
          el('div', { class: 'mat-btn-row mat-btn-row--plain' },
            it.office ? el('a', { class: 'mat-btn mat-btn--primary', href: '/office/open/' + it.id, target: '_blank', rel: 'noopener' }, icon('external'), el('span', { text: t('Drive.OpenInOffice') })) : null,
            el('a', { class: 'mat-btn ' + (it.office ? 'mat-btn--secondary' : 'mat-btn--primary'), href: `/files/${it.id}/content?download=true` }, icon('download'), el('span', { text: t('Drive.Download') })))));
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

// ---- keys -----------------------------------------------------------------------------------------------------------------------

function wireKeys() {
  document.addEventListener('keydown', e => {
    if (e.target.closest('input, textarea, select, [contenteditable], dialog')) return;
    const ids = selectedIds();
    if (e.key === 'Delete' && ids.length) {
      e.preventDefault();
      bulk(ctx.mode === 'trash' ? 'delete' : 'trash', ids);
    } else if (e.key === 'F2' && ids.length === 1) {
      e.preventDefault();
      const row = root.querySelector(`[data-node="${ids[0]}"]`);
      if (row) rename(itemOf(row));
    }
  });
}
