(() => {
  let port;
  let ready = false;
  let middleClickEnabled = true;
  let claimed = false;
  let source = null;
  let activeId = null;
  function connect() {
    if (port) return;
    try {
      const connection = chrome.runtime.connect({ name: "reader" });
      port = connection;
      connection.onMessage.addListener(message => {
        if ("ready" in message) ready = message.ready === true;
        if ("middleClickEnabled" in message) {
          middleClickEnabled = message.middleClickEnabled;
          if (!middleClickEnabled) source = null;
        }
        if ("activeId" in message) activeId = message.activeId;
      });
      connection.onDisconnect.addListener(() => {
        void chrome.runtime.lastError;
        if (port !== connection) return;
        ready = false;
        port = null;
        source = null;
        activeId = null;
      });
    } catch { /* The extension may have been reloaded. A page reload reconnects it. */ }
  }
  function dismiss(documentInput = false) {
    if (!source && !(documentInput && activeId)) return;
    const message = documentInput && activeId ? { type: "documentInput", id: activeId } : { type: "dismiss" };
    source = null;
    try { port?.postMessage(message); } catch { ready = false; }
  }
  function readSelection() {
    const control = document.activeElement;
    if (control?.matches("input,textarea")) {
      if (control.type === "password") return null;
      const start = control.selectionStart, end = control.selectionEnd;
      if (start == null || end == null || end <= start || end - start > 8000) return null;
      return { text: control.value.slice(start, end).trim(), control, start, end };
    }
    const selection = window.getSelection();
    if (!selection || selection.rangeCount !== 1 || selection.isCollapsed) return null;
    const text = selection.toString();
    return text.length <= 8000 ? { text: text.trim(), range: selection.getRangeAt(0) } : null;
  }
  function selectedLineAt(range, x, y) {
    // A range spanning a block can include the block's entire width. Check its actual text at the pointer.
    const node = document.caretRangeFromPoint(x, y)?.startContainer;
    if (node?.nodeType !== Node.TEXT_NODE || !range.intersectsNode(node)) return null;
    const selected = document.createRange();
    selected.selectNodeContents(node);
    if (node === range.startContainer) selected.setStart(node, range.startOffset);
    if (node === range.endContainer) selected.setEnd(node, range.endOffset);
    return Array.from(selected.getClientRects()).find(rect =>
      x >= rect.left && x < rect.right && y >= rect.top && y < rect.bottom);
  }
  // Read the live range synchronously. The browser decides autoscroll on down and link opening on auxclick.
  window.addEventListener("mousedown", event => {
    if (!event.isTrusted) return;
    if (event.button !== 1) { dismiss(true); return; }
    claimed = false;
    if (!ready) {
      connect();
      try { port?.postMessage({ type: "reconnect" }); } catch { /* Retain the ordinary click. */ }
      return;
    }
    if (!middleClickEnabled || !event.cancelable || event.target.closest?.('input[type="password"]')) { dismiss(true); return; }
    const selection = readSelection();
    if (!selection?.text) { dismiss(true); return; }
    const rect = selection.range && selectedLineAt(selection.range, event.clientX, event.clientY);
    const scale = window.devicePixelRatio;
    const offsets = rect ? [rect.left-event.clientX, rect.top-event.clientY, rect.right-event.clientX, rect.bottom-event.clientY]
      .map(value => Math.max(-32768, Math.min(32768, Math.round(value * scale)))) : [0, 0, 0, 0];
    const point = [(event.screenX - screen.availLeft) / screen.availWidth, (event.screenY - screen.availTop) / screen.availHeight];
    try { port.postMessage({ type: "lookup", text: selection.text, offsets, point }); } catch { ready = false; return; }
    source = selection;
    if (source.range) source.range = source.range.cloneRange();
    claimed = true;
    event.preventDefault();
    event.stopImmediatePropagation();
  }, true);
  window.addEventListener("mouseup", event => {
    if (event.isTrusted && event.button === 1 && claimed) {
      event.preventDefault();
      event.stopImmediatePropagation();
    }
  }, true);
  window.addEventListener("auxclick", event => {
    if (event.isTrusted && event.button === 1 && claimed) {
      claimed = false;
      event.preventDefault();
      event.stopImmediatePropagation();
    }
  }, true);
  document.addEventListener("selectionchange", () => {
    if (!source) return;
    const current = readSelection();
    if (!current || current.text !== source.text || current.control !== source.control ||
        current.start !== source.start || current.end !== source.end ||
        (current.range && (current.range.compareBoundaryPoints(Range.START_TO_START, source.range) ||
          current.range.compareBoundaryPoints(Range.END_TO_END, source.range)))) dismiss();
  }, true);
  window.addEventListener("keydown", event => { if (event.key === "Escape") dismiss(true); }, true);
  window.addEventListener("scroll", () => dismiss(true), { capture: true, passive: true });
  window.addEventListener("wheel", () => dismiss(true), { capture: true, passive: true });
  window.addEventListener("blur", () => {
    if (source && ready) {
      try { port.postMessage({ type: "blur" }); } catch { ready = false; }
    }
  });
  window.addEventListener("pagehide", () => { dismiss(); port?.disconnect(); port = null; ready = false; });
  window.addEventListener("pageshow", connect);
  document.addEventListener("visibilitychange", () => { if (document.hidden) dismiss(); else connect(); });
  connect();
})();
