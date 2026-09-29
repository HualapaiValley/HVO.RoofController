// MJPEG camera player. Each CameraStream component owns one player created by createPlayer(), reading the web UI's
// relay of the camera (camera/{id}/mjpeg) from the page's own origin.
//
// The stream is read with fetch() and split into JPEG frames here rather than handed to an <img>, because an <img>
// keeps showing the last frame when a connected stream stops delivering. Every decoded frame records its arrival
// time, so "live" is only ever reported with per-frame evidence; a connection that stays open without frames is
// reported as stalled, and a failed connection as reconnecting/offline.

const STALL_AFTER_MS = 5000;
const ABANDON_STALLED_AFTER_MS = 15000;
const WATCHDOG_INTERVAL_MS = 1000;
const INITIAL_BACKOFF_MS = 1000;
const MAX_BACKOFF_MS = 30000;
const MAX_BUFFER_BYTES = 8 * 1024 * 1024;
const HEADER_SCAN_BYTES = 1024;

function indexOfMarker(bytes, start, end, second) {
  for (let i = start; i < end - 1; i++) {
    if (bytes[i] === 0xff && bytes[i + 1] === second) {
      return i;
    }
  }
  return -1;
}

function readContentLength(bytes, start, end) {
  const from = Math.max(start, end - HEADER_SCAN_BYTES);
  let text = '';
  for (let i = from; i < end; i++) {
    text += String.fromCharCode(bytes[i]);
  }
  const matches = [...text.matchAll(/content-length:\s*(\d+)/gi)];
  if (matches.length === 0) {
    return -1;
  }
  const value = Number.parseInt(matches[matches.length - 1][1], 10);
  return Number.isFinite(value) && value > 0 ? value : -1;
}

// Splits a multipart/x-mixed-replace byte stream into JPEG frames. Uses the part's Content-Length when present and
// falls back to SOI/EOI markers otherwise.
export class MjpegParser {
  constructor(onFrame) {
    this.onFrame = onFrame;
    this.buffer = new Uint8Array(64 * 1024);
    this.start = 0;
    this.end = 0;
    // Positions that let a frame arriving in many chunks be scanned once rather than on every chunk.
    this.waitUntil = 0;
    this.eoiScanFrom = 0;
  }

  push(chunk) {
    this.append(chunk);
    this.parse();
  }

  append(chunk) {
    const pending = this.end - this.start;
    if (pending + chunk.length > MAX_BUFFER_BYTES) {
      // A frame this large means the stream is not what we expect; drop what we have and resynchronize.
      this.start = 0;
      this.end = 0;
      this.waitUntil = 0;
      this.eoiScanFrom = 0;
    }

    if (this.end + chunk.length > this.buffer.length) {
      const shift = this.start;
      const remaining = this.end - this.start;
      if (remaining + chunk.length <= this.buffer.length) {
        this.buffer.copyWithin(0, this.start, this.end);
      } else {
        const next = new Uint8Array(Math.min((remaining + chunk.length) * 2, MAX_BUFFER_BYTES * 2));
        next.set(this.buffer.subarray(this.start, this.end), 0);
        this.buffer = next;
      }
      this.start = 0;
      this.end = remaining;
      this.waitUntil = Math.max(0, this.waitUntil - shift);
      this.eoiScanFrom = Math.max(0, this.eoiScanFrom - shift);
    }

    this.buffer.set(chunk, this.end);
    this.end += chunk.length;
  }

  parse() {
    const bytes = this.buffer;
    for (;;) {
      if (this.end < this.waitUntil) {
        return;
      }

      const soi = indexOfMarker(bytes, this.start, this.end, 0xd8);
      if (soi < 0) {
        // Keep a possible partial marker and any part headers still being received.
        this.start = Math.max(this.start, this.end - HEADER_SCAN_BYTES);
        return;
      }

      const contentLength = readContentLength(bytes, this.start, soi);
      let frameEnd = -1;
      if (contentLength > 0) {
        if (this.end - soi < contentLength) {
          this.start = Math.max(this.start, soi - HEADER_SCAN_BYTES);
          this.waitUntil = soi + contentLength;
          return;
        }
        frameEnd = soi + contentLength;
      } else {
        const eoi = indexOfMarker(bytes, Math.max(soi + 2, this.eoiScanFrom), this.end, 0xd9);
        if (eoi < 0) {
          this.start = Math.max(this.start, soi - HEADER_SCAN_BYTES);
          this.eoiScanFrom = Math.max(soi + 2, this.end - 1);
          return;
        }
        frameEnd = eoi + 2;
      }

      this.waitUntil = 0;
      this.eoiScanFrom = 0;
      this.onFrame(bytes.slice(soi, frameEnd));
      this.start = frameEnd;
    }
  }
}

