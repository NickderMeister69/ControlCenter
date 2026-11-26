'use strict';

const $ = id => document.getElementById(id);
let files = [];
let me = null;
let activeCat = 'Alle';
let pendingFiles = [];
let maxUploadBytes = 0;

function fmtSize(b) {
  if (!b) return '0 B';
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.min(Math.floor(Math.log(b) / Math.log(1024)), u.length - 1);
  return (b / 1024 ** i).toFixed(i ? 1 : 0).replace('.', ',') + ' ' + u[i];
}
function fmtDate(ts) {
  return new Date(ts).toLocaleString('de-DE', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' });
}
function el(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
}
let toastTimer;
function toast(msg, type = '') {
  const t = $('toast');
  t.textContent = msg;
  t.className = 'toast ' + type;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.add('hidden'), 3500);
}

async function api(url, opts = {}) {
  const res = await fetch(url, { credentials: 'same-origin', ...opts });
  if (res.status === 401 && url !== '/api/login') { showLogin(); throw new Error('Nicht angemeldet'); }
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || 'Fehler ' + res.status);
  return data;
}

// ---------- Login ----------
function showLogin() {
  $('app').classList.add('hidden');
  $('login').classList.remove('hidden');
  $('loginName').value = localStorageGet('dc_name') || '';
  ($('loginName').value ? $('loginPw') : $('loginName')).focus();
}
function showApp() {
  $('login').classList.add('hidden');
  $('app').classList.remove('hidden');
  $('userName').textContent = me.name;
  $('adminBadge').classList.toggle('hidden', !me.admin);
  loadFiles();
}
function localStorageGet(k) { try { return localStorage.getItem(k); } catch { return null; } }
function localStorageSet(k, v) { try { localStorage.setItem(k, v); } catch {} }

$('loginForm').addEventListener('submit', async e => {
  e.preventDefault();
  $('loginError').textContent = '';
  try {
    me = await api('/api/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: $('loginName').value, password: $('loginPw').value })
    });
    localStorageSet('dc_name', me.name);
    $('loginPw').value = '';
    showApp();
  } catch (err) {
    $('loginError').textContent = err.message;
  }
});

$('logoutBtn').addEventListener('click', async () => {
  await fetch('/api/logout', { method: 'POST' });
  me = null;
  showLogin();
});

// ---------- Dateiliste ----------
async function loadFiles() {
  try {
    const data = await api('/api/files');
    files = data.files;
    maxUploadBytes = data.maxUploadBytes;
    $('maxInfo').textContent = 'Max. ' + fmtSize(maxUploadBytes) + ' pro Datei';
    $('statCount').textContent = files.length;
    $('statSize').textContent = fmtSize(files.reduce((s, f) => s + f.size, 0));
    $('statFree').textContent = fmtSize(Math.max(0, data.limit - data.used));
    render();
  } catch (err) {
    if (err.message !== 'Nicht angemeldet') toast(err.message, 'err');
  }
}

