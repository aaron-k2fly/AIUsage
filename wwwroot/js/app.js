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

  // --- startup ---
  Bridge.call('ping')
    .then(() => { navigate(); runScan(true); })
    .catch(e => App.toast('Bridge unavailable: ' + e.message, true));
})();