function backoffDelay(attempt) {
  const ceiling = Math.min(MAX_BACKOFF_MS, INITIAL_BACKOFF_MS * 2 ** Math.min(attempt, 10));
  return Math.round(ceiling / 2 + Math.random() * (ceiling / 2));
}

class CameraPlayer {
  constructor(container, canvas, controls, downloadLink, dotNetRef, url) {
    this.container = container;
    this.canvas = canvas;
    this.controls = controls;
    this.downloadLink = downloadLink;
    this.dotNetRef = dotNetRef;
    this.url = url;
    this.ctx = canvas ? canvas.getContext('2d', { alpha: false }) : null;

    this.disposed = false;
    this.paused = false;
    this.abort = null;
    this.reader = null;
    this.retryTimer = undefined;
    this.watchdogTimer = undefined;
    this.hideTimer = undefined;
    this.attempt = 0;
    this.connectedAt = 0;
    this.lastFrameAt = 0;
    this.decoding = false;
    this.pendingFrame = null;
    this.state = '';

    this.recorder = null;
    this.recorderStream = null;
    this.recordChunks = [];
    this.downloadUrl = null;

    this.listeners = [];
    this.bindInteraction();
    this.revealControls();
    this.watchdogTimer = setInterval(() => this.checkFreshness(), WATCHDOG_INTERVAL_MS);
    this.connect();
  }

  // ----- state reporting -----

  report(state, detail) {
    if (this.disposed) {
      return;
    }
    const key = `${state}|${detail ?? ''}`;
    if (key === this.state) {
      return;
    }
    this.state = key;
    const lastFrameUnixMs = this.lastFrameAt ? Date.now() - (performance.now() - this.lastFrameAt) : null;
    this.dotNetRef.invokeMethodAsync('OnStreamStateChanged', state, detail ?? null, lastFrameUnixMs)
      .catch(() => { /* circuit gone; the component is being disposed */ });
  }

  // ----- connection lifecycle -----

  async connect() {
    if (this.disposed || this.paused) {
      return;
    }

    this.cancelConnection();
    const controller = new AbortController();
    this.abort = controller;
    this.connectedAt = 0;
    this.report(this.lastFrameAt ? 'reconnecting' : 'connecting');

    const separator = this.url.includes('?') ? '&' : '?';
    const requestUrl = `${this.url}${separator}t=${Date.now()}`;

    try {
      const response = await fetch(requestUrl, {
        credentials: 'same-origin',
        cache: 'no-store',
        signal: controller.signal
      });

      if (this.disposed || controller.signal.aborted) {
        // Cancelled while the response was on its way: close it, and leave the attempt that replaced it alone.
        response.body?.cancel().catch(() => { /* already closed */ });
        return;
      }

      if (!response.ok || !response.body) {
        this.fail(response.status === 401 || response.status === 403 ? 'unauthorized' : 'offline', `HTTP ${response.status}`);
        return;
      }

      this.connectedAt = performance.now();
      this.report('waiting');
      const parser = new MjpegParser(frame => this.queueFrame(frame));
      const reader = response.body.getReader();
      this.reader = reader;

      for (;;) {
        const { value, done } = await reader.read();
        if (done) {
          break;
        }
        if (this.disposed || controller.signal.aborted) {
          return;
        }
        if (value && value.length) {
          parser.push(value);
        }
      }

      if (!controller.signal.aborted) {
        this.fail('offline', 'Stream ended');
      }
    } catch (error) {
      if (this.disposed || controller.signal.aborted) {
        return;
      }
      this.fail('offline', error && error.name === 'TypeError' ? 'Network error' : 'Stream error');
    } finally {
      if (this.abort === controller) {
        this.reader = null;
      }
    }
  }

  fail(state, detail) {
    if (this.disposed) {
      return;
    }
    this.cancelConnection();
    const delay = backoffDelay(this.attempt++);
    this.report(state, `${detail}; retrying in ${Math.ceil(delay / 1000)} s`);
    this.scheduleRetry(delay);
  }

