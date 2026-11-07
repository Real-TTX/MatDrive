/* MatDrive – small helpers of the scripts of the explorer and the editor: texts, DOM, ids, requests, dialogs.
   Everything a person typed goes into the page as text (textContent) or through the sanitizer of the app; never as HTML of the server. */

let texts = null;

/** A text of the catalog (the JSON block the layout writes, keys "Js.*"); {0}, {1} ... are replaced by the arguments. */
export function t(key, ...args) {
  if (!texts) {
    const block = document.getElementById('md-texts');
    try { texts = block ? JSON.parse(block.textContent) : {}; } catch { texts = {}; }
  }
  const text = texts['Js.' + key] ?? key;
  return args.length ? text.replace(/\{(\d+)\}/g, (_, i) => String(args[Number(i)] ?? '')) : text;
}

/** The language of the page (en or de). */
export const lang = () => (document.documentElement.lang || 'en').slice(0, 2);

/** An element: el('button', { class: 'x', type: 'button', onclick: fn }, 'text', child). */
export function el(tag, attrs, ...children) {
  const node = document.createElement(tag);
  if (attrs) {
    for (const [name, value] of Object.entries(attrs)) {
      if (value === undefined || value === null || value === false) continue;
      if (name.startsWith('on') && typeof value === 'function') node.addEventListener(name.slice(2), value);
      else if (name === 'dataset') Object.assign(node.dataset, value);
      else if (name === 'text') node.textContent = value;
      else if (name === 'style' && typeof value === 'object') Object.assign(node.style, value);
      else node.setAttribute(name, value === true ? '' : String(value));
    }
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

/** An icon of the sprite (the toolkit's "mat-i-*" or the app's own, wwwroot/img/app-icons.svg). */
export function icon(name, cls) {
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  svg.setAttribute('class', cls ? 'mat-icon ' + cls : 'mat-icon');
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('focusable', 'false');
  const use = document.createElementNS(ns, 'use');
  use.setAttribute('href', '#mat-i-' + name);
  svg.append(use);
  return svg;
}

/** A short id that is unique enough inside one document. */
export function uid(prefix = '') {
  const bytes = new Uint8Array(6);
  crypto.getRandomValues(bytes);
  return prefix + Array.from(bytes, b => b.toString(36).padStart(2, '0')).join('').slice(0, 9);
}

export const clone = value => (value === undefined ? undefined : structuredClone(value));

export const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

/** The anti-forgery token of the page (the layout carries it). */
export function token() {
  return document.querySelector('[data-page-token] input[name="__RequestVerificationToken"]')?.value || '';
}

/** A JSON request; throws an error with .status (0 = no network) and .body for an answer that is not 2xx. */
export async function request(method, url, body, options = {}) {
  const headers = { Accept: 'application/json' };
  if (method !== 'GET') headers.RequestVerificationToken = token();
  let payload;
  if (body instanceof Blob || body instanceof ArrayBuffer) { payload = body; headers['Content-Type'] = body.type || 'application/octet-stream'; }
  else if (body !== undefined) { payload = JSON.stringify(body); headers['Content-Type'] = 'application/json'; }
  let response;
  try {
    response = await fetch(url, { method, headers, body: payload, credentials: 'same-origin', cache: 'no-store', signal: options.signal });
  } catch (e) {
    const error = new Error('network');
    error.status = 0;
    throw error;
  }
  const type = response.headers.get('Content-Type') || '';
  const data = type.includes('json') ? await response.json().catch(() => null) : null;
  if (!response.ok) {
    const error = new Error('HTTP ' + response.status);
    error.status = response.status;
    error.body = data;
    throw error;
  }
  return data;
}

export function debounce(fn, ms) {
  let timer = 0;
  const run = (...args) => { clearTimeout(timer); timer = setTimeout(() => fn(...args), ms); };
  run.cancel = () => clearTimeout(timer);
  run.flush = (...args) => { clearTimeout(timer); fn(...args); };
  return run;
}

/** A toast of the toolkit. */
export function toast(message, tone = 'info') {
  if (window.MatUi?.toast) window.MatUi.toast(message, { tone });
}

/** A panel at the bottom right while a long operation runs (copies to and between servers, unzip, delete of a large folder); the answer removes it. */
export function busy(label) {
  const node = el('section', { class: 'md-uploads md-busy', role: 'status', 'aria-live': 'polite' },
    el('div', { class: 'md-uploads__head' }, el('span', { class: 'mat-spinner mat-spinner--sm', 'aria-hidden': 'true' }), el('strong', { class: 'md-uploads__title', text: label })));
  document.body.append(node);
  return () => node.remove();
}

/** A file download made in the browser. */
export function download(name, data, type) {
  const blob = data instanceof Blob ? data : new Blob([data], { type });
  const url = URL.createObjectURL(blob);
  const a = el('a', { href: url, download: name });
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 2000);
}

/** Asks for a file (or several) with the file picker of the browser. */
export function pickFile(accept, multiple = false) {
  return new Promise(resolve => {
    const input = el('input', { type: 'file', accept, multiple, hidden: true });
    input.addEventListener('change', () => { resolve(multiple ? Array.from(input.files) : input.files[0] || null); input.remove(); });
    input.addEventListener('cancel', () => { resolve(multiple ? [] : null); input.remove(); });
    document.body.append(input);
    input.click();
  });
}

/** A file as a data: address (pictures are kept inside the document). */
export function readAsDataUrl(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result);
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
}

