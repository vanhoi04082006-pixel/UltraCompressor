'use strict';

/* ============================================================
   UltraCompressor — giao diện web
   Nói chuyện với lõi .NET qua cầu postMessage (xem Bridge/AppHost.cs).
   Không phụ thuộc thư viện ngoài, không cần bước build.
   ============================================================ */

const $  = (id) => document.getElementById(id);
const el = (tag, className, text) => {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text != null) node.textContent = text;
  return node;
};

// ---------------------------------------------------------------- cầu nối

let nextId = 1;
const pending = new Map();

function call(cmd, args = {}) {
  return new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    chrome.webview.postMessage({ id, cmd, args });
  });
}

function handleMessage(msg) {
  if (msg.event) {
    handleEvent(msg.event, msg.data);
    return;
  }

  const entry = pending.get(msg.id);
  if (!entry) return;
  pending.delete(msg.id);

  if (msg.error) entry.reject(new Error(msg.error));
  else entry.resolve(msg.data);
}

function handleEvent(name, data) {
  if (name === 'state') {
    state = data;
    renderAll();
  } else if (name === 'items') {
    items = data.items || [];
    if (data.jobId === openJobId) renderItems();
  } else if (name === 'foldersDropped') {
    addFolders(data.paths || []);
  } else if (name === 'dropHover') {
    showDropZone(data.active === true);
  } else if (name === 'notice') {
    toast(data.message, data.level || 'info');
  }
}

window.chrome?.webview?.addEventListener('message', (e) => handleMessage(e.data));

// ---------------------------------------------------------------- trạng thái cục bộ

let state = null;
let items = [];
let openJobId = null;
let searchTerm = '';
let itemSearch = { search: '', state: 'all', kind: 'all' };

// ---------------------------------------------------------------- tiện ích

const KIND_LABEL = { Image: 'Ảnh', Video: 'Video', Audio: 'Âm thanh', Gif: 'GIF', Pdf: 'PDF' };

const STATUS_TAG = {
  Waiting:   'tag-queued',
  Running:   'tag-running',
  Paused:    'tag-paused',
  PendingReview: 'tag-review',
  Committed: 'tag-done',
  Failed:    'tag-error',
  Cancelled: 'tag-cancelled',
};

