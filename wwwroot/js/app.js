// Hash router + shared helpers. Views register themselves on window.Views.
(function () {
  const content = document.getElementById('content');
  const routes = window.Views || {};

  function navigate() {
    const route = (location.hash || '#dashboard').slice(1);
    const view = routes[route] || routes.dashboard;
    document.querySelectorAll('#sidebar a').forEach(a =>
      a.classList.toggle('active', a.dataset.route === route));
    content.innerHTML = '';
    view.render(content);
  }

  window.addEventListener('hashchange', navigate);

  // --- shared helpers ---
  window.App = {
    toast(message, isError) {
      const el = document.getElementById('toast');
      el.textContent = message;
      el.className = 'show' + (isError ? ' error' : '');
      clearTimeout(el._t);
      el._t = setTimeout(() => { el.className = ''; }, isError ? 6000 : 3000);
    },
    esc(s) {
      return String(s ?? '').replace(/[&<>"']/g, c =>
        ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    },
    fmtNum(n) {
      n = n || 0;
      if (n >= 1e6) return (n / 1e6).toFixed(1) + 'M';
      if (n >= 1e3) return (n / 1e3).toFixed(1) + 'k';
      return String(n);
    },
    fmtDate(iso) {
      if (!iso) return '';
      const d = new Date(iso);
      return isNaN(d) ? '' : d.toLocaleDateString() + ' ' + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    },
    refresh: navigate,
    // Promise<boolean> confirm modal (window.confirm is unreliable in the WebView). Message is
    // rendered as text (line breaks preserved via CSS), so it's injection-safe.
    confirm(message, okLabel = 'Continue', danger = false) {
      return new Promise(resolve => {
        const ov = document.createElement('div');
        ov.className = 'modal-overlay';
        ov.innerHTML = `<div class="modal">
          <div class="modal-msg"></div>
          <div class="modal-actions">
            <button class="btn" data-act="cancel">Cancel</button>
            <button class="btn ${danger ? 'btn-danger' : 'btn-primary'}" data-act="ok"></button>
          </div></div>`;
        ov.querySelector('.modal-msg').textContent = message;
        ov.querySelector('[data-act="ok"]').textContent = okLabel;
        const done = v => { ov.remove(); resolve(v); };
        ov.querySelector('[data-act="ok"]').addEventListener('click', () => done(true));
        ov.querySelector('[data-act="cancel"]').addEventListener('click', () => done(false));
        ov.addEventListener('click', e => { if (e.target === ov) done(false); });
        document.body.appendChild(ov);
      });
    },
    // Promise<string|null> multi-button chooser (returns the chosen button key, null if dismissed).
    // buttons: [{ key, label, primary?, danger? }]. Message rendered as text (injection-safe).
    choose(message, buttons, danger = false) {
      return new Promise(resolve => {
        const ov = document.createElement('div');
        ov.className = 'modal-overlay';
        const btns = buttons.map(b =>
          `<button class="btn ${b.primary ? 'btn-primary' : b.danger ? 'btn-danger' : ''}" data-key="${App.esc(b.key)}">${App.esc(b.label)}</button>`
        ).join('');
        ov.innerHTML = `<div class="modal"><div class="modal-msg"></div><div class="modal-actions">${btns}</div></div>`;
        ov.querySelector('.modal-msg').textContent = message;
        const done = v => { ov.remove(); resolve(v); };
        ov.querySelectorAll('[data-key]').forEach(b => b.addEventListener('click', () => done(b.dataset.key)));
        ov.addEventListener('click', e => { if (e.target === ov) done(null); });
        document.body.appendChild(ov);
      });
    },
    // Shared "Export to Excel" — no client timeout since the native save dialog may stay open.
    async exportExcel(action) {
      try {
        const r = await Bridge.call(action, {}, 0);
        if (r && r.saved) App.toast(`Exported ${r.rows} row(s) to ${r.path}`);
        else App.toast('Export cancelled');
      } catch (e) {
        App.toast('Export failed: ' + e.message, true);
      }
    }
  };

  // --- scan button ---
  const scanBtn = document.getElementById('scan-now');
  const scanStatus = document.getElementById('scan-status');
  async function runScan(auto) {
    scanBtn.disabled = true;
    scanStatus.textContent = 'Scanning…';
    try {
      const r = await Bridge.call('scan.run');
      scanStatus.textContent = `${r.sessions} sessions (${r.newFiles} new, ${r.updatedFiles} updated files)`;
      // The background startup scan must not re-render a view the user may be
      // typing into (re-render goes through innerHTML and wipes form state) —
      // it only refreshes the input-free dashboard. An explicit "Scan now"
      // click refreshes whatever is on screen.
      const route = (location.hash || '#dashboard').slice(1);
      if (!auto || route === 'dashboard') App.refresh();
    } catch (e) {
      // A background (startup) scan failure is shown only in the sidebar status so it
      // isn't mistaken for a failure of whatever the user just clicked. An explicit
      // "Scan now" click still surfaces a toast.
      scanStatus.textContent = 'Scan failed';
      if (!auto) App.toast('Scan failed: ' + e.message, true);
    } finally {
      scanBtn.disabled = false;
    }
  }
  scanBtn.addEventListener('click', () => runScan(false));

  // --- Live Code session indicator (sidebar dot: green = ≥1 session running, red = none) ---
  let lastSessionList = [];
  async function updateLiveDot() {
    const dot = document.getElementById('lc-nav-dot');
    if (!dot) return;
    try {
      const r = await Bridge.call('livecode.running', {}, 5000);
      const count = (r && r.count) || 0;
      dot.classList.toggle('on', count > 0);
      dot.title = count > 0 ? `${count} active session${count > 1 ? 's' : ''}` : 'No active session';
    } catch { /* leave last state */ }
  }

  // Hover panel over the Live Code nav item: lists live tabs; click focuses that tab.
  function setupNavPopover() {
    const wrap = document.getElementById('lc-nav-item');
    const pop = document.getElementById('lc-nav-popover');
    if (!wrap || !pop) return;

    function fldBase(p) {
      if (!p) return '';
      const parts = String(p).split(/[\\/]/).filter(Boolean);
      return parts.length ? parts[parts.length - 1] : p;
    }
    function render(list) {
      lastSessionList = list;
      if (!list.length) { pop.innerHTML = `<div class="lc-nav-pop-empty">No active sessions</div>`; return; }
      pop.innerHTML = `<div class="lc-nav-pop-head">Live Code sessions</div>` +
        list.map((s, i) => `<div class="lc-nav-pop-row" data-tab="${App.esc(s.tabId)}">
          <span class="dot ${s.running ? 'running' : ''}"></span>
          <span class="lbl">${App.esc(s.ticketKey || ('Session ' + (i + 1)))}</span>
          <span class="fld">${App.esc(fldBase(s.folder))}</span>
        </div>`).join('');
      pop.querySelectorAll('.lc-nav-pop-row').forEach(row =>
        row.addEventListener('click', () => {
          hide();
          const id = row.dataset.tab;
          if (window.Views.livecode && window.Views.livecode.focusTab) window.Views.livecode.focusTab(id);
          else location.hash = '#livecode';
        }));
    }
    function show() { pop.classList.add('show'); }
    function hide() { pop.classList.remove('show'); }

    wrap.addEventListener('mouseenter', async () => {
      render(lastSessionList);   // instant from cache
      show();
      try { const r = await Bridge.call('livecode.list', {}, 5000); render((r && r.tabs) || []); }
      catch { /* keep cached */ }
    });
    wrap.addEventListener('mouseleave', hide);
  }

  // --- startup ---
  Bridge.call('ping')
    .then(() => {
      navigate();
      runScan(true);
      setupNavPopover();
      updateLiveDot();
      setInterval(updateLiveDot, 3000);
    })
    .catch(e => App.toast('Bridge unavailable: ' + e.message, true));
})();
