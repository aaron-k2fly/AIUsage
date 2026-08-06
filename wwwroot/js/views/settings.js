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
        ${s.jiraSiteUrlInsecure ? `<div class="lc-warn" style="border:1px solid #f0c98a;border-radius:6px;padding:8px 10px;margin-bottom:12px;align-items:flex-start">⚠ The saved site URL is not an
          <strong>https://</strong> address, so JIRA is disabled — your email and API token would otherwise be sent
          in cleartext on every request. Enter an https:// URL and save.</div>` : ''}
        <label>Site URL</label>
        <input id="set-site" type="url" placeholder="https://yourcompany.atlassian.net" value="${App.esc(s.jiraSiteUrl)}">
        <label>Email</label>
        <input id="set-email" placeholder="you@example.com" value="${App.esc(s.jiraEmail)}">
        <label>API token ${s.jiraTokenSet ? '<span class="badge confirmed">set</span>' : '<span class="badge dead">not set</span>'}</label>
        <input id="set-token" type="password" placeholder="${s.jiraTokenSet ? 'leave empty to keep current token' : 'paste a JIRA API token'}">
        <div class="footnote">Stored DPAPI-encrypted for your Windows user. Create tokens at id.atlassian.com → Security → API tokens.
          Must be an <strong>https://</strong> site URL — the token is sent as a reversible Basic credential on every request.
          Pointing the site URL at a <em>different host</em> clears the stored token, so it can never be replayed to a new server.</div>
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
      </div>

      <div class="panel form-narrow">
        <h2>Live Code</h2>
        <label>Assigned tickets to list in the picker (1–20)</label>
        <input id="set-lc-ticket-count" type="number" min="1" max="20" step="1" value="${App.esc(s.livecodeTicketCount)}">
        <div style="margin-top:12px">
          <button class="btn btn-primary" onclick="Views.settings.save()">Save</button>
        </div>
        <div class="footnote">How many of your most recently updated assigned tickets appear at the top of the Live Code session (finished tickets are always excluded).</div>
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
        backfillFrom: document.getElementById('set-backfill').value,
        livecodeTicketCount: document.getElementById('set-lc-ticket-count').value
      };
      try {
        const r = await Bridge.call('settings.set', payload);
        App.toast(r && r.tokenCleared
          ? 'Settings saved — the site URL now points at a different host, so the stored API token was cleared. Paste a token for the new host.'
          : 'Settings saved');
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
