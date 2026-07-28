window.Views = window.Views || {};
window.Views.livecode = (function () {
  // Multiple concurrent sessions, one per tab. The tabs array + activeTabId live in this closure
  // so they survive navigation (the backend keeps each session running; on return we reconcile
  // with livecode.list and replay each running terminal's buffer). Per-tab settings default to the
  // page's last-used values (folder/shell/model/customAgent/auto-approve), remembered by the
  // backend across app restarts.
  let tabs = [];
  let activeTabId = null;
  let pendingFocusId = null;

  // Page-global config + shared state (one JIRA ticket list shared by all tabs; plan/usage/etc.).
  const G = {
    cfg: {},            // from livecode.config
    tickets: [],        // latest 3 assigned (shared across tabs)
    ticketsLoaded: false,
    metricsTimer: null,
    activeTimer: null,
    usageTimer: null,   // rolling session/week usage-limit bars (livecode.usage)
    outputSub: null,    // single pty.output subscription, routed by tabId
    exitSub: null
  };

  const MAX_TABS = 6;

  const MODELS = [
    { value: '', label: 'Default (session)' },
    { value: 'fable', label: 'Fable' },
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

  // --- tab model ---------------------------------------------------------------
  function makeTab() {
    return {
      tabId: (crypto.randomUUID ? crypto.randomUUID() : String(Date.now()) + Math.random()),
      ticket: null,             // selected { key, summary, status, ... }
      ticketKeyHint: '',        // ticket key reconstructed from the backend (label only)
      folder: G.cfg.lastFolder || '',
      shell: G.cfg.lastShell || 'powershell',
      model: G.cfg.lastModel || '',
      agent: '',
      customAgent: G.cfg.lastCustomAgent || '',
      customAgentName: G.cfg.lastCustomAgentName || '',
      autoApprove: !!G.cfg.autoApprove,
      bypass: false,
      running: false,
      canResume: false,
      activeFolder: '',         // dir the session actually runs in (folder, or worktree cwd)
      isolated: false,          // running in an isolated git worktree
      resumedPick: false,       // running a session picked from Resume Sessions (locks shell/model/agent)
      folderSessions: [],       // cached list for the Resume Sessions button/modal
      term: { inst: null, fit: null, ro: null }   // this tab's xterm handles
    };
  }

  const activeTab = () => tabs.find(t => t.tabId === activeTabId) || null;
  const tabById = id => tabs.find(t => t.tabId === id) || null;
  const tabLabel = (t, i) => t.ticket ? t.ticket.key : (t.ticketKeyHint || ('Session ' + (i + 1)));

  // --- same-folder conflict detection -----------------------------------------
  const normFolder = p => String(p || '').trim().replace(/[\\/]+/g, '/').replace(/\/+$/, '').toLowerCase();

  // The first OTHER tab whose currently-running session works in the same folder, else null.
  function conflictingTab(selectedFolder, excludeTabId) {
    const n = normFolder(selectedFolder);
    if (!n) return null;
    return tabs.find(t => t.tabId !== excludeTabId && t.running && normFolder(t.activeFolder) === n) || null;
  }

  const WT_WARN =
    '⚠ Another running session is already working in this folder.\n\n' +
    'Running multiple agents on the same folder at once can cause file conflicts, corrupted ' +
    'edits, and lost work. Continuing on the same folder is entirely at your own risk.';

  // Resolve how to proceed when starting tab `t`: 'none' | 'worktree', or null to abort.
  async function resolveIsolation(t) {
    if (!conflictingTab(t.folder, t.tabId)) return 'none';
    let isGitRepo = false;
    try { const r = await Bridge.call('livecode.folderInfo', { folder: t.folder }, 5000); isGitRepo = !!(r && r.isGitRepo); }
    catch { /* treat as non-git → worktree option omitted */ }
    let msg = WT_WARN;
    const buttons = [];
    if (isGitRepo) buttons.push({ key: 'worktree', label: 'Use isolated worktree (safe)', primary: true });
    else msg += '\n\n(Isolation with a worktree needs a git repository; this folder is not one.)';
    buttons.push({ key: 'same', label: 'Continue in same folder (own risk)', danger: true });
    buttons.push({ key: 'cancel', label: 'Cancel' });
    const choice = await App.choose(msg, buttons, true);
    if (choice === 'worktree') return 'worktree';
    if (choice === 'same') return 'none';
    return null; // cancel / dismiss
  }

  // --- load / navigation -------------------------------------------------------
  async function load(el) {
    let cfg;
    try {
      cfg = await Bridge.call('livecode.config');
    } catch (e) {
      el.innerHTML = `<div class="panel empty">Failed to load Live Code: ${App.esc(e.message)}</div>`;
      return;
    }
    G.cfg = cfg || {};

    // Re-entering the page (the router wiped the DOM): tear down the old per-page wiring (event
    // subscriptions, terminal instances, timers) but DON'T stop the backend sessions.
    teardownPage();

    renderShell(el);                 // tab bar + panel host + terminals host + shared bottom panel
    await reconcile();               // merge closure tabs with backend's live list
    subscribeEvents();               // one pty.output/pty.exit subscription, routed by tabId
    loadTickets();                   // shared ticket list
    renderTabBar();
    renderTabPanel();                // controls for the active tab
    await reattachAll();             // replay running terminals' buffers
    showActiveTerminal();

    pollMetrics();
    pollActive();
    pollUsage();
    G.metricsTimer = setInterval(pollMetrics, 4000); // active tab tokens/context (light DB scan)
    G.activeTimer = setInterval(pollActive, 2000);   // shared active-sessions list (cheap)
    G.usageTimer = setInterval(pollUsage, 60000);    // session/week usage bars (backend cached 5 min)
    ensureHashCleanup();
    applyPendingFocus();
  }

  // Tear down everything created for one page mounting, WITHOUT stopping backend sessions.
  function teardownPage() {
    if (G.outputSub) { try { G.outputSub(); } catch {} G.outputSub = null; }
    if (G.exitSub) { try { G.exitSub(); } catch {} G.exitSub = null; }
    if (G.metricsTimer) { clearInterval(G.metricsTimer); G.metricsTimer = null; }
    if (G.activeTimer) { clearInterval(G.activeTimer); G.activeTimer = null; }
    if (G.usageTimer) { clearInterval(G.usageTimer); G.usageTimer = null; }
    tabs.forEach(disposeTabTerm);
  }

  // Merge the closure's tabs[] with the backend's live sessions so nothing is orphaned after a
  // full reload (or if a session was started before this page mounting existed).
  async function reconcile() {
    let backend = [];
    try { const r = await Bridge.call('livecode.list', {}, 5000); backend = (r && r.tabs) || []; }
    catch { /* offline; fall back to the closure */ }

    for (const b of backend) {
      let t = tabById(b.tabId);
      if (!t) { t = makeTab(); t.tabId = b.tabId; tabs.push(t); }
      if (b.folder) { t.folder = b.folder; t.activeFolder = b.folder; }
      if (b.model) t.model = b.model;
      if (b.ticketKey) t.ticketKeyHint = b.ticketKey;
      t.running = !!b.running;
      t.canResume = !!b.canResume;
    }
    if (!tabs.length) tabs.push(makeTab());
    if (!activeTabId || !tabById(activeTabId)) activeTabId = tabs[0].tabId;
  }

  // --- rendering ---------------------------------------------------------------
  function renderShell(el) {
    el.innerHTML = `<h1>Live Code Session</h1>

      ${G.cfg.claudeInstalled === false ? `<div class="panel lc-warn">⚠ Claude Code CLI not found on PATH.
        Install it from <b>claude.ai/code</b> to run sessions — Start is disabled until it's available.</div>` : ''}

      <div class="panel lc-tabbar-panel"><div id="lc-tabbar" class="lc-tabbar"></div></div>

      <div id="lc-tabpanel"></div>

      <div class="panel lc-terminal-wrap"><div id="lc-terminals" class="lc-terminals"></div></div>

      <div class="panel">
        <div class="lc-metrics">
          <div class="lc-metric"><div class="lc-metric-label">Plan</div>
            <div class="lc-metric-val">${App.esc(G.cfg.plan || '—')}</div></div>
          <div class="lc-metric" title="Input + output tokens over the last 7 days, counted on the day each was spent (cache excluded)"><div class="lc-metric-label">Tokens — last 7 days</div>
            <div class="lc-metric-val" id="lc-tok-week">—</div>
            <div class="lc-metric-sub">${G.cfg.usageResetsAt ? 'usage limits reset ' + App.esc(fmtResetDate(G.cfg.usageResetsAt)) : ''}</div></div>
          <div class="lc-usage" id="lc-usage"></div>
        </div>
        <div class="lc-active">
          <div class="lc-metric-label">Active Claude Code sessions <span class="muted">(top 5, last 5 min)</span></div>
          <div id="lc-active-list" class="lc-active-list"><span class="muted">—</span></div>
        </div>
      </div>`;
  }

  function renderTabBar() {
    const bar = document.getElementById('lc-tabbar');
    if (!bar) return;
    bar.innerHTML = tabs.map((t, i) => `
      <div class="lc-tab ${t.tabId === activeTabId ? 'active' : ''}" data-tab="${t.tabId}" title="${App.esc(t.folder || '')}">
        <span class="lc-tab-dot ${t.running ? 'running' : ''}"></span>
        <span class="lc-tab-label">${App.esc(tabLabel(t, i))}</span>
        ${t.isolated ? `<span class="lc-tab-wt" title="Isolated git worktree">⑂</span>` : ''}
        <span class="lc-tab-shell">${t.shell === 'bash' ? 'bash' : 'ps'}</span>
        <button class="lc-tab-close" data-close="${t.tabId}" title="Close tab">×</button>
      </div>`).join('') +
      (tabs.length < MAX_TABS ? `<button class="lc-newtab" id="lc-newtab" title="New session tab">＋ New tab</button>` : '');

    bar.querySelectorAll('.lc-tab').forEach(d =>
      d.addEventListener('click', e => { if (e.target.closest('.lc-tab-close')) return; switchTab(d.dataset.tab); }));
    bar.querySelectorAll('.lc-tab-close').forEach(b =>
      b.addEventListener('click', e => { e.stopPropagation(); closeTab(b.dataset.close); }));
    const nt = document.getElementById('lc-newtab');
    if (nt) nt.addEventListener('click', () => newTab());
  }

  function renderTabPanel() {
    const host = document.getElementById('lc-tabpanel');
    const t = activeTab();
    if (!host || !t) return;

    host.innerHTML = `
      <div class="panel lc-tickets">
        <div class="lc-section-head lc-tickets-head">
          <span>Ticket to work on <span class="muted">(latest ${G.cfg.ticketCount || 3} assigned to you)</span></span>
          ${G.cfg.jiraConfigured ? `<button class="btn lc-refetch" id="lc-refetch-tickets" title="Re-fetch your assigned tickets from JIRA">↻ Re-fetch</button>` : ''}
        </div>
        <div id="lc-ticket-list" class="lc-ticket-list"><span class="muted">Loading…</span></div>
      </div>

      <div class="panel lc-row">
        <label class="lc-label">Working folder</label>
        <input id="lc-folder" class="lc-grow" placeholder="C:\\path\\to\\project" value="${App.esc(t.folder)}">
        <button class="btn" id="lc-browse">Browse…</button>
        <button class="btn" id="lc-resume-sessions" disabled title="Resume an existing session in this folder">Resume Sessions</button>
      </div>

      <div class="panel lc-row">
        <label class="lc-label">Shell</label>
        <div class="tabs" style="margin:0">
          <button class="btn ${t.shell === 'powershell' ? 'active' : ''}" data-shell="powershell">PowerShell</button>
          <button class="btn ${t.shell === 'bash' ? 'active' : ''}" data-shell="bash">Git Bash</button>
        </div>
        <span class="muted" style="margin-left:8px">Git Bash falls back to PowerShell if not installed.</span>
      </div>

      <div class="panel lc-row">
        <label class="lc-label">Model</label>
        <select id="lc-model">
          ${MODELS.map(m => `<option value="${m.value}" ${m.value === t.model ? 'selected' : ''}>${m.label}</option>`).join('')}
        </select>
        <label class="lc-label" style="margin-left:16px">Agent <span class="muted">(if any)</span></label>
        <select id="lc-agent" class="lc-grow"><option value="">(none — default)</option></select>
      </div>

      <div class="panel lc-row">
        <label class="lc-label">Custom Agent <span class="muted">(optional)</span></label>
        <input id="lc-custom-agent" class="lc-grow" placeholder="path to an agent .md file — Claude will use this agent on the ticket" value="${App.esc(t.customAgent)}">
        <button class="btn" id="lc-custom-agent-browse">Browse…</button>
        <span id="lc-custom-agent-name" class="badge ai" style="${t.customAgentName ? '' : 'display:none'}">✨ ${App.esc(t.customAgentName)}</span>
      </div>

      <div class="panel lc-row">
        <button class="btn btn-primary" id="lc-start" disabled>▶ Start session</button>
        <button class="btn" id="lc-stop" disabled>■ Stop</button>
        <button class="btn" id="lc-resume" disabled title="Resume this tab's previous Claude conversation">▷ Resume</button>
        <button class="btn" id="lc-reset" disabled title="Quit Claude (/exit) and restart a fresh session on the same ticket">↺ Reset</button>
        <span style="flex:1"></span>
        <label class="lc-check"><input type="checkbox" id="lc-auto" ${t.autoApprove ? 'checked' : ''}> Auto-approve confirmations</label>
        <label class="lc-check" title="Runs every action with no confirmation — use only in a folder you trust">
          <input type="checkbox" id="lc-bypass" ${t.bypass ? 'checked' : ''}> <span style="color:var(--danger)">Bypass ALL permissions</span></label>
      </div>

      <div class="panel lc-row lc-tab-metrics">
        <span class="lc-metric-label">This session</span>
        <span>Tokens <b id="lc-tok-session">—</b><span id="lc-tok-agents" class="muted"></span></span>
        <span>Cache <b id="lc-cache">—</b></span>
        <span>Context <b id="lc-ctx">—</b></span>
      </div>`;

    wireTabPanel();
    renderTicketList();
    loadAgents();
    updateButtons();
    refreshControlLocks(t);
    loadFolderSessions(t);
  }

  function wireTabPanel() {
    const t = activeTab();
    if (!t) return;

    document.getElementById('lc-folder').addEventListener('input', e => { t.folder = e.target.value.trim(); updateButtons(); renderTabBar(); });
    document.getElementById('lc-folder').addEventListener('change', () => { saveConfig(); loadAgents(); loadFolderSessions(t); });
    document.getElementById('lc-browse').addEventListener('click', browse);
    document.getElementById('lc-resume-sessions').addEventListener('click', () => openResumeSessions(t));
    const refetchBtn = document.getElementById('lc-refetch-tickets');
    if (refetchBtn) refetchBtn.addEventListener('click', refetchTickets);

    document.querySelectorAll('[data-shell]').forEach(b =>
      b.addEventListener('click', () => {
        t.shell = b.dataset.shell;
        document.querySelectorAll('[data-shell]').forEach(x => x.classList.toggle('active', x.dataset.shell === t.shell));
        saveConfig(); renderTabBar();
      }));

    document.getElementById('lc-model').addEventListener('change', e => { t.model = e.target.value; saveConfig(); });
    document.getElementById('lc-agent').addEventListener('change', e => {
      t.agent = e.target.value;
      if (t.agent) { // selecting an agent clears + disables the Custom Agent input
        t.customAgent = '';
        t.customAgentName = '';
        const ca = document.getElementById('lc-custom-agent'); if (ca) ca.value = '';
        const nm = document.getElementById('lc-custom-agent-name'); if (nm) nm.style.display = 'none';
        saveConfig();
      }
      refreshControlLocks(t);
    });
    document.getElementById('lc-custom-agent').addEventListener('input', e => {
      t.customAgent = e.target.value.trim();
      t.customAgentName = '';
      const el = document.getElementById('lc-custom-agent-name'); if (el) el.style.display = 'none';
    });
    document.getElementById('lc-custom-agent').addEventListener('change', () => saveConfig());
    document.getElementById('lc-custom-agent-browse').addEventListener('click', browseCustomAgent);
    document.getElementById('lc-auto').addEventListener('change', async e => {
      if (!e.target.checked) { t.autoApprove = false; saveConfig(); return; }
      const ok = await App.confirm(
        'Auto-approve confirmations?\n\n' +
        'Claude Code will try to automatically approve any prompts it raises during the session ' +
        '(such as file edits) so it can keep working without waiting for you. Only use this in a ' +
        'folder you trust.',
        'Enable auto-approve');
      t.autoApprove = ok;
      e.target.checked = ok;
      saveConfig();
    });
    document.getElementById('lc-bypass').addEventListener('change', async e => {
      if (!e.target.checked) { t.bypass = false; return; }
      const ok = await App.confirm(
        'Bypass ALL permission checks?\n\n' +
        'Claude Code will run every action — editing files AND running shell commands — with NO ' +
        'confirmation. Only use this in a folder you trust.',
        'Enable bypass', true);
      t.bypass = ok;
      e.target.checked = ok;
    });

    document.getElementById('lc-start').addEventListener('click', start);
    document.getElementById('lc-stop').addEventListener('click', stop);
    document.getElementById('lc-resume').addEventListener('click', resume);
    document.getElementById('lc-reset').addEventListener('click', reset);
  }

  // --- tabs: create / switch / close ------------------------------------------
  function newTab() {
    if (tabs.length >= MAX_TABS) { App.toast(`Up to ${MAX_TABS} tabs.`, true); return; }
    const t = makeTab();
    tabs.push(t);
    activeTabId = t.tabId;
    renderTabBar();
    renderTabPanel();
    showActiveTerminal();
  }

  function switchTab(tabId) {
    if (tabId === activeTabId || !tabById(tabId)) return;
    activeTabId = tabId;
    renderTabBar();
    renderTabPanel();
    showActiveTerminal();
    pollMetrics();
  }

  async function closeTab(tabId) {
    const t = tabById(tabId);
    if (!t) return;
    if (t.running) {
      const ok = await App.confirm(
        'This tab has a running session.\n\nStop it and close the tab?',
        'Stop and close', true);
      if (!ok) return;
    }
    let res;
    try { res = await Bridge.call('livecode.closeTab', { tabId }, 0); } catch { /* dispose anyway */ }
    if (res && res.worktreeKept) App.toast(`Worktree kept (${res.worktreeReason}): ${res.worktreePath}`, true);
    else if (res && res.worktreePath) App.toast('Worktree removed.');
    disposeTabTerm(t);
    const div = terminalDiv(tabId, false);
    if (div) div.remove();
    tabs = tabs.filter(x => x.tabId !== tabId);
    if (!tabs.length) { newTab(); return; }               // always keep at least one tab
    if (activeTabId === tabId) activeTabId = tabs[0].tabId;
    renderTabBar();
    renderTabPanel();
    showActiveTerminal();
  }

  // Called by the sidebar hover panel: jump to the page and focus a specific tab.
  function focusTab(tabId) {
    pendingFocusId = tabId;
    if ((location.hash || '').slice(1) !== 'livecode') location.hash = '#livecode';
    else applyPendingFocus();
  }
  function applyPendingFocus() {
    if (!pendingFocusId) return;
    const id = pendingFocusId; pendingFocusId = null;
    if (tabById(id)) switchTab(id);
  }

  // --- tickets / agents --------------------------------------------------------
  async function loadTickets() {
    if (!G.cfg.jiraConfigured) { G.ticketsLoaded = true; renderTicketList(); return; }
    try {
      const r = await Bridge.call('livecode.tickets', {}, 0);
      G.tickets = (r && r.tickets) || [];
    } catch (e) {
      G.tickets = [];
    }
    G.ticketsLoaded = true;
    renderTicketList();
  }

  // Manual re-fetch (↻ button beside the ticket list): pull the assigned tickets from JIRA again,
  // showing a busy state on the button. livecode.tickets always hits JIRA live (no cache).
  async function refetchTickets() {
    const btn = document.getElementById('lc-refetch-tickets');
    if (btn) { btn.disabled = true; btn.textContent = '↻ Fetching…'; }
    G.ticketsLoaded = false;
    renderTicketList();
    await loadTickets();
    if (btn) { btn.disabled = false; btn.textContent = '↻ Re-fetch'; }
  }

  function renderTicketList() {
    const listEl = document.getElementById('lc-ticket-list');
    const t = activeTab();
    if (!listEl || !t) return;
    if (!G.cfg.jiraConfigured) {
      listEl.innerHTML = `<span class="muted">JIRA isn’t configured. Add your site, email and token in
        <a href="#settings">Settings</a> to see assigned tickets — a ticket must be selected to start a session.</span>`;
      return;
    }
    if (!G.ticketsLoaded) { listEl.innerHTML = `<span class="muted">Loading…</span>`; return; }
    if (!G.tickets.length) {
      listEl.innerHTML = `<span class="muted">No tickets currently assigned to you.</span>`;
      return;
    }
    const sel = t.ticket ? t.ticket.key : null;
    listEl.innerHTML = G.tickets.map((tk, i) => `
      <button class="lc-ticket ${tk.key === sel ? 'selected' : ''}" data-idx="${i}">
        <span class="badge">${App.esc(tk.key)}</span>
        <span class="lc-ticket-sum">${App.esc(tk.summary || '')}</span>
        <span class="muted lc-ticket-status">${App.esc(tk.status || '')}</span>
      </button>`).join('');
    listEl.querySelectorAll('.lc-ticket').forEach(b =>
      b.addEventListener('click', () => selectTicket(+b.dataset.idx)));
  }

  function selectTicket(idx) {
    const t = activeTab();
    if (!t) return;
    t.ticket = G.tickets[idx] || null;
    t.ticketKeyHint = '';
    document.querySelectorAll('.lc-ticket').forEach((b, i) => b.classList.toggle('selected', i === idx));
    updateButtons();
    renderTabBar();
  }

  async function loadAgents() {
    const selEl = document.getElementById('lc-agent');
    const t = activeTab();
    if (!selEl || !t) return;
    try {
      const agents = await Bridge.call('livecode.listAgents', { folder: t.folder });
      const keep = t.agent;
      selEl.innerHTML = `<option value="">(none — default)</option>` +
        agents.map(a => `<option value="${App.esc(a.name)}" title="${App.esc(a.description || '')}">
          ${App.esc(a.name)} <span>(${App.esc(a.scope)})</span></option>`).join('');
      if (keep && [...selEl.options].some(o => o.value === keep)) selEl.value = keep;
      else t.agent = '';
    } catch { /* leave the "(none)" default */ }
  }

  // --- Resume Sessions (pick an existing session in this folder) ---------------
  async function loadFolderSessions(t) {
    const btn = document.getElementById('lc-resume-sessions');
    if (!t.folder) { t.folderSessions = []; if (btn && t === activeTab()) btn.disabled = true; return; }
    try {
      const r = await Bridge.call('livecode.sessionsInFolder', { folder: t.folder }, 5000);
      t.folderSessions = (r && r.sessions) || [];
    } catch { t.folderSessions = []; }
    if (btn && t === activeTab()) btn.disabled = !t.folderSessions.length;
  }

  function fmtSessionTime(iso) {
    const d = new Date(iso);
    if (isNaN(d)) return '';
    return d.toLocaleDateString([], { month: 'short', day: 'numeric' }) + ' ' +
           d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  async function openResumeSessions(t) {
    await loadFolderSessions(t); // freshen
    const list = t.folderSessions;
    if (!list.length) { App.toast('No previous sessions in this folder.', true); return; }

    const ov = document.createElement('div');
    ov.className = 'modal-overlay';
    ov.innerHTML = `<div class="modal">
      <div class="lc-section-head">Resume a session <span class="muted">in ${App.esc(t.folder)}</span></div>
      <div class="lc-session-list">${list.map((s, i) => `
        <button class="lc-session-row" data-idx="${i}">
          <span class="lc-session-label">${App.esc(s.label)}</span>
          <span class="lc-session-meta">${App.esc(fmtSessionTime(s.updated))} · ${App.esc(String(s.sessionId).slice(0, 8))}</span>
        </button>`).join('')}</div>
      <div class="modal-actions"><button class="btn" data-act="cancel">Cancel</button></div>
    </div>`;
    const close = () => ov.remove();
    ov.querySelector('[data-act="cancel"]').addEventListener('click', close);
    ov.addEventListener('click', e => { if (e.target === ov) close(); });
    ov.querySelectorAll('.lc-session-row').forEach(row =>
      row.addEventListener('click', () => { close(); resumePickedSession(t, list[+row.dataset.idx].sessionId); }));
    document.body.appendChild(ov);
  }

  async function resumePickedSession(t, sessionId) {
    if (t.running) {
      const ok = await App.confirm('Stop the current session and resume the selected one?', 'Resume');
      if (!ok) return;
    }
    if (G.cfg.claudeInstalled === false) { App.toast('Claude Code CLI not found — install it to resume.', true); return; }
    if (G.cfg.apiKeyPresent) {
      const ok = await App.confirm(
        'ANTHROPIC_API_KEY is set in your environment.\n\n' +
        'Resume with it removed so your Claude subscription is used (not metered API billing)?',
        'Resume on subscription');
      if (!ok) return;
    }
    const term = createTerm(t);
    try {
      await Bridge.call('livecode.resumeSession', {
        tabId: t.tabId, folder: t.folder, sessionId,
        shell: t.shell, autoApprove: t.autoApprove, bypass: t.bypass,
        cols: term.cols, rows: term.rows
      }, 0);
      t.running = true;
      t.canResume = true;
      t.resumedPick = true;
      t.activeFolder = t.folder;
      t.isolated = false;
      updateButtons(); renderTabBar(); refreshControlLocks(t);
      App.toast('Resuming session ' + String(sessionId).slice(0, 8) + '…');
      pollMetrics();
      term.focus();
    } catch (e) {
      App.toast('Failed to resume session: ' + e.message, true);
      disposeTabTerm(t); t.running = false; t.resumedPick = false; updateButtons(); refreshControlLocks(t);
    }
  }

  async function browse() {
    const t = activeTab();
    if (!t) return;
    try {
      const r = await Bridge.call('livecode.pickFolder', { current: t.folder }, 0);
      if (r && r.path) {
        t.folder = r.path;
        const inp = document.getElementById('lc-folder'); if (inp) inp.value = r.path;
        updateButtons(); saveConfig(); loadAgents(); loadFolderSessions(t); renderTabBar();
      }
    } catch (e) {
      App.toast('Folder picker unavailable — type the path instead. (' + e.message + ')', true);
    }
  }

  async function browseCustomAgent() {
    const t = activeTab();
    if (!t) return;
    try {
      const r = await Bridge.call('livecode.pickAgentFile', { current: t.customAgent }, 0);
      if (r && r.path) {
        t.customAgent = r.path;
        t.customAgentName = r.agentName || '';
        const inp = document.getElementById('lc-custom-agent'); if (inp) inp.value = r.path;
        const el = document.getElementById('lc-custom-agent-name');
        if (el) { el.textContent = '✨ ' + (r.agentName || '(unnamed)'); el.style.display = ''; }
        saveConfig();
        App.toast(r.agentName ? `Custom agent loaded: ${r.agentName}` : 'Agent file selected.');
      }
    } catch (e) {
      App.toast('File picker unavailable — type the path instead.', true);
    }
  }

  // Single authority for control locking: Custom Agent is disabled when an Agent is selected OR a
  // picked-resume session is running; Shell + Model are disabled only while a picked-resume runs.
  function refreshControlLocks(t) {
    if (!t) return;
    const lockResume = t.resumedPick && t.running;
    const ca = document.getElementById('lc-custom-agent');
    const cab = document.getElementById('lc-custom-agent-browse');
    const caDisabled = !!t.agent || lockResume;
    if (ca) ca.disabled = caDisabled;
    if (cab) cab.disabled = caDisabled;
    document.querySelectorAll('[data-shell]').forEach(b => { b.disabled = lockResume; });
    const model = document.getElementById('lc-model');
    if (model) model.disabled = lockResume;
  }

  function updateButtons() {
    const t = activeTab();
    if (!t) return;
    const start = document.getElementById('lc-start');
    const stopBtn = document.getElementById('lc-stop');
    const resumeBtn = document.getElementById('lc-resume');
    const resetBtn = document.getElementById('lc-reset');
    const installed = G.cfg.claudeInstalled !== false;
    if (start) start.disabled = t.running || !t.ticket || !t.folder || !installed;
    if (stopBtn) stopBtn.disabled = !t.running;
    if (resumeBtn) resumeBtn.disabled = t.running || !t.canResume || !installed;
    if (resetBtn) resetBtn.disabled = !t.running || !installed;
  }

  function saveConfig() {
    const t = activeTab();
    if (!t) return;
    Bridge.call('livecode.saveConfig', {
      folder: t.folder, shell: t.shell, model: t.model,
      autoApprove: t.autoApprove, customAgent: t.customAgent
    }).catch(() => {});
  }

  let hashHooked = false;
  function ensureHashCleanup() {
    if (hashHooked) return;
    hashHooked = true;
    window.addEventListener('hashchange', () => {
      if ((location.hash || '').slice(1) !== 'livecode') {
        if (G.metricsTimer) { clearInterval(G.metricsTimer); G.metricsTimer = null; }
        if (G.activeTimer) { clearInterval(G.activeTimer); G.activeTimer = null; }
      }
    });
  }

  // --- metrics -----------------------------------------------------------------
  async function pollMetrics() {
    const t = activeTab();
    try {
      const m = await Bridge.call('livecode.metrics', { tabId: t ? t.tabId : '' }, 0);
      const set = (id, v) => { const el = document.getElementById(id); if (el) el.textContent = v; };
      set('lc-tok-week', App.fmtNum(m.weekTokens));
      set('lc-tok-session', m.active ? App.fmtNum(m.sessionTokens) : '—');
      // Cache reported separately (created + read) so "Tokens" stays consistent with the dashboard.
      set('lc-cache', m.active ? App.fmtNum(m.cacheTokens) : '—');
      set('lc-ctx', m.active ? `${App.fmtNum(m.contextTokens)} of ${App.fmtNum(m.contextSize)} (${m.contextPct}%)` : '—');
      // Show an "incl. agents" hint + tooltip breakdown when sub-agents contributed tokens.
      const tokEl = document.getElementById('lc-tok-session');
      if (tokEl) {
        if (m.active && m.agentTokens > 0) {
          tokEl.title = `Main ${App.fmtNum(m.mainTokens)} + agents ${App.fmtNum(m.agentTokens)} (input + output, same as the dashboard)`;
          set('lc-tok-agents', ` incl. ${App.fmtNum(m.agentTokens)} agents`);
        } else {
          tokEl.title = 'input + output (same as the dashboard)';
          set('lc-tok-agents', '');
        }
      }
      // Cache tooltip: created vs read split (read is re-counted each turn, so it's usually the bulk).
      const cacheEl = document.getElementById('lc-cache');
      if (cacheEl) cacheEl.title = m.active
        ? `created ${App.fmtNum(m.cacheCreation)} · read ${App.fmtNum(m.cacheRead)}` : '';
    } catch { /* transient */ }
  }

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

  // Rolling usage-limit bars (session 5h + week 7d) from livecode.usage — server-computed % + reset
  // time, backend-cached 5 min. Silently hidden when signed out / offline (available:false).
  async function pollUsage() {
    const host = document.getElementById('lc-usage');
    if (!host) return;
    try {
      const u = await Bridge.call('livecode.usage', {}, 0);
      if (!u || !u.available) { host.innerHTML = ''; return; }
      host.innerHTML = usageRow('SESSION', u.sessionPct, u.sessionResetsAt)
                     + usageRow('WEEK', u.weekPct, u.weekResetsAt);
    } catch { /* transient — keep last render */ }
  }

  // One usage bar row: clamped fill + threshold color (≥95% crit, ≥80% warn) + "N% · resets …".
  function usageRow(label, pct, resetsAt) {
    if (pct == null) return '';
    const c = Math.max(0, Math.min(100, Number(pct) || 0));
    const cls = c >= 95 ? ' crit' : c >= 80 ? ' warn' : '';
    const reset = resetsAt ? ` · resets ${App.esc(fmtReset(resetsAt))}` : '';
    return `<div class="lc-usage-row">
      <span class="lc-usage-label">${App.esc(label)}</span>
      <div class="lc-bar-track"><div class="lc-bar-fill${cls}" style="width:${c}%"></div></div>
      <span class="lc-usage-pct">${Math.round(c)}%${reset}</span>
    </div>`;
  }

  // Compact reset time: just the time if it lands today (local), else "Fri 07:00 am".
  function fmtReset(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    if (isNaN(d)) return '';
    const hm = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    return d.toDateString() === new Date().toDateString()
      ? hm : `${d.toLocaleDateString([], { weekday: 'short' })} ${hm}`;
  }

  // --- terminals ---------------------------------------------------------------
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

  // Single subscription for ALL tabs; incoming events are routed to the right terminal by tabId,
  // so background tabs keep receiving output too (switching tabs is instant, no replay needed).
  function subscribeEvents() {
    G.outputSub = Bridge.on('pty.output', d => {
      if (!d || !d.tabId || !d.data) return;
      const t = tabById(d.tabId);
      if (t && t.term.inst) t.term.inst.write(b64ToBytes(d.data));
    });
    G.exitSub = Bridge.on('pty.exit', d => {
      if (!d || !d.tabId) return;
      const t = tabById(d.tabId);
      if (!t) return;
      if (t.term.inst) t.term.inst.write(`\r\n\x1b[90m[process exited with code ${d.code}]\x1b[0m\r\n`);
      t.running = false;
      t.resumedPick = false;
      renderTabBar();
      if (t.tabId === activeTabId) { updateButtons(); refreshControlLocks(t); }
    });
  }

  // Find (or create) the persistent terminal <div> for a tab inside #lc-terminals.
  function terminalDiv(tabId, create) {
    const host = document.getElementById('lc-terminals');
    if (!host) return null;
    let d = host.querySelector(`.lc-terminal[data-tab="${tabId}"]`);
    if (!d && create) {
      d = document.createElement('div');
      d.className = 'lc-terminal empty';
      d.dataset.tab = tabId;
      d.textContent = 'The live Claude Code terminal appears here once a session is started.';
      host.appendChild(d);
    }
    return d;
  }

  // Create (or recreate) the xterm for a tab, optionally replaying its buffered output.
  function createTerm(t, replayB64) {
    disposeTabTerm(t);
    const host = terminalDiv(t.tabId, true);
    host.classList.remove('empty');
    host.textContent = '';

    const term = new Terminal({
      cursorBlink: true,
      fontFamily: '"Cascadia Mono", "Consolas", monospace',
      fontSize: 13,
      theme: { background: '#1e1e1e', foreground: '#d4d4d4' }
    });
    const fit = new FitAddon.FitAddon();
    term.loadAddon(fit);
    term.open(host);
    fit.fit();
    t.term.inst = term;
    t.term.fit = fit;

    if (replayB64) term.write(b64ToBytes(replayB64));

    term.onData(data => Bridge.call('pty.input', { tabId: t.tabId, data: strToB64(data) }, 0).catch(() => {}));
    t.term.ro = new ResizeObserver(() => refit(t));
    t.term.ro.observe(host);
    showActiveTerminal();
    return term;
  }

  // Show only the active tab's terminal; keep the others mounted but hidden.
  function showActiveTerminal() {
    const host = document.getElementById('lc-terminals');
    if (!host) return;
    host.querySelectorAll('.lc-terminal').forEach(d => {
      d.style.display = d.dataset.tab === activeTabId ? '' : 'none';
    });
    const t = activeTab();
    if (t) refit(t);
  }

  function refit(t) {
    if (!t || !t.term.fit || !t.term.inst) return;
    try {
      t.term.fit.fit();
      if (t.running) Bridge.call('pty.resize', { tabId: t.tabId, cols: t.term.inst.cols, rows: t.term.inst.rows }, 0).catch(() => {});
    } catch { /* element not laid out */ }
  }

  function disposeTabTerm(t) {
    if (t.term.ro) { try { t.term.ro.disconnect(); } catch {} t.term.ro = null; }
    if (t.term.inst) { try { t.term.inst.dispose(); } catch {} t.term.inst = null; }
    t.term.fit = null;
  }

  // On (re)entering the page, reconnect each running tab's terminal by replaying its buffer.
  async function reattachAll() {
    for (const t of tabs) {
      let at;
      try { at = await Bridge.call('livecode.attach', { tabId: t.tabId }, 0); } catch { continue; }
      if (!at) continue;
      t.canResume = !!(at.canResume || at.running);
      if (at.running) {
        const term = createTerm(t, at.data);
        t.running = true;
        if (!t.activeFolder) t.activeFolder = t.folder; // authoritative value comes from reconcile/list
        if (t.tabId === activeTabId) { Bridge.call('pty.resize', { tabId: t.tabId, cols: term.cols, rows: term.rows }, 0).catch(() => {}); }
      } else {
        t.running = false;
      }
    }
    renderTabBar();
    updateButtons();
  }

  // --- session actions (operate on the active tab) -----------------------------
  async function start() {
    const t = activeTab();
    if (!t || !t.folder || t.running) return;
    if (G.cfg.claudeInstalled === false) {
      App.toast('Claude Code CLI not found — install it (claude.ai/code) to run a session.', true);
      return;
    }
    if (G.cfg.apiKeyPresent) {
      const ok = await App.confirm(
        'ANTHROPIC_API_KEY is set in your environment.\n\n' +
        'This session will run with it removed so your Claude subscription is used ' +
        '(not metered API billing). Continue?',
        'Run on subscription');
      if (!ok) return;
    }
    // Warn if another running tab already works in this folder; may switch to worktree isolation.
    const isolation = await resolveIsolation(t);
    if (isolation === null) return; // user cancelled
    saveConfig();
    const term = createTerm(t);
    try {
      const r = await Bridge.call('livecode.start', {
        tabId: t.tabId,
        shell: t.shell, folder: t.folder,
        model: t.model, agent: t.agent, customAgent: t.customAgent,
        ticketKey: t.ticket ? t.ticket.key : null,
        ticketSummary: t.ticket ? t.ticket.summary : null,
        autoApprove: t.autoApprove, bypass: t.bypass,
        isolation,
        cols: term.cols, rows: term.rows
      }, 0);
      t.running = true;
      t.canResume = true;
      t.activeFolder = (r && r.folder) || t.folder;
      t.isolated = !!(r && r.isolated);
      updateButtons(); renderTabBar();
      if (r && r.isolated) App.toast('Running in isolated worktree: ' + r.worktreePath);
      if (r && r.fellBack) App.toast('Git Bash not found — using PowerShell instead.', true);
      if (r && r.agentUsed) App.toast(`Using the ${r.agentUsed} agent on ${t.ticket.key}.`);
      if (r && r.kickoff) App.toast(`Starting Claude Code on ${t.ticket.key} (linked to the ticket)…`);
      pollMetrics();
      term.focus();
    } catch (e) {
      App.toast('Failed to start session: ' + e.message, true);
      disposeTabTerm(t); t.running = false; updateButtons(); renderTabBar();
    }
  }

  async function resume() {
    const t = activeTab();
    if (!t || t.running || !t.canResume) return;
    if (G.cfg.claudeInstalled === false) { App.toast('Claude Code CLI not found — install it to resume.', true); return; }
    if (G.cfg.apiKeyPresent) {
      const ok = await App.confirm(
        'ANTHROPIC_API_KEY is set in your environment.\n\n' +
        'Resume with it removed so your Claude subscription is used (not metered API billing)?',
        'Resume on subscription');
      if (!ok) return;
    }
    saveConfig();
    const term = createTerm(t);
    try {
      await Bridge.call('livecode.resume', {
        tabId: t.tabId,
        shell: t.shell, folder: t.folder, model: t.model, agent: t.agent,
        autoApprove: t.autoApprove, bypass: t.bypass, cols: term.cols, rows: term.rows
      }, 0);
      t.running = true;
      updateButtons(); renderTabBar();
      App.toast('Resuming the previous session…');
      pollMetrics();
      term.focus();
    } catch (e) {
      App.toast('Failed to resume: ' + e.message, true);
      disposeTabTerm(t); t.running = false; updateButtons(); renderTabBar();
    }
  }

  async function reset() {
    const t = activeTab();
    if (!t || !t.running) return;
    saveConfig();
    const term = createTerm(t);
    try {
      // Reset reuses the tab's current directory (the worktree cwd when isolated) — never a new
      // worktree (isolation:'none'); the entry keeps its existing WorktreeInfo for close cleanup.
      const r = await Bridge.call('livecode.reset', {
        tabId: t.tabId,
        shell: t.shell, folder: t.activeFolder || t.folder, model: t.model, agent: t.agent, customAgent: t.customAgent,
        ticketKey: t.ticket ? t.ticket.key : null,
        ticketSummary: t.ticket ? t.ticket.summary : null,
        autoApprove: t.autoApprove, bypass: t.bypass,
        isolation: 'none',
        cols: term.cols, rows: term.rows
      }, 0);
      t.running = true;
      t.canResume = true;
      updateButtons(); renderTabBar();
      App.toast(r && r.kickoff && t.ticket ? `Reset — restarted Claude on ${t.ticket.key}.` : 'Reset — restarted the session.');
      pollMetrics();
      term.focus();
    } catch (e) {
      App.toast('Reset failed: ' + e.message, true);
      disposeTabTerm(t); t.running = false; updateButtons(); renderTabBar();
    }
  }

  async function stop() {
    const t = activeTab();
    if (!t) return;
    try { await Bridge.call('livecode.stop', { tabId: t.tabId }, 0); } catch { /* ignore */ }
    t.running = false;
    t.resumedPick = false;
    updateButtons(); renderTabBar(); refreshControlLocks(t);
  }

  return { render: load, focusTab };
})();
