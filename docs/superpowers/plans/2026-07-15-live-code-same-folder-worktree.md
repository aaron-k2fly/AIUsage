# Live Code Same-Folder Warning + Git-Worktree Isolation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Warn when a Live Code tab would start a session in a folder already used by another running tab, and let the user run the new session in an isolated git worktree instead of the same folder.

**Architecture:** Frontend detects same-`activeFolder` running conflicts and shows a git-repo-aware 3-way `App.choose` dialog; the chosen `isolation` (`'worktree'`/`'none'`) flows to the backend, where a new `Terminal/GitWorktree.cs` creates an isolated worktree (`git worktree add`) and the session launches in it. `closeTab` removes the worktree only if clean.

**Tech Stack:** .NET 10 / C#, `System.Diagnostics.Process` for `git`, Photino bridge, vanilla JS, xterm.js.

## Global Constraints

- `net10.0`, nullable + implicit usings; namespaces mirror folders.
- No test project — verify via `dotnet build`, `node --check`, headless boot (`--route livecode`), and a real `git worktree` round-trip in a scratch repo.
- Synchronous handlers return `Task.FromResult<object?>(...)` — never `Task.Run(() => {...; return null;})`.
- Frontend: classic scripts + globals; views on `window.Views`, helpers on `window.App`.
- Per-session bridge actions carry `tabId` (existing). New action `livecode.folderInfo`.
- Keep `CLAUDE.md`, `.claude/STRUCTURE.md`, `PROGRESS.md` in sync.
- Path comparison normalized (lowercase, `\`/`/` unified, trailing slash stripped) — Windows-safe.

---

## File Structure

- **Create** `Terminal/GitWorktree.cs` — `IsGitRepo`, `Create`, `TryRemoveIfClean`, `WorktreeInfo` record. Runs `git` via `Process`; never throws out of `IsGitRepo`/`TryRemoveIfClean` (Create may throw on git failure, caught by caller).
- **Modify** `Bridge/Handlers/LiveCodeHandlers.cs` — `LiveSession.Worktree`; `livecode.folderInfo`; `isolation` in `StartTicketSession`; worktree cwd for launch/transcript/link; `closeTab` cleanup; `StopSession` preserves `Worktree`.
- **Modify** `wwwroot/js/app.js` — add `App.choose(message, buttons, danger)`.
- **Modify** `wwwroot/js/views/livecode.js` — `tab.activeFolder`/`tab.isolated`; conflict detection; dialog + `isolation`; worktree marker + toasts.
- **Modify** `wwwroot/css/app.css` — `.lc-tab-wt` marker.
- **Modify** `CLAUDE.md`, `.claude/STRUCTURE.md`, `PROGRESS.md`.

---

### Task 1: `GitWorktree` helper

**Files:**
- Create: `Terminal/GitWorktree.cs`

**Interfaces produced:**
- `record WorktreeInfo(string WorktreePath, string Cwd, string Branch, string BaseSha, string Toplevel)`
- `static bool GitWorktree.IsGitRepo(string? folder)`
- `static WorktreeInfo GitWorktree.Create(string folder, string suffix)` (throws `InvalidOperationException` on git failure)
- `static (bool removed, string? keptReason) GitWorktree.TryRemoveIfClean(WorktreeInfo info)`

- [ ] **Step 1: Write `Terminal/GitWorktree.cs`.**

```csharp
using System.Diagnostics;
using System.Text;

namespace AIUsage.Terminal;

/// <summary>Isolated git-worktree operations for Live Code session isolation. All git is run via
/// the `git` CLI. IsGitRepo/TryRemoveIfClean never throw; Create throws on git failure so the
/// caller can surface the error and not launch.</summary>
public sealed record WorktreeInfo(string WorktreePath, string Cwd, string Branch, string BaseSha, string Toplevel);

public static class GitWorktree
{
    /// <summary>True when <paramref name="folder"/> is inside a git work tree.</summary>
    public static bool IsGitRepo(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return false;
        var (ok, stdout, _) = Run(folder, "rev-parse", "--is-inside-work-tree");
        return ok && stdout.Trim() == "true";
    }

    /// <summary>Create a new worktree off HEAD on a fresh branch, in a sibling
    /// &lt;toplevel&gt;-worktrees/&lt;suffix&gt;-&lt;hex&gt; folder. Returns the info incl. the cwd to launch in
    /// (re-applying any subfolder the user selected beneath the repo root). Throws on git error.</summary>
    public static WorktreeInfo Create(string folder, string suffix)
    {
        var toplevel = MustRun(folder, "top-level", "rev-parse", "--show-toplevel").Trim();
        var baseSha = MustRun(toplevel, "HEAD sha", "rev-parse", "HEAD").Trim();
        var hex = Guid.NewGuid().ToString("N")[..8];
        var safe = Sanitize(suffix);
        var branch = $"livecode/{safe}-{hex}";

        var parent = Path.GetDirectoryName(toplevel.TrimEnd('/', '\\')) ?? toplevel;
        var baseName = Path.GetFileName(toplevel.TrimEnd('/', '\\'));
        var path = Path.Combine(parent, $"{baseName}-worktrees", $"{safe}-{hex}");

        MustRun(toplevel, "worktree add", "worktree", "add", "-b", branch, path);

        var rel = Path.GetRelativePath(toplevel, folder);
        var cwd = (rel == "." || rel.StartsWith("..")) ? path : Path.Combine(path, rel);
        return new WorktreeInfo(path, cwd, branch, baseSha, toplevel);
    }

    /// <summary>Remove the worktree + branch only if clean: no uncommitted changes AND no commits
    /// on the branch beyond its base. Otherwise keep it and return the reason.</summary>
    public static (bool removed, string? keptReason) TryRemoveIfClean(WorktreeInfo info)
    {
        try
        {
            var (statusOk, status, _) = Run(info.WorktreePath, "status", "--porcelain");
            if (statusOk && status.Trim().Length > 0) return (false, "has uncommitted changes");

            var (aheadOk, ahead, _) = Run(info.Toplevel, "rev-list", $"{info.BaseSha}..{info.Branch}", "--count");
            if (aheadOk && int.TryParse(ahead.Trim(), out var n) && n > 0) return (false, "has unmerged commits");

            var (rmOk, _, rmErr) = Run(info.Toplevel, "worktree", "remove", info.WorktreePath);
            if (!rmOk) return (false, string.IsNullOrWhiteSpace(rmErr) ? "worktree is locked or in use" : rmErr.Trim());

            Run(info.Toplevel, "branch", "-D", info.Branch); // best-effort
            return (true, null);
        }
        catch { return (false, "cleanup failed"); }
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-');
        var r = sb.ToString().Trim('-');
        return r.Length == 0 ? "session" : r;
    }

    private static string MustRun(string cwd, string what, params string[] args)
    {
        var (ok, stdout, stderr) = Run(cwd, args);
        if (!ok) throw new InvalidOperationException($"git {what} failed: {(stderr.Length > 0 ? stderr.Trim() : "unknown error")}");
        return stdout;
    }

    private static (bool ok, string stdout, string stderr) Run(string cwd, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return (false, "", "could not start git");
            var so = p.StandardOutput.ReadToEnd();
            var se = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return (p.ExitCode == 0, so, se);
        }
        catch (Exception ex) { return (false, "", ex.Message); }
    }
}
```

- [ ] **Step 2: Build.**

Run: `dotnet build`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 3: Exercise the helper end-to-end in a scratch git repo (headless).**

Add a temporary debug verb OR verify via `git` directly. Simplest: verify by hand in a scratch repo that the sequence works (this is the exact sequence `Create`/`TryRemoveIfClean` run):

```bash
cd "$TEMP" && rm -rf wt-test && mkdir wt-test && cd wt-test && git init -q && git commit -q --allow-empty -m init
git rev-parse --show-toplevel && git rev-parse HEAD
git worktree add -b livecode/test-abc12345 "../wt-test-worktrees/test-abc12345"
git -C "../wt-test-worktrees/test-abc12345" status --porcelain    # empty = clean
git rev-list HEAD..livecode/test-abc12345 --count                 # 0 = no extra commits
git worktree remove "../wt-test-worktrees/test-abc12345" && git branch -D livecode/test-abc12345 && echo REMOVED_CLEAN
```
Expected: worktree adds, status empty, count 0, `REMOVED_CLEAN` printed.

- [ ] **Step 4: Commit.**

```bash
git add Terminal/GitWorktree.cs
git commit -m "Live Code: GitWorktree helper (create/remove-if-clean) for session isolation"
```

---

### Task 2: Backend — folderInfo, isolation, worktree cleanup

**Files:**
- Modify: `Bridge/Handlers/LiveCodeHandlers.cs`

**Interfaces:**
- Consumes: `GitWorktree.*` (Task 1).
- Produces:
  - `livecode.folderInfo { folder }` → `{ isGitRepo: bool }`.
  - `livecode.start` / `livecode.reset` accept `isolation` (`"worktree"`|`"none"`, default none);
    start/reset response adds `isolated: bool`, `worktreePath: string|null`.
  - `livecode.closeTab { tabId }` → `{ worktreeKept: bool, worktreeReason: string|null, worktreePath: string|null }`.

- [ ] **Step 1: Add `Worktree` to `LiveSession`.**

In the `LiveSession` class add:
```csharp
        public WorktreeInfo? Worktree;      // set when the session runs in an isolated git worktree
