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
    usageResetsAt: '',
    claudeInstalled: true,
    agentsDir: '',
    canResume: false
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
    state.claudeInstalled = cfg.claudeInstalled !== false;
    state.agentsDir = cfg.lastAgentsDir || '';

    // Re-entering the page (the router wiped the DOM): detach the old terminal wiring but DON'T
    // stop the backend session — a running session survives navigation and we reconnect below.
    disposeTerminalDom();
    if (term.metricsTimer) { clearInterval(term.metricsTimer); term.metricsTimer = null; }
    if (term.activeTimer) { clearInterval(term.activeTimer); term.activeTimer = null; }

    render(el);
    loadTickets();
    loadAgents();
    reattach(); // reconnect to a session still running from before we navigated away

    pollMetrics();
    pollActive();
    term.metricsTimer = setInterval(pollMetrics, 4000); // tokens/context (does a light DB scan)
    term.activeTimer = setInterval(pollActive, 2000);   // active sessions (cheap, near-real-time)
    ensureHashCleanup();
  }

  function render(el) {
    el.innerHTML = `<h1>Live Code Session</h1>

      ${state.claudeInstalled ? '' : `<div class="panel lc-warn">⚠ Claude Code CLI not found on PATH.
        Install it from <b>claude.ai/code</b> to run sessions — Start is disabled until it's available.</div>`}

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
        <label class="lc-label">Agents folder <span class="muted">(optional)</span></label>
        <input id="lc-agents-dir" class="lc-grow" placeholder="folder with agent .md files (or a project root); also scans the working folder + ~/.claude/agents" value="${App.esc(state.agentsDir)}">
        <button class="btn" id="lc-agents-browse">Browse…</button>
      </div>

      <div class="panel lc-row">
        <button class="btn btn-primary" id="lc-start" disabled>▶ Start session</button>
        <button class="btn" id="lc-stop" disabled>■ Stop</button>
        <button class="btn" id="lc-resume" disabled title="Resume the previous session's Claude conversation">▷ Resume</button>
        <button class="btn" id="lc-reset" disabled title="Quit Claude (/exit) and restart a fresh session on the same ticket">↺ Reset</button>
        <span style="flex:1"></span>
        <label class="lc-check"><input type="checkbox" id="lc-auto" ${state.autoApprove ? 'checked' : ''}> Auto-approve confirmations</label>
        <label class="lc-check" title="Runs every action with no confirmation — use only in a folder you trust">
          <input type="checkbox" id="lc-bypass"> <span style="color:var(--danger)">Bypass ALL permissions</span></label>
      </div>

      <div class="panel lc-terminal-wrap">
        <div id="lc-terminal" class="lc-terminal empty">The live Claude Code terminal appears here once a session is started.</div>
      </div>

      <div class="panel">
        <div class="lc-metrics">
          <div class="lc-metric"><div class="lc-metric-label">Plan</div>
            <div class="lc-metric-val">${App.esc(state.plan || '—')}</div></div>
          <div class="lc-metric"><div class="lc-metric-label">Tokens — this session</div>
            <div class="lc-metric-val" id="lc-tok-session">—</div></div>
          <div class="lc-metric"><div class="lc-metric-label">Tokens — this week</div>
            <div class="lc-metric-val" id="lc-tok-week">—</div>
            <div class="lc-metric-sub">${state.usageResetsAt ? 'usage limits reset ' + App.esc(fmtResetDate(state.usageResetsAt)) : ''}</div></div>
          <div class="lc-metric"><div class="lc-metric-label">Context window</div>
            <div class="lc-metric-val" id="lc-ctx">—</div></div>
        </div>
        <div class="lc-active">
          <div class="lc-metric-label">Active Claude Code sessions <span class="muted">(top 2, last 5 min)</span></div>
          <div id="lc-active-list" class="lc-active-list"><span class="muted">—</span></div>
        </div>
      </div>`;

    wire();
  }

  function wire() {
    document.getElementById('lc-folder').addEventListener('input', e => {
      state.folder = e.target.value.trim();
      updateButtons();
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
    document.getElementById('lc-agents-dir').addEventListener('input', e => { state.agentsDir = e.target.value.trim(); });
    document.getElementById('lc-agents-dir').addEventListener('change', () => { saveConfig(); loadAgents(); });
    document.getElementById('lc-agents-browse').addEventListener('click', browseAgents);
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
    document.getElementById('lc-resume').addEventListener('click', resume);
    document.getElementById('lc-reset').addEventListener('click', reset);
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
    updateButtons();
  }

  async function loadAgents() {
    const sel = document.getElementById('lc-agent');
    if (!sel) return;
    try {
      const agents = await Bridge.call('livecode.listAgents', { folder: state.folder, agentsDir: state.agentsDir });
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
        updateButtons();
        saveConfig();
        loadAgents();
      }
    } catch (e) {
      App.toast('Folder picker unavailable — type the path instead. (' + e.message + ')', true);
    }
  }

  async function browseAgents() {
    try {
      const r = await Bridge.call('livecode.pickFolder', { current: state.agentsDir }, 0);
      if (r && r.path) {
        state.agentsDir = r.path;
        document.getElementById('lc-agents-dir').value = r.path;
        saveConfig();
        loadAgents();
      }
    } catch (e) {
      App.toast('Folder picker unavailable — type the path instead.', true);
    }
  }

  function updateButtons() {
    const start = document.getElementById('lc-start');
    const stopBtn = document.getElementById('lc-stop');
    const resumeBtn = document.getElementById('lc-resume');
    const resetBtn = document.getElementById('lc-reset');
    // Start needs a folder + the CLI, and is disabled while a session runs.
    if (start) start.disabled = state.running || !state.folder || !state.claudeInstalled;
    if (stopBtn) stopBtn.disabled = !state.running;
    // Resume continues the previous conversation — only when idle and one exists.
    if (resumeBtn) resumeBtn.disabled = state.running || !state.canResume || !state.claudeInstalled;
    // Reset (quit + fresh shell) — only while a session is running.
    if (resetBtn) resetBtn.disabled = !state.running || !state.claudeInstalled;
  }

  function saveConfig() {
    Bridge.call('livecode.saveConfig', {
      folder: state.folder, shell: state.shell, model: state.model,
      autoApprove: state.autoApprove, agentsDir: state.agentsDir
    }).catch(() => {});
  }

  let hashHooked = false;
  function ensureHashCleanup() {
    if (hashHooked) return;
    hashHooked = true;
    window.addEventListener('hashchange', () => {
      if ((location.hash || '').slice(1) !== 'livecode') {
        if (term.metricsTimer) { clearInterval(term.metricsTimer); term.metricsTimer = null; }
        if (term.activeTimer) { clearInterval(term.activeTimer); term.activeTimer = null; }
      }
    });
  }

  // --- live terminal (xterm.js over the ConPTY bridge) ---
  const term = { inst: null, fit: null, unsub: [], ro: null, metricsTimer: null, activeTimer: null };

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

  // Fast, scan-free refresh of the active-sessions list (near-real-time).
  async function pollActive() {
    const al = document.getElementById('lc-active-list');
    if (!al) return;
    try {
      const r = await Bridge.call('livecode.activeSessions', {}, 5000);
      const list = (r && r.activeSessions) || [];
      al.innerHTML = list.length
        ? list.map(s => `<span class="lc-active-item"><b>${App.esc(s.folder)}</b>
            <span class="muted">${App.fmtNum(s.contextTokens)} of ${App.fmtNum(s.contextSize)} (${s.contextPct}%)</span></span>`).join('')
        : '<span class="muted">none active in the last 5 minutes</span>';
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

  // Create the xterm terminal, wire I/O, and (on re-attach) replay buffered output. Reused by
  // start, resume, and reattach.
  function mountTerminal(replayB64) {
    disposeTerminalDom(); // drop any prior terminal wiring so we never double-subscribe
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

    if (replayB64) t.write(b64ToBytes(replayB64)); // replay the running session's recent output

    term.unsub.push(Bridge.on('pty.output', d => { if (d && d.data) t.write(b64ToBytes(d.data)); }));
    term.unsub.push(Bridge.on('pty.exit', d => {
      t.write(`\r\n\x1b[90m[process exited with code ${d ? d.code : '?'}]\x1b[0m\r\n`);
      markStopped();
    }));
    t.onData(data => Bridge.call('pty.input', { data: strToB64(data) }, 0).catch(() => {}));
    term.ro = new ResizeObserver(() => refit());
    term.ro.observe(host);
    return t;
  }

  async function start() {
    if (!state.folder || state.running) return;
    if (!state.claudeInstalled) {
      App.toast('Claude Code CLI not found — install it (claude.ai/code) to run a session.', true);
      return;
    }

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
    const t = mountTerminal();
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
      state.canResume = true;
      updateButtons();
      if (r && r.fellBack) App.toast('Git Bash not found — using PowerShell instead.', true);
      if (r && r.kickoff) App.toast(`Starting Claude Code on ${state.ticket.key} (linked to the ticket)…`);
      pollMetrics(); // immediate refresh; the page-level timer keeps it updated
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

  async function resume() {
    if (state.running || !state.canResume) return;
    if (!state.claudeInstalled) { App.toast('Claude Code CLI not found — install it to resume.', true); return; }
    if (state.apiKeyPresent) {
      const ok = await App.confirm(
        'ANTHROPIC_API_KEY is set in your environment.\n\n' +
        'Resume with it removed so your Claude subscription is used (not metered API billing)?',
        'Resume on subscription');
      if (!ok) return;
    }
    saveConfig();
    const t = mountTerminal();
    try {
      await Bridge.call('livecode.resume', {
        shell: state.shell, folder: state.folder, model: state.model, agent: state.agent,
        autoApprove: state.autoApprove, bypass: state.bypass, cols: t.cols, rows: t.rows
      }, 0);
      state.running = true;
      updateButtons();
      App.toast('Resuming the previous session…');
      pollMetrics();
      t.focus();
    } catch (e) {
      App.toast('Failed to resume: ' + e.message, true);
      teardownTerminal();
    }
  }

  async function reset() {
    if (!state.running) return;
    saveConfig();
    const t = mountTerminal(); // fresh terminal for the restarted session
    try {
      const r = await Bridge.call('livecode.reset', {
        shell: state.shell, folder: state.folder, model: state.model, agent: state.agent,
        ticketKey: state.ticket ? state.ticket.key : null,
        ticketSummary: state.ticket ? state.ticket.summary : null,
        autoApprove: state.autoApprove, bypass: state.bypass,
        cols: t.cols, rows: t.rows
      }, 0);
      state.running = true;
      state.canResume = true;
      updateButtons();
      App.toast(r && r.kickoff && state.ticket
        ? `Reset — restarted Claude on ${state.ticket.key}.`
        : 'Reset — restarted the session.');
      pollMetrics();
      t.focus();
    } catch (e) {
      App.toast('Reset failed: ' + e.message, true);
      teardownTerminal();
    }
  }

  // On (re)entering the page, reconnect to a session that's still running (replaying its buffered
  // output), or just enable Resume if a stopped session can be continued.
  async function reattach() {
    let at;
    try { at = await Bridge.call('livecode.attach', {}, 0); } catch { return; }
    if (!at) return;
    state.canResume = !!(at.canResume || at.running);
    if (at.running) {
      const t = mountTerminal(at.data);
      state.running = true;
      Bridge.call('pty.resize', { cols: t.cols, rows: t.rows }, 0).catch(() => {});
      t.focus();
    } else {
      state.running = false;
    }
    updateButtons();
  }

  async function stop() {
    try { await Bridge.call('livecode.stop', {}, 0); } catch { /* ignore */ }
    // Keep the terminal visible with its final output; Resume stays available.
    markStopped();
  }

  function markStopped() {
    state.running = false;
    // Leave the page-level metrics timer running — week tokens and active sessions stay live.
    updateButtons();
  }

  // Detach the frontend terminal (unsubscribe + dispose) WITHOUT stopping the backend session,
  // so a running session survives navigation.
  function disposeTerminalDom() {
    term.unsub.forEach(fn => { try { fn(); } catch {} });
    term.unsub = [];
    if (term.ro) { try { term.ro.disconnect(); } catch {} term.ro = null; }
    if (term.inst) { try { term.inst.dispose(); } catch {} term.inst = null; }
    term.fit = null;
  }

  function teardownTerminal() {
    disposeTerminalDom();
    markStopped();
  }

  return { render: load };
})();
