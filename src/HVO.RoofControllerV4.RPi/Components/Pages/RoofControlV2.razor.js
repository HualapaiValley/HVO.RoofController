// Scoped JS for RoofControlV2 component
// Note: Browsers do not natively play RTSP. This module attempts to provide guidance
// and limited support if the stream is exposed as HLS (.m3u8) or MP4.

let player;

export async function initCamera(videoEl, url) {
  if (!videoEl) return;
  try {
    const lower = (url || '').toLowerCase();

    // MJPEG streams are rendered via <img> in the component; nothing to initialize here.
    if (lower.endsWith('.mjpg') || lower.endsWith('.mjpeg') || lower.includes('/mjpg/')) {
      return;
    }

    // RTSP is not supported in browsers. Show a message overlay.
    if (lower.startsWith('rtsp://')) {
      // Try a helpful default: MediaMTX-style HLS endpoint on the same host, port 8888
      // Example: rtsp://192.168.0.92/live.sdp -> http://192.168.0.92:8888/live.sdp/index.m3u8
      const hlsUrl = rtspToHls(url);
      if (hlsUrl) {
        console.info('Attempting HLS fallback for RTSP stream:', hlsUrl);
        await tryHls(videoEl, hlsUrl);
        return;
      }
      console.warn('RTSP is not supported by browsers. Use an RTSP-to-HLS or RTSP-to-WebRTC gateway.');
      showOverlay(videoEl, 'RTSP is not playable in the browser. Please proxy via HLS or WebRTC.');
      return;
    }

    // Simple HLS path (native in Safari). For Chromium, you can add hls.js support later.
    if (lower.endsWith('.m3u8')) { return await tryHls(videoEl, url); }

    // Fallback: assume direct MP4 or a source the browser can handle
    videoEl.src = url;
    await videoEl.play().catch(() => {/* ignore */});
  } catch (err) {
    console.error('initCamera error', err);
    showOverlay(videoEl, 'Unable to start stream.');
  }
}

async function tryHls(videoEl, url) {
  // Native support (Safari)
  if (videoEl.canPlayType('application/vnd.apple.mpegurl')) {
    videoEl.src = url;
    await videoEl.play().catch(() => {/* ignore */});
    return;
  }

  // Dynamically import hls.js for Chromium/Firefox
  try {
    const mod = await import('https://cdn.jsdelivr.net/npm/hls.js@1.5.12/dist/hls.min.js');
    const Hls = mod.default || mod.Hls || window.Hls;
    if (Hls && Hls.isSupported()) {
      player = new Hls({ lowLatencyMode: true });
      player.loadSource(url);
      player.attachMedia(videoEl);
      return;
    }
  } catch (e) {
    console.warn('Failed to load hls.js', e);
  }

  showOverlay(videoEl, 'HLS is not supported in this browser.');
}

function rtspToHls(rtspUrl) {
  try {
    const u = new URL(rtspUrl);
    // Build MediaMTX default HLS path on port 8888
    const path = u.pathname.endsWith('/') ? u.pathname.slice(0, -1) : u.pathname; // /live.sdp
    const host = window.location.hostname; // Assume gateway runs with the web app
    return `http://${host}:8888${path}/index.m3u8`;
  } catch {
    return null;
  }
}

function showOverlay(videoEl, message) {
  const wrapper = videoEl.parentElement;
  if (!wrapper) return;
  const overlay = document.createElement('div');
  overlay.style.position = 'absolute';
  overlay.style.inset = '0';
  overlay.style.display = 'grid';
  overlay.style.placeContent = 'center';
  overlay.style.background = 'rgba(0,0,0,0.45)';
  overlay.style.color = 'white';
  overlay.style.fontWeight = '600';
  overlay.style.textAlign = 'center';
  overlay.style.padding = '0.75rem';
  overlay.textContent = message;
  wrapper.appendChild(overlay);
}

export function dispose() {
  try { player?.destroy?.(); } catch { /* noop */ }
  player = undefined;
}
