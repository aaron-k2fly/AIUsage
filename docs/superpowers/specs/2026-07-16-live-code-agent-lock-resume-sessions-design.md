# Live Code — agent/custom-agent exclusion + Resume Sessions picker

**Date:** 2026-07-16
**Branch:** LIVE-CODE-SESSION
**Status:** Approved design

## Goal

Two Live Code refinements:

1. **Agent ↔ Custom Agent exclusion** — selecting an Agent from the dropdown (anything other than
   `(none)`) clears and disables the Custom Agent input, so the two never conflict.
2. **Resume Sessions** — a button beside "Browse" (working-folder row) that lists the existing
   Claude Code sessions for that folder and resumes a chosen one in the tab's terminal, locking
   the Shell / Model / Custom Agent controls for that resumed session.

## Background (current state)

- `wwwroot/js/views/livecode.js`: per-tab controls rendered in `renderTabPanel()`; the Agent
  dropdown (`#lc-agent`) sets `tab.agent`; the Custom Agent input (`#lc-custom-agent`) + Browse
  (`#lc-custom-agent-browse`) set `tab.customAgent`. Backend already treats a custom agent as
  taking precedence over the dropdown.
- Sessions live as transcripts at `~/.claude/projects/<encoded-cwd>/<session-id>.jsonl`, where the
  cwd is encoded by replacing `:`, `\`, `/` with `-` (see `TranscriptPath` / `ActiveSessions`).
- `Scanner/SessionAggregator.cs` owns the transcript JSONL schema; `ReadLive` already reads
  cwd/model/context. `Scanner/ActiveSessions.cs` enumerates recent transcripts across all folders.
- `Bridge/Handlers/LiveCodeHandlers.cs`: `LaunchInPty` spawns the shell + optionally types a
  kickoff; `BuildResumeCommand` builds `claude --resume <id> … 'continue'`.

## Decisions (locked)

- Agent→CustomAgent exclusion is **one-directional** (picking an agent wins; to use a custom agent,
  set Agent back to `(none)` first).
- Resume target: **current active tab**; if it is already running, **confirm replace** before
  resuming.
- Resume command: **`claude --resume <id>`** with no trailing prompt (interactive).
- If a folder has **no** sessions, the Resume Sessions button is **disabled**.
- While a **picked** resumed session runs, disable **Shell**, **Model**, and **Custom Agent**
  (input + Browse). Re-enable when it stops.

## Feature 1 — Agent ↔ Custom Agent exclusion

`wwwroot/js/views/livecode.js` only.

- New helper `refreshControlLocks(t)` computes disabled states in one place and is the single
  authority for locking (also used by Feature 2):

  ```javascript
  function refreshControlLocks(t) {
    if (!t) return;
    const lockResume = t.resumedPick && t.running;         // picked-resume lock (Feature 2)
    const ca = document.getElementById('lc-custom-agent');
    const cab = document.getElementById('lc-custom-agent-browse');
    const caDisabled = (t.agent !== '' && t.agent != null) || lockResume;
    if (ca) ca.disabled = caDisabled;
    if (cab) cab.disabled = caDisabled;
    document.querySelectorAll('[data-shell]').forEach(b => { b.disabled = lockResume; });
    const model = document.getElementById('lc-model');
    if (model) model.disabled = lockResume;
  }
  ```

- Agent dropdown `change` handler: when a non-empty agent is chosen, clear the custom agent:

  ```javascript
  document.getElementById('lc-agent').addEventListener('change', e => {
    t.agent = e.target.value;
    if (t.agent) {                       // picking an agent clears + disables Custom Agent
      t.customAgent = '';
      t.customAgentName = '';
      const ca = document.getElementById('lc-custom-agent'); if (ca) ca.value = '';
      const nm = document.getElementById('lc-custom-agent-name'); if (nm) nm.style.display = 'none';
      saveConfig();
    }
    refreshControlLocks(t);
  });
  ```

- Call `refreshControlLocks(t)` at the end of `renderTabPanel()` (so a tab that already has an
  agent selected renders with Custom Agent disabled).

## Feature 2 — Resume Sessions

### Backend

**`Scanner/SessionAggregator.cs`** — add:

```csharp
/// <summary>First user prompt text in a transcript (trimmed to one line, ~maxLen chars), or null.
/// Used to label sessions in the Resume Sessions picker.</summary>
public static string? FirstUserPrompt(string filePath, int maxLen = 90)
```
Implementation mirrors the existing user-message parsing (the same path that feeds ticket
inference at line ~144): iterate lines, JSON-parse, find the first object with `type == "user"`
whose `message.content` is a string (or a content array with a `text` part), collapse whitespace,
trim to `maxLen` (append `…` if cut). Malformed lines are skipped; never throws (returns null on
any failure).

**`Scanner/FolderSessions.cs`** (new):

```csharp
public sealed record FolderSession(string SessionId, string Label, string? UpdatedIso);