function render() {
  const cats = [...new Set(files.map(f => f.category))].sort((a, b) => a.localeCompare(b, 'de'));
  if (activeCat !== 'Alle' && !cats.includes(activeCat)) activeCat = 'Alle';

  const chips = $('chips');
  chips.replaceChildren();
  if (cats.length > 1) {
    for (const c of ['Alle', ...cats]) {
      const b = el('button', 'chip' + (c === activeCat ? ' active' : ''), c);
      b.addEventListener('click', () => { activeCat = c; render(); });
      chips.append(b);
    }
  }
  const dl = $('categoryList');
  dl.replaceChildren(...cats.map(c => { const o = el('option'); o.value = c; return o; }));

  const q = $('search').value.trim().toLowerCase();
  let shown = files.filter(f =>
    (activeCat === 'Alle' || f.category === activeCat) &&
    (!q || (f.name + ' ' + f.note + ' ' + f.uploader + ' ' + f.category).toLowerCase().includes(q)));
  const sort = $('sort').value;
  shown.sort((a, b) =>
    sort === 'name' ? a.name.localeCompare(b.name, 'de') :
    sort === 'size' ? b.size - a.size :
    sort === 'popular' ? b.downloads - a.downloads :
    b.uploadedAt - a.uploadedAt);

  const list = $('list');
  list.replaceChildren();
  $('empty').classList.toggle('hidden', files.length !== 0);
  if (files.length && !shown.length) list.append(el('p', 'empty muted', 'Nichts gefunden.'));

  for (const f of shown) {
    const item = el('div', 'item');
    const ext = (f.name.includes('.') ? f.name.split('.').pop() : 'file').slice(0, 4);
    item.append(el('div', 'ficon', ext));

    const info = el('div', 'info');
    const name = el('div', 'fname', f.name);
    name.title = f.name;
    info.append(name);
    const meta = el('div', 'meta');
    meta.append(
      el('span', '', fmtSize(f.size)),
      el('span', '', f.category),
      el('span', '', 'von ' + f.uploader),
      el('span', '', fmtDate(f.uploadedAt)),
      el('span', '', f.downloads + '× geladen'));
    info.append(meta);
    if (f.note) info.append(el('div', 'note', f.note));
    item.append(info);

    const actions = el('div', 'actions');
    const a = el('a', 'dl', 'Download');
    a.href = '/api/download/' + encodeURIComponent(f.id);
    a.addEventListener('click', () => setTimeout(loadFiles, 1500));
    actions.append(a);
    if (me && me.admin) {
      const del = el('button', 'del', '🗑');
      del.title = 'Löschen';
      del.addEventListener('click', () => removeFile(f));
      actions.append(del);
    }
    item.append(actions);
    list.append(item);
  }
}

async function removeFile(f) {
  if (!confirm(`„${f.name}“ wirklich löschen?`)) return;
  try {
    await api('/api/files/' + encodeURIComponent(f.id), { method: 'DELETE' });
    toast('Gelöscht', 'ok');
    loadFiles();
  } catch (err) { toast(err.message, 'err'); }
}

$('search').addEventListener('input', render);
$('sort').addEventListener('change', render);

// ---------- Upload (direkt zu R2, in Teilen, fortsetzbar) ----------
const PARALLEL = 3;
let uploading = false;
let cancelRequested = false;
const activeXhrs = new Set();

function setPending(list) {
  if (uploading) return;
  const tooBig = list.filter(f => maxUploadBytes && f.size > maxUploadBytes);
  if (tooBig.length) toast(tooBig.map(f => f.name).join(', ') + ' ist zu groß (max. ' + fmtSize(maxUploadBytes) + ')', 'err');
  pendingFiles = list.filter(f => f.size > 0 && (!maxUploadBytes || f.size <= maxUploadBytes)).slice(0, 50);
  $('uploadForm').classList.toggle('hidden', pendingFiles.length === 0);
  $('pending').replaceChildren(...pendingFiles.map(f =>
    el('span', '', f.name + ' · ' + fmtSize(f.size) + (localStorageGet(resumeKey(f)) ? ' · wird fortgesetzt' : ''))));
}

$('fileInput').addEventListener('change', e => { setPending([...e.target.files]); e.target.value = ''; });
const drop = $('drop');
['dragenter', 'dragover'].forEach(ev => drop.addEventListener(ev, e => { e.preventDefault(); drop.classList.add('over'); }));
['dragleave', 'drop'].forEach(ev => drop.addEventListener(ev, e => { e.preventDefault(); drop.classList.remove('over'); }));
drop.addEventListener('drop', e => setPending([...e.dataTransfer.files]));

$('cancelBtn').addEventListener('click', () => {
  if (!uploading) return setPending([]);
  if (!confirm('Upload pausieren? Wähl später dieselbe Datei nochmal aus, dann geht es an der Stelle weiter.')) return;
  cancelRequested = true;
  for (const x of activeXhrs) x.abort();
});

function resumeKey(f) { return 'dc_up_' + f.name + '_' + f.size + '_' + f.lastModified; }
function localStorageDel(k) { try { localStorage.removeItem(k); } catch {} }
const sleep = ms => new Promise(r => setTimeout(r, ms));
const abortErr = () => Object.assign(new Error('Pausiert'), { status: -1 });

