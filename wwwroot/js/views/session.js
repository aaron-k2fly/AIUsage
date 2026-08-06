// Session detail page (#session/<id>). Reached by clicking a row on the Sessions list.
// Data comes from `sessions.detail`, which re-parses the one transcript on demand for the
// exact per-tool / per-model breakdown the list doesn't store. Cost is derived here from a
// model-family rate table so the $/Mtok footnote lives next to the numbers it explains.
window.Views = window.Views || {};
window.Views.session = (function () {
  // $ per million tokens, by model family (Claude public list prices).
  const RATES = {
    opus:   { in: 15,  out: 75, cacheWrite: 18.75, cacheRead: 1.5  },
    sonnet: { in: 3,   out: 15, cacheWrite: 3.75,  cacheRead: 0.3  },
    haiku:  { in: 0.8, out: 4,  cacheWrite: 1.0,   cacheRead: 0.08 }
  };
  const REVIEW = { pending: 'needs review', linked: 'linked', not_ticket_related: 'not ticket-related' };
  // Distinct colours for the tool-mix segments (one per tool, in count order).
  const TOOL_COLORS = ['#4f6df5', '#9bb0f8', '#c86b9b', '#2e9e5b', '#d9822b', '#7c6cd6', '#38b2b2', '#c3c9d6'];

  function familyOf(model) {
    const m = (model || '').toLowerCase();
    if (m.includes('haiku')) return 'haiku';
    if (m.includes('sonnet')) return 'sonnet';
    return 'opus';
  }
  function shortModel(m) { return (m || '').replace(/^claude-/, '') || 'unknown'; }
  function money(n) { return '$' + (n || 0).toFixed(2); }
  function pct(n) { return (100 * (n || 0)).toFixed(0) + '%'; }
  function fmtDur(ms) {
    if (!ms || ms < 0) return '0m';
    const min = Math.round(ms / 60000);
    const h = Math.floor(min / 60), m = min % 60;
    return h ? (m ? `${h}h ${m}m` : `${h}h`) : `${m}m`;
  }

  function kv(label, value) {
    return `<div class="kv-row"><span class="kv-key">${App.esc(label)}</span><span class="kv-val">${value}</span></div>`;
  }

  // Badge/assign actions use data-* attributes dispatched by the shared delegated listener in
  // sessions.js — NOT inline onclick strings, where App.esc cannot protect a JS string literal
  // (the attribute is entity-decoded before it is compiled). See AIU-03 in the 2026-08 audit.
  function ticketBadges(links, id) {
    if (!links) return '<span class="muted">No tickets linked</span>';
    const sid = App.esc(id);
    return links.split(';').map(pair => {
      const [key, source] = pair.split('|');
      const k = App.esc(key);
      const confirm = source === 'auto'
        ? `<a href="#" title="Confirm this link" data-sess-act="confirm" data-sess-id="${sid}" data-sess-key="${k}">✓</a>`
        : '';
      return `<span class="badge ${App.esc(source)}" title="${App.esc(source)}">${k} ${confirm}
        <a href="#" title="Remove link" data-sess-act="unlink" data-sess-id="${sid}" data-sess-key="${k}">×</a></span>`;
    }).join(' ');
  }

  function toolsCard(d) {
    if (!d.tools || !d.tools.length) return `<h2>Tools</h2><div class="muted">No tool calls recorded.</div>`;
    const total = d.tools.reduce((s, t) => s + t.count, 0) || 1;
    const bar = d.tools.map((t, i) =>
      `<span style="width:${(100 * t.count / total).toFixed(2)}%;background:${TOOL_COLORS[i % TOOL_COLORS.length]}"
             title="${App.esc(t.name)} ×${t.count}"></span>`).join('');
    const list = d.tools.map(t => `${App.esc(t.name)} ×${t.count}`).join(' · ');
    return `<h2>Tools</h2>
      <div class="seg-bar">${bar}</div>
      <div class="tool-list">${list}</div>`;
  }

  function modelsCard(d) {
    if (!d.models || !d.models.length) return '';
    const maxOut = Math.max(1, ...d.models.map(m => m.output));
    const rows = d.models.map(m => `
      <div class="model-row">
        <span class="model-name">${App.esc(shortModel(m.model))}</span>
        <span class="model-track"><span class="model-fill" style="width:${(100 * m.output / maxOut).toFixed(2)}%"></span></span>
        <span class="model-out">${App.fmtNum(m.output)} out</span>
      </div>`).join('');
    return `<h2 style="margin-top:20px">Models</h2>${rows}`;
  }

  function extChip(text) { return `<span class="ext-chip">${App.esc(text)}</span>`; }
  function extGroup(label, items, fmt) {
    const body = (items && items.length)
      ? `<div class="ext-items">${items.map(fmt).join('')}</div>`
      : `<span class="ext-none muted">—</span>`;
    return `<div class="ext-group"><div class="ext-label">${label}</div>${body}</div>`;
  }
  function extensionsCard(d) {
    const total = (d.agents || []).length + (d.mcps || []).length + (d.skills || []).length + (d.hooks || []).length;
    const inner = total === 0
      ? `<div class="muted">No sub-agents, MCP tools, skills or hooks recorded for this session.</div>`
      : extGroup('Agents', d.agents, i => extChip(`${i.name} ×${i.count}`))
        + extGroup('MCP tools', d.mcps, i => extChip(`${i.server}${i.tool ? ' · ' + i.tool : ''} ×${i.count}`))
        + extGroup('Skills', d.skills, i => extChip(`${i.name} ×${i.count}`))
        + extGroup('Hooks', d.hooks, i => extChip(`${i.name} ×${i.count}`));
    return `<h2>Agents & extensions</h2>${inner}`;
  }

  function costCard(d) {
    // Sum cost per model family; fall back to the primary model's rates if there's no per-model split.
    let input = 0, output = 0, cacheWrite = 0, cacheRead = 0;
    const models = (d.models && d.models.length)
      ? d.models
      : [{ model: d.model, input: d.inputTokens, output: d.outputTokens, cacheCreation: d.cacheCreationTokens, cacheRead: d.cacheReadTokens }];
    for (const m of models) {
      const r = RATES[familyOf(m.model)];
      input += (m.input || 0) * r.in / 1e6;
      output += (m.output || 0) * r.out / 1e6;
      cacheWrite += (m.cacheCreation || 0) * r.cacheWrite / 1e6;
      cacheRead += (m.cacheRead || 0) * r.cacheRead / 1e6;
    }
    const total = input + output + cacheWrite + cacheRead;
    const cacheDenom = (d.cacheReadTokens || 0) + (d.cacheCreationTokens || 0);
    const cacheHit = cacheDenom ? (d.cacheReadTokens || 0) / cacheDenom : 0;
    const t = total || 1;
    const r = RATES[familyOf(d.model)];

    const bar = (label, cls, cost) => `
      <div class="cost-row">
        <span class="cost-label">${label}</span>
        <span class="cost-track"><span class="cost-fill ${cls}" style="width:${(100 * cost / t).toFixed(2)}%"></span></span>
        <span class="cost-amt">${money(cost)} · ${pct(cost / t)}</span>
      </div>`;

    return `<h2>Token cost</h2>
      ${kv('Est. cost', money(total))}
      ${kv('Cache hit', pct(cacheHit))}
      ${kv('Output share', pct(output / t) + ' of cost')}
      <div class="cost-bars">
        ${bar('Cache read', 'cr', cacheRead)}
        ${bar('Cache write', 'cw', cacheWrite)}
        ${bar('Output', 'out', output)}
        ${bar('Input', 'in', input)}
      </div>
      <div class="footnote">Cache-read is billed ~0.1×, so a huge token count is the cheapest part — cost concentrates
        in cache-write (loaded context) + output. Rates $/Mtok for ${App.esc(shortModel(d.model))}:
        in ${r.in} · out ${r.out} · cache-write ${r.cacheWrite} · cache-read ${r.cacheRead}.</div>`;
  }

  function overviewCard(d) {
    const totalTokens = (d.inputTokens || 0) + (d.outputTokens || 0) + (d.cacheCreationTokens || 0) + (d.cacheReadTokens || 0);
    const cache = (d.cacheCreationTokens || 0) + (d.cacheReadTokens || 0);
    const splitMs = (d.agentMs || 0) + (d.activeMs || 0) + (d.idleMs || 0);
    let totalMs = splitMs;
    if (!totalMs && d.startedAt && d.endedAt) {
      const span = new Date(d.endedAt) - new Date(d.startedAt);
      if (span > 0) totalMs = span;
    }
    const timeSplit = splitMs
      ? `Agent ${fmtDur(d.agentMs)} · Active ${fmtDur(d.activeMs)} · Idle ${fmtDur(d.idleMs)} · Total ${fmtDur(totalMs)}`
      : (totalMs ? `Total ${fmtDur(totalMs)}` : '<span class="muted">—</span>');

    const sub = d.subagentTokens || {};
    const subTotal = (sub.inOut || 0) + (sub.cacheCreation || 0) + (sub.cacheRead || 0);
    const subNote = subTotal
      ? ` <span class="muted" title="Task-tool sub-agents (in+out ${App.fmtNum(sub.inOut)}, cache ${App.fmtNum((sub.cacheCreation || 0) + (sub.cacheRead || 0))})">+ ${App.fmtNum(subTotal)} sub-agents</span>`
      : '';

    return `<h2>Overview</h2>
      ${kv('Started', d.startedAt ? App.esc(App.fmtDate(d.startedAt)) : '<span class="muted">—</span>')}
      ${kv('Ended', d.endedAt ? App.esc(App.fmtDate(d.endedAt)) : '<span class="muted">—</span>')}
      ${kv('Time split', timeSplit)}
      ${kv('Primary model', App.esc(shortModel(d.model)))}
      ${kv('Category', App.esc(d.category || '—'))}
      ${kv('Review', App.esc(REVIEW[d.reviewState] || d.reviewState || '—'))}
      ${kv('Total tokens', `${totalTokens.toLocaleString()} <span class="muted">(in ${App.fmtNum(d.inputTokens)} · out ${App.fmtNum(d.outputTokens)} · cache ${App.fmtNum(cache)})</span>${subNote}`)}
      ${kv('Messages', `${d.promptCount || 0} prompts · ${d.replyCount || 0} replies · ${d.toolCallCount || 0} tool calls`)}`;
  }

  async function render(el, sessionId) {
    Views.sessions.bindActions(el);   // one delegated click/keydown listener, attached once
    if (!sessionId) {
      el.innerHTML = `<div class="panel empty">No session selected. <a href="#sessions">Back to Sessions</a></div>`;
      return;
    }
    el.innerHTML = `<div class="muted">Loading session…</div>`;
    let d;
    try {
      d = await Bridge.call('sessions.detail', { sessionId });
    } catch (e) {
      el.innerHTML = `<a class="back-link" href="#" data-sess-act="back">← Back</a>
        <div class="panel empty">Failed to load session: ${App.esc(e.message)}</div>`;
      return;
    }

    const head = `<div class="detail-top">
      <a class="back-link" href="#" data-sess-act="back">← Back</a>
      <div class="detail-head">${App.esc(d.title || '(untitled session)')} · <span class="mono">${App.esc(d.id)}</span></div>
    </div>`;

    const missing = d.transcriptAvailable ? '' :
      `<div class="panel lc-warn">⚠ Transcript file not found — showing stored totals only (per-tool, per-model and timing detail unavailable).</div>`;

    el.innerHTML = `${head}${missing}
      <div class="detail-grid">
        <div class="panel">${overviewCard(d)}</div>
        <div class="panel">${toolsCard(d)}${modelsCard(d)}</div>
        <div class="panel">${extensionsCard(d)}</div>
        <div class="panel">${costCard(d)}</div>
        <div class="panel">
          <h2>Tickets</h2>
          <div class="ticket-row">${ticketBadges(d.links, d.id)}</div>
          <div class="assign-row">
            <input id="assign-${App.esc(d.id)}" placeholder="ABC-123"
                   data-sess-act="assign-input" data-sess-id="${App.esc(d.id)}">
            <button class="btn btn-small" data-sess-act="assign" data-sess-id="${App.esc(d.id)}">Assign</button>
          </div>
        </div>
      </div>`;
  }

  return {
    render,
    back() {
      if (history.length > 1) history.back();
      else location.hash = '#sessions';
    }
  };
})();