  scheduleRetry(delay) {
    this.clearRetry();
    this.retryTimer = setTimeout(() => {
      this.retryTimer = undefined;
      this.connect();
    }, delay);
  }

  clearRetry() {
    if (this.retryTimer !== undefined) {
      clearTimeout(this.retryTimer);
      this.retryTimer = undefined;
    }
  }

  cancelConnection() {
    const controller = this.abort;
    this.abort = null;
    if (controller) {
      try {
        controller.abort();
      } catch {
        // ignored
      }
    }
    const reader = this.reader;
    this.reader = null;
    if (reader) {
      reader.cancel().catch(() => { /* already closed */ });
    }
  }

  checkFreshness() {
    if (this.disposed || this.paused || !this.connectedAt) {
      return;
    }

    const now = performance.now();
    const reference = Math.max(this.lastFrameAt, this.connectedAt);
    const age = now - reference;
    if (age < STALL_AFTER_MS) {
      return;
    }

    if (age >= ABANDON_STALLED_AFTER_MS) {
      this.fail('stalled', 'No frames received');
      return;
    }

    this.report('stalled', 'Connected, but no new frame');
  }

  // ----- frames -----

  queueFrame(frame) {
    if (this.disposed) {
      return;
    }
    this.pendingFrame = frame;
    if (!this.decoding) {
      this.drainFrames();
    }
  }

  async drainFrames() {
    this.decoding = true;
    try {
      while (this.pendingFrame && !this.disposed) {
        const frame = this.pendingFrame;
        this.pendingFrame = null;
        let bitmap;
        try {
          bitmap = await createImageBitmap(new Blob([frame], { type: 'image/jpeg' }));
        } catch {
          continue; // corrupt or partial frame; wait for the next one
        }

        try {
          if (this.disposed || !this.ctx) {
            return;
          }
          if (this.canvas.width !== bitmap.width || this.canvas.height !== bitmap.height) {
            this.canvas.width = bitmap.width;
            this.canvas.height = bitmap.height;
          }
          this.ctx.drawImage(bitmap, 0, 0);
          this.lastFrameAt = performance.now();
          this.attempt = 0;
          if (!this.paused) {
            this.report('live');
          }
        } finally {
          bitmap.close();
        }
      }
    } finally {
      this.decoding = false;
    }
  }

  // ----- commands from .NET -----

  play() {
    if (this.disposed) {
      return;
    }
    this.paused = false;
    this.attempt = 0;
    this.clearRetry();
    this.connect();
    this.revealControls();
  }

  pause() {
    if (this.disposed) {
      return;
    }
    this.paused = true;
    this.clearRetry();
    this.cancelConnection();
    this.connectedAt = 0;
    this.report('paused');
    this.revealControls(true);
  }

  restart() {
    this.play();
  }

  async fullscreen() {
    if (this.disposed) {
      return;
    }
    this.revealControls();
    try {
      if (document.fullscreenElement) {
        await document.exitFullscreen();
      } else {
        await this.container?.requestFullscreen({ navigationUI: 'hide' });
      }
    } catch {
      // Fullscreen can be refused by the browser; nothing to do.
    }
  }

  snapshot() {
    if (this.disposed || !this.canvas || !this.lastFrameAt) {
      return false;
    }
    this.publishDownload(this.canvas.toDataURL('image/jpeg', 0.92), 'snapshot', 'jpg', 'Download snapshot');
    return true;
  }

  startRecord() {
    if (this.disposed || this.recorder || !this.canvas || typeof MediaRecorder === 'undefined' || !this.canvas.captureStream) {
      return false;
    }

    const stream = this.canvas.captureStream(25);
    let mime = 'video/webm;codecs=vp9';
    if (!MediaRecorder.isTypeSupported(mime)) {
      mime = MediaRecorder.isTypeSupported('video/webm;codecs=vp8') ? 'video/webm;codecs=vp8' : 'video/webm';
    }

    let recorder;
    try {
      recorder = new MediaRecorder(stream, { mimeType: mime, videoBitsPerSecond: 4_000_000 });
    } catch {
      stream.getTracks().forEach(track => track.stop());
      return false;
    }

    const chunks = [];
    recorder.ondataavailable = event => {
      if (event.data && event.data.size) {
        chunks.push(event.data);
      }
    };
    recorder.onstop = () => {
      stream.getTracks().forEach(track => track.stop());
      if (this.disposed || chunks.length === 0) {
        return;
      }
      const blob = new Blob(chunks, { type: recorder.mimeType || 'video/webm' });
      this.publishDownload(URL.createObjectURL(blob), 'recording', 'webm', 'Download recording');
    };

    this.recorder = recorder;
    this.recorderStream = stream;
    this.recordChunks = chunks;
    recorder.start(1000);
    this.revealControls(true);
    return true;
  }

