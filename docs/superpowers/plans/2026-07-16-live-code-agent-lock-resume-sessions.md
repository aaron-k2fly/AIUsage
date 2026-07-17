# Live Code Agent-Lock + Resume Sessions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Agent dropdown and Custom Agent mutually exclusive, and add a "Resume Sessions" picker that lists a folder's existing Claude Code sessions and resumes a chosen one in the tab's terminal (locking Shell/Model/Custom Agent).

**Architecture:** Add a per-folder session enumerator (`Scanner/FolderSessions.cs`) built on a new `SessionAggregator.FirstUserPrompt`, expose it via `livecode.sessionsInFolder`, add `livecode.resumeSession` (types `claude --resume <id>`), and drive the frontend with a single `refreshControlLocks(tab)` authority plus a modal picker.

**Tech Stack:** .NET 10 / C#, Photino bridge, vanilla JS, xterm.js.

## Global Constraints

- `net10.0`, nullable + implicit usings; namespaces mirror folders.
- No test project — verify via `dotnet build`, `node --check`, and headless boot (`--route livecode`, grep the message log).
- Synchronous handlers return `Task.FromResult<object?>(...)` — never `Task.Run(() => {...; return null;})`.
- Frontend: classic scripts + globals; views on `window.Views`, helpers on `window.App`.
- Transcript cwd encoding: replace `':'`, `'\\'`, `'/'` with `'-'` (same as `TranscriptPath`).
- A real user prompt line: `type == "user"` with **string** `message.content` (array content = tool-result noise — skip it).
- Keep `CLAUDE.md`, `.claude/STRUCTURE.md`, `PROGRESS.md` in sync.

---

## File Structure

- **Modify** `Scanner/SessionAggregator.cs` — add `FirstUserPrompt(filePath, maxLen)`.
- **Create** `Scanner/FolderSessions.cs` — `FolderSession` record + `List(folder, max)`.
- **Modify** `Bridge/Handlers/LiveCodeHandlers.cs` — actions `livecode.sessionsInFolder`, `livecode.resumeSession`; helper `BuildResumeSessionCommand`.
- **Modify** `wwwroot/js/views/livecode.js` — `refreshControlLocks`; agent→custom-agent exclusion; Resume Sessions button + `loadFolderSessions` + modal + `resumePickedSession`; unlock on stop/exit; `resumedPick`/`folderSessions` on the tab model.
- **Modify** `wwwroot/css/app.css` — session-list modal styles.
- **Modify** `CLAUDE.md`, `.claude/STRUCTURE.md`, `PROGRESS.md`.

---

### Task 1: Per-folder session enumerator

**Files:**
- Modify: `Scanner/SessionAggregator.cs`
- Create: `Scanner/FolderSessions.cs`

**Interfaces produced:**
- `static string? SessionAggregator.FirstUserPrompt(string filePath, int maxLen = 90)`
- `record FolderSession(string SessionId, string Label, string? UpdatedIso)`
- `static List<FolderSession> FolderSessions.List(string? folder, int max = 25)`

- [ ] **Step 1: Add `FirstUserPrompt` to `SessionAggregator`.**

Add as a public static method (near `ReadLive`):

