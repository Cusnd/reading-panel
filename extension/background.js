const readers = new Set();
let native = null;
let ready = false;
let middleClickEnabled = true;
let active = null;

function send(port, message) {
  try { port.postMessage(message); } catch { /* The document or native host closed. */ }
}

function setReady(value) {
  ready = value;
  if (!ready || !middleClickEnabled) active = null;
  for (const port of readers) send(port, { ready, middleClickEnabled, activeId: activeIdFor(port) });
  chrome.action.setBadgeText({ text: value ? "" : "off" });
  chrome.action.setTitle({ title: value ? "Reading Panel connected" : "Reading Panel offline · click to reconnect" });
}

function activeIdFor(port) {
  return active?.port.sender.tab.id === port.sender.tab.id ? active.id : null;
}

function changeActive(next) {
  const previousTab = active?.port.sender.tab.id;
  active = next;
  for (const port of readers) {
    if (port.sender.tab.id === previousTab || activeIdFor(port)) send(port, { activeId: activeIdFor(port) });
  }
}

function connectHost() {
  if (native) return;
  const host = chrome.runtime.connectNative("app.readingpanel.host");
  native = host;
  host.onMessage.addListener(message => {
    if (native === host && message.type === "ready") {
      middleClickEnabled = message.middleClickEnabled !== false;
      setReady(true);
    }
  });
  host.onDisconnect.addListener(() => {
    void chrome.runtime.lastError;
    if (native !== host) return;
    native = null;
    setReady(false);
  });
}

chrome.runtime.onConnect.addListener(port => {
  if (port.name !== "reader" || !port.sender?.tab || port.sender.id !== chrome.runtime.id ||
      !/^https?:\/\//i.test(port.sender.url || "")) {
    port.disconnect();
    return;
  }
  readers.add(port);
  send(port, { ready, middleClickEnabled, activeId: activeIdFor(port) });
  port.onMessage.addListener(message => {
    if (message?.type === "reconnect" && !native) { connectHost(); return; }
    if (!ready || !native || !message) return;
    if (message.type === "lookup" && middleClickEnabled && typeof message.text === "string" && message.text.trim() &&
        message.text.length <= 8000 && Array.isArray(message.offsets) && message.offsets.length === 4 &&
        message.offsets.every(v => Number.isFinite(v) && Math.abs(v) <= 32768)) {
      const id = crypto.randomUUID();
      changeActive({ port, id });
      const point = Array.isArray(message.point) && message.point.length === 2 &&
        message.point.every(v => Number.isFinite(v) && v >= 0 && v <= 1) ? message.point : null;
      send(native, { type: "lookup", id, text: message.text, offsets: message.offsets, point });
    } else if (message.type === "documentInput" && active &&
        (active.port === port || (message.id === active.id && active.port.sender.tab.id === port.sender.tab.id))) {
      send(native, { type: "dismiss", id: active.id });
      changeActive(null);
    } else if ((message.type === "dismiss" || message.type === "blur") && active?.port === port) {
      send(native, { type: message.type, id: active.id });
      if (message.type === "dismiss") changeActive(null);
    }
  });
  port.onDisconnect.addListener(() => {
    void chrome.runtime.lastError;
    readers.delete(port);
    if (active?.port === port) {
      if (native) send(native, { type: "dismiss", id: active.id });
      changeActive(null);
    }
  });
  connectHost();
});
chrome.action.onClicked.addListener(connectHost);