function putPart(url, blob, onProgress) {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    activeXhrs.add(xhr);
    xhr.open('PUT', url);
    xhr.upload.onprogress = e => onProgress(e.loaded);
    const end = () => activeXhrs.delete(xhr);
    xhr.onload = () => { end(); xhr.status >= 200 && xhr.status < 300 ? resolve() : reject(Object.assign(new Error('R2 ' + xhr.status), { status: xhr.status })); };
    xhr.onerror = () => { end(); reject(Object.assign(new Error('Verbindung weg'), { status: 0 })); };
    xhr.onabort = () => { end(); reject(abortErr()); };
    xhr.send(blob);
  });
}

// Wiederholt eine Aktion bei Netzwerkfehlern mit wachsender Wartezeit
async function withRetry(fn, label) {
  for (let attempt = 1; ; attempt++) {
    if (cancelRequested) throw abortErr();
    try { return await fn(); }
    catch (err) {
      if (err.status === -1 || cancelRequested) throw abortErr();
      if (err.status === 401) { showLogin(); throw err; }
      if (err.status === 403 || err.status === 404 || err.status === 507 || err.status === 400) throw err;
      const wait = Math.min(30, 2 ** Math.min(attempt, 5));
      for (let s = wait; s > 0; s--) {
        if (cancelRequested) throw abortErr();
        $('progressText').textContent = `${label}: Verbindung weg – neuer Versuch in ${s} s …`;
        await sleep(1000);
      }
    }
  }
}

