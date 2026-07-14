window.Views = window.Views || {};
window.Views.livecode = (function () {
  // Session selections persist across navigations via this closure (not across app restarts;
  // the backend remembers folder/shell/model/auto-approve as last-used defaults).
  const state = {
    ticket: null,      // { key, summary, status, ... }
    folder: '',
    shell: 'powershell',
    model: '',
    agent: '',
    autoApprove: false,
    tickets: [],
    jiraConfigured: false,
    apiKeyPresent: false,
    bypass: false,
    running: false,
    plan: '',
    usageResetsAt: ''
  };

  const MODELS = [
    { value: '', label: 'Default (session)' },
    { value: 'opus', label: 'Opus' },
    { value: 'sonnet', label: 'Sonnet' },
    { value: 'haiku', label: 'Haiku' }
  ];

  function fmtResetDate(iso) {
    const d = new Date(iso);
    if (isNaN(d)) return iso;
    return d.toLocaleDateString([], { month: 'short', day: 'numeric' }) +
           ' at ' + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  async function load(el) {
    let cfg;
    try {
      cfg = await Bridge.call('livecode.config');
    } catch (e) {
      el.innerHTML = `<div class="panel empty">Failed to load Live Code: ${App.esc(e.message)}</div>`;
      return;
    }
    state.folder = cfg.lastFolder || '';
    state.shell = cfg.lastShell || 'powershell';
    state.model = cfg.lastModel || '';
    state.autoApprove = !!cfg.autoApprove;
    state.jiraConfigured = !!cfg.jiraConfigured;
    state.apiKeyPresent = !!cfg.apiKeyPresent;
    state.plan = cfg.plan || '';
    state.usageResetsAt = cfg.usageResetsAt || '';

    // Re-entering the page (navigation re-renders via innerHTML): drop any stale terminal
    // wiring and stop an orphaned backend session so we start clean. Re-attaching to a
    // running session's scrollback is out of scope for v1.
    if (term.inst || state.running) {
      Bridge.call('livecode.stop', {}, 0).catch(() => {});
      teardownTerminal();
    }

    render(el);
    loadTickets();
    loadAgents();
  }

  function render(el) {
    el.innerHTML = `<h1>Live Code Session</h1>

      <div class="panel lc-tickets">
        <div class="lc-section-head">Ticket to work on <span class="muted">(latest 3 assigned to you)</span></div>
        <div id="lc-ticket-list" class="lc-ticket-list"><span class="muted">Loading…</span></div>
      </div>

      <div class="panel lc-row">
        <label class="lc-label">Working folder</label>
        <input id="lc-folder" class="lc-grow" placeholder="C:\\path\\to\\project" value="${App.esc(state.folder)}">
        <button class="btn" id="lc-browse">Browse…</button>
      </div>

      <div class="panel lc-row">
        <label class="lc-label">Shell</label>
        <div class="tabs" style="margin:0">
          <button class="btn ${state.shell === 'powershell' ? 'active' : ''}" data-shell="powershell">PowerShell</button>
          <button class="btn ${state.shell === 'bash' ? 'active' : ''}" data-shell="bash">Git Bash</button>
        </div>
        <span class="muted" style="margin-left:8px">Git Bash falls back to PowerShell if not installed.</span>
      </div>

      <div class="panel lc-row">
        <label class="lc-label">Model</label>
        <select id="lc-model">
          ${MODELS.map(m => `<option value="${m.value}" ${m.value === state.model ? 'selected' : ''}>${m.label}</option>`).join('')}
        </select>
        <label class="lc-label" style="margin-left:16px">Agent <span class="muted">(if any)</span></label>
        <select id="lc-agent" class="lc-grow"><option value="">(none — default)</option></select>
      </div>

      <div class="panel lc-row">
        <button class="btn btn-primary" id="lc-start" disabled>▶ Start session</button>
        <button class="btn" id="lc-stop" disabled>■ Stop</button>
        <span style="flex:1"></span>
        <label class="lc-check"><input type="checkbox" id="lc-auto" ${state.autoApprove ? 'checked' : ''}> Auto-approve confirmations</label>
        <label class="lc-check" title="Runs every action with no confirmation — use only in a folder you trust">
          <input type="checkbox" id="lc-bypass"> <span style="color:var(--danger)">Bypass ALL permissions</span></label>
      </div>

      <div class="panel lc-terminal-wrap">
        <div id="lc-terminal" class="lc-terminal empty">The live Claude Code terminal appears here once a session is started.</div>
      </div>

      <div class="panel lc-metrics">
        <div class="lc-metric"><div class="lc-metric-label">Plan</div>
          <div class="lc-metric-val">${App.esc(state.plan || '—')}</div></div>
        <div class="lc-metric"><div class="lc-metric-label">Tokens — this session</div>
          <div class="lc-metric-val" id="lc-tok-session">—</div></div>
        <div class="lc-metric"><div class="lc-metric-label">Tokens — this week</div>
          <div class="lc-metric-val" id="lc-tok-week">—</div>
          <div class="lc-metric-sub">${state.usageResetsAt ? 'usage limits reset ' + App.esc(fmtResetDate(state.usageResetsAt)) : ''}</div></div>
        <div class="lc-metric"><div class="lc-metric-label">Context window</div>
          <div class="lc-metric-val" id="lc-ctx">—</div></div>
      </div>`;

    wire();
  }

  function wire() {
    document.getElementById('lc-folder').addEventListener('input', e => {
      state.folder = e.target.value.trim();
      updateStartEnabled();
    });
    document.getElementById('lc-folder').addEventListener('change', () => { saveConfig(); loadAgents(); });

    document.getElementById('lc-browse').addEventListener('click', browse);

    document.querySelectorAll('[data-shell]').forEach(b =>
      b.addEventListener('click', () => {
        state.shell = b.dataset.shell;
        document.querySelectorAll('[data-shell]').forEach(x =>
          x.classList.toggle('active', x.dataset.shell === state.shell));
        saveConfig();
      }));

    document.getElementById('lc-model').addEventListener('change', e => { state.model = e.target.value; saveConfig(); });
    document.getElementById('lc-agent').addEventListener('change', e => { state.agent = e.target.value; });
    document.getElementById('lc-auto').addEventListener('change', e => { state.autoApprove = e.target.checked; saveConfig(); });
    document.getElementById('lc-bypass').addEventListener('change', async e => {
      if (!e.target.checked) { state.bypass = false; return; }
      const ok = await App.confirm(
        'Bypass ALL permission checks?\n\n' +
        'Claude Code will run every action — editing files AND running shell commands — with NO ' +
        'confirmation. Only use this in a folder you trust.',
        'Enable bypass', true);
      state.bypass = ok;
      e.target.checked = ok; // revert the box if the user cancelled
    });

    document.getElementById('lc-start').addEventListener('click', start);
    document.getElementById('lc-stop').addEventListener('click', stop);
  }

  async function loadTickets() {
    const listEl = document.getElementById('lc-ticket-list');
    if (!listEl) return;
    if (!state.jiraConfigured) {
      listEl.innerHTML = `<span class="muted">JIRA isn’t configured. Add your site, email and token in
        <a href="#settings">Settings</a> to see assigned tickets. You can still pick a folder and start a session.</span>`;
      return;
    }
    try {
      const r = await Bridge.call('livecode.tickets', {}, 0);
      state.tickets = r.tickets || [];
      if (!state.tickets.length) {
        listEl.innerHTML = `<span class="muted">No tickets currently assigned to you.</span>`;
        return;
      }
      listEl.innerHTML = state.tickets.map((t, i) => `
        <button class="lc-ticket" data-idx="${i}">
          <span class="badge">${App.esc(t.key)}</span>
          <span class="lc-ticket-sum">${App.esc(t.summary || '')}</span>
          <span class="muted lc-ticket-status">${App.esc(t.status || '')}</span>
        </button>`).join('');
      listEl.querySelectorAll('.lc-ticket').forEach(b =>
        b.addEventListener('click', () => selectTicket(+b.dataset.idx)));
    } catch (e) {
      listEl.innerHTML = `<span class="muted">Failed to load tickets: ${App.esc(e.message)}</span>`;
    }
  }

  function selectTicket(idx) {
    state.ticket = state.tickets[idx] || null;
    document.querySelectorAll('.lc-ticket').forEach((b, i) => b.classList.toggle('selected', i === idx));
    updateStartEnabled();
  }

  async function loadAgents() {
    const sel = document.getElementById('lc-agent');
    if (!sel) return;
    try {
      const agents = await Bridge.call('livecode.listAgents', { folder: state.folder });
      const keep = state.agent;
      sel.innerHTML = `<option value="">(none — default)</option>` +
        agents.map(a => `<option value="${App.esc(a.name)}" title="${App.esc(a.description || '')}">
          ${App.esc(a.name)} <span>(${App.esc(a.scope)})</span></option>`).join('');
      // preserve the previous selection if it still exists
      if (keep && [...sel.options].some(o => o.value === keep)) sel.value = keep;
      else state.agent = '';
    } catch {
      // leave the "(none)" default in place
    }
  }

  async function browse() {
    try {
      const r = await Bridge.call('livecode.pickFolder', { current: state.folder }, 0);
      if (r && r.path) {
        state.folder = r.path;
        document.getElementById('lc-folder').value = r.path;
        updateStartEnabled();
        saveConfig();
        loadAgents();
      }
    } catch (e) {
      App.toast('Folder picker unavailable — type the path instead. (' + e.message + ')', true);
    }
  }

  function updateStartEnabled() {
    const btn = document.getElementById('lc-start');
    // A folder is enough to open a terminal; a ticket drives the M3 kickoff prompt.
    if (btn) btn.disabled = state.running || !state.folder;
  }

  function saveConfig() {
    Bridge.call('livecode.saveConfig', {
      folder: state.folder, shell: state.shell, model: state.model, autoApprove: state.autoApprove
    }).catch(() => {});
  }

  // --- live terminal (xterm.js over the ConPTY bridge) ---
  const term = { inst: null, fit: null, unsub: [], ro: null, metricsTimer: null };

  async function pollMetrics() {
    try {
      const m = await Bridge.call('livecode.metrics', {}, 0);
      const set = (id, v) => { const el = document.getElementById(id); if (el) el.textContent = v; };
      set('lc-tok-week', App.fmtNum(m.weekTokens));
      set('lc-tok-session', m.active ? App.fmtNum(m.sessionTokens) : '—');
      // Context window is the one real "used of max" limit (200k / 1M).
      set('lc-ctx', m.active ? `${App.fmtNum(m.contextTokens)} of ${App.fmtNum(m.contextSize)} (${m.contextPct}%)` : '—');
    } catch { /* transient */ }
  }

  const b64ToBytes = b64 => {
    const bin = atob(b64);
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
  };
  const strToB64 = s => {
    const bytes = new TextEncoder().encode(s);
    let bin = '';
    for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
    return btoa(bin);
  };

  async function start() {
    if (!state.folder || state.running) return;

    // If an API key is present, warn before running: the session strips it so the Claude
    // subscription is used (not metered API billing).
    if (state.apiKeyPresent) {
      const ok = await App.confirm(
        'ANTHROPIC_API_KEY is set in your environment.\n\n' +
        'This session will run with it removed so your Claude subscription is used ' +
        '(not metered API billing). Continue?',
        'Run on subscription');
      if (!ok) return;
    }
    saveConfig();

    const host = document.getElementById('lc-terminal');
    host.classList.remove('empty');
    host.textContent = '';

    const t = new Terminal({
      cursorBlink: true,
      fontFamily: '"Cascadia Mono", "Consolas", monospace',
      fontSize: 13,
      theme: { background: '#1e1e1e', foreground: '#d4d4d4' }
    });
    const fit = new FitAddon.FitAddon();
    t.loadAddon(fit);
    t.open(host);
    fit.fit();
    term.inst = t;
    term.fit = fit;

    // Subscribe BEFORE starting so no initial output is missed.
    term.unsub.push(Bridge.on('pty.output', d => { if (d && d.data) t.write(b64ToBytes(d.data)); }));
    term.unsub.push(Bridge.on('pty.exit', d => {
      t.write(`\r\n\x1b[90m[process exited with code ${d ? d.code : '?'}]\x1b[0m\r\n`);
      markStopped();
    }));

    t.onData(data => Bridge.call('pty.input', { data: strToB64(data) }, 0).catch(() => {}));

    term.ro = new ResizeObserver(() => refit());
    term.ro.observe(host);

    try {
      const r = await Bridge.call('livecode.start', {
        shell: state.shell, folder: state.folder,
        model: state.model, agent: state.agent,
        ticketKey: state.ticket ? state.ticket.key : null,
        ticketSummary: state.ticket ? state.ticket.summary : null,
        autoApprove: state.autoApprove,
        bypass: state.bypass,
        cols: t.cols, rows: t.rows
      }, 0);
      state.running = true;
      updateStartEnabled();
      document.getElementById('lc-stop').disabled = false;
      if (r && r.fellBack) App.toast('Git Bash not found — using PowerShell instead.', true);
      if (r && r.kickoff) App.toast(`Starting Claude Code on ${state.ticket.key}…`);
      pollMetrics();
      term.metricsTimer = setInterval(pollMetrics, 3000);
      t.focus();
    } catch (e) {
      App.toast('Failed to start session: ' + e.message, true);
      teardownTerminal();
    }
  }

  function refit() {
    if (!term.fit || !term.inst) return;
    try {
      term.fit.fit();
      if (state.running) Bridge.call('pty.resize', { cols: term.inst.cols, rows: term.inst.rows }, 0).catch(() => {});
    } catch { /* element not laid out */ }
  }

  async function stop() {
    try { await Bridge.call('livecode.stop', {}, 0); } catch { /* ignore */ }
    markStopped();
  }

  function markStopped() {
    state.running = false;
    if (term.metricsTimer) { clearInterval(term.metricsTimer); term.metricsTimer = null; }
    const stopBtn = document.getElementById('lc-stop');
    if (stopBtn) stopBtn.disabled = true;
    updateStartEnabled();
  }

  function teardownTerminal() {
    term.unsub.forEach(fn => { try { fn(); } catch {} });
    term.unsub = [];
    if (term.ro) { try { term.ro.disconnect(); } catch {} term.ro = null; }
    if (term.inst) { try { term.inst.dispose(); } catch {} term.inst = null; }
    term.fit = null;
    markStopped();
  }

  return { render: load };
})();
