/* ============================================================================
 * RioEditor — WebAssembly interop module
 * ---------------------------------------------------------------------------
 * WASM has no native WebView. Instead the editor document is hosted in a
 * same-origin <iframe srcdoc>, layered over the Avalonia canvas and kept in
 * sync with the position of the placeholder control in the Avalonia layout.
 *
 * Because srcdoc inherits the parent origin, the host can call into the frame
 * (executeScript) and the frame can post back (window.parent.postMessage),
 * which is exactly the same protocol the native WebView uses on desktop.
 * ========================================================================== */

let frame = null;
let messageHandler = null;
let pendingHtml = null;

/** Registers the .NET callback that receives engine messages. */
export function registerMessageHandler(handler) {
  messageHandler = handler;

  window.addEventListener('message', (event) => {
    // Same-origin only, and only our own envelope shape.
    if (event.source !== (frame && frame.contentWindow)) return;
    const data = event.data;
    if (!data || typeof data.rio !== 'string') return;
    if (messageHandler) messageHandler(data.rio);
  });
}

/** Creates (or reloads) the editor frame with a complete HTML document. */
export function loadHtml(html) {
  if (!frame) {
    frame = document.createElement('iframe');
    frame.id = 'rio-editor-frame';
    frame.setAttribute('title', 'RioEditor editing surface');
    // allow-same-origin is required for the bridge; scripts are ours and inline.
    frame.setAttribute('sandbox', 'allow-same-origin allow-scripts allow-popups allow-downloads');
    document.body.appendChild(frame);
  }
  pendingHtml = html;
  frame.srcdoc = html;
}

/** Evaluates host-generated JavaScript inside the frame. */
export function executeScript(script) {
  if (!frame || !frame.contentWindow) return false;
  try {
    // eslint-disable-next-line no-eval
    frame.contentWindow.eval(script);
    return true;
  } catch (e) {
    console.warn('[rio] executeScript failed', e);
    return false;
  }
}

/**
 * Positions the frame over the Avalonia placeholder control.
 * Coordinates arrive in device-independent pixels; CSS pixels are the same unit
 * in the browser backend, so no scaling conversion is needed.
 */
export function setBounds(x, y, width, height) {
  if (!frame) return;
  frame.style.left = x + 'px';
  frame.style.top = y + 'px';
  frame.style.width = Math.max(0, width) + 'px';
  frame.style.height = Math.max(0, height) + 'px';
  frame.style.display = width > 0 && height > 0 ? 'block' : 'none';
}

export function setVisible(visible) {
  if (frame) frame.style.display = visible ? 'block' : 'none';
}

/** Whether the frame document has finished parsing. */
export function isLoaded() {
  return !!(frame && frame.contentWindow && frame.contentWindow.rio);
}

export function focusEditor() {
  if (frame && frame.contentWindow) frame.contentWindow.focus();
}

/* ---------------------------------------------------------------- storage */

export function storageGet(key) {
  try { return window.localStorage.getItem(key); } catch (e) { return null; }
}

export function storageSet(key, value) {
  try { window.localStorage.setItem(key, value); } catch (e) { /* quota / private mode */ }
}

export function storageRemove(key) {
  try { window.localStorage.removeItem(key); } catch (e) { /* ignore */ }
}

/* ---------------------------------------------------------------- download */

/** Save fallback: the sandbox cannot write files, so hand the user a download. */
export function downloadText(fileName, text) {
  const blob = new Blob([text], { type: 'text/markdown;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = fileName || 'document.md';
  document.body.appendChild(anchor);
  anchor.click();
  document.body.removeChild(anchor);
  setTimeout(() => URL.revokeObjectURL(url), 2000);
}