function escapeHtml(s) {
  return String(s ?? '').replace(/[&<>"']/g, (c) => (
    { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
  ));
}

function toast(message, level = 'info', title = null) {
  const node = el('div', `toast is-${level}`);
  const body = el('div');
  if (title) body.appendChild(el('div', 't-title', title));
  body.appendChild(el('div', 't-body', message));
  node.appendChild(body);
  $('toasts').appendChild(node);

  setTimeout(() => {
    node.style.opacity = '0';
    node.style.transform = 'translateY(6px)';
    node.style.transition = 'opacity 200ms, transform 200ms';
    setTimeout(() => node.remove(), 220);
  }, level === 'error' ? 7000 : 3800);
}

/** Hộp xác nhận trả về Promise<boolean>. */
function confirmDialog({ title, text, items: list = [], okText = 'Tiếp tục', danger = true }) {
  return new Promise((resolve) => {
    $('confirmTitle').textContent = title;
    $('confirmText').textContent = text;
    $('confirmList').hidden = list.length === 0;
    $('confirmList').innerHTML = list.map((i) => `<li>${escapeHtml(i)}</li>`).join('');
    $('btnConfirmOk').textContent = okText;
    $('btnConfirmOk').className = `btn ${danger ? 'btn-danger' : 'btn-primary'}`;

    const root = $('modalConfirm');
    const ok = $('btnConfirmOk');
    const closers = root.querySelectorAll('[data-close]');

    const finish = (result) => {
      ok.removeEventListener('click', onOk);
      closers.forEach((c) => c.removeEventListener('click', onClose));
      document.removeEventListener('keydown', onKey);
      root.hidden = true;
      resolve(result);
    };

    const onOk = () => finish(true);
    const onClose = () => finish(false);
    const onKey = (e) => {
      if (e.key === 'Escape') { e.stopPropagation(); finish(false); }
      if (e.key === 'Enter')  { e.preventDefault(); finish(true); }
    };

    ok.addEventListener('click', onOk);
    closers.forEach((c) => c.addEventListener('click', onClose));
    document.addEventListener('keydown', onKey);
    root.hidden = false;
    ok.focus();
  });
}

function openModal(id) { $(id).hidden = false; }

function closeModal(id) { $(id).hidden = true; }

function anyModalOpen() {
  return [...document.querySelectorAll('.modal-root')].some((m) => !m.hidden);
}

// ============================================================ vẽ giao diện

function renderAll() {
  if (!state) return;

  renderSummary();
  renderJobs();
  renderAlerts();
  renderToolbarState();
  renderStatusbar();

  if (openJobId) renderItems();
}

function renderSummary() {
  const value = $('sumSaved');
  value.textContent = state.totalSavedText;
  value.classList.toggle('is-zero', state.totalBytesSaved <= 0);
  $('sumPercent').textContent = state.totalSavedPercentText;
  $('sumFiles').textContent = `${state.processedFiles} / ${state.totalFiles} tệp`;
  $('sumSpeed').textContent = state.isRunning ? state.speedText : '—';

  const running = state.jobs.filter((j) => j.status === 'Running');
  const etas = running.map((j) => j.etaSeconds).filter((s) => s > 0);
  $('sumEta').textContent = etas.length
    ? formatTime(Math.max(...etas))
    : '—';

  $('sumThreads').textContent = `${state.concurrency} luồng`;

  const chips = $('kindChips');
  chips.innerHTML = '';
  for (const k of state.byKind) {
    const chip = el('span', 'kchip');
    chip.appendChild(el('b', null, KIND_LABEL[k.kind] || k.kind));
    chip.appendChild(el('span', 'kval', k.savedBytes > 0 ? `-${formatSize(k.savedBytes)}` : '0'));
    chip.appendChild(el('span', 'kcount', `${k.count} tệp`));
    chips.appendChild(chip);
  }
}

function renderJobs() {
  const body = $('jobBody');
  const jobs = state.jobs.filter((j) =>
    !searchTerm || j.folderName.toLowerCase().includes(searchTerm));

  $('jobEmpty').style.display = state.jobs.length === 0 ? 'flex' : 'none';
  $('jobTable').style.display = state.jobs.length === 0 ? 'none' : 'table';

  body.innerHTML = '';
  for (const job of jobs) {
    body.appendChild(jobRow(job));
  }
}

function jobRow(job) {
  const tr = el('tr');
  if (job.id === openJobId) tr.classList.add('selected');

  // Thư mục
  // Tên và đường dẫn phải nằm trong span riêng: đặt trực tiếp vào <td> thì
  // max-width của ô không có tác dụng với bảng có table-layout auto, chữ sẽ tràn
  // sang ô bên cạnh khi bảng chi tiết làm cột này bị bóp.
  const tdName = el('td', 'col-name');
  tdName.appendChild(el('span', 'j-name', job.folderName));
  tdName.appendChild(el('span', 'sub', job.folderPath));
  tdName.title = job.folderPath;
  tr.appendChild(tdName);

  // Trạng thái
  const tdStatus = el('td');
  const tag = el('span', `tag ${STATUS_TAG[job.status] || 'tag-queued'}`, job.statusText);
  tdStatus.appendChild(tag);
  if (job.errorMessage) {
    tdStatus.appendChild(el('div', 'sub', job.errorMessage));
    tdStatus.title = job.errorMessage;
  }
  tr.appendChild(tdStatus);

  // Tiết kiệm
  const tdSaved = el('td', 'num');
  tdSaved.appendChild(el('div', null, job.savedText));
  tdSaved.appendChild(el('span', 'sub', job.savedPercentText));
  tr.appendChild(tdSaved);

  // ETA
  tr.appendChild(el('td', 'num', job.etaText || '—'));

  // Tiến độ
  const tdProgress = el('td');
  const wrap = el('div', 'progress');
  const bar = el('div', 'progress-bar');
  const fill = el('div', 'progress-fill');
  if (job.status === 'PendingReview' || job.status === 'Committed') fill.classList.add('is-done');
  if (job.status === 'Failed') fill.classList.add('is-error');
  fill.style.width = `${Math.max(0, Math.min(100, job.progress))}%`;
  bar.appendChild(fill);
  wrap.appendChild(bar);
  wrap.appendChild(el('span', 'progress-text', `${job.progress}%`));
  wrap.appendChild(el('span', 'progress-text', `${job.processedCount}/${job.totalFiles}`));

  // Khi job đang chạy, thanh tổng chỉ là số tệp. Người dùng cần biết đang bận tệp nào và
  // tới đâu — tệp nào kẹt ở 0% suốt 20 phút thì nhìn số tệp không thấy khác gì.
  if (job.activeFileName) {
    const active = el('div', 'active-file');
    active.appendChild(el('span', 'active-name', job.activeFileName));
    active.appendChild(el('span', 'active-pct', `${Math.max(0, job.activePercent)}%`));
    active.title = `Đang nén: ${job.activeFileName}`;
    wrap.appendChild(active);
  }

  tdProgress.appendChild(wrap);
  tr.appendChild(tdProgress);

  // Hành động
  const tdActions = el('td', 'actions-col');
  const actions = el('div', 'row-actions');

  actions.appendChild(iconButton('☰', 'Chi tiết', () => openDetail(job.id)));
  if (job.canPause) {
    actions.appendChild(iconButton('❙❙', 'Tạm dừng job này', () => call('pauseJob', { jobId: job.id })));
  }
  if (job.status === 'Paused') {
    actions.appendChild(iconButton('▶', 'Tiếp tục job này', () => call('resumeJob', { jobId: job.id })));
  }
  if (job.canCancel) {
    actions.appendChild(iconButton('✕', 'Dừng job này', async () => {
      if (await confirmDialog({ title: 'Dừng job', text: `Dừng xử lý “${job.folderName}”?`, okText: 'Dừng' })) {
        await call('cancelJob', { jobId: job.id });
      }
    }, 'danger'));
  }
  if (job.canReview && job.pendingBackups > 0) {
    actions.appendChild(iconButton('✓', 'Duyệt — xoá bản sao lưu', () => reviewJob(job), 'ok'));
  } else if (job.canReview && job.dryRun && job.bytesSaved > 0) {
    actions.appendChild(iconButton('✓', 'Duyệt — nén thật và thay thế tệp gốc', () => reviewJob(job), 'ok'));
  }
  if (job.canReview) {
    actions.appendChild(iconButton('↩', 'Hoàn tác — khôi phục bản gốc', () => undoJob(job), 'danger'));
  }
  actions.appendChild(iconButton('🗑', 'Bỏ khỏi danh sách', () => removeJob(job), 'danger'));

  tdActions.appendChild(actions);
  tr.appendChild(tdActions);

  return tr;
}

function iconButton(glyph, title, onClick, extra = '') {
  const b = el('button', `iconbtn ${extra}`.trim(), glyph);
  b.title = title;
  b.setAttribute('aria-label', title);
  b.addEventListener('click', (e) => { e.stopPropagation(); onClick(); });
  return b;
}

function renderAlerts() {
  const box = $('alerts');
  box.innerHTML = '';

  const problems = state.tools.filter((t) => t.health === 'Broken' || t.health === 'Missing');
  if (problems.length === 0) {
    box.hidden = true;
    return;
  }

  for (const p of problems) {
    const level = p.health === 'Missing' ? 'warn' : 'error';
    const alert = el('div', `alert alert-${level}`);

    alert.appendChild(el('span', 'ico', p.health === 'Missing' ? '△' : '✕'));

    const body = el('div', 'alert-body');
    body.appendChild(el('strong', null, `${p.displayName} ${p.health === 'Missing' ? 'chưa có' : 'không chạy được'}`));
    body.appendChild(el('p', null, p.message || ''));
    if (p.path) body.appendChild(el('p', null, p.path));
    alert.appendChild(body);

    const actions = el('div', 'alert-actions');
    const fix = el('button', 'btn btn-sm btn-primary', 'Chỉ định đường dẫn');
    fix.addEventListener('click', () => { openSettings(); renderToolList(); });
    actions.appendChild(fix);
    alert.appendChild(actions);

    box.appendChild(alert);
  }

  box.hidden = false;
}

function renderToolbarState() {
  const runnable = state.jobs.some((j) => j.status === 'Waiting' || j.status === 'Paused');
  const anyRunning = state.isRunning;
  const anyCommitted = state.jobs.some((j) => j.pendingBackups > 0);
  const reviewing = state.jobs.some((j) => j.canReview && j.pendingBackups === 0 && j.bytesSaved > 0);

  $('btnStart').disabled = !runnable || anyRunning;
  $('btnPause').disabled = !anyRunning && !state.isPaused;
  $('btnStop').disabled = !anyRunning;
  $('btnClear').disabled = anyRunning || state.jobs.length === 0;
  $('btnUndoAll').disabled = !anyCommitted;
  $('btnAdd').disabled = anyRunning;

  $('btnPause').innerHTML = state.isPaused
    ? '<span class="ico">▶</span> Tiếp tục'
    : '<span class="ico">❙❙</span> Tạm dừng';
}

function renderStatusbar() {
  const box = $('toolChips');
  box.innerHTML = '';
  for (const t of state.tools) {
    const dot = el('span',
      t.health === 'Ok' || t.health === 'Optional' ? 'dotok'
        : t.health === 'Broken' ? 'dotbad' : 'dotwarn');
    dot.title = t.version || '';
    box.appendChild(dot);
    box.appendChild(document.createTextNode(`${t.displayName} `));
  }
}

// ============================================================ bảng chi tiết

async function openDetail(jobId) {
  openJobId = jobId;
  document.querySelector('.workspace').classList.add('split');
  $('detailPanel').hidden = false;
  $('detailTitle').textContent = state.jobs.find((j) => j.id === jobId)?.folderName || 'Chi ti?t';
  await loadItems();
}

// ============================================================ so sánh trước / sau

// Giữ dữ liệu của màn hình đang mở để nút Phát biết đường dẫn, và để đóng modal không
// phải dựng lại từ đầu.
/* Dữ liệu của màn hình đang mở. Giữ lại để các nút điều khiển biết đang so tệp nào. */
let compareState = null;
let comparePlayers = { original: null, compressed: null };

async function openCompare(jobId, filePath) {
  if (!jobId || !filePath) return;

  $('compareTitle').textContent = 'Đang dựng ảnh xem trước…';
  for (const id of ['shotOriginal', 'shotCompressed']) $(id).innerHTML = '';
  for (const id of ['factsOriginal', 'factsCompressed']) $(id).innerHTML = '';
  $('compareSaved').textContent = '';
  $('compareNote').textContent = '';
  comparePlayers = { original: null, compressed: null };
  openModal('modalCompare');

  let payload;
  try {
    payload = await call('getCompare', { jobId, filePath });
  } catch (err) {
    $('compareTitle').textContent = 'So sánh trước / sau';
    $('compareNote').textContent = err.message;
    return;
  }

  if (!payload?.ok) {
    $('compareTitle').textContent = 'So sánh trước / sau';
    $('compareNote').textContent = payload?.error || 'Không lấy được dữ liệu.';
    return;
  }

  const data = payload.compare;
  compareState = data;
  $('compareTitle').textContent = data.fileName;

  $('compareSaved').textContent = data.compressed
    ? `Tiết kiệm ${data.savedText} (${data.savedPercentText})`
    : '';

  $('compareNote').textContent = data.note || '';
  $('compareNote').hidden = !data.note;

  renderCompareSide('Original', data.original);
  renderCompareSide('Compressed', data.compressed);

  const playable = [data.original, data.compressed].some((s) => s && s.url && s.kind === 'Video');
  $('btnPlayBoth').disabled = !playable;
  $('btnPauseBoth').disabled = !playable;
}

/* Bề mặt hiển thị: video nhúng có thanh điều khiển, ảnh và GIF dùng <img>, PDF không nhúng
   được nên chỉ hiện ảnh xem trước. Mọi thứ nằm trong khung cao cố định để hai bên luôn
   cùng kích thước, so trực tiếp được. */
function renderCompareSide(prefix, side) {
  const key = prefix === 'Original' ? 'original' : 'compressed';
  const shot = $(`shot${prefix}`);
  const facts = $(`facts${prefix}`);

  shot.innerHTML = '';
  facts.innerHTML = '';
  $(`name${prefix}`).textContent = '';
  comparePlayers[key] = null;

  if (!side) {
    shot.appendChild(el('p', 'compare-none', 'Chưa có bản nén trên đĩa'));
    return;
  }

  if (!side.exists) {
    shot.appendChild(el('p', 'compare-none', 'Không tìm thấy tệp'));
    return;
  }

  $(`name${prefix}`).textContent = side.fileName;

  if (side.url && side.kind === 'Video') {
    const video = document.createElement('video');
    video.src = side.url;
    video.controls = true;
    video.preload = 'metadata';
    video.className = 'compare-media';
    video.setAttribute('playsinline', '');
    shot.appendChild(video);
    comparePlayers[key] = video;
  } else if (side.url && (side.kind === 'Image' || side.kind === 'Gif')) {
    const img = document.createElement('img');
    img.src = side.url;
    img.alt = side.fileName;
    img.className = 'compare-media';
    shot.appendChild(img);
  } else if (side.url && side.kind === 'Audio') {
    const audio = document.createElement('audio');
    audio.src = side.url;
    audio.controls = true;
    audio.className = 'compare-audio';
    shot.appendChild(audio);
    comparePlayers[key] = audio;
  } else if (side.thumbnail) {
    const img = document.createElement('img');
    img.src = side.thumbnail;
    img.alt = side.fileName;
    img.className = 'compare-media';
    shot.appendChild(img);
  } else {
    shot.appendChild(el('p', 'compare-none', 'Không có ảnh xem trước'));
  }

  const rows = [
    ['Dung lượng', side.sizeText],
    ['Khung hình', side.resolutionText],
    ['Thời lượng', side.durationText],
    ['Bitrate', side.bitrateText],
  ];

  for (const [label, value] of rows) {
    if (!value || value === '—') continue;
    facts.appendChild(el('dt', null, label));
    facts.appendChild(el('dd', null, value));
  }
}

/* Phát cả hai cùng lúc. Không kéo được hai trình phát về đúng thời điểm tuyệt đối vì
   tải giải mã mỗi bên một tốc độ, nhưng chơi cùng lệnh là đủ để so sánh trực quan. */
function playBoth() {
  for (const p of Object.values(comparePlayers)) {
    if (!p) continue;
    p.currentTime = 0;
    p.play().catch(() => {});
  }
}

function pauseBoth() {
  for (const p of Object.values(comparePlayers)) {
    if (p) p.pause();
  }
}

/* Thanh kéo giữa hai cột. Dùng biến CSS --split để phần trăm hai cột, thay vì tính lại
   bằng JavaScript. */
function initCompareSplit() {
  const grid = $('compareGrid');
  const handle = $('compareSplit');
  if (!grid || !handle) return;

  const apply = (percent) => {
    const clamped = Math.max(15, Math.min(85, percent));
    grid.style.setProperty('--split', `${clamped}%`);
  };

  let dragging = false;

  const move = (clientX) => {
    const box = grid.getBoundingClientRect();
    if (box.width === 0) return;
    apply(((clientX - box.left) / box.width) * 100);
  };

  handle.addEventListener('pointerdown', (e) => {
    dragging = true;
    handle.setPointerCapture(e.pointerId);
    document.body.classList.add('is-resizing');
    e.preventDefault();
  });

  handle.addEventListener('pointermove', (e) => {
    if (dragging) move(e.clientX);
  });

  const stop = (e) => {
    if (!dragging) return;
    dragging = false;
    document.body.classList.remove('is-resizing');
    if (handle.hasPointerCapture?.(e.pointerId)) handle.releasePointerCapture(e.pointerId);
  };

  handle.addEventListener('pointerup', stop);
  handle.addEventListener('pointercancel', stop);

  // Bàn phím: mũi tên trái/phải dịch 2% mỗi lần bấm.
  handle.addEventListener('keydown', (e) => {
    const current = parseFloat(getComputedStyle(grid).getPropertyValue('--split')) || 50;
    if (e.key === 'ArrowLeft') { apply(current - 2); e.preventDefault(); }
    else if (e.key === 'ArrowRight') { apply(current + 2); e.preventDefault(); }
  });
}



function closeDetail() {
  openJobId = null;
  items = [];
  document.querySelector('.workspace').classList.remove('split');
  $('detailPanel').hidden = true;
  renderJobs();
}

async function loadItems() {
  if (!openJobId) return;
  const data = await call('getItems', {
    jobId: openJobId,
    search: itemSearch.search,
    state: itemSearch.state,
    kind: itemSearch.kind,
  });
  items = data.items || [];
  renderItems();
}

function renderItems() {
  const body = $('itemBody');
  body.innerHTML = '';

  for (const item of items) {
    const tr = el('tr');

    const tdName = el('td', 'col-name');
    tdName.appendChild(document.createTextNode(item.fileName));
    const sub = el('span', 'sub', item.kind + (item.durationText ? ` · ${item.durationText}` : ''));
    tdName.appendChild(sub);
    tdName.title = item.filePath;
    tr.appendChild(tdName);

    tr.appendChild(el('td', 'num', item.oldSizeText));
    tr.appendChild(el('td', 'num', item.isApplied || item.detail ? item.newSizeText : '—'));

    const tdResult = el('td', 'num');
    tdResult.appendChild(el('div', null, item.savedText && item.savedBytes > 0 ? item.savedText : '—'));
    if (item.savedPercentText) tdResult.appendChild(el('span', 'sub', item.savedPercentText));
    tr.appendChild(tdResult);

    const tdState = el('td');
    const tagClass = item.state === 'done' ? 'tag-done'
      : item.state === 'skipped' ? 'tag-skipped'
      : item.state === 'predicted' ? 'tag-review'
      : item.state === 'processing' ? 'tag-running' : 'tag-queued';
    tdState.appendChild(el('span', `tag ${tagClass}`, item.stateText));
    if (item.detail) {
      tdState.appendChild(el('div', 'sub', item.detail));
      tdState.title = item.message || item.detail;
    }

    // Thanh tiến độ riêng cho tệp đang nén. Không có nó thì một tập video 20 phút và một
    // tệp ảnh nhỏ đều chỉ hiện "Đang xử lý" — không phân biệt được tệp nào sắp xong.
    if (item.isProcessing) {
      const bar = el('div', 'progress is-inline');
      const fill = el('div', 'progress-fill');
      fill.style.width = `${Math.max(0, Math.min(100, item.percent))}%`;
      bar.appendChild(fill);
      bar.appendChild(el('span', 'progress-text', `${Math.max(0, item.percent)}%`));
      tdState.appendChild(bar);
    }

    tr.appendChild(tdState);

    const tdActions = el('td', 'actions-col');
    const actions = el('div', 'row-actions');

    if (item.canPreview && item.hasBackup) {
      actions.appendChild(iconButton('▷', 'Xem bản gốc', () => preview(item, true)));
    }
    actions.appendChild(iconButton('◫', 'So sánh bản gốc với bản nén', () =>
      openCompare(openJobId, item.filePath)));
    if (item.isApplied && item.hasBackup) {
      actions.appendChild(iconButton('↩', 'Khôi phục tệp này', () => undoItem(item), 'danger'));
    }
    actions.appendChild(iconButton('📂', 'Mở thư mục chứa tệp', () =>
      call('openPath', { path: item.filePath })));

    tdActions.appendChild(actions);
    tr.appendChild(tdActions);
    body.appendChild(tr);
  }

  const total = state.jobs.find((j) => j.id === openJobId)?.totalFiles || 0;
  $('itemFoot').textContent = `Hiện ${items.length} / ${total} tệp`;
}

// ============================================================ hành động

async function addFolders(paths) {
  if (!paths || paths.length === 0) return;
  const data = await call('addFolder', { paths });
  const problems = data?.problems || [];
  const added = (data?.results || []).filter((r) => r.added).reduce((n, r) => n + r.files, 0);

  if (added > 0) toast(`Đã thêm ${added} tệp vào danh sách.`, 'ok');
  for (const p of problems) toast(p, 'warn');
}

// ============================================================ kéo thả từ Explorer

/* WebView2 không có sự kiện "đã thả" ở phía .NET, nên kéo từ Explorer tới được
   Chromium gửi xuống trang dưới dạng HTML5 drag/drop. Bắt buộc phải preventDefault()
   ở dragover, nếu không trình duyệt sẽ coi đây là thả không hợp lệ và không bắn sự kiện drop.

   Đường dẫn thật lấy theo thứ tự:
     1. file.path            — WebView2 để lộ đường dẫn tuyệt đối của File.
     2. entry.fullPath       — dạng URL của mục trình duyệt, ví dụ "/C:/Media/Phim".
                               Bỏ dấu "/" đầu là ra đường dẫn Windows hợp lệ.
   Cần cả hai vì WebView2 phiên bản nào đó có thể bỏ trống file.path. */

function entryPathFromUrl(fullPath) {
  if (!fullPath) return null;
  let p = fullPath.replace(/^\//, '');
  if (!p) return null;
  if (!/^[A-Za-z]:/.test(p)) return null;      // chỉ nhận đường dẫn tuyệt đối ổ đĩa
  try { p = decodeURIComponent(p); } catch { /* giữ nguyên nếu không giải mã được */ }
  return p.replace(/\//g, '\\');
}

function pathFromDropItem(item) {
  const entry = item.webkitGetAsEntry ? item.webkitGetAsEntry() : null;

  if (entry) {
    // Thư mục không có đối tượng File để lấy path, phải dựng từ fullPath.
    if (entry.isDirectory) {
      return { path: entryPathFromUrl(entry.fullPath), isDir: true };
    }
    const entryFile = entry.file ? entry.file() : null;
    if (entryFile && entryFile.path) return { path: entryFile.path, isDir: false };
    return { path: entryPathFromUrl(entry.fullPath), isDir: false };
  }

  const file = item.getAsFile ? item.getAsFile() : null;
  return { path: file && file.path ? file.path : null, isDir: false };
}

function hasFiles(dt) {
  if (dt.types && Array.prototype.includes.call(dt.types, 'Files')) return true;
  return !!(dt.items && Array.prototype.some.call(dt.items, (i) => i.kind === 'file'));
}

function parentFolderOf(path) {
  const cut = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/'));
  if (cut <= 0) return null;                    // không có phần thư mục cha
  const parent = path.slice(0, cut);
  // Chặn thả ngay tại gốc ổ đĩa: quét toàn bộ ổ là ý tưởng rất tệ.
  if (/^[A-Za-z]:\\?$/.test(parent)) return null;
  return parent;
}

async function handleDroppedPaths(entries) {
  const usable = entries.filter((e) => e && e.path);
  if (usable.length === 0) {
    toast('Không đọc được đường dẫn từ nội dung vừa thả.', 'warn');
    return;
  }

  // Bộ quét chỉ nhận thư mục, nên tệp rời phải gom về thư mục chứa nó.
  const folders = [];
  let loose = 0;

  for (const entry of usable) {
    if (entry.isDir) {
      folders.push(entry.path);
      continue;
    }
    const parent = parentFolderOf(entry.path);
    if (parent) {
      folders.push(parent);
      loose += 1;
    } else {
      toast(`Bỏ qua "${entry.path}" vì nằm ngay gốc ổ đĩa.`, 'warn');
    }
  }

  // Bỏ trùng theo dạng không phân biệt hoa/thường, giống cách .NET so sánh đường dẫn.
  const seen = new Set();
  const unique = folders.filter((f) => {
    const key = f.replace(/\\+$/, '').toLowerCase();
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });

  if (loose > 0) toast(`${loose} tệp rời được gom về thư mục chứa nó.`, 'info');
  await addFolders(unique);
}

/* Lop phu "Tha vao day".
 *
 * Duong dan thay the khong lay tu JavaScript: trang chay tren https la ngu canh an toan
 * nen Chromium khong dua duong dan tep ra cho trang. Cua so Windows nhan tha truc tiep
 * (MainForm.OnDragDrop) va day duong dan that sang day qua su kien "foldersDropped";
 * su kien "dropHover" chi bao lop phu nen bat/tat.
 *
 * Van giu preventDefault tren dragover: du WebView2 da dung nhan tha, nhung thieu lenh nay
 * tranh trinh duyet tu mo tep bi tha neu cau hinh AllowExternalDrop doi trong tuong lai. */
function showDropZone(on) {
  const zone = $('dropZone');
  if (zone) zone.classList.toggle('is-active', on === true);
}

function initDropZone() {
  const preventFileNavigation = (e) => {
    if (!hasFiles(e.dataTransfer)) return;
    e.preventDefault();
  };

  window.addEventListener('dragover', preventFileNavigation);
  window.addEventListener('drop', preventFileNavigation);
}

async function start() {
  const mode = $('selMode').value;
  const args = { dryRun: mode === 'dry' };

  if (mode === 'export') {
    const target = $('txtExport').value.trim();
    if (!target) {
      toast('Chọn thư mục đích trước khi xuất.', 'warn');
      $('txtExport').focus();
      return;
    }
    args.outputFolder = target;
  } else {
    args.outputFolder = null;
  }

  if (mode === 'overwrite') {
    const ok = await confirmDialog({
      title: 'Ghi đè tệp gốc',
      text: 'Tệp gốc sẽ bị thay bằng bản nén. Bản gốc được giữ lại trong tệp .bak cùng thư mục và có thể hoàn tác bất cứ lúc nào.',
      okText: 'Ghi đè',
      danger: false,
    });
    if (!ok) return;
  }

  const result = await call('start', args);
  if (result?.blocked?.length) {
    await confirmDialog({
      title: 'Chưa thể bắt đầu',
      text: 'Một công cụ cần thiết đang thiếu hoặc hỏng. Job chứa các loại media này sẽ bị bỏ qua cho tới khi bạn sửa.',
      items: result.blocked,
      okText: 'Mở cài đặt',
    });
    openSettings();
    renderToolList();
    return;
  }

  if (mode === 'export') toast('Đang xuất kết quả ra thư mục đích.', 'info');
  else if (mode === 'dry') toast('Đang chạy thử — tệp gốc chưa bị thay đổi.', 'info');
}

async function removeJob(job) {
  if (job.pendingBackups > 0) {
    const ok = await confirmDialog({
      title: 'Bỏ khỏi danh sách',
      text: `Job “${job.folderName}” đã ghi đè ${job.pendingBackups} tệp. Bỏ khỏi danh sách không hoàn tác — bản gốc vẫn nằm trong các tệp .bak.`,
      okText: 'Bỏ khỏi danh sách',
    });
    if (!ok) return;
  }
  await call('removeJob', { jobId: job.id });
}

async function reviewJob(job) {
  // Job chạy ở chế độ thử chưa ghi gì cả, nên "Duyệt" là nén thật rồi mới thay thế.
  if (job.dryRun) {
    const ok = await confirmDialog({
      title: 'Duyệt kết quả',
      text: `Nén thật ${job.folderName} và thay thế tệp gốc? Bản gốc được giữ ở tệp .bak cùng thư mục và có thể hoàn tác bất cứ lúc nào.`,
      okText: 'Nén và thay thế',
      danger: false,
    });
    if (!ok) return;

    const result = await call('commit', { jobId: job.id });
    if (result?.Failed > 0) {
      await confirmDialog({
        title: 'Duyệt chưa xong',
        text: `${result.Failed} tệp không xử lý được.`,
        items: result.Errors,
        okText: 'Đã biết',
        danger: false,
      });
    }
    return;
  }

  const ok = await confirmDialog({
    title: 'Duyệt kết quả',
    text: `Xoá ${job.pendingBackups} tệp sao lưu của “${job.folderName}”? Sau khi xoá sẽ không hoàn tác lại được nữa.`,
    okText: 'Duyệt và xoá bản sao lưu',
  });
  if (!ok) return;

  const result = await call('commit', { jobId: job.id });
  if (result?.Failed > 0) {
    await confirmDialog({
      title: 'Xoá bản sao lưu chưa xong',
      text: `${result.Failed} tệp không xoá được, thường là đang được mở bởi trình phát hoặc trình xem ảnh.`,
      items: result.Errors,
      okText: 'Đã biết',
      danger: false,
    });
  } else {
    toast(`Đã duyệt “${job.folderName}”.`, 'ok');
  }
}

async function undoJob(job) {
  const ok = await confirmDialog({
    title: 'Hoàn tác',
    text: `Khôi phục bản gốc cho mọi tệp đã nén trong “${job.folderName}”? Các tệp nén sẽ bị thay bằng bản gốc.`,
    okText: 'Hoàn tác',
  });
  if (!ok) return;

  const result = await call('undo', { jobId: job.id });
  if (result?.Failed > 0) {
    await confirmDialog({
      title: 'Hoàn tác chưa xong',
      text: `${result.Failed} tệp không khôi phục được.`,
      items: result.Errors,
      okText: 'Đã biết',
      danger: false,
    });
  } else {
    toast(`Đã khôi phục ${result.Restored} tệp.`, 'ok');
  }
}

async function undoItem(item) {
  const result = await call('undoItem', { jobId: openJobId, filePath: item.filePath });
  if (result?.ok) {
    toast(`Đã khôi phục “${item.fileName}”.`, 'ok');
  } else {
    toast(result?.error || 'Không khôi phục được.', 'error');
  }
}

async function preview(item, backup) {
  const result = await call('preview', { filePath: item.filePath, backup });
  if (!result?.ok) toast(result?.error || 'Không xem trước được.', 'error');
}

async function purgeBackups() {
  const data = await call('purgeBackups');
  toast(data.removed > 0
    ? `Đã dọn ${data.removed} tệp sao lưu quá hạn.`
    : 'Không có tệp sao lưu nào quá hạn.', 'ok');
}

// ============================================================ cài đặt

function openSettings() {
  const c = state.config;

  $('cfgMinSaving').value = c.minSavingPercent;
  $('cfgMinSize').value = Math.round((c.minFileSizeBytes || 0) / (1024 * 1024));
  $('cfgSubfolders').checked = c.includeSubfolders;
  $('cfgFreeSpace').checked = c.checkFreeSpace;
  $('cfgMeasureQuality').checked = c.measureQuality;
  $('cfgConcurrency').value = c.maxConcurrent;
  $('cfgKeepDays').value = c.keepBackupDays;
  $('cfgExclude').value = (c.excludePatterns || []).join(', ');
  $('cfgLogLevel').value = c.logLevel;
  $('selLevel').value = c.level;
  $('cpuHint').textContent = `Máy này có ${navigator.hardwareConcurrency || '?'} luồng xử lý. Để 0 để ứng dụng tự chọn.`;

  renderToolList();
  openModal('modalSettings');
}

function renderToolList() {
  const box = $('toolList');
  box.innerHTML = '';

  for (const t of state.tools) {
    const row = el('div', 'tool-row');

    const dot = el('span',
      t.health === 'Ok' || t.health === 'Optional' ? 'dotok'
        : t.health === 'Broken' ? 'dotbad' : 'dotwarn');
    row.appendChild(dot);
    row.appendChild(el('span', 'tname', t.displayName + (t.required ? '' : ' (tuỳ chọn)')));

    const msg = el('span', `tmsg ${t.health === 'Broken' ? 'err' : t.health === 'Missing' ? 'warn' : ''}`,
      t.message || t.version || t.path || 'Chưa kiểm tra');
    msg.title = t.path || '';
    row.appendChild(msg);

    const pick = el('button', 'btn btn-sm btn-ghost', 'Chọn…');
    pick.addEventListener('click', () => pickToolPath(t.kind));
    row.appendChild(pick);

    box.appendChild(row);
  }
}

async function pickToolPath(kind) {
  const data = await call('setToolPath', { kind, path: null });
  if (data?.cancelled) return;

  const refreshed = await call('checkTools');
  if (refreshed?.tools) {
    state.tools = refreshed.tools;
    renderToolList();
    renderStatusbar();
    renderAlerts();
  }
  toast(data?.ok ? 'Đã cập nhật đường dẫn công cụ.' : 'Không đổi được đường dẫn.', data?.ok ? 'ok' : 'error');
}

async function saveConfig() {
  const c = state.config;

  c.level = $('selLevel').value;
  c.minSavingPercent = Number($('cfgMinSaving').value) || 0;
  c.minFileSizeBytes = Math.round((Number($('cfgMinSize').value) || 0) * 1024 * 1024);
  c.includeSubfolders = $('cfgSubfolders').checked;
  c.checkFreeSpace = $('cfgFreeSpace').checked;
  c.measureQuality = $('cfgMeasureQuality').checked;
  c.maxConcurrent = Number($('cfgConcurrency').value) || 0;
  c.keepBackupDays = Number($('cfgKeepDays').value) || 0;
  c.excludePatterns = $('cfgExclude').value.split(',').map((s) => s.trim()).filter(Boolean);
  c.logLevel = $('cfgLogLevel').value;

  await call('saveConfig', { config: c });
  closeModal('modalSettings');
  toast('Đã lưu cài đặt.', 'ok');
}

// ============================================================ hướng dẫn + nhật ký

async function showGuide() {
  const g = await call('guide');

  const rows = (p) => `
    <tr><td>Video</td><td class="num">CRF ${p.crf}</td><td class="num">${p.preset}</td>
        <td class="num">${p.maxWidth}px</td><td class="num">128k</td></tr>
    <tr><td>Ảnh</td><td class="num">-q:v ${p.imageQuality}</td><td class="num">—</td>
        <td class="num">${p.maxWidth}px</td><td class="num">—</td></tr>
    <tr><td>Âm thanh</td><td class="num">${p.audioKbps}k</td><td class="num">—</td>
        <td class="num">—</td><td class="num">${p.audioKbps}k</td></tr>
    <tr><td>GIF</td><td class="num">--lossy ${p.gifLossy}</td><td class="num">—</td>
        <td class="num">—</td><td class="num">—</td></tr>
    <tr><td>PDF</td><td class="num">${p.pdf}</td><td class="num">—</td>
        <td class="num">—</td><td class="num">—</td></tr>`;

  $('guideBody').innerHTML = `
    <div class="guide">
      <h3>Ba cách ghi kết quả</h3>
      <p><b>Thử trước — chỉ xem kết quả</b> (mặc định): nén thật từng tệp để đo, ghi lại
      xem sẽ tiết kiệm bao nhiêu, rồi xoá kết quả. Tệp gốc không bị đụng tới.
      Bấm <b>Duyệt</b> khi đã hài lòng để nén thật và thay thế.</p>
      <p><b>Nén thật, thay thế tệp gốc</b>: ghi đè luôn. Bản gốc được giữ ở tệp
      <code>.bak</code> cùng thư mục và hoàn tác được bất cứ lúc nào.</p>
      <p><b>Xuất kết quả ra thư mục khác</b>: giữ nguyên thư mục gốc, kết quả nằm ở
      thư mục đích với đúng cấu trúc thư mục con.</p>

      <h3>Cách dùng</h3>
      <ol>
        <li>Bấm <b>Thêm thư mục</b> hoặc kéo thả thư mục vào cửa sổ. Thư mục con được quét tự động.</li>
        <li>Chọn <b>mức nén</b> và <b>cách ghi</b>.</li>
        <li>Bấm <b>Bắt đầu</b>. Xem kết quả ở cột <b>Tiết kiệm</b>.</li>
        <li>Mở <b>Chi tiết</b> để xem từng tệp, tệp nào bị giữ nguyên và vì sao.</li>
        <li>Bấm <b>Duyệt</b> để áp dụng, hoặc <b>Hoàn tác</b> để trả bản gốc về.</li>
      </ol>

      <h3>Vì sao tệp bị “Giữ nguyên”?</h3>
      <ul>
        <li><b>Không giảm</b> — bản nén lớn hơn bản gốc (thường gặp với video đã nén tốt sẵn).</li>
        <li><b>Tiết kiệm ít</b> — tiết kiệm dưới ngưỡng bạn đặt trong Cài đặt.</li>
        <li><b>Thiếu công cụ</b> — ví dụ nén PDF mà máy chưa có Ghostscript.</li>
        <li><b>Lỗi nén</b> — xem mục <b>Nhật ký</b> để biết nguyên nhân.</li>
      </ul>

      <h3>Tham số nén</h3>
      <table>
        <thead><tr><th>Loại</th><th class="num">Nhẹ</th><th class="num">Cân bằng</th>
          <th class="num">Mạnh</th><th class="num">Âm thanh</th></tr></thead>
        <tbody>
          ${g.levels.map(rows).join('')}
        </tbody>
      </table>
      <p>Các tham số này giữ nguyên như bản gốc. Chỉ có ngưỡng “tiết kiệm tối thiểu” được
      tách riêng và nằm trong Cài đặt, vì bản gốc gộp nhầm ngưỡng này vào mức nén nên mức
      “Mạnh” lại khó đạt hơn mức “Nhẹ”.</p>

      <h3>Định dạng hỗ trợ</h3>
      <p>${(g.extensions || []).join(' · ')}</p>
      <p>WAV và FLAC được bỏ qua có chủ ý — chúng thường là bản lưu trữ, nén lại chỉ tổn hại.</p>
    </div>`;

  openModal('modalGuide');
}

async function showLog() {
  const data = await call('logTail', { max: 500 });
  const entries = data?.entries || [];

  $('logView').innerHTML = entries.map((e) => {
    const time = new Date(e.at).toLocaleTimeString('vi-VN', { hour12: false });
    const scope = e.filePath ? `${e.category} | ${e.filePath}` : e.category;
    return `<span class="lv-${e.level}">${escapeHtml(time)} ${escapeHtml(e.level.toUpperCase().padEnd(7))} ${escapeHtml(scope)}</span>\n${escapeHtml(e.message)}\n`;
  }).join('') || 'Chưa có dòng nhật ký nào.';

  openModal('modalLog');
  $('logView').scrollTop = $('logView').scrollHeight;
}

// ============================================================ định dạng

function formatSize(bytes) {
  if (bytes == null) return '—';
  if (bytes < 0) return '—';
  if (bytes < 1024) return `${bytes} bytes`;
  if (bytes < 1048576) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1073741824) return `${(bytes / 1048576).toFixed(2)} MB`;
  return `${(bytes / 1073741824).toFixed(2)} GB`;
}

function formatTime(seconds) {
  if (seconds == null || seconds < 0) return '—';
  if (seconds > 86400) return '> 1 ngày';
  const s = Math.floor(seconds);
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  const pad = (n) => String(n).padStart(2, '0');
  return h > 0 ? `${pad(h)}:${pad(m)}:${pad(sec)}` : `${pad(m)}:${pad(sec)}`;
}

// ============================================================ gắn sự kiện

function wire() {
  $('btnAdd').addEventListener('click', async () => {
    const result = await call('browseFolder');
    if (result?.cancelled) return;

    const added = (result?.results || []).filter((r) => r.added).reduce((n, r) => n + r.files, 0);
    if (added > 0) toast(`Đã thêm ${added} tệp vào danh sách.`, 'ok');
    for (const p of result?.problems || []) toast(p, 'warn');
  });

  $('btnStart').addEventListener('click', start);

  $('btnPause').addEventListener('click', () => {
    call(state.isPaused ? 'resumeAll' : 'pauseAll');
  });

  $('btnStop').addEventListener('click', async () => {
    if (await confirmDialog({
      title: 'Dừng tất cả',
      text: 'Dừng mọi job đang chạy? Các tệp đã nén xong vẫn giữ nguyên, có thể chạy tiếp sau.',
      okText: 'Dừng tất cả',
    })) {
      await call('cancelAll');
    }
  });

  $('btnClear').addEventListener('click', async () => {
    if (await confirmDialog({
      title: 'Xoá danh sách',
      text: 'Xoá mọi job khỏi danh sách? Tệp trên đĩa không bị đụng tới. Các tệp đã nén vẫn hoàn tác được qua tệp .bak.',
      okText: 'Xoá danh sách',
    })) {
      await call('clearAll');
    }
  });

  $('btnUndoAll').addEventListener('click', async () => {
    const jobs = state.jobs.filter((j) => j.pendingBackups > 0);
    if (jobs.length === 0) return;

    const ok = await confirmDialog({
      title: 'Hoàn tác tất cả',
      text: `Khôi phục bản gốc cho ${jobs.length} thư mục? Mọi tệp đã bị nén sẽ trở lại bản gốc.`,
      items: jobs.map((j) => `${j.folderName} — ${j.pendingBackups} tệp`),
      okText: 'Hoàn tác tất cả',
    });
    if (!ok) return;

    for (const job of jobs) {
      const result = await call('undo', { jobId: job.id });
      if (result?.Restored > 0) toast(`“${job.folderName}”: đã khôi phục ${result.Restored} tệp.`, 'ok');
      for (const e of result?.Errors || []) toast(e, 'error');
    }
  });

  $('selMode').addEventListener('change', (e) => {
    $('exportField').hidden = e.target.value !== 'export';
  });

  $('selLevel').addEventListener('change', (e) => {
    state.config.level = e.target.value;
    call('saveConfig', { config: state.config });
  });

  $('searchJobs').addEventListener('input', (e) => {
    searchTerm = e.target.value.trim().toLowerCase();
    renderJobs();
  });

  $('btnCloseDetail').addEventListener('click', closeDetail);

  $('searchItems').addEventListener('input', debounce((e) => {
    itemSearch.search = e.target.value.trim();
    loadItems();
  }, 220));

  $('filterState').addEventListener('change', (e) => {
    itemSearch.state = e.target.value;
    loadItems();
  });

  $('filterKind').addEventListener('change', (e) => {
    itemSearch.kind = e.target.value;
    loadItems();
  });

  $('btnSettings').addEventListener('click', openSettings);
  $('btnGuide').addEventListener('click', showGuide);
  $('btnLog').addEventListener('click', showLog);
  $('btnRefreshLog').addEventListener('click', showLog);
  $('btnOpenLogs').addEventListener('click', () => call('openLogs'));
  $('btnPlayBoth').addEventListener('click', playBoth);
  $('btnPauseBoth').addEventListener('click', pauseBoth);
  $('btnOpenOriginal').addEventListener('click', async () => {
    const result = await call('playFile', { path: compareState?.original?.path });
    if (!result?.ok) toast(result?.error || 'Không mở được trình phát.', 'warn');
  });
  initCompareSplit();
  $('btnOpenData').addEventListener('click', () => call('openData'));
  $('btnCheckTools').addEventListener('click', async () => {
    const data = await call('checkTools');
    if (data?.tools) {
      state.tools = data.tools;
      renderStatusbar();
      renderAlerts();
    }
    toast('Đã kiểm tra lại công cụ.', 'ok');
  });

  $('btnSaveConfig').addEventListener('click', saveConfig);
  $('btnPurge').addEventListener('click', purgeBackups);

  $('btnTheme').addEventListener('click', () => {
    const next = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
    document.documentElement.dataset.theme = next;
    $('themeIcon').textContent = next === 'dark' ? '◐' : '◑';
    localStorage.setItem('uc-theme', next);
  });

  document.querySelectorAll('[data-close]').forEach((node) => {
    node.addEventListener('click', () => closeModal(node.closest('.modal-root').id));
  });

  document.addEventListener('keydown', (e) => {
    if (anyModalOpen()) return;
    const typing = ['INPUT', 'SELECT', 'TEXTAREA'].includes(document.activeElement?.tagName);
    if (typing) return;

    if (e.ctrlKey && e.key === 'o') { e.preventDefault(); $('btnAdd').click(); }
    else if (e.ctrlKey && e.key === 'Enter') { e.preventDefault(); if (!$('btnStart').disabled) start(); }
    else if (e.key === ' ') { e.preventDefault(); if (!$('btnPause').disabled) $('btnPause').click(); }
    else if (e.key === '?') { e.preventDefault(); showGuide(); }
    else if (e.key === 'l' || e.key === 'L') { e.preventDefault(); showLog(); }
    else if (e.key === 't' || e.key === 'T') { e.preventDefault(); $('btnTheme').click(); }
    else if (e.key === 'Escape' && openJobId) { closeDetail(); }
  });
}

function debounce(fn, ms) {
  let timer;
  return (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), ms);
  };
}

// ============================================================ khởi động

(function init() {
  const savedTheme = localStorage.getItem('uc-theme');
  if (savedTheme) {
    document.documentElement.dataset.theme = savedTheme;
    $('themeIcon').textContent = savedTheme === 'dark' ? '◐' : '◑';
  }

  wire();
  initDropZone();
  call('getState').then((data) => {
    state = data;
    $('selLevel').value = data.config.level;
    renderAll();
  }).catch((err) => {
    toast(`Không kết nối được với lõi xử lý: ${err.message}`, 'error');
  });
})();