async function apiJson(url, body, method = 'POST') {
  const res = await fetch(url, {
    method, headers: body ? { 'Content-Type': 'application/json' } : {}, body: body ? JSON.stringify(body) : undefined
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw Object.assign(new Error(data.error || 'Fehler ' + res.status), { status: res.status });
  return data;
}

async function uploadOne(file, idx, total, category, note, progress) {
  const key = resumeKey(file);
  let id = localStorageGet(key);
  let state = null;

  if (id) {
    try {
      state = await apiJson('/api/upload/' + id, null, 'GET');
      if (state.done) { localStorageDel(key); return; }
      if (state.size !== file.size) state = null;
    } catch (e) { if (e.status === 401) { showLogin(); throw e; } state = null; }
  }
  if (!state) {
    state = await withRetry(() => apiJson('/api/upload/init', { name: file.name, size: file.size, category, note }), 'Start');
    id = state.id;
    localStorageSet(key, id);
  }

  const partSize = state.partSize;
  const totalParts = Math.ceil(file.size / partSize);
  const partLen = n => Math.min(partSize, file.size - (n - 1) * partSize);
  const done = new Set(state.parts);
  const inFlight = new Map(); // Teil -> hochgeladene Bytes
  const bytesNow = () => {
    let b = 0;
    for (const n of done) b += partLen(n);
    for (const v of inFlight.values()) b += v;
    return b;
  };
  const queue = [];
  for (let n = 1; n <= totalParts; n++) if (!done.has(n)) queue.push(n);
  progress(idx, total, file, bytesNow());

  const urlCache = {};
  async function getUrl(n) {
    if (!urlCache[n] || urlCache[n].exp < Date.now()) {
      const batch = [n, ...queue.slice(0, 19)];
      const r = await apiJson('/api/upload/' + id + '/urls', { parts: batch });
      for (const [k, u] of Object.entries(r.urls)) urlCache[k] = { url: u, exp: Date.now() + 50 * 60 * 1000 };
    }
    return urlCache[n].url;
  }

  async function worker() {
    while (queue.length) {
      const n = queue.shift();
      const start = (n - 1) * partSize;
      await withRetry(async () => {
        inFlight.set(n, 0);
        try {
          const url = await getUrl(n);
          await putPart(url, file.slice(start, start + partLen(n)), loaded => {
            inFlight.set(n, loaded);
            progress(idx, total, file, bytesNow());
          });
        } catch (e) {
          inFlight.delete(n);
          if (e.status === 403) { delete urlCache[n]; e.status = 0; } // Link abgelaufen -> neu holen
          throw e;
        }
      }, `Datei ${idx + 1}`);
      inFlight.delete(n);
      done.add(n);
      progress(idx, total, file, bytesNow());
    }
  }

  const workers = [];
  for (let i = 0; i < PARALLEL; i++) workers.push(worker());
  try { await Promise.all(workers); }
  catch (e) { cancelRequested = true; for (const x of activeXhrs) x.abort(); await Promise.allSettled(workers); throw e; }

  $('progressText').textContent = 'Wird zusammengesetzt …';
  await withRetry(() => apiJson('/api/upload/' + id + '/complete', {}), 'Abschluss');
  localStorageDel(key);
}

$('uploadBtn').addEventListener('click', async () => {
  if (!pendingFiles.length || uploading) return;
  uploading = true;
  cancelRequested = false;
  $('uploadBtn').disabled = true;
  $('cancelBtn').textContent = 'Pausieren';
  $('progress').classList.remove('hidden');

  const list = pendingFiles.slice();
  const category = $('category').value;
  const note = $('note').value;
  let sp = { t: 0, bytes: null, speed: 0 };

  const progress = (idx, total, file, bytes) => {
    const now = Date.now();
    if (sp.bytes === null) sp = { t: now, bytes, speed: 0 };
    else if (now - sp.t > 1500) {
      const cur = Math.max(0, (bytes - sp.bytes) / ((now - sp.t) / 1000));
      sp.speed = sp.speed ? sp.speed * 0.6 + cur * 0.4 : cur;
      sp.t = now; sp.bytes = bytes;
    }
    const pct = bytes / file.size * 100;
    const left = sp.speed > 0 ? (file.size - bytes) / sp.speed : 0;
    $('progressBar').style.width = pct + '%';
    $('progressText').textContent =
      (total > 1 ? `Datei ${idx + 1}/${total} · ` : '') +
      `${pct.toFixed(1).replace('.', ',')} % · ${fmtSize(bytes)} / ${fmtSize(file.size)}` +
      (sp.speed > 0 ? ` · ${fmtSize(sp.speed)}/s · noch ${fmtDuration(left)}` : '');
  };

  let ok = 0;
  try {
    for (let i = 0; i < list.length; i++) {
      sp = { t: 0, bytes: null, speed: 0 };
      await uploadOne(list[i], i, list.length, category, note, progress);
      ok++;
    }
    toast(`${ok} Datei(en) hochgeladen`, 'ok');
    $('note').value = '';
    pendingFiles = [];
  } catch (err) {
    if (err.status === -1) toast('Upload pausiert. Wähl dieselbe Datei nochmal aus, um weiterzumachen.');
    else if (err.status !== 401) toast(err.message, 'err');
    pendingFiles = list.slice(ok);
  } finally {
    uploading = false;
    $('uploadBtn').disabled = false;
    $('cancelBtn').textContent = 'Abbrechen';
    $('progress').classList.add('hidden');
    $('progressBar').style.width = '0';
    setPending(pendingFiles);
    loadFiles();
  }
});

function fmtDuration(s) {
  s = Math.round(s);
  if (s < 60) return s + ' s';
  if (s < 3600) return Math.floor(s / 60) + ' min ' + (s % 60) + ' s';
  return Math.floor(s / 3600) + ' h ' + Math.floor((s % 3600) / 60) + ' min';
}

window.addEventListener('beforeunload', e => { if (uploading) { e.preventDefault(); e.returnValue = ''; } });

// ---------- Start ----------
(async () => {
  try {
    const data = await api('/api/me');
    document.title = data.title;
    document.querySelectorAll('.app-title').forEach(n => { n.textContent = data.title; });
    me = data.user;
    me ? showApp() : showLogin();
  } catch { showLogin(); }
  setInterval(() => { if (me && !uploading && !document.hidden) loadFiles(); }, 30000);
})();
