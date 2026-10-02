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
    // Bỏ qua nếu người dùng đã đóng bảng chi tiết, tránh dựng lại hàng loạt DOM cho
    // một bảng không ai nhìn.
    if (data.jobId !== openJobId) return;
    items = data.items || [];
    renderItems();
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

/* Viết tắt 2 ký tự cho badge cột đầu. Rút gọn vì cột này chỉ 24px. */
const KIND_SHORT = { Image: 'Ả', Video: 'VD', Audio: 'Â', Gif: 'GIF', Pdf: 'PDF' };

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

function closeModal(id) {
  // Modal so sanh giu hai the <video> dang phat. An hop thoai khong du: phai dung phat
  // va xoa timer dong bo, neu khong video chay ngam va vong tua tiep tuc sau khi da
  // dong - moi lan mo lai lai chong them mot vong tua chay nen.
  if (id === 'modalCompare') disposeCompare();
  $(id).hidden = true;
}


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
  renderNowBar();
  renderResumeBar();

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
    !searchTerm || j.displayName.toLowerCase().includes(searchTerm));

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

  // Badge loại: thư mục thì gộp theo loại nhiều nhất, tệp lẻ thì theo chính tệp đó.
  const kinds = job.kindCounts ? Object.keys(job.kindCounts) : [];
  const primary = job.isFileJob
    ? (kinds[0] || 'File')
    : (kinds.sort((a, b) => (job.kindCounts[b] || 0) - (job.kindCounts[a] || 0))[0] || 'File');

  tr.appendChild(el('td', 'col-kind')).appendChild(
    el('span', `kind-badge k-${primary}`, job.isFileJob ? 'F' : (KIND_SHORT[primary] || '?')));
  tr.title = `${job.displayName} — ${job.totalFiles} tệp`;

  // Tên và đường dẫn phải nằm trong span riêng: đặt trực tiếp vào <td> thì
  // max-width của ô không có tác dụng với bảng có table-layout auto, chữ sẽ tràn
  // sang ô bên cạnh khi bảng chi tiết làm cột này bị bớp.
  const tdName = el('td', 'col-name');

  const name = el('span', 'j-name');
  if (job.isFileJob) name.appendChild(el('span', 'file-tag', 'TỆP'));

  // Mức nén của job. Nếu khác mức đang chọn thì tô đậm + cảnh báo, vì job sẽ nén bằng mức
  // riêng của nó chứ không phải mức trên thanh công cụ.
  const levelTag = el('span', `level-tag${job.levelDiffersFromCurrent ? ' is-stale' : ''}`, job.level || '—');
  levelTag.title = job.levelDiffersFromCurrent
    ? `Job này dùng mức "${job.level}", khác mức đang chọn trên thanh công cụ.`
    : `Mức nén: ${job.level}`;
  name.appendChild(levelTag);
  name.appendChild(document.createTextNode(job.displayName));

  tdName.appendChild(name);
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

  // Tiến độ: hai dòng để vừa trong cột hẹp. Dòng trên là thanh + phần trăm tổng thể,
  // dòng dưới là số tệp và tệp đang nén. Gộp tất cả vào một dòng thì ở 150px các con số
  // tràn sang ô bên cạnh và đè lên nút thao tác.
  const tdProgress = el('td', 'col-progress');
  const wrap = el('div', 'progress');
  const top = el('div', 'progress-top');
  const bar = el('div', 'progress-bar');
  const fill = el('div', 'progress-fill');
  if (job.status === 'PendingReview' || job.status === 'Committed') fill.classList.add('is-done');
  if (job.status === 'Failed') fill.classList.add('is-error');
  fill.style.width = `${Math.max(0, Math.min(100, job.progress))}%`;
  bar.appendChild(fill);
  top.append(bar, el('span', 'progress-text', `${job.progress}%`));
  wrap.appendChild(top);

  const bottom = el('div', 'progress-bottom');
  bottom.appendChild(el('span', 'progress-text', `${job.processedCount}/${job.totalFiles}`));

  // Khi job đang chạy, thanh tổng chỉ là số tệp. Người dùng cần biết đang bận tệp nào và
  // tới đâu — tệp nào kẹt ở 0% suốt 20 phút thì nhìn số tệp không thấy khác gì.
  if (job.activeFileName) {
    const active = el('div', 'active-file');
    active.appendChild(el('span', 'active-name', job.activeFileName));
    active.appendChild(el('span', 'active-pct', `${Math.max(0, job.activePercent)}%`));
    active.title = `Đang nén: ${job.activeFileName}`;
    bottom.appendChild(active);
  } else {
    bottom.appendChild(el('span', 'active-name', '—'));
  }

  wrap.appendChild(bottom);
  tdProgress.appendChild(wrap);
  tr.appendChild(tdProgress);

  // Hành động
  const tdActions = el('td', 'actions-col');
  const actions = el('div', 'row-actions');

  actions.appendChild(iconButton('☰', 'Chi tiết', () => openDetail(job.id)));

  // Mức nén của job lệch với mức đang chọn: cho nút sửa ngay, không bắt người dùng tự nhớ.
  if (job.levelDiffersFromCurrent) {
    actions.appendChild(iconButton('⟳', `Dùng mức đang chọn thay cho "${job.level}"`, async () => {
      const result = await call('applyLevel', { jobId: job.id });
      if (result?.ok) toast(`"${job.displayName}" chuyển sang mức ${result.level}.`, 'ok');
      else toast(result?.error || 'Không đổi được mức nén.', 'error');
    }, 'ok'));
  }

  if (job.canPause) {
    actions.appendChild(iconButton('❙❙', 'Tạm dừng job này', () => call('pauseJob', { jobId: job.id })));
  }
  if (job.status === 'Paused') {
    actions.appendChild(iconButton('▶', 'Tiếp tục job này', () => call('resumeJob', { jobId: job.id })));
  }
  if (job.canCancel) {
    actions.appendChild(iconButton('✕', 'Dừng job này', async () => {
      if (await confirmDialog({ title: 'Dừng job', text: `Dừng xử lý “${job.displayName}”?`, okText: 'Dừng' })) {
        await call('cancelJob', { jobId: job.id });
      }
    }, 'danger'));
  }
  // Nút Duyệt do engine quyết định (needsApprove), không tự đoán từ pendingBackups hay
  // dryRun. Đoán ở đây thì chỉ cần một điều kiện lệch là mất nút, và người dùng thấy job
  // "nén xong" mà không có nút nào để bấm.
  if (job.needsApprove) {
    const n = job.pendingBackups > 0
      ? 'Duyệt — dọn bản sao lưu'
      : 'Duyệt — nén thật và thay thế tệp gốc';
    actions.appendChild(iconButton('✓', n, () => reviewJob(job), 'ok'));
  }
  if (job.canReview && job.pendingBackups > 0) {
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

// Tệp nằm trong dòng bảng thì khi nhiều job chạy song song phải săn từng dòng. Ở đây
// gom tất cả tệp đang chạy về một chỗ, tên tệp phía sau là tên job cho khỏi lẫn.
function renderNowBar() {
  const bar = $('nowBar');
  const box = $('nowFiles');
  const active = state.jobs.filter((j) => j.activeFileName);

  if (active.length === 0) {
    bar.hidden = true;
    box.innerHTML = '';
    return;
  }

  bar.hidden = false;
  box.innerHTML = '';
  for (const job of active) {
    const item = el('div', 'nowbar-item');
    const jobName = el('span', 'nowbar-job', job.displayName);
    jobName.title = job.displayName;
    const file = el('span', 'nowbar-file', job.activeFileName);
    file.title = job.activeFileName;
    item.appendChild(jobName);
    item.appendChild(file);
    item.appendChild(el('span', 'nowbar-pct', `${Math.max(0, job.activePercent)}%`));
    box.appendChild(item);
  }
}

// Job nạp từ phiên lần trước còn chờ. Không tự chạy: người dùng vừa mở app lên, bấm
// nhầm là chạy tiếp cả mấy chục tệp rồi xoá bản gốc trước khi kịp nhìn.
function renderResumeBar() {
  const bar = $('resumeBar');
  const restored = state.jobs.filter((j) => j.wasRestored && j.status === 'Waiting');

  if (restored.length === 0) {
    bar.hidden = true;
    return;
  }

  bar.hidden = false;
  const files = restored.reduce((n, j) => n + (j.totalFiles - j.processedCount), 0);
  $('resumeText').innerHTML =
    `Có <b>${restored.length}</b> job chưa nén xong từ lần chạy trước `
    + `(${files} tệp). Bấm <b>Tiếp tục</b> để nén nốt, hoặc <b>Bỏ qua</b> để dọn khỏi danh sách.`;
  $('btnResume').dataset.jobs = restored.map((j) => j.id).join(',');
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
  itemRows.clear();
  delete $('itemBody').dataset.signature;
  document.querySelector('.workspace').classList.add('split');
  $('detailPanel').hidden = false;
  $('detailTitle').textContent = state.jobs.find((j) => j.id === jobId)?.displayName || 'Chi tiết';
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

  // Đang nén thì phát hai video trong ứng dụng sẽ tranh CPU với ffmpeg. Không phải
  // lỗi, nhưng người dùng sẽ tưởng ứng dụng treo — nên nói trước và chỉ đường vòng.
  if (payload?.busy) {
    $('compareNote').textContent =
      'Đang có job nén chạy. Phát trong ứng dụng sẽ chậm và dễ giật vì ffmpeg đang chiếm CPU —'
      + ' nên dùng "Mở cả hai cạnh nhau", ffplay chạy ngoài ứng dụng nên không tranh.';
    $('compareNote').hidden = false;
  }

  renderCompareSide('Original', data.original);
  renderCompareSide('Compressed', data.compressed);

  const playable = [data.original, data.compressed].some((s) => s && s.url && s.kind === 'Video');
  $('btnPlayBoth').disabled = !playable;
  $('btnPauseBoth').disabled = !playable;

  // ffplay canh nhau chi lam duoc khi ca hai ben deu la video va deu ton tai tren dia.
  // Thieu mot ben thi bam cung chi nhan loi, nen tat san cho khoi gay hy vong gia.
  const bothVideo = data.original?.kind === 'Video' && data.compressed?.kind === 'Video'
    && data.original?.exists && data.compressed?.exists;
  $('btnOpenBoth').disabled = !bothVideo;

  // Chạy thử thì trên đĩa chưa có bản nén, nút mở bản nén phải ẩn chứ không bấm
  // được rồi báo lỗi.
  $('btnOpenOriginal').hidden = !data.original?.exists;
  $('btnOpenCompressed').hidden = !data.compressed?.exists;
  $('btnOpenCompressed').disabled = !data.compressed?.exists;
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

  // Dòng báo lỗi phát media, mặc định ẩn. Dùng cho trường hợp thẻ <video>/<img> bị
  // Chromium từ chối: trình duyệt không đưa lý do lên giao diện, nên nếu không có dòng
  // này thì người dùng chỉ thấy khung đen và tưởng tệp hỏng.
  const note = el('p', 'compare-note', '');
  note.hidden = true;
  facts.appendChild(note);

  // GIF hiển thị bằng ảnh xem trước tĩnh, KHÔNG nhúng thẳng tệp .gif.
  //
  // Một GIF động nhúng bằng <img> sẽ vẽ lại liên tục trong đúng tiến trình
  // msedgewebview2 đang render cả giao diện, cộng dồn với video đang phát hai bên thì
  // kéo cả cửa sổ xuống khung hình. Ảnh tĩnh vẫn so được chất lượng mà không tốn gì; muốn
  // xem bản động thì bấm "Mở bản gốc ra ngoài".
  if (side.url && side.kind === 'Video') {
    const video = document.createElement('video');
    video.src = side.url;
    video.controls = true;
    video.preload = 'metadata';
    video.className = 'compare-media';
    video.setAttribute('playsinline', '');
    shot.appendChild(video);
    comparePlayers[key] = video;
    // Khung đen, 0:00, không báo gì là kiểu lỗi tệ nhất: người dùng không phân biệt
    // được "tệp hỏng" với "ứng dụng hỏng". Chromium không đưa lý do lên giao diện, nên
    // phải tự bắt sự kiện error và nói rõ.
    video.addEventListener('error', () => {
      const err = video.error;
      const why = err?.message ? `: ${err.message}` : '';
      note.textContent = `Không phát được (mã ${err?.code ?? '?'})${why}. Bấm "Mở bản gốc ra ngoài" để xem bằng trình phát khác.`;
      note.hidden = false;
    });
  } else if (side.url && side.kind === 'Image') {
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
  } else if (side.kind === 'Gif' && side.url) {
    // Không dựng được ảnh xem trước (thiếu ffmpeg) — vẫn hiện tệp thật cho có gì đó xem.
    const img = document.createElement('img');
    img.src = side.url;
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

/* Phát cả hai cùng lúc, và GIỮ cho chúng ở cùng thời điểm.

   Chỉ gọi play() trên cả hai là chưa đủ: hai tệp có độ dài và tốc độ giải mã khác nhau,
   chúng sẽ trôi dần ra khỏi nhau, và lúc đó bạn đang so hai khoảng thời gian KHÁC NHAU —
   tức so sánh sai. Người dùng tua một bên thì bên kia phải nhảy theo.

   Nhưng vòng tua phải CÓ HẠN. Bản trước cứ 100 ms một lần, lệch quá 0,15 giây là tua,
   không bao giờ dừng. Khi máy đang nén (ffmpeg chiếm hết CPU) thì hai bên không bao giờ
   hội tụ được, nên nó tua vô hạn — mỗi lần tua lại là một yêu cầu media mới đi qua
   WebResourceRequested, mà sự kiện đó chạy trên UI thread. Kết quả là cửa sổ đứng hình.
   Nay giới hạn số lần, giãn dần, rồi nói rõ thay vì cứ tua tiếp. */
const SYNC_TOLERANCE_SEC = 0.15;
const SYNC_MAX_ATTEMPTS = 8;
const SYNC_BACKOFF_MS = [100, 150, 250, 400, 600, 900, 1200, 1800];

let compareSync = null;

function stopCompareSync() {
  if (compareSync?.timer) clearInterval(compareSync.timer);
  compareSync = null;
}

function scheduleSync(state, delayIndex) {
  const delay = SYNC_BACKOFF_MS[Math.min(delayIndex, SYNC_BACKOFF_MS.length - 1)];
  state.timer = setInterval(() => tickSync(state), delay);
}

function tickSync(state) {
  const { first, rest } = state;
  if (first.paused) return;

  // Không tua phần tử chưa đủ dữ liệu: gán currentTime lúc nó đang đói thì chỉ làm nó
  // hỏng thêm, và sinh thêm một vòng yêu cầu media nữa.
  if (rest.some((p) => p.readyState < 2 || p.seeking)) return;

  const drift = Math.max(...rest.map((p) => Math.abs(p.currentTime - first.currentTime)));
  if (drift <= SYNC_TOLERANCE_SEC) {
    // Đã bám nhau trở lại: quên lần thử trước, bắt đầu đếm lại từ đầu.
    state.attempts = 0;
    // Chỉ xoá thông báo của vòng đồng bộ. compareNote còn đang giữ cảnh báo
    // "đang có job nén chạy" — xóa cả hai làm người dùng mất cảnh báo đó ngay
    // lúc vừa bật video xong.
    const note = $('compareNote');
    if (note.dataset.sync === '1') { note.hidden = true; note.dataset.sync = ''; }
    return;
  }

  state.attempts += 1;
  if (state.attempts > SYNC_MAX_ATTEMPTS) {
    stopCompareSync();
    const note = $('compareNote');
    note.textContent =
      `Đã dừng tự đồng bộ: hai bên lệch ${drift.toFixed(2)}s mà không bám được` +
      ` (thường là máy đang bận nén). Dùng "Mở cả hai cạnh nhau" — ffplay chạy ngoài ứng dụng nên không tranh CPU.`;
    note.dataset.sync = '1';
    note.hidden = false;
    return;
  }

  for (const p of rest) p.currentTime = first.currentTime;
  scheduleSync(state, state.attempts);
}

function playBoth() {
  const players = Object.values(comparePlayers).filter(Boolean);
  if (players.length === 0) return;

  for (const p of players) {
    p.currentTime = 0;
    p.muted = true;
  }

  for (const p of players) p.play().catch(() => {});

  const [first, ...rest] = players;
  if (!first || rest.length === 0) return;

  stopCompareSync();
  compareSync = { first, rest, attempts: 0, timer: null };
  scheduleSync(compareSync, 0);
}

function pauseBoth() {
  stopCompareSync();
  for (const p of Object.values(comparePlayers)) {
    if (p) p.pause();
  }
}

/** Dọn trình phát khi đóng modal so sánh.

    Bản trước `closeModal` chỉ ẩn hộp thoại: không dừng video, không xoá timer. Nghĩa là
    sau khi đóng, hai video vẫn chạy ngầm và vòng tua vẫn tiếp tục — mỗi lần mở lại là
    thêm một vòng tua chạy nền. Đó là lý do hiện tượng đứng hình tích luỹ dần theo số
    lần bấm chứ không xuất hiện ngay lần đầu. */
function disposeCompare() {
  pauseBoth();
  for (const key of Object.keys(comparePlayers)) {
    const p = comparePlayers[key];
    if (!p) continue;
    p.pause();
    // Bỏ hẳn src để Chromium giải phóng bộ giải mã và đóng file handle.
    p.removeAttribute('src');
    p.load();
    comparePlayers[key] = null;
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



/* ============================================================ bảng chi tiết */

/*
 * Giữ nguyên phần tử DOM của từng tệp thay vì dựng lại bảng mỗi 250 ms.
 *
 * Trước đây renderItems() xoá sạch tbody rồi tạo lại toàn bộ hàng, 4 lần mỗi giây:
 * mọi nút bị tạo lại, trạng thái rê chuột mất, và với job vài nghìn tệp thì đó là hàng
 * nghìn phần tử bị huỷ tạo mới mỗi lần — một trong các nguồn làm giao diện nặng.
 * Nay chỉ dựng lại khi danh sách tệp thực sự đổi; nếu không thì chỉ ghi vào ô cũ.
 */
const itemRows = new Map();

function renderItems() {
  const body = $('itemBody');

  // Danh sách tệp có đổi không (thêm/xoá/lọc)? Đổi thì dựng lại hàng.
  const signature = items.map((i) => i.filePath).join('\u0000');
  if (body.dataset.signature !== signature) {
    body.dataset.signature = signature;
    body.innerHTML = '';
    itemRows.clear();
    for (const item of items) body.appendChild(buildItemRow(item));
  } else {
    for (const item of items) updateItemRow(item);
  }

  const job = state.jobs.find((j) => j.id === openJobId);
  const total = job ? job.totalFiles : 0;
  const shown = items.length;
  $('itemFoot').textContent = shown === total
    ? `${total} tệp`
    : `Hiện ${shown} / ${total} tệp`;
}

function buildItemRow(item) {
  const tr = el('tr');
  tr.dataset.path = item.filePath;

  const tdName = el('td', 'col-name');
  tdName.appendChild(document.createTextNode(item.fileName));
  const sub = el('span', 'sub');
  tdName.appendChild(sub);
  tdName.title = item.filePath;
  tr.appendChild(tdName);

  tr.appendChild(el('td', 'num')).textContent = item.oldSizeText;
  tr.appendChild(el('td', 'num'));
  tr.appendChild(el('td', 'num'));

  const tdPct = el('td', 'num col-pct');
  tdPct.appendChild(el('span', 'pct-val'));
  tr.appendChild(tdPct);

  tr.appendChild(el('td'));

  const tdActions = el('td', 'actions-col');
  const actions = el('div', 'row-actions');

  const previewBtn = iconButton('▷', 'Xem bản gốc', () => preview(item, true));
  const compareBtn = iconButton('◫', 'So sánh bản gốc với bản nén', () =>
    openCompare(openJobId, item.filePath));
  const undoBtn = iconButton('↩', 'Khôi phục tệp này', () => undoItem(item), 'danger');
  undoBtn.hidden = true;
  const openBtn = iconButton('📂', 'Mở thư mục chứa tệp', () =>
    call('openPath', { path: item.filePath }));

  actions.append(previewBtn, compareBtn, undoBtn, openBtn);
  tdActions.appendChild(actions);
  tr.appendChild(tdActions);

  const cells = tr.querySelectorAll('td');
  const row = {
    tr,
    name: tdName,
    sub,
    oldSize: cells[1],
    newSize: cells[2],
    result: cells[3],
    pct: cells[4],
    state: cells[5],
    previewBtn,
    undoBtn,
  };

  itemRows.set(item.filePath, row);
  updateItemRow(item);
  return tr;
}

function updateItemRow(item) {
  const row = itemRows.get(item.filePath);
  if (!row) return;

  // Trạng thái + thanh tiến độ từng tệp, luôn có mặt ở cột % kể cả khi tệp đang chờ.
  //
  // Trước đây thanh chỉ hiện khi `isProcessing`, và mất ngay khi tệp xong — nên không
  // bao giờ thấy con số 100% của bất kỳ tệp nào, cũng không biết tệp đang chạy ở mức nào.
  const running = item.isProcessing;
  const done = item.state === 'done';
  const pct = done ? 100 : (running ? Math.max(0, item.percent || 0) : 0);

  row.pct.classList.toggle('is-running', running);
  row.pct.classList.toggle('is-done', done);
  row.pct.firstChild.textContent = running ? `${pct}%` : (done ? '100%' : '—');

  row.sub.textContent = [item.kind, item.durationText, item.elapsedText && `nén ${item.elapsedText}`]
    .filter(Boolean).join(' · ');

  row.oldSize.textContent = item.oldSizeText;
  row.newSize.textContent = (item.isApplied || item.isPredicted || item.detail) ? item.newSizeText : '—';

  row.result.innerHTML = '';
  if (item.savedBytes > 0) {
    row.result.appendChild(el('div', null, item.savedText));
    if (item.savedPercentText) row.result.appendChild(el('span', 'sub', item.savedPercentText));
  } else {
    row.result.appendChild(document.createTextNode('—'));
  }

  const tagClass = done ? 'tag-done'
    : item.state === 'skipped' ? 'tag-skipped'
    : item.state === 'predicted' ? 'tag-review'
    : running ? 'tag-running' : 'tag-queued';

  row.state.innerHTML = '';
  row.state.appendChild(el('span', `tag ${tagClass}`, item.stateText));
  if (item.detail) {
    row.state.appendChild(el('div', 'sub', item.detail));
  }

  // Tham số nén đã dùng cho tệp này. Đưa vào title để không làm bảng chật, nhưng người
  // dùng thì luôn thấy được bằng cách rê chuột — và không phải mở nhật ký ra đọc.
  const plan = item.plan || '';
  const notes = [item.message, item.detail, plan].filter(Boolean).join(' · ');
  row.state.title = notes;
  if (plan) {
    const planEl = el('div', 'sub plan-note', plan);
    planEl.title = plan;
    row.state.appendChild(planEl);
  }

  row.previewBtn.hidden = !(item.canPreview && item.hasCompressed);
  row.undoBtn.hidden = !(item.isApplied && item.hasBackup);
}

function closeDetail() {
  openJobId = null;
  items = [];
  itemRows.clear();
  $('itemBody').innerHTML = '';
  delete $('itemBody').dataset.signature;
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

// ============================================================ hành động

function reportAdded(data) {
  const results = data?.results || [];
  const problems = data?.problems || [];
  const added = results.filter((r) => r.added).reduce((n, r) => n + r.files, 0);

  if (added > 0) {
    toast(`Đã thêm ${added} tệp vào danh sách.`, 'ok');
  } else if (results.length > 0) {
    // Có thư mục/tệp được gửi tới nhưng không lấy được tệp nào đủ điều kiện. Im lặng ở
    // đây khiến người dùng tưởng kéo-thả hỏng; nói rõ đã nhận gì và vì sao không thêm.
    toast('Không thêm được tệp nào. Xem danh sách báo lỗi bên dưới.', 'warn');
  }

  for (const p of problems) toast(p, 'warn');
}

async function browse(folderPicker) {
  const result = await call(folderPicker ? 'browseFolder' : 'browseFiles');
  if (!result || result.cancelled) return;
  if (result.ok === false) { toast(result.error || 'Không mở được hộp thoại.', 'error'); return; }
  reportAdded(result);
}

// Chạy tiếp đúng việc đang dở: mỗi job giữ chế độ đã lưu, không lấy chế độ trên
// thanh công cụ. Gọi "start" ở đây sẽ đổi luôn DryRun của job đã nén dở.
async function resumeJobs() {
  const ids = ($('btnResume').dataset.jobs || '').split(',').filter(Boolean);
  if (ids.length === 0) return;

  const result = await call('resumeJobs', { jobIds: ids });
  if (result?.blocked?.length) {
    await confirmDialog({
      title: 'Chưa thể chạy tiếp',
      text: 'Một công cụ cần thiết đang thiếu hoặc hỏng. Job chứa các loại media này sẽ bị bỏ qua cho tới khi bạn sửa.',
      items: result.blocked,
      okText: 'Mở cài đặt',
    });
    openSettings();
    renderToolList();
    return;
  }

  toast(`Đang chạy tiếp ${ids.length} job.`, 'info');
}

// Bỏ khỏi danh sách: xoá hẳn khỏi phiên, không đụng tệp trên đĩa.
async function dismissResume() {
  const ids = ($('btnResume').dataset.jobs || '').split(',').filter(Boolean);
  for (const id of ids) await call('removeJob', { jobId: id });
  toast('Đã bỏ các job cũ khỏi danh sách. Tệp trên đĩa giữ nguyên.', 'info');
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
      text: `Job “${job.displayName}” còn ${job.pendingBackups} tệp đã nén xong nhưng bản gốc vẫn nằm trong <code>.bak</code> (thường là job bị Huỷ giữa chừng). Bỏ khỏi danh sách không hoàn tác — bản gốc vẫn còn, nhưng sẽ không còn nút Hoàn tác để bấm.`,
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
      text: `Nén thật ${job.displayName} và thay thế tệp gốc? Bản gốc được giữ ở tệp .bak cùng thư mục và có thể hoàn tác bất cứ lúc nào.`,
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
    text: `Xoá ${job.pendingBackups} tệp sao lưu của “${job.displayName}”? Sau khi xoá sẽ không hoàn tác lại được nữa.`,
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
    toast(`Đã duyệt “${job.displayName}”.`, 'ok');
  }
}

async function undoJob(job) {
  const ok = await confirmDialog({
    title: 'Hoàn tác',
    text: `Khôi phục bản gốc cho mọi tệp đã nén trong “${job.displayName}”? Các tệp nén sẽ bị thay bằng bản gốc.`,
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
    ? `Đã dọn ${data.removed} tệp .bak rơi vãi.`
    : 'Không có tệp .bak rơi vãi nào.', 'ok');
}

// ============================================================ cài đặt

function openSettings() {
  const c = state.config;

  $('cfgMinSaving').value = c.minSavingPercent;
  $('cfgVideoCodec').value = c.videoCodec || 'hevc';
  $('cfgMinSize').value = Math.round((c.minFileSizeBytes || 0) / (1024 * 1024));
  $('cfgSubfolders').checked = c.includeSubfolders;
  $('cfgFreeSpace').checked = c.checkFreeSpace;
  $('cfgMeasureQuality').checked = c.measureQuality;
  $('cfgAdaptiveSearch').checked = !!c.enableAdaptiveSearch;
  $('cfgConcurrency').value = c.maxConcurrent;
  $('cfgExclude').value = (c.excludePatterns || []).join(', ');
  $('cfgLogLevel').value = c.logLevel;
  $('selLevel').value = c.level;
  $('cpuHint').textContent = `Máy này có ${navigator.hardwareConcurrency || '?'} luồng xử lý. Để 0 để ứng dụng tự chọn.`;
  $('cfgProjectDir').textContent = state.projectDirectory || '—';
  $('cfgDataDir').textContent = state.dataDirectory || '—';

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
  c.videoCodec = $('cfgVideoCodec').value;
  c.minFileSizeBytes = Math.round((Number($('cfgMinSize').value) || 0) * 1024 * 1024);
  c.includeSubfolders = $('cfgSubfolders').checked;
  c.checkFreeSpace = $('cfgFreeSpace').checked;
  c.measureQuality = $('cfgMeasureQuality').checked;
  c.enableAdaptiveSearch = $('cfgAdaptiveSearch').checked;
  c.maxConcurrent = Number($('cfgConcurrency').value) || 0;
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
        <td class="num">${p.videoMaxWidth}px</td><td class="num">${p.audioKbps}k</td></tr>
    <tr><td>Ảnh</td><td class="num">-q:v ${p.imageQuality}</td><td class="num">—</td>
        <td class="num">${p.imageMaxWidth}px</td><td class="num">—</td></tr>
    <tr><td>Âm thanh</td><td class="num">${p.audioKbps}k</td><td class="num">—</td>
        <td class="num">—</td><td class="num">${p.audioKbps}k</td></tr>
    <tr><td>GIF</td><td class="num">--lossy ${p.gifLossy}</td>
        <td class="num">${p.gifFps} fps${p.gifScale < 1 ? ` · x${p.gifScale}` : ''}</td>
        <td class="num">—</td><td class="num">—</td></tr>
    <tr><td>PDF</td><td class="num">${p.pdf}</td><td class="num">—</td>
        <td class="num">—</td><td class="num">—</td></tr>`;

  $('guideBody').innerHTML = `
    <div class="guide">
      <h3>Thêm việc vào danh sách</h3>
      <ul>
        <li><b>Thêm thư mục</b> hoặc <code>Ctrl+O</code> — thư mục con được quét tự động.</li>
        <li><b>Thêm tệp</b> hoặc <code>Ctrl+Shift+O</code> — chọn một hoặc nhiều tệp lẻ.</li>
        <li><b>Kéo thả</b> thư mục hoặc tệp vào bất kỳ đâu trong cửa sổ này.</li>
      </ul>

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
        <li>Thêm thư mục / tệp, hoặc kéo thả vào cửa sổ.</li>
        <li>Chọn <b>mức nén</b> và <b>cách ghi</b>.</li>
        <li>Bấm <b>Bắt đầu</b>. Dòng hàng đợi cho biết đang nén tệp nào và tới đâu.</li>
        <li>Mở <b>Chi tiết</b> (nút ☰) để xem từng tệp. Cột <b>%</b> chạy theo từng tệp đang
            nén, cột <b>Trạng thái</b> nói tệp nào bị giữ nguyên và vì sao.</li>
        <li>Bấm <b>◫</b> trên một tệp để <b>so sánh song song</b> bản gốc với bản đã nén:
            video và âm thanh phát thẳng trong ứng dụng, có thanh kéo giữa hai cột để
            chia tỉ lệ.</li>
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
      <p><b>Một mức nén là một con số dùng chung</b> cho mọi loại media — không có mức riêng
      cho ảnh, cho video hay cho GIF. Nhưng mỗi loại đọc <b>thông số riêng</b> từ mức đó,
      nên chọn “Cân bằng” nghĩa là đồng thời: video CRF 23, ảnh <code>-q:v 5</code>,
      âm thanh 192k, PDF <code>/ebook</code>, GIF lossy 20/20 fps. Bảng dưới liệt kê
      từng thông số cho ba mức.</p>
      <table>
        <thead><tr><th>Loại</th><th class="num">Nhẹ</th><th class="num">Cân bằng</th>
          <th class="num">Mạnh</th><th class="num">Âm thanh</th></tr></thead>
        <tbody>
          ${g.levels.map(rows).join('')}
        </tbody>
      </table>
      <p>Độ rộng tối đa <b>tách riêng cho ảnh và video</b>, vì chúng bị ràng buộc bởi hai
      thứ khác nhau: ảnh nhìn toàn màn hình và có thể phóng to, còn video đã bị giới hạn bởi
      khung hình mà mắt theo kịp. Nên mức “Nhẹ” giữ video tới 4K nhưng ảnh chỉ 2560px; mức
      “Mạnh” giữ video ở 1920px còn ảnh vẫn 1600px. Riêng PDF, <code>/prepress</code> của
      bản cũ là thiết lập cho in offset (giữ ảnh 300dpi, tệp rất lớn) nên đã đổi thành
      <code>/default</code> — đúng nghĩa “giữ chất lượng”. Tham số này lấy từ bản gốc; chỉ có
      ngưỡng “tiết kiệm tối thiểu” được tách riêng và nằm trong Cài đặt, vì bản gốc gộp nhầm
      ngưỡng này vào mức nén nên mức “Mạnh” lại khó đạt hơn mức “Nhẹ”.</p>

      <h3>Định dạng hỗ trợ</h3>
      <p>${(g.extensions || []).join(' · ')}</p>
      <p>WAV và FLAC được bỏ qua có chủ ý — chúng thường là bản lưu trữ, nén lại chỉ tổn hại.</p>

      <h3>Nơi lưu dữ liệu</h3>
      <dl class="pathlist">
        <dt>Dự án</dt><dd class="mono">${escapeHtml(g.appDirectory || '—')}</dd>
        <dt>Dữ liệu</dt><dd class="mono">${escapeHtml(g.dataDirectory || '—')}</dd>
      </dl>
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
  $('btnAdd').addEventListener('click', () => browse(true));
  $('btnAddFiles').addEventListener('click', () => browse(false));

  $('btnPickExport').addEventListener('click', async () => {
    const result = await call('browseExport');
    if (result?.ok && result.path) $('txtExport').value = result.path;
  });

  $('btnStart').addEventListener('click', start);
$('btnResume').addEventListener('click', resumeJobs);
$('btnDismissResume').addEventListener('click', dismissResume);

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
      if (result?.Restored > 0) toast(`“${job.displayName}”: đã khôi phục ${result.Restored} tệp.`, 'ok');
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
  $('btnOpenBoth').addEventListener('click', async () => {
    const jobId = openJobId;
    const filePath = compareState?.filePath;
    if (!jobId || !filePath) return;

    const result = await call('playBothExternal', { jobId, filePath });
    if (!result?.ok) toast(result?.error || 'Không mở được ffplay.', 'warn');
  });
  $('btnPauseBoth').addEventListener('click', pauseBoth);
  for (const [button, key] of [['btnOpenOriginal', 'original'], ['btnOpenCompressed', 'compressed']]) {
    $(button).addEventListener('click', async () => {
      const side = compareState?.[key];
      if (!side?.path) return;

      const result = await call('playFile', { path: side.path });
      if (!result?.ok) toast(result?.error || 'Không mở được trình phát.', 'warn');
    });
  }
  initCompareSplit();
  $('btnOpenProject').addEventListener('click', () => call('openProject'));
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

    if (e.ctrlKey && e.shiftKey && (e.key === 'o' || e.key === 'O')) { e.preventDefault(); $('btnAddFiles').click(); }
    else if (e.ctrlKey && e.key === 'o') { e.preventDefault(); $('btnAdd').click(); }
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
  call('getState').then((data) => {
    state = data;
    $('selLevel').value = data.config.level;
    renderAll();
  }).catch((err) => {
    toast(`Không kết nối được với lõi xử lý: ${err.message}`, 'error');
  });
})();