```csharp
/// <summary>The first real user prompt in a transcript (string message.content — array content is
/// tool-result noise), collapsed to one line and trimmed to <paramref name="maxLen"/> chars. Null
/// if none/unreadable. Used to label sessions in the Resume Sessions picker.</summary>
public static string? FirstUserPrompt(string filePath, int maxLen = 90)
{
    try
    {
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!TryGetString(root, "type", out var type) || type != "user") continue;
                if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
                if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) continue;
                var text = content.GetString();
                if (string.IsNullOrWhiteSpace(text)) continue;
                var collapsed = System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
                return collapsed.Length <= maxLen ? collapsed : collapsed[..maxLen].TrimEnd() + "…";
            }
        }
    }
    catch { /* IO error — best effort */ }
    return null;
}
```
(Uses the file's existing `using System.Text.Json;` and the private `TryGetString`. `File.ReadLines` streams, so it stops at the first prompt without reading the whole file.)

- [ ] **Step 2: Create `Scanner/FolderSessions.cs`.**

```csharp
namespace AIUsage.Scanner;

/// <summary>A resumable Claude Code session found in a specific working folder's transcript dir.</summary>
public sealed record FolderSession(string SessionId, string Label, string? UpdatedIso);

/// <summary>Lists the existing Claude Code sessions whose transcript lives in a given working
/// folder's encoded project dir (~/.claude/projects/&lt;encoded-cwd&gt;), newest-first. Powers the Live
/// Code "Resume Sessions" picker.</summary>
public static class FolderSessions
{
    public static List<FolderSession> List(string? folder, int max = 25)
    {
        var results = new List<FolderSession>();
        if (string.IsNullOrWhiteSpace(folder)) return results;

        var encoded = folder.Replace(':', '-').Replace('\\', '-').Replace('/', '-');
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects", encoded);
        if (!Directory.Exists(dir)) return results;

        try
        {
            var files = new DirectoryInfo(dir)
                .EnumerateFiles("*.jsonl", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(max);
            foreach (var f in files)
                results.Add(new FolderSession(
                    Path.GetFileNameWithoutExtension(f.Name),
                    SessionAggregator.FirstUserPrompt(f.FullName) ?? "(no prompt recorded)",
                    f.LastWriteTimeUtc.ToString("o")));
        }
        catch (IOException) { /* dir vanished mid-enumerate — return what we have */ }
        return results;
    }
}
```

- [ ] **Step 3: Build.**

Run: `dotnet build`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 4: Commit.**

```bash
git add Scanner/SessionAggregator.cs Scanner/FolderSessions.cs
git commit -m "Live Code: per-folder session enumerator (FolderSessions + FirstUserPrompt)"
```

---

### Task 2: Backend actions — sessionsInFolder + resumeSession

**Files:**
- Modify: `Bridge/Handlers/LiveCodeHandlers.cs`

**Interfaces:**
- Consumes: `FolderSessions.List` (Task 1); existing `ShellResolver.Resolve`, `LaunchInPty`, `RequireTabId`, `GetInt`, `TryGetBool`.
- Produces:
  - `livecode.sessionsInFolder { folder }` → `{ sessions: [{ sessionId, label, updated }] }`.
  - `livecode.resumeSession { tabId, folder, sessionId, shell, autoApprove, bypass, cols, rows }` → `{ shell, fellBack, kickoff }`.

- [ ] **Step 1: Register `livecode.sessionsInFolder`.**

Add near `livecode.folderInfo` in `Register`:
```csharp
        router.Register("livecode.sessionsInFolder", payload =>
        {
            var folder = SessionHandlers.GetString(payload, "folder");
            var sessions = FolderSessions.List(folder)
                .Select(s => new { sessionId = s.SessionId, label = s.Label, updated = s.UpdatedIso });
            return Task.FromResult<object?>(new { sessions });
        });
```

- [ ] **Step 2: Register `livecode.resumeSession`.**

Add after the `livecode.resume` handler:
```csharp
        // Resume a specific past session chosen in the Resume Sessions picker, in the tab's terminal
        // (interactive `claude --resume <id>`, no continue prompt).
        router.Register("livecode.resumeSession", payload =>
        {
            var tabId = RequireTabId(payload);
            var shellReq = SessionHandlers.GetString(payload, "shell") ?? "powershell";
            var folder = SessionHandlers.GetString(payload, "folder");
            var sessionId = SessionHandlers.GetString(payload, "sessionId");
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("sessionId is required.");
            if (!string.IsNullOrWhiteSpace(folder) && !Directory.Exists(folder))
                throw new ArgumentException($"Folder not found: {folder}");
            TryGetBool(payload, "autoApprove", out var autoApprove);
            TryGetBool(payload, "bypass", out var bypass);
            var cols = (short)GetInt(payload, "cols", 120);
            var rows = (short)GetInt(payload, "rows", 30);
            var permissionMode = bypass ? "bypassPermissions" : autoApprove ? "acceptEdits" : null;

            var shell = ShellResolver.Resolve(shellReq);
            var kickoff = BuildResumeSessionCommand(shell.Kind, sessionId!, permissionMode);
            return Task.FromResult<object?>(
                LaunchInPty(router, tabId, shell, folder, cols, rows, kickoff, permissionMode, sessionId!,
                            model: null, ticketKey: null, trackSession: true));
        });
```

- [ ] **Step 3: Add `BuildResumeSessionCommand`.**

Add next to `BuildResumeCommand`:
```csharp
    /// <summary>`claude --resume <id>` (+ permission flag) with NO positional prompt — reopens the
    /// chosen session interactively (used by the Resume Sessions picker).</summary>
    private static string BuildResumeSessionCommand(string shellKind, string sessionId, string? permissionMode)
    {
        var sb = new StringBuilder("claude --resume ").Append(sessionId);
        if (permissionMode is not null) sb.Append(" --permission-mode ").Append(permissionMode);
        return sb.ToString();
    }
```
(`shellKind` is unused today but kept for signature symmetry with `BuildResumeCommand`; if the compiler warns about an unused parameter it will not — it's a normal parameter. Leave as-is for consistency.)

- [ ] **Step 4: Build.**

Run: `dotnet build`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 5: Headless verification — sessionsInFolder returns data.**

The frontend calls `livecode.sessionsInFolder` on render (Task 3), but to verify the backend now, run a boot and confirm no errors; then confirm the action is registered by checking the boot log has no unregistered-action errors:
```bash
(dotnet run --no-build -- --route livecode > "$TEMP/rs_boot.log" 2>&1 &) ; sleep 12
grep -i "error\|exception\|\"ok\":false" "$TEMP/rs_boot.log" || echo "no errors"
taskkill //IM AIUsage.exe //T //F 2>/dev/null | head -1
```
Expected: `no errors`. (Full data verification happens in Task 3 Step 9 once the frontend calls it with the last-used folder.)

- [ ] **Step 6: Commit.**

```bash
git add Bridge/Handlers/LiveCodeHandlers.cs
git commit -m "Live Code: sessionsInFolder + resumeSession bridge actions"
```

---

### Task 3: Frontend — agent lock + Resume Sessions picker

**Files:**
- Modify: `wwwroot/js/views/livecode.js`
- Modify: `wwwroot/css/app.css`

**Interfaces:**
- Consumes: `livecode.sessionsInFolder`, `livecode.resumeSession` (Task 2); `App.confirm`.
- Produces: `refreshControlLocks(t)`, `loadFolderSessions(t)`, `openResumeSessions(t)`, `resumePickedSession(t, sessionId)`.

- [ ] **Step 1: Tab model fields.**

In `makeTab()` (after `isolated: false,`) add:
```javascript
      resumedPick: false,       // running a session picked from Resume Sessions (locks shell/model/agent)
      folderSessions: [],       // cached list for the Resume Sessions button/modal
```

- [ ] **Step 2: Add `refreshControlLocks`.**

Add near `updateButtons`:
```javascript
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
```

- [ ] **Step 3: Agent dropdown clears + disables Custom Agent.**

Replace the existing `lc-agent` change handler in `wireTabPanel()`:
```javascript
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
```

- [ ] **Step 4: Resume Sessions button in the working-folder row.**

In `renderTabPanel()`, change the working-folder row's Browse line to add the button after it:
```javascript
        <input id="lc-folder" class="lc-grow" placeholder="C:\\path\\to\\project" value="${App.esc(t.folder)}">
        <button class="btn" id="lc-browse">Browse…</button>
        <button class="btn" id="lc-resume-sessions" disabled title="Resume an existing session in this folder">Resume Sessions</button>
```

- [ ] **Step 5: Wire the button + folder-driven enable, and call locks/loader on render.**

In `wireTabPanel()`, add after the `lc-browse` click wiring:
```javascript
    document.getElementById('lc-resume-sessions').addEventListener('click', () => openResumeSessions(t));
```
Change the folder `change` handler to also refresh the session list:
```javascript
    document.getElementById('lc-folder').addEventListener('change', () => { saveConfig(); loadAgents(); loadFolderSessions(t); });
```
At the END of `renderTabPanel()` (after `updateButtons();`) add:
```javascript
    refreshControlLocks(t);
    loadFolderSessions(t);
```
In `browse()` success branch (after `loadAgents();`) add `loadFolderSessions(t);` (the `browse()` already has the tab as `t`).

- [ ] **Step 6: `loadFolderSessions`.**

Add near `loadAgents`:
```javascript
  async function loadFolderSessions(t) {
    const btn = document.getElementById('lc-resume-sessions');
    if (!t.folder) { t.folderSessions = []; if (btn && t === activeTab()) btn.disabled = true; return; }
    try {
      const r = await Bridge.call('livecode.sessionsInFolder', { folder: t.folder }, 5000);
      t.folderSessions = (r && r.sessions) || [];
    } catch { t.folderSessions = []; }
    if (btn && t === activeTab()) btn.disabled = !t.folderSessions.length;
  }
```

- [ ] **Step 7: Modal + resume.**

Add:
```javascript
  function fmtSessionTime(iso) {
    const d = new Date(iso);
    if (isNaN(d)) return '';
    return d.toLocaleDateString([], { month: 'short', day: 'numeric' }) + ' ' +
           d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  async function openResumeSessions(t) {
    await loadFolderSessions(t);                 // freshen
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
```

- [ ] **Step 8: Unlock on stop/exit.**

In `stop()`, after `t.running = false;` add:
```javascript
    t.resumedPick = false;
    refreshControlLocks(t);
```
In the global `pty.exit` handler (in `subscribeEvents`), after `t.running = false;` add:
```javascript
      t.resumedPick = false;
      if (t.tabId === activeTabId) refreshControlLocks(t);
```

- [ ] **Step 9: CSS.**

In `app.css` after the Live Code tab styles, add:
```css
.lc-session-list { display: flex; flex-direction: column; gap: 6px; max-height: 50vh; overflow-y: auto; margin: 10px 0 4px; }
.lc-session-row {
  display: flex; flex-direction: column; gap: 2px; text-align: left; width: 100%;
  border: 1px solid var(--border); background: #fff; border-radius: 6px; padding: 8px 10px; cursor: pointer;
}
.lc-session-row:hover { border-color: var(--accent); background: var(--accent-soft); }
.lc-session-label { color: var(--text); }
.lc-session-meta { color: var(--muted); font-size: 11.5px; }
```

- [ ] **Step 10: Build + JS checks.**

Run: `node --check wwwroot/js/views/livecode.js && dotnet build`
Expected: JS valid; `Build succeeded`.

- [ ] **Step 11: Headless boot — sessionsInFolder called with the last folder.**

Run:
```bash
(dotnet run --no-build -- --route livecode > "$TEMP/rs_boot2.log" 2>&1 &) ; sleep 12
grep -o "\"sessions\":\[[^]]*" "$TEMP/rs_boot2.log" | head -1 || echo "no sessions payload (folder may be empty)"
grep -i "error\|exception\|\"ok\":false" "$TEMP/rs_boot2.log" || echo "no errors"
taskkill //IM AIUsage.exe //T //F 2>/dev/null | head -1
```
Expected: a `"sessions":[…]` payload for the last-used folder (or the empty note), and `no errors`.

- [ ] **Step 12: Manual UI verification.**

Run: `dotnet run`. Verify: (a) picking an Agent clears the Custom Agent text and disables the input+Browse; setting Agent to `(none)` re-enables them. (b) In a folder with prior sessions, "Resume Sessions" is enabled → modal lists sessions (prompt label + time + id); pick one → terminal runs `claude --resume <id>`, and Shell/Model/Custom Agent are disabled; Stop re-enables them. (c) In a folder with no sessions, the button is disabled.

- [ ] **Step 13: Commit.**

```bash
git add wwwroot/js/views/livecode.js wwwroot/css/app.css
git commit -m "Live Code: agent/custom-agent lock + Resume Sessions picker"
```

---

### Task 4: Docs sync + final verify

**Files:**
- Modify: `CLAUDE.md`, `.claude/STRUCTURE.md`, `PROGRESS.md`

- [ ] **Step 1:** In `CLAUDE.md` Live Code paragraph, note the agent/custom-agent exclusion and the Resume Sessions picker (`livecode.sessionsInFolder`, `livecode.resumeSession`, `claude --resume <id>` interactive, Shell/Model/Custom-Agent lock).
- [ ] **Step 2:** In `.claude/STRUCTURE.md`: add `Scanner/FolderSessions.cs` (tree + file table); note `SessionAggregator.FirstUserPrompt`; add `livecode.sessionsInFolder` + `livecode.resumeSession` to the bridge catalog; note the exclusion + picker + `refreshControlLocks` in the `livecode.js` row and the two actions in the LiveCodeHandlers row.
- [ ] **Step 3:** Append a dated section to `PROGRESS.md` and bump `_Last updated_` to 2026-07-16.
- [ ] **Step 4: Final build.**

Run: `dotnet build`
Expected: `Build succeeded`.

- [ ] **Step 5: Commit.**

```bash
git add CLAUDE.md .claude/STRUCTURE.md PROGRESS.md
git commit -m "Docs: Live Code agent lock + Resume Sessions"
```

---

## Self-Review

- **Spec coverage:** agent→custom-agent clear+disable (Task 3 Steps 2–3) ✓; re-enable on none (Task 3 Step 2 `!!t.agent`) ✓; render reflects state (Task 3 Step 5 `refreshControlLocks` at end of render) ✓; FolderSessions enumerator + FirstUserPrompt (Task 1) ✓; `sessionsInFolder`/`resumeSession` actions + interactive `--resume` (Task 2) ✓; button disabled when no sessions (Task 3 Step 6) ✓; modal picker (Task 3 Step 7) ✓; current tab + confirm-replace (Task 3 Step 7 `resumePickedSession`) ✓; Shell/Model/Custom-Agent lock while picked-resume runs + unlock on stop/exit (Task 3 Steps 2,7,8) ✓; docs (Task 4) ✓.
- **Type consistency:** `FolderSession(SessionId, Label, UpdatedIso)` (Task 1) → `sessionsInFolder` maps to `{sessionId,label,updated}` (Task 2) → modal reads `s.sessionId/label/updated` (Task 3) ✓; `resumeSession` payload `{tabId,folder,sessionId,shell,autoApprove,bypass,cols,rows}` identical Task 2↔3 ✓; `refreshControlLocks(t)` signature consistent across Task 3 ✓; `BuildResumeSessionCommand(shellKind, sessionId, permissionMode)` defined + called in Task 2 ✓.
- **Placeholders:** none — exact code, paths, and verification commands throughout.