```

- [ ] **Step 2: `StopSession` must preserve `Worktree`.**

`StopSession(LiveSession e)` is unchanged EXCEPT it must not touch `e.Worktree` (it already only nulls Session/ActiveFolder/ActiveSessionId/ActiveModel — leave it that way; add a comment):
```csharp
    // NOTE: e.Worktree is intentionally preserved here (needed for reset reuse + close cleanup).
```

- [ ] **Step 3: Register `livecode.folderInfo`.**

Add near the other stateless handlers in `Register`:
```csharp
        router.Register("livecode.folderInfo", payload =>
        {
            var folder = SessionHandlers.GetString(payload, "folder");
            return Task.FromResult<object?>(new { isGitRepo = GitWorktree.IsGitRepo(folder) });
        });
```

- [ ] **Step 4: Honor `isolation` in `StartTicketSession`.**

In `StartTicketSession`, after the existing `folder` existence check and after `var shell = ShellResolver.Resolve(shellReq);`, resolve the launch folder + optional worktree:
```csharp
        var isolation = SessionHandlers.GetString(payload, "isolation");
        WorktreeInfo? worktree = null;
        var launchFolder = folder;
        if (string.Equals(isolation, "worktree", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(folder))
        {
            worktree = GitWorktree.Create(folder!, ticketKey ?? ("tab-" + tabId[..Math.Min(8, tabId.Length)]));
            launchFolder = worktree.Cwd;   // launch, transcript, and auto-link all use the worktree cwd
        }
```
Then replace subsequent uses of `folder` for launch/transcript/link with `launchFolder`:
- `SessionRepo.LinkLiveCodeSession(conn, sessionId, TranscriptPath(launchFolder, sessionId), launchFolder, ticketKey!);`
- `LaunchInPty(router, tabId, shell, launchFolder, cols, rows, kickoff, permissionMode, sessionId, model, ticketKey, trackSession: kickoff is not null);`
- After `LaunchInPty`, record the worktree on the entry:
```csharp
        if (worktree is not null)
            lock (Gate) { if (Tabs.TryGetValue(tabId, out var e)) e.Worktree = worktree; }
```
- Update the return object:
```csharp
        return new { shell = shell.Kind, fellBack = shell.FellBack, kickoff = kickoff is not null,
                     agentUsed = agentName, isolated = worktree is not null, worktreePath = worktree?.WorktreePath };
```
(`GitWorktree.Create` throwing propagates out of the handler → bridge error → frontend toast, no session.)

- [ ] **Step 5: `closeTab` cleans up the worktree (remove-if-clean).**

Replace the `livecode.closeTab` handler body:
```csharp
        router.Register("livecode.closeTab", payload =>
        {
            var tabId = RequireTabId(payload);
            WorktreeInfo? wt;
            lock (Gate)
            {
                if (!Tabs.TryGetValue(tabId, out var e))
                    return Task.FromResult<object?>(new { worktreeKept = false, worktreeReason = (string?)null, worktreePath = (string?)null });
                e.Session?.Dispose();
                wt = e.Worktree;
                Tabs.Remove(tabId);
            }
            if (wt is null)
                return Task.FromResult<object?>(new { worktreeKept = false, worktreeReason = (string?)null, worktreePath = (string?)null });
            var (removed, reason) = GitWorktree.TryRemoveIfClean(wt);
            return Task.FromResult<object?>(new { worktreeKept = !removed, worktreeReason = reason, worktreePath = wt.WorktreePath });
        });
```

- [ ] **Step 6: Build.**

Run: `dotnet build`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 7: Headless boot check (folderInfo wired, no regressions).**

Run: `(dotnet run --no-build -- --route livecode > "$TEMP/wt_boot.log" 2>&1 &) ; sleep 12 ; grep -c "SendWebMessage" "$TEMP/wt_boot.log"; grep -i "error\|exception" "$TEMP/wt_boot.log" || echo "no errors"`
Then close the app: `taskkill //IM AIUsage.exe //T //F`
Expected: many SendWebMessage lines, no errors/exceptions.

- [ ] **Step 8: Commit.**

```bash
git add Bridge/Handlers/LiveCodeHandlers.cs
git commit -m "Live Code: folderInfo + git-worktree isolation (isolation param, closeTab cleanup)"
```

---

### Task 3: Frontend — App.choose + conflict warning + isolation wiring

**Files:**
- Modify: `wwwroot/js/app.js`
- Modify: `wwwroot/js/views/livecode.js`
- Modify: `wwwroot/css/app.css`

**Interfaces:**
- Consumes: `livecode.folderInfo`, `isolation` on start/reset, closeTab response (Task 2).
- Produces: `App.choose(message, buttons, danger) : Promise<string|null>`.

- [ ] **Step 1: Add `App.choose` to `app.js`.**

Insert into the `window.App = { … }` object (after `confirm`):
```javascript
    // Promise<string|null> multi-button chooser (returns the chosen button key, null if dismissed).
    // buttons: [{ key, label, primary?, danger? }]. Message rendered as text (injection-safe).
    choose(message, buttons, danger = false) {
      return new Promise(resolve => {
        const ov = document.createElement('div');
        ov.className = 'modal-overlay';
        const btns = buttons.map(b =>
          `<button class="btn ${b.primary ? 'btn-primary' : b.danger ? 'btn-danger' : ''}" data-key="${App.esc(b.key)}">${App.esc(b.label)}</button>`
        ).join('');
        ov.innerHTML = `<div class="modal"><div class="modal-msg"></div><div class="modal-actions">${btns}</div></div>`;
        ov.querySelector('.modal-msg').textContent = message;
        const done = v => { ov.remove(); resolve(v); };
        ov.querySelectorAll('[data-key]').forEach(b => b.addEventListener('click', () => done(b.dataset.key)));
        ov.addEventListener('click', e => { if (e.target === ov) done(null); });
        document.body.appendChild(ov);
      });
    },
```

- [ ] **Step 2: `livecode.js` — track effective folder + isolation on the tab model.**

In `makeTab()` add fields:
```javascript
      activeFolder: '',        // dir the session actually runs in (folder, or worktree cwd)
      isolated: false,         // running in an isolated git worktree
```
In `reconcile()` inside the backend-merge loop, set `if (b.folder) t.activeFolder = b.folder;` (alongside the existing `t.folder` handling — keep both: `folder` is the selection, `activeFolder` is where it runs).

- [ ] **Step 3: `livecode.js` — conflict detection helpers.**

Add near the tab helpers:
```javascript
  function normFolder(p) { return String(p || '').trim().replace(/[\\/]+/g, '/').replace(/\/+$/, '').toLowerCase(); }
  function conflictingTab(selectedFolder, excludeTabId) {
    const n = normFolder(selectedFolder);
    if (!n) return null;
    return tabs.find(t => t.tabId !== excludeTabId && t.running && normFolder(t.activeFolder) === n) || null;
  }
  const WT_WARN =
    '⚠ Another running session is already working in this folder.\n\n' +
    'Running multiple agents on the same folder at once can cause file conflicts, corrupted ' +
    'edits, and lost work. Continuing on the same folder is entirely at your own risk.';

  // Returns 'none' | 'worktree' to proceed, or null to abort.
  async function resolveIsolation(t) {
    if (!conflictingTab(t.folder, t.tabId)) return 'none';
    let isGitRepo = false;
    try { const r = await Bridge.call('livecode.folderInfo', { folder: t.folder }, 5000); isGitRepo = !!(r && r.isGitRepo); }
    catch { /* treat as non-git */ }
    const buttons = [];
    let msg = WT_WARN;
    if (isGitRepo) buttons.push({ key: 'worktree', label: 'Use isolated worktree (safe)', primary: true });
    else msg += '\n\n(Isolation with a worktree needs a git repository; this folder is not one.)';
    buttons.push({ key: 'same', label: 'Continue in same folder (own risk)', danger: true });
    buttons.push({ key: 'cancel', label: 'Cancel' });
    const choice = await App.choose(msg, buttons, true);
    if (choice === 'worktree') return 'worktree';
    if (choice === 'same') return 'none';
    return null; // cancel / dismiss
  }
```

- [ ] **Step 4: `livecode.js` — call it from `start()` and `reset()`, pass `isolation`, record results.**

In `start()`, after the API-key confirm block and before `saveConfig()`:
```javascript
    const isolation = await resolveIsolation(t);
    if (isolation === null) return;      // user cancelled
```
Change the `livecode.start` payload to include `isolation,` and after success set:
```javascript
      t.activeFolder = (r && r.folder) || t.folder;
      t.isolated = !!(r && r.isolated);
      if (r && r.isolated) App.toast('Running in isolated worktree: ' + r.worktreePath);
```
In `reset()`, reuse the worktree: pass `folder: t.activeFolder || t.folder` and `isolation: 'none'` in the payload (do NOT create a second worktree); after success keep `t.activeFolder` as-is. (Reset is a same-tab restart, so no new conflict check.)
In `start()`'s success path also ensure non-isolated sets `t.activeFolder = t.folder`.

- [ ] **Step 5: `livecode.js` — worktree marker on the tab + close toast.**

In `renderTabBar()`, add a marker after the label when isolated:
```javascript
        <span class="lc-tab-label">${App.esc(tabLabel(t, i))}</span>
        ${t.isolated ? `<span class="lc-tab-wt" title="Isolated git worktree">⑂</span>` : ''}
```
In `closeTab()`, capture the response and toast:
```javascript
    let res;
    try { res = await Bridge.call('livecode.closeTab', { tabId }, 0); } catch { /* dispose anyway */ }
    if (res && res.worktreeKept) App.toast(`Worktree kept (${res.worktreeReason}): ${res.worktreePath}`, true);
    else if (res && res.worktreePath) App.toast('Worktree removed.');
```
(Replace the existing `try { await Bridge.call('livecode.closeTab', …) } catch {}` line.)

Also set `t.activeFolder` on reattach: in `reattachAll()`, when `at.running`, add `t.activeFolder = t.activeFolder || t.folder;` (best-effort; the authoritative value comes from `reconcile`/`livecode.list`).

- [ ] **Step 6: CSS marker in `app.css`.**

After the `.lc-tab-shell` rule:
```css
.lc-tab-wt { color: var(--accent); font-size: 12px; }
```

- [ ] **Step 7: Build + JS checks.**

Run: `node --check wwwroot/js/app.js && node --check wwwroot/js/views/livecode.js && dotnet build`
Expected: JS valid; `Build succeeded`.

- [ ] **Step 8: Manual UI verification.**

Run: `dotnet run`. In a git-repo folder: start a session in tab 1; open tab 2, select the SAME folder, Start → warning dialog appears with 3 buttons. Choose "Use isolated worktree" → tab 2 shows `⑂`, toast names the worktree path, and both run independently. Close tab 2 with no changes → "Worktree removed". Repeat but have the worktree make an edit → close → "Worktree kept (has uncommitted changes)". In a non-git folder: the dialog shows only same-folder / cancel.

- [ ] **Step 9: Commit.**

```bash
git add wwwroot/js/app.js wwwroot/js/views/livecode.js wwwroot/css/app.css
git commit -m "Live Code: same-folder warning + worktree isolation (App.choose, conflict dialog)"
```

---

### Task 4: Docs sync + final verify

**Files:**
- Modify: `CLAUDE.md`, `.claude/STRUCTURE.md`, `PROGRESS.md`

- [ ] **Step 1:** In `CLAUDE.md` Live Code paragraph, note the same-folder warning + git-worktree isolation (new `livecode.folderInfo`, `isolation` param, `GitWorktree.cs`, remove-if-clean on close).
- [ ] **Step 2:** In `.claude/STRUCTURE.md`: add `Terminal/GitWorktree.cs` to the Terminal section + file table; add `livecode.folderInfo` to the bridge catalog; note `isolation`/`isolated`/`worktreePath` and `closeTab` cleanup in the LiveCodeHandlers row; note `App.choose` in the `app.js` row; note `LiveSession.Worktree`.
- [ ] **Step 3:** Append a dated section to `PROGRESS.md` and bump `_Last updated_`.
- [ ] **Step 4: Final build.**

Run: `dotnet build`
Expected: `Build succeeded`.

- [ ] **Step 5: Commit.**

```bash
git add CLAUDE.md .claude/STRUCTURE.md PROGRESS.md
git commit -m "Docs: Live Code same-folder warning + worktree isolation"
```

---

## Self-Review

- **Spec coverage:** trigger on Start/Reset vs running (Task 3 `conflictingTab` in start/reset) ✓; 3-way git-repo-aware dialog (Task 3 `resolveIsolation` + Task 2 `folderInfo`) ✓; own-risk disclaimer (Task 3 `WT_WARN`) ✓; worktree create off HEAD, sibling path, branch name (Task 1 `Create`) ✓; launch/transcript/link use worktree cwd (Task 2 Step 4) ✓; remove-if-clean on close (Task 1 `TryRemoveIfClean` + Task 2 Step 5) ✓; non-git omits worktree option (Task 3 Step 3) ✓; reset reuses worktree (Task 3 Step 4) ✓; marker + toasts (Task 3 Steps 4–5) ✓; StopSession preserves Worktree (Task 2 Step 2) ✓; docs (Task 4) ✓.
- **Type consistency:** `WorktreeInfo(WorktreePath, Cwd, Branch, BaseSha, Toplevel)` used identically in Tasks 1–2; `isolation` string param and `{isolated, worktreePath}` response consistent Task 2↔3; `closeTab` `{worktreeKept, worktreeReason, worktreePath}` consistent Task 2↔3; `App.choose` signature consistent Task 3 Step 1↔3.
- **Placeholders:** none — exact code, paths, and verification commands throughout.