/** A picture made smaller in the browser (at most maxSide pixels on its longer side), as a data: address. */
export async function shrinkImage(file, maxSide = 1600) {
  const url = await readAsDataUrl(file);
  if (file.type === 'image/svg+xml' || file.type === 'image/gif') return url;
  const img = await new Promise((resolve, reject) => { const i = new Image(); i.onload = () => resolve(i); i.onerror = reject; i.src = url; });
  const scale = Math.min(1, maxSide / Math.max(img.naturalWidth, img.naturalHeight));
  if (scale >= 1 && file.size < 900 * 1024) return url;
  const canvas = el('canvas', { width: Math.round(img.naturalWidth * scale), height: Math.round(img.naturalHeight * scale) });
  canvas.getContext('2d').drawImage(img, 0, 0, canvas.width, canvas.height);
  return canvas.toDataURL(file.type === 'image/png' ? 'image/png' : 'image/jpeg', 0.88);
}

/**
 * A dialog of the toolkit with content of the app (built with el(), never HTML).
 * buttons: [{ label, value, kind: 'primary'|'secondary'|'danger', icon }] – positive to negative; a 'danger' button stands at the end after a gap.
 * Resolves with the value of the button (or null for the X / Escape); onOpen(dialog) can focus a field. The form's fields are in dialog.elements.
 */
export function dialog({ title, icon: iconName, body, buttons, size = 'sm', onOpen, validate }) {
  return new Promise(resolve => {
    const d = el('dialog', { class: `mat-dialog mat-dialog--${size} md-dialog`, role: 'dialog', 'data-static': true });
    const form = el('form', { method: 'dialog', class: 'mat-dialog__form', novalidate: true });
    const titleId = uid('dlg');
    d.setAttribute('aria-labelledby', titleId);
    const head = el('div', { class: 'mat-dialog__head' }, iconName ? icon(iconName) : null, el('h2', { class: 'mat-dialog__title', id: titleId, text: title }),
      el('button', { type: 'button', class: 'mat-btn mat-btn--ghost mat-btn--icon mat-dialog__close', 'data-dialog-close': true, 'aria-label': t('Editor.Close') }, icon('x')));
    const row = el('div', { class: 'mat-btn-row mat-btn-row--plain' });
    (buttons || [{ label: t('Editor.Ok'), value: 'ok', kind: 'primary' }]).forEach((b, i) => {
      if (b.kind === 'danger') row.append(el('span', { class: 'mat-btn-row__spacer' }));
      row.append(el('button', {
        type: b.value === 'cancel' ? 'button' : 'submit', value: b.value, class: `mat-btn mat-btn--${b.kind || 'secondary'}`,
        'data-dialog-close': b.value === 'cancel' ? true : null, autofocus: i === 0 && !onOpen ? true : null,
      }, b.icon ? icon(b.icon) : null, el('span', { text: b.label })));
    });
    form.append(head, el('div', { class: 'mat-dialog__body' }, body), el('div', { class: 'mat-dialog__foot' }, row));
    d.append(form);
    document.body.append(d);
    let submitted = null;
    form.addEventListener('submit', e => {
      e.preventDefault();
      const value = e.submitter?.value || 'ok';
      if (value !== 'cancel' && validate && !validate(form, value)) return;
      submitted = value;
      window.MatUi.dialog.close(d, value);
    });
    d.addEventListener('close', () => { resolve(submitted); setTimeout(() => d.remove(), 0); });
    window.MatUi.dialog.open(d);
    if (onOpen) onOpen(form);
  });
}

export const confirm = (message, options) => window.MatUi.confirm(message, options);
export const alert = (message, options) => window.MatUi.alert(message, options);
export const prompt = (message, options) => window.MatUi.prompt(message, options);

/**
 * Undo and redo by snapshots of the content (JSON). Call push() BEFORE a change (or after it with the state before), undo()/redo() give the content to show.
 * Cheap enough for documents of a few MB; an app with very large content can keep its own history of operations instead.
 */
export class History {
  constructor(limit = 100) { this.limit = limit; this.past = []; this.future = []; }
  push(state) {
    const s = typeof state === 'string' ? state : JSON.stringify(state);
    if (this.past.length && this.past[this.past.length - 1] === s) return;
    this.past.push(s);
    if (this.past.length > this.limit) this.past.shift();
    this.future = [];
  }
  get canUndo() { return this.past.length > 0; }
  get canRedo() { return this.future.length > 0; }
  undo(current) {
    if (!this.past.length) return null;
    this.future.push(typeof current === 'string' ? current : JSON.stringify(current));
    return JSON.parse(this.past.pop());
  }
  redo(current) {
    if (!this.future.length) return null;
    this.past.push(typeof current === 'string' ? current : JSON.stringify(current));
    return JSON.parse(this.future.pop());
  }
  clear() { this.past = []; this.future = []; }
}

/** Whether the platform's main modifier is pressed (Cmd on a Mac, Ctrl elsewhere). */
export const isMac = /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent);
export const mod = e => (isMac ? e.metaKey : e.ctrlKey);

/** "Ctrl+B" / "⌘B" for a tooltip. */
export function keyLabel(combo) {
  if (!combo) return '';
  return combo.split('+').map(k => (k === 'Mod' ? (isMac ? '⌘' : 'Ctrl') : k === 'Shift' ? (isMac ? '⇧' : 'Shift') : k === 'Alt' ? (isMac ? '⌥' : 'Alt') : k)).join(isMac ? '' : '+');
}

/** A date/time for people in the language of the page. */
export function stamp(date) {
  const d = date instanceof Date ? date : new Date(date);
  return d.toLocaleString(lang(), { dateStyle: 'medium', timeStyle: 'short' });
}