public static class FolderSessions
{
    /// <summary>Existing Claude Code sessions whose transcript lives in <paramref name="folder"/>'s
    /// encoded project dir, newest-first, capped at <paramref name="max"/>. Empty when the folder
    /// has no transcripts. Top-level files only (sidechain subdirs ignored).</summary>
    public static List<FolderSession> List(string? folder, int max = 25);
}
```
- Returns `[]` for null/empty/non-existent folder or missing project dir.
- Encodes the folder like `TranscriptPath` (`':'`,`'\\'`,`'/'` → `'-'`), reads
  `~/.claude/projects/<encoded>/*.jsonl` (TopDirectoryOnly), orders by `LastWriteTimeUtc` desc,
  takes `max`. For each: `SessionId = filename-without-ext`, `Label = SessionAggregator.FirstUserPrompt(f) ?? "(no prompt recorded)"`,
  `UpdatedIso = LastWriteTimeUtc "o"`.

**`Bridge/Handlers/LiveCodeHandlers.cs`** — add two actions:

```csharp
router.Register("livecode.sessionsInFolder", payload =>
{
    var folder = SessionHandlers.GetString(payload, "folder");
    var sessions = FolderSessions.List(folder)
        .Select(s => new { sessionId = s.SessionId, label = s.Label, updated = s.UpdatedIso });
    return Task.FromResult<object?>(new { sessions });
});
```

```csharp
// Resume a specific past session (chosen in the Resume Sessions picker) in the tab's terminal.
router.Register("livecode.resumeSession", payload =>
{
    var tabId = RequireTabId(payload);
    var shellReq = SessionHandlers.GetString(payload, "shell") ?? "powershell";
    var folder = SessionHandlers.GetString(payload, "folder");
    var sessionId = SessionHandlers.GetString(payload, "sessionId");
    if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("sessionId is required.");
    if (!string.IsNullOrWhiteSpace(folder) && !Directory.Exists(folder))
        throw new ArgumentException($"Folder not found: {folder}");
    TryGetBool(payload, "autoApprove", out var autoApprove);
    TryGetBool(payload, "bypass", out var bypass);
    var cols = (short)GetInt(payload, "cols", 120);
    var rows = (short)GetInt(payload, "rows", 30);
    var permissionMode = bypass ? "bypassPermissions" : autoApprove ? "acceptEdits" : null;

    var shell = ShellResolver.Resolve(shellReq);
    var kickoff = BuildResumeSessionCommand(shell.Kind, sessionId!, permissionMode); // no 'continue'
    return Task.FromResult<object?>(
        LaunchInPty(router, tabId, shell, folder, cols, rows, kickoff, permissionMode, sessionId!, model: null,
                    ticketKey: null, trackSession: true));
});
```

New helper `BuildResumeSessionCommand(shellKind, sessionId, permissionMode)` = `claude --resume <id>`
+ optional `--permission-mode` (no model, no agent, no positional prompt). Note: `LaunchInPty`
already types the `kickoff` once after the first prompt draws, so `--resume` is typed in like any
other launch.

### Frontend (`wwwroot/js/views/livecode.js`, `wwwroot/css/app.css`)

- **Tab model:** add `resumedPick: false` and `folderSessions: []` to `makeTab()`.
- **Button:** in `renderTabPanel()` working-folder row, after Browse:
  `<button class="btn" id="lc-resume-sessions" disabled>Resume Sessions</button>`.
- **Populate/enable:** new `loadFolderSessions(t)` calls `livecode.sessionsInFolder {folder:t.folder}`,
  stores `t.folderSessions`, and sets the button `disabled = !t.folderSessions.length`. Called at
  the end of `renderTabPanel()`, and on folder `change` / after `browse()` (alongside `loadAgents`).
- **Modal:** `openResumeSessions(t)` builds a modal (reusing `.modal`/`.modal-overlay`) with a
  scrollable `.lc-session-list`; each row shows `label`, formatted `updated`, and short session id
  (`sessionId.slice(0,8)`). Rows are buttons; a Cancel button dismisses. (Re-fetches on open for
  freshness, then renders.)
- **Row select** → `resumePickedSession(t, sessionId)`:
  1. If `t.running`, `const ok = await App.confirm('Stop the current session and resume the selected one?', 'Resume'); if (!ok) return;` (backend `LaunchInPty` stops the tab's current session first anyway).
  2. API-key confirm (same as `start`/`resume`) if `G.cfg.apiKeyPresent`.
  3. `const term = createTerm(t);`
  4. `await Bridge.call('livecode.resumeSession', { tabId:t.tabId, folder:t.folder, sessionId, shell:t.shell, autoApprove:t.autoApprove, bypass:t.bypass, cols:term.cols, rows:term.rows }, 0);`
  5. `t.running = true; t.canResume = true; t.resumedPick = true; t.activeFolder = t.folder; t.isolated = false;`
     `updateButtons(); renderTabBar(); refreshControlLocks(t); term.focus();` toast `Resuming session <short id>…`.
  6. On error: toast + `disposeTabTerm(t); t.running=false; t.resumedPick=false; updateButtons(); refreshControlLocks(t);`.
- **Unlock on stop:** in `stop()`, the `pty.exit` handler, and `markStopped`-equivalent paths, set
  `t.resumedPick = false` and call `refreshControlLocks(t)` for the affected tab so Shell/Model/
  Custom Agent re-enable. (In the global `pty.exit` handler this applies to the event's tab.)
- **CSS:** `.lc-session-list` (max-height ~50vh, scroll), `.lc-session-row` (flex row, hover), and
  small muted meta styles.

## Data flow (Resume Sessions)

1. User sets a folder → `loadFolderSessions` fills the cache; button enabled iff sessions exist.
2. Click Resume Sessions → modal lists sessions (label + updated + id).
3. Pick a row → (confirm replace if running) → `livecode.resumeSession` types `claude --resume <id>`
   in a fresh terminal for the active tab; Shell/Model/Custom Agent lock.
4. Stopping the session unlocks the controls.

## Error handling

- `sessionsInFolder` / `FolderSessions.List` / `FirstUserPrompt` are best-effort and never throw
  out of the handler (return empty/null on any IO/parse failure).
- `resumeSession` validates `sessionId` and folder existence; a bad `--resume` id simply surfaces
  in the terminal output (Claude reports it), consistent with the existing Resume button.
- Folder path handling reuses the existing encoding; no new normalization needed.

## Out of scope (YAGNI)

- Resuming into a new tab (decided: current tab, confirm-replace).
- Sending a prompt on resume (decided: interactive, no prompt).
- Deleting / renaming / filtering sessions in the picker.
- Reverse exclusion (custom agent disabling the Agent dropdown).
- Ticket badges in the picker (the first-prompt label already shows the ticket for app-started
  sessions).

## Docs to update

`CLAUDE.md` (Live Code paragraph), `.claude/STRUCTURE.md` (new actions `livecode.sessionsInFolder`
+ `livecode.resumeSession`, `Scanner/FolderSessions.cs`, `SessionAggregator.FirstUserPrompt`,
frontend notes), and `PROGRESS.md`.
