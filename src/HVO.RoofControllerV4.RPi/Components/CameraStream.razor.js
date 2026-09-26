let canvas;
let ctx;
let img;
let container;
let controls;
let raf;
let recording = false;
let rec;
let recChunks = [];
let hideTimer;

const isContainerFocused = () => !!container && container.contains(document.activeElement);
const isPointerOver = () => container?.matches(':hover');

function clearHideTimer() {
  if (hideTimer) {
    clearTimeout(hideTimer);
    hideTimer = undefined;
  }
}

function scheduleHide(delay = 250) {
  clearHideTimer();
  hideTimer = setTimeout(() => {
    if (!controls) {
      return;
    }

    if (recording || isContainerFocused() || isPointerOver()) {
      scheduleHide(delay);
      return;
    }

    controls.classList.add('is-hidden');
  }, delay);
}

function revealControls(persist = false) {
  if (!controls) {
    return;
  }

  controls.classList.remove('is-hidden');

  if (!persist) {
    scheduleHide();
  } else {
    clearHideTimer();
  }
}

function bindInteractionHandlers() {
  if (!container || container.dataset.mjpegBound === '1') {
    return;
  }

  const show = () => revealControls();
  const hideSoon = () => scheduleHide();
  const focusContainer = () => container && container.focus({ preventScroll: true });

  container.addEventListener('mousemove', show);
  container.addEventListener('click', show);
  container.addEventListener('keydown', show);
  container.addEventListener('touchstart', show, { passive: true });
  container.addEventListener('focus', show, true);
  container.addEventListener('blur', hideSoon, true);
  container.addEventListener('mouseleave', hideSoon);
  container.addEventListener('mousedown', focusContainer);
  container.addEventListener('touchstart', focusContainer, { passive: true });

  container.dataset.mjpegBound = '1';
}

function drawLoop() {
  if (!canvas || !img) {
    return;
  }

  try {
    ctx?.drawImage(img, 0, 0, canvas.width, canvas.height);
  } catch {
    // ignored – canvas might not be ready for a single frame
  }

  raf = requestAnimationFrame(drawLoop);
}

function stopLoop() {
  if (raf) {
    cancelAnimationFrame(raf);
    raf = undefined;
  }
}

export function init(canvasSel, imgSel, containerSel, controlsSel) {
  canvas = document.querySelector(canvasSel);
  img = document.querySelector(imgSel);
  container = document.querySelector(containerSel) || canvas?.parentElement || undefined;
  controls = controlsSel ? document.querySelector(controlsSel) : container?.querySelector('.controls-bar');

  if (canvas) {
    ctx = canvas.getContext('2d', { alpha: false, desynchronized: true });
  }

  if (container && !container.hasAttribute('tabindex')) {
    container.setAttribute('tabindex', '0');
  }

  if (!raf) {
    raf = requestAnimationFrame(drawLoop);
  }

  if (img) {
    img.onerror = () => {
      const url = img.src.split('?')[0];
      img.src = `${url}?t=${Date.now()}`;
    };
  }

  bindInteractionHandlers();
  revealControls();
}

export function play() {
  if (!raf) {
    raf = requestAnimationFrame(drawLoop);
  }

  revealControls();
}

export function pause() {
  stopLoop();
  revealControls(true);
}

export function restart(newSrc) {
  if (newSrc && img) {
    img.src = newSrc;
  }

  revealControls();
}

export async function fullscreen(containerSel) {
  const el = document.querySelector(containerSel) || container || canvas;
  revealControls();

  try {
    if (document.fullscreenElement) {
      await document.exitFullscreen();
    } else {
      await el?.requestFullscreen({ navigationUI: 'hide' });
    }
  } catch {
    // ignored
  }
}

export function snapshot(downloadLinkId) {
  if (!canvas) {
    return;
  }

  const a = document.getElementById(downloadLinkId);
  if (!a) {
    return;
  }

  const url = canvas.toDataURL('image/jpeg', 0.92);
  a.href = url;
  a.download = `snapshot-${new Date().toISOString().replace(/[:.]/g, '-')}.jpg`;
  a.style.display = 'inline-flex';
  a.textContent = 'Download snapshot';

  revealControls(true);
  scheduleHide(4000);
}

export function startRecord() {
  if (recording || !canvas || typeof MediaRecorder === 'undefined') {
    return;
  }

  const stream = canvas.captureStream(25);
  let mime = 'video/webm;codecs=vp9';
  if (!MediaRecorder.isTypeSupported(mime)) {
    mime = MediaRecorder.isTypeSupported('video/webm;codecs=vp8') ? 'video/webm;codecs=vp8' : 'video/webm';
  }

  try {
    rec = new MediaRecorder(stream, { mimeType: mime, videoBitsPerSecond: 4_000_000 });
  } catch {
    return;
  }

  recChunks = [];
  rec.ondataavailable = e => {
    if (e.data && e.data.size) {
      recChunks.push(e.data);
    }
  };
  rec.start(1000);
  recording = true;

  revealControls(true);
}

export function stopRecord(downloadLinkId) {
  if (!recording || !rec) {
    return;
  }

  const target = document.getElementById(downloadLinkId);
  if (!target) {
    rec.stop();
    recording = false;
    return;
  }

  rec.onstop = () => {
    const blob = new Blob(recChunks, { type: rec.mimeType });
    const url = URL.createObjectURL(blob);
    target.href = url;
    target.download = `recording-${new Date().toISOString().replace(/[:.]/g, '-')}.webm`;
    target.style.display = 'inline-flex';
    target.textContent = 'Download recording';

    revealControls(true);
    scheduleHide(4000);
  };

  rec.stop();
  recording = false;
}

export function dispose() {
  stopLoop();
  clearHideTimer();
  recChunks = [];
  rec = undefined;
  recording = false;
}
