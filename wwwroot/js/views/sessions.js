window.Views = window.Views || {};
window.Views.sessions = (function () {
  let filter = 'all';

  const TABS = [
    { id: 'all', label: 'All' },
    { id: 'pending', label: 'Needs review' },
    { id: 'not_ticket_related', label: 'Not ticket-related' }
  ];

  function projectName(dir) {
    if (!dir) return '';
    const parts = dir.split(/[\\/]/).filter(Boolean);
    return parts.slice(-2).join('/');
  }

  function toolMix(s) {
    const edit = (s.editCount || 0) + (s.writeCount || 0);
    const read = s.readCount || 0;
    const bash = s.bashCount || 0;
    const other = s.otherToolCount || 0;
    const total = edit + read + bash + other;
    if (!total) return '<span class="muted">—</span>';
    const pct = n => (100 * n / total).toFixed(0) + '%';
    return `<div class="tool-mix" title="edit/write ${edit}, read ${read}, shell ${bash}, other ${other}">
      <span class="tm-edit" style="width:${pct(edit)}"></span>
      <span class="tm-read" style="width:${pct(read)}"></span>
      <span class="tm-bash" style="width:${pct(bash)}"></span>
      <span class="tm-other" style="width:${pct(other)}"></span>
    </div>`;
  }

  function linkBadges(s) {
    if (!s.links) return '<span class="muted">none</span>';
    return s.links.split(';').map(pair => {
      const [key, source] = pair.split('|');
      const k = App.esc(key), sid = App.esc(s.id);
      const confirm = source === 'auto'
        ? `<a href="#" title="Confirm this link" onclick="Views.sessions.confirm('${sid}','${k}');return false">✓</a>`
        : '';
      return `<span class="badge ${App.esc(source)}" title="${App.esc(source)}">${k} ${confirm}
        <a href="#" title="Remove link" onclick="Views.sessions.unlink('${sid}','${k}');return false">×</a></span>`;
    }).join(' ');
  }

  function rowActions(s) {
    const sid = App.esc(s.id);
    const dismiss = s.reviewState === 'not_ticket_related'
      ? `<button class="btn btn-small" onclick="Views.sessions.reopen('${sid}')">Reopen</button>`
      : `<button class="btn btn-small" title="Mark as not ticket-related" onclick="Views.sessions.dismiss('${sid}')">Dismiss</button>`;
    return `<div style="display:flex;gap:4px;align-items:center">
      <input id="assign-${sid}" placeholder="ABC-123" style="width:90px;padding:3px 6px;font-size:12px"
             onkeydown="if(event.key==='Enter')Views.sessions.assign('${sid}')">
      <button class="btn btn-small" onclick="Views.sessions.assign('${sid}')">Assign</button>
      ${dismiss}
    </div>`;
  }

  async function load(el) {
    let sessions;
    try {
      sessions = await Bridge.call('sessions.list', { filter });
    } catch (e) {
      el.innerHTML = `<div class="panel empty">Failed to load sessions: ${App.esc(e.message)}</div>`;
      return;
    }

    const tabs = TABS.map(t =>
      `<button class="btn ${t.id === filter ? 'active' : ''}" onclick="Views.sessions.setFilter('${t.id}')">${t.label}</button>`
    ).join('');
    const controls = `
      <div style="display:flex;align-items:center;margin-bottom:14px">
        <div class="tabs" style="margin:0">${tabs}</div>
        <span style="flex:1"></span>
        <button class="btn" onclick="App.exportExcel('export.sessions')">⬇ Export to Excel</button>
      </div>`;

    if (!sessions.length) {
      el.innerHTML = `<h1>Sessions</h1>${controls}
        <div class="panel empty">No sessions here. Use “Scan now” to pick up Claude Code transcripts.</div>`;
      return;
    }

    const rows = sessions.map(s => `
      <tr>
        <td>
          <a class="session-link" href="#session/${App.esc(s.id)}" title="View session detail">${App.esc(s.title || '(untitled session)')}</a>
          <div class="muted" style="font-size:11.5px">${App.esc(projectName(s.projectDir))}
            ${s.reviewState === 'pending' ? '<span class="badge pending">needs review</span>' : ''}</div>
        </td>
        <td class="muted" style="white-space:nowrap">${App.fmtDate(s.startedAt)}</td>
        <td class="muted">${App.esc((s.model || '').replace('claude-', ''))}</td>
        <td title="input ${s.inputTokens || 0} / output ${s.outputTokens || 0}">
          ${App.fmtNum((s.inputTokens || 0) + (s.outputTokens || 0))}</td>
        <td>${toolMix(s)}</td>
        <td>${linkBadges(s)}</td>
        <td>${rowActions(s)}</td>
      </tr>`).join('');

    el.innerHTML = `<h1>Sessions</h1>
      ${controls}
      <div class="panel" style="padding:0">
        <table>
          <thead><tr>
            <th>Session</th><th>Started</th><th>Model</th><th>Tokens</th>
            <th>Tools</th><th>Tickets</th><th>Actions</th>
          </tr></thead>
          <tbody>${rows}</tbody>
        </table>
      </div>
      <div class="footnote">Tokens = input + output (cache reads excluded). Tool bar: blue = edit/write, light blue = read, orange = shell, grey = other.</div>`;
  }

  async function act(promise, okMessage) {
    try {
      await promise;
      if (okMessage) App.toast(okMessage);
      App.refresh();
    } catch (e) {
      App.toast(e.message, true);
    }
  }

  return {
    render: load,
    setFilter(f) { filter = f; App.refresh(); },
    assign(sessionId) {
      const input = document.getElementById('assign-' + sessionId);
      const key = (input && input.value || '').trim();
      if (!key) { App.toast('Enter a ticket key first', true); return; }
      act(Bridge.call('sessions.assignTicket', { sessionId, ticketKey: key }), 'Ticket linked');
    },
    confirm(sessionId, ticketKey) {
      act(Bridge.call('sessions.confirmLink', { sessionId, ticketKey }), 'Link confirmed');
    },
    unlink(sessionId, ticketKey) {
      act(Bridge.call('sessions.removeLink', { sessionId, ticketKey }), 'Link removed');
    },
    dismiss(sessionId) { act(Bridge.call('sessions.dismiss', { sessionId })); },
    reopen(sessionId) { act(Bridge.call('sessions.reopen', { sessionId })); }
  };
})();
