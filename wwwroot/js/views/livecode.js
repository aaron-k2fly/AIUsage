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
    jiraConfigured: false
  };

  const MODELS = [
    { value: '', label: 'Default (session)' },
    { value: 'opus', label: 'Opus' },
    { value: 'sonnet', label: 'Sonnet' },
    { value: 'haiku', label: 'Haiku' }
  ];

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
      </div>

      <div class="panel lc-terminal-wrap">
        <div id="lc-terminal" class="lc-terminal empty">The live Claude Code terminal appears here once a session is started.</div>
      </div>

      <div class="panel lc-metrics">
        <div class="lc-metric"><div class="lc-metric-label">Tokens — this session</div><div class="lc-metric-val" id="lc-tok-session">—</div></div>
        <div class="lc-metric"><div class="lc-metric-label">Tokens — this week</div><div class="lc-metric-val" id="lc-tok-week">—</div></div>
        <div class="lc-metric"><div class="lc-metric-label">Context window</div><div class="lc-metric-val" id="lc-ctx">—</div></div>
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

    document.getElementById('lc-start').addEventListener('click', start);
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
    if (btn) btn.disabled = !(state.ticket && state.folder);
  }

  function saveConfig() {
    Bridge.call('livecode.saveConfig', {
      folder: state.folder, shell: state.shell, model: state.model, autoApprove: state.autoApprove
    }).catch(() => {});
  }

  function start() {
    // Terminal wiring arrives in M2; for now validate + persist and let the user know.
    if (!state.ticket || !state.folder) return;
    saveConfig();
    App.toast(`Ready to run ${state.ticket.key} in ${state.folder} (${state.shell}) — live terminal lands in the next step.`);
  }

  return { render: load };
})();