  stopRecord() {
    const recorder = this.recorder;
    this.recorder = null;
    this.recorderStream = null;
    if (recorder && recorder.state !== 'inactive') {
      recorder.stop();
    }
    this.scheduleHide(4000);
    return true;
  }

  publishDownload(href, prefix, extension, label) {
    if (this.disposed || !this.downloadLink) {
      if (href.startsWith('blob:')) {
        URL.revokeObjectURL(href);
      }
      return;
    }
    this.revokeDownload();
    if (href.startsWith('blob:')) {
      this.downloadUrl = href;
    }
    this.downloadLink.href = href;
    this.downloadLink.download = `${prefix}-${new Date().toISOString().replace(/[:.]/g, '-')}.${extension}`;
    this.downloadLink.textContent = label;
    this.downloadLink.style.display = 'inline-flex';
    this.revealControls(true);
    this.scheduleHide(4000);
  }

  revokeDownload() {
    if (this.downloadUrl) {
      URL.revokeObjectURL(this.downloadUrl);
      this.downloadUrl = null;
    }
  }

  // ----- control bar auto-hide -----

  bindInteraction() {
    const container = this.container;
    if (!container) {
      return;
    }
    const on = (target, type, handler, options) => {
      target.addEventListener(type, handler, options);
      this.listeners.push(() => target.removeEventListener(type, handler, options));
    };
    const show = () => this.revealControls();
    const hideSoon = () => this.scheduleHide();
    const focusContainer = () => container.focus({ preventScroll: true });

    on(container, 'mousemove', show);
    on(container, 'click', show);
    on(container, 'keydown', show);
    on(container, 'touchstart', show, { passive: true });
    on(container, 'focus', show, true);
    on(container, 'blur', hideSoon, true);
    on(container, 'mouseleave', hideSoon);
    on(container, 'mousedown', focusContainer);
    on(container, 'touchstart', focusContainer, { passive: true });
  }

  clearHide() {
    if (this.hideTimer !== undefined) {
      clearTimeout(this.hideTimer);
      this.hideTimer = undefined;
    }
  }

  scheduleHide(delay = 2500) {
    this.clearHide();
    if (this.disposed) {
      return;
    }
    this.hideTimer = setTimeout(() => {
      this.hideTimer = undefined;
      if (this.disposed || !this.controls) {
        return;
      }
      const container = this.container;
      const busy = this.recorder || this.paused || (container && (container.contains(document.activeElement) || container.matches(':hover')));
      if (busy) {
        this.scheduleHide(delay);
        return;
      }
      this.controls.classList.add('is-hidden');
    }, delay);
  }

  revealControls(persist = false) {
    if (this.disposed || !this.controls) {
      return;
    }
    this.controls.classList.remove('is-hidden');
    if (persist) {
      this.clearHide();
    } else {
      this.scheduleHide();
    }
  }

  // ----- teardown -----

  dispose() {
    if (this.disposed) {
      return;
    }
    // Mark disposed first so late callbacks (fetch reads, decode, recorder onstop) do nothing.
    this.disposed = true;

    this.clearRetry();
    this.clearHide();
    if (this.watchdogTimer !== undefined) {
      clearInterval(this.watchdogTimer);
      this.watchdogTimer = undefined;
    }
    this.cancelConnection();
    this.pendingFrame = null;

    const recorder = this.recorder;
    const recorderStream = this.recorderStream;
    this.recorder = null;
    this.recorderStream = null;
    this.recordChunks = [];
    if (recorder && recorder.state !== 'inactive') {
      try {
        recorder.stop();
      } catch {
        // ignored
      }
    }
    if (recorderStream) {
      recorderStream.getTracks().forEach(track => track.stop());
    }

    this.revokeDownload();
    this.listeners.forEach(remove => remove());
    this.listeners = [];
    this.dotNetRef = null;
  }
}

export function createPlayer(container, canvas, controls, downloadLink, dotNetRef, url) {
  return new CameraPlayer(container, canvas, controls, downloadLink, dotNetRef, url);
}
