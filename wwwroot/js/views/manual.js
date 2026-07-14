window.Views = window.Views || {};
window.Views.manual = (function () {
  const TOOL_SUGGESTIONS = ['Claude Code', 'GitHub Copilot', 'Cursor', 'ChatGPT', 'Claude.ai'];

  async function load(el) {
    let categories = [], entries = [];
    try {
      [categories, entries] = await Promise.all([
        Bridge.call('categories.list'),
        Bridge.call('manual.list')
      ]);
    } catch (e) {
      el.innerHTML = `<div class="panel empty">Failed to load: ${App.esc(e.message)}</div>`;
      return;
    }

    const today = new Date().toISOString().slice(0, 10);
    const catOptions = categories.map(c => `<option value="${c.id}">${App.esc(c.name)}</option>`).join('');
    const toolOptions = TOOL_SUGGESTIONS.map(t => `<option value="${App.esc(t)}">`).join('');

    const entryRows = entries.map(e => `
      <tr>
        <td><span class="badge">${App.esc(e.ticketKey)}</span>
            <div class="muted" style="font-size:11.5px">${App.esc(e.ticketSummary || '')}</div></td>
        <td class="muted" style="white-space:nowrap">${App.esc(e.entryDate)}</td>
        <td>${App.esc(e.category || '—')}</td>
        <td>${App.esc(e.description || '')}</td>
        <td class="muted">${App.esc(e.toolUsed || '')}</td>
        <td><button class="btn btn-small" onclick="Views.manual.remove(${e.id})">Delete</button></td>
      </tr>`).join('');

    el.innerHTML = `<h1>Manual entry</h1>
      <div class="panel form-narrow">
        <label>Ticket key</label>
        <input id="me-key" placeholder="SFTY-1234" style="text-transform:uppercase">
        <label>Date</label>
        <input id="me-date" type="date" value="${today}">
        <label>What did the AI do?</label>
        <select id="me-category">${catOptions}</select>
        <label>Description (optional)</label>
        <textarea id="me-desc" rows="3" placeholder="e.g. Generated the workflow validation and its unit tests"></textarea>
        <label>AI tool</label>
        <input id="me-tool" list="me-tools" value="Claude Code">
        <datalist id="me-tools">${toolOptions}</datalist>
        <div style="margin-top:14px">
          <button class="btn btn-primary" onclick="Views.manual.save()">Add entry</button>
        </div>
        <div class="footnote">Manual entries are the only way usage of non-Claude-Code tools (Copilot, Cursor, ChatGPT…) is counted.</div>
      </div>
      <div style="display:flex;align-items:center;margin:0 0 10px">
        <h2 style="margin:0">Recent entries</h2>
        <span style="flex:1"></span>
        <button class="btn" onclick="App.exportExcel('export.manual')">⬇ Export to Excel</button>
      </div>
      ${entries.length ? `<div class="panel" style="padding:0"><table>
        <thead><tr><th>Ticket</th><th>Date</th><th>Activity</th><th>Description</th><th>Tool</th><th></th></tr></thead>
        <tbody>${entryRows}</tbody></table></div>`
        : '<div class="panel empty">No manual entries yet.</div>'}`;
  }

  return {
    render: load,
    async save() {
      const payload = {
        ticketKey: document.getElementById('me-key').value.trim().toUpperCase(),
        entryDate: document.getElementById('me-date').value,
        categoryId: Number(document.getElementById('me-category').value),
        description: document.getElementById('me-desc').value.trim(),
        toolUsed: document.getElementById('me-tool').value.trim()
      };
      try {
        await Bridge.call('manual.create', payload);
        App.toast('Entry added');
        App.refresh();
      } catch (e) {
        App.toast(e.message, true);
      }
    },
    async remove(id) {
      try {
        await Bridge.call('manual.delete', { id });
        App.refresh();
      } catch (e) {
        App.toast(e.message, true);
      }
    }
  };
})();
