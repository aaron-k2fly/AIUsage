window.Views = window.Views || {};
window.Views.settings = (function () {
  async function load(el) {
    let s;
    try {
      s = await Bridge.call('settings.get');
    } catch (e) {
      el.innerHTML = `<div class="panel empty">Failed to load settings: ${App.esc(e.message)}</div>`;
      return;
    }

    el.innerHTML = `<h1>Settings</h1>
      <div class="panel form-narrow">
        <h2>JIRA (read-only)</h2>
        <label>Site URL</label>
        <input id="set-site" placeholder="https://yourcompany.atlassian.net" value="${App.esc(s.jiraSiteUrl)}">
        <label>Email</label>
        <input id="set-email" placeholder="you@example.com" value="${App.esc(s.jiraEmail)}">
        <label>API token ${s.jiraTokenSet ? '<span class="badge confirmed">set</span>' : '<span class="badge dead">not set</span>'}</label>
        <input id="set-token" type="password" placeholder="${s.jiraTokenSet ? 'leave empty to keep current token' : 'paste a JIRA API token'}">
        <div class="footnote">Stored DPAPI-encrypted for your Windows user. Create tokens at id.atlassian.com → Security → API tokens.</div>
        <label>JQL for “Fetch more from JIRA” (imports tickets into the Tickets list)</label>
        <input id="set-fetch-jql" value="${App.esc(s.jiraFetchJql)}">
        <div style="margin-top:12px; display:flex; gap:8px">
          <button class="btn btn-primary" onclick="Views.settings.save()">Save</button>
          <button class="btn" onclick="Views.settings.test()">Test connection</button>
        </div>
      </div>

      <div class="panel form-narrow">
        <h2>Scanner</h2>
        <label>Scan paths (semicolon-separated; empty = default)</label>
        <input id="set-paths" placeholder="${App.esc(s.defaultScanPath)}" value="${App.esc(s.scanPaths)}">
        <label>Project key allowlist (comma-separated, e.g. SFTY,QS — filters inferred ticket keys)</label>
        <input id="set-allowlist" placeholder="empty = allow all keys" value="${App.esc(s.projectKeyAllowlist)}" style="text-transform:uppercase">
        <label>Backfill from (ignore transcript files older than this date)</label>
        <input id="set-backfill" type="date" value="${App.esc(s.backfillFrom)}">
        <div style="margin-top:12px">
          <button class="btn btn-primary" onclick="Views.settings.save()">Save</button>
        </div>
        <div class="footnote">Changing the allowlist removes auto-inferred links that no longer match (manually assigned and confirmed links are kept).</div>
      </div>`;
  }

  return {
    render: load,
    async save() {
      const payload = {
        jiraSiteUrl: document.getElementById('set-site').value.trim(),
        jiraEmail: document.getElementById('set-email').value.trim(),
        jiraToken: document.getElementById('set-token').value,
        jiraFetchJql: document.getElementById('set-fetch-jql').value.trim(),
        scanPaths: document.getElementById('set-paths').value.trim(),
        projectKeyAllowlist: document.getElementById('set-allowlist').value.trim().toUpperCase(),
        backfillFrom: document.getElementById('set-backfill').value
      };
      try {
        await Bridge.call('settings.set', payload);
        App.toast('Settings saved');
        App.refresh();
      } catch (e) {
        App.toast(e.message, true);
      }
    },
    async test() {
      try {
        const r = await Bridge.call('jira.test');
        App.toast(`Connected to JIRA as ${r.user}`);
      } catch (e) {
        App.toast(e.message, true);
      }
    }
  };
})();
