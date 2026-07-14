// Promise-based request/response over Photino's string message bus.
// Usage: Bridge.call('sessions.list', { state: 'pending' }).then(...)
(function () {
  const pending = new Map();
  const listeners = new Map(); // event name -> Set of handlers (server-pushed events)

  window.external.receiveMessage(function (raw) {
    let msg;
    try { msg = JSON.parse(raw); } catch { return; }
    if (!msg) return;
    // Unsolicited server->client events (e.g. live terminal output) carry no request id.
    if (msg.type === 'event') {
      const hs = listeners.get(msg.event);
      if (hs) hs.forEach(h => { try { h(msg.data); } catch (e) { console.error(e); } });
      return;
    }
    if (!msg.id) return;
    const waiter = pending.get(msg.id);
    if (!waiter) return;
    pending.delete(msg.id);
    if (msg.ok) waiter.resolve(msg.data);
    else waiter.reject(new Error(msg.error || 'Unknown error'));
  });

  window.Bridge = {
    // timeoutMs: 0 disables the timeout — for actions whose duration is unbounded
    // (e.g. tickets.sync over many tickets); rejecting early would strand a backend
    // operation that is still running.
    call(action, payload, timeoutMs = 120000) {
      const id = crypto.randomUUID();
      return new Promise((resolve, reject) => {
        pending.set(id, { resolve, reject });
        window.external.sendMessage(JSON.stringify({ id, action, payload: payload || {} }));
        if (timeoutMs > 0) {
          setTimeout(() => {
            if (pending.has(id)) {
              pending.delete(id);
              reject(new Error(`Timeout waiting for '${action}'`));
            }
          }, timeoutMs);
        }
      });
    },

    // Subscribe to a server-pushed event (e.g. 'pty.output'). Returns an unsubscribe fn.
    on(event, handler) {
      let hs = listeners.get(event);
      if (!hs) { hs = new Set(); listeners.set(event, hs); }
      hs.add(handler);
      return () => hs.delete(handler);
    }
  };
})();
