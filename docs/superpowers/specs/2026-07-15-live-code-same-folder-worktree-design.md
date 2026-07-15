# Live Code — same-folder warning + git-worktree isolation

**Date:** 2026-07-15
**Branch:** LIVE-CODE-SESSION
**Status:** Approved design

## Goal

When two Live Code tabs would run Claude Code sessions in the **same working folder** at the
same time, warn the user that concurrent agents on one folder can conflict — and let them
either continue on the same folder (at their own risk) or run the new session in an isolated
**git worktree** so the agents can't step on each other.

## Background (current state)

Live Code runs multiple sessions, one per tab, each identified by a `tabId`
(`Bridge/Handlers/LiveCodeHandlers.cs` → `Dictionary<tabId, LiveSession>`). A session's cwd
is the tab's selected `folder`; `StartTicketSession` → `LaunchInPty` spawns the shell there.
There is currently no guard against two tabs using the same folder.

## Decisions (locked)

- **Trigger:** on **Start** (and Reset), warn if the folder the tab is about to use matches the
  **currently-running** effective folder of another app tab. Scope is app tabs only (external
  Claude sessions are not policed).
- **Dialog:** 3 choices when the folder is a git repo — `[Use isolated worktree (safe)]` ·
  `[Continue in same folder (own risk)]` · `[Cancel]`. When it is **not** a git repo, omit the
  worktree option (only `[Continue in same folder (own risk)]` · `[Cancel]`) with a note that
  isolation needs a git repo.
- **Cleanup:** on tab close, **remove the worktree only if clean** (no uncommitted changes AND
  no unmerged commits); otherwise keep it and tell the user why.
- The disclaimer must state that continuing on the same folder is **entirely at the user's own
  risk**.

## Architecture

### 1. Conflict detection (frontend — `wwwroot/js/views/livecode.js`)

- Add **`tab.activeFolder`** — the directory a session actually runs in (the selected folder,
  or the worktree cwd when isolated). Set it from the Start/Reset responses (`r.folder`), from
  `reattachAll`/`reconcile` (`livecode.list` → `folder`).
- `normFolder(p)` = `String(p||'').trim().replace(/[\\/]+/g, '/').replace(/\/+$/,'').toLowerCase()`.
- `conflictingTab(selectedFolder, excludeTabId)` returns the first other tab with
  `running === true` and `normFolder(tab.activeFolder) === normFolder(selectedFolder)`, else null.
- Called in `start()` and `reset()` before launching (excluding the current tab).

### 2. Warning dialog (`wwwroot/js/app.js`)

Add a reusable **`App.choose(message, buttons, danger)`**:

```
// buttons: [{ key, label, primary?, danger? }]
// resolves to the chosen key, or null if dismissed (overlay click / Cancel-less dismiss)
App.choose(message, buttons, danger = false) : Promise<string|null>
```

Same `.modal` / `.modal-overlay` styling as `App.confirm`; message rendered as text
(injection-safe). Overlay-click resolves `null`.

Flow in `start()`/`reset()`:

1. `const other = conflictingTab(tab.folder, tab.tabId); if (!other) → isolation = 'none'` and
   proceed as today.
2. Else `const { isGitRepo } = await Bridge.call('livecode.folderInfo', { folder: tab.folder })`.
3. Build buttons: if `isGitRepo` →
   `[{key:'worktree',label:'Use isolated worktree (safe)',primary:true}, {key:'same',label:'Continue in same folder (own risk)',danger:true}, {key:'cancel',label:'Cancel'}]`;
   else drop the `worktree` button and append the git-repo note to the message.
4. `const choice = await App.choose(WARN_MESSAGE + note, buttons, true)`.
   - `null`/`'cancel'` → abort (no launch).
   - `'same'` → `isolation = 'none'`.
   - `'worktree'` → `isolation = 'worktree'`.
5. Pass `isolation` in the `livecode.start` / `livecode.reset` payload.

`WARN_MESSAGE`:
> ⚠ Another running session is already working in this folder.
>
> Running multiple agents on the same folder at once can cause file conflicts, corrupted
> edits, and lost work. Continuing on the same folder is entirely at your own risk.

On a successful start/reset, set `tab.activeFolder = r.folder` and `tab.isolated = !!r.isolated`;
toast `Running in isolated worktree: <r.worktreePath>` when isolated.

### 3. Git worktree helper (`Terminal/GitWorktree.cs` — new)

Static, isolated, runs `git` via `System.Diagnostics.Process` (reuse a small runner that
returns `(exitCode, stdout, stderr)`; never throws to the caller — returns results/booleans).

```
public sealed record WorktreeInfo(string WorktreePath, string Cwd, string Branch, string BaseSha, string Toplevel);

public static bool IsGitRepo(string folder);
    // git -C <folder> rev-parse --is-inside-work-tree  → stdout == "true"

public static WorktreeInfo Create(string folder, string suffix);
    // toplevel = git -C <folder> rev-parse --show-toplevel
    // baseSha  = git -C <toplevel> rev-parse HEAD
    // branch   = "livecode/" + Sanitize(suffix) + "-" + 8-hex
    // path     = <parent(toplevel)>/<basename(toplevel)>-worktrees/<Sanitize(suffix)>-<8-hex>
    // git -C <toplevel> worktree add -b <branch> <path>       (off HEAD)
    // rel = Path.GetRelativePath(toplevel, folder); Cwd = rel=="."? path : Path.Combine(path, rel)
    // throws on git failure (caller catches → toast, no launch)

public static (bool removed, string? keptReason) TryRemoveIfClean(WorktreeInfo info);
    // dirty  = git -C <WorktreePath> status --porcelain           (non-empty ⇒ dirty)
    // ahead  = git -C <Toplevel> rev-list <BaseSha>..<Branch> --count   (>0 ⇒ unmerged commits)
    // if dirty → ("has uncommitted changes"); if ahead → ("has unmerged commits")
    // else: git -C <Toplevel> worktree remove <WorktreePath>; git -C <Toplevel> branch -D <Branch>
    //       → (true, null); if remove fails → (false, "worktree is locked/in use")
```

`Sanitize` keeps `[A-Za-z0-9._-]`, replaces others with `-` (branch/path safe). `suffix` is the
ticket key when present, else `tab-<short tabId>`.

### 4. Backend wiring (`Bridge/Handlers/LiveCodeHandlers.cs`)

- **`LiveSession`** gains `public WorktreeInfo? Worktree;`.
- New action **`livecode.folderInfo`** `{ folder }` → `{ isGitRepo = GitWorktree.IsGitRepo(folder) }`
  (returns `false` for empty/missing folder; never throws).
- **`StartTicketSession`** reads `isolation` (`SessionHandlers.GetString(payload,"isolation")`).
  When `isolation == "worktree"`:
  - `var info = GitWorktree.Create(folder!, ticketKey ?? ("tab-" + tabId[..8]));`
  - use `info.Cwd` as the launch folder for the shell, transcript path (`TranscriptPath`),
    auto-link (`LinkLiveCodeSession`), and the pinned session id.
  - store `info` on the entry: after `LaunchInPty`, set `Entry(tabId).Worktree = info` under `Gate`
    (or pass it into `LaunchInPty`).
  - Return object adds `isolated = true, worktreePath = info.WorktreePath`. Non-worktree returns
    `isolated = false`.
  - On `GitWorktree.Create` throwing, let it propagate → the bridge returns an error → frontend
    toasts and does not mark running.
- **`livecode.reset`** reuses the existing worktree: the frontend passes `folder = tab.activeFolder`
  and `isolation = 'none'`, so reset relaunches in the same directory (the entry's `Worktree` is
  preserved by `StopSession`, which must NOT null `Worktree`).
- **`livecode.closeTab`**: after disposing the session and before/after removing the entry, if
  `entry.Worktree is not null` call `GitWorktree.TryRemoveIfClean(entry.Worktree)`; return
  `{ worktreeKept, worktreeReason, worktreePath }` so the frontend can toast. Best-effort; wrap in
  try/catch.

`StopSession` change: keep clearing `Session`/`ActiveSessionId`/`ActiveFolder`/`ActiveModel`, but
**do not** touch `Worktree` (needed at close for cleanup and reset reuse).

### 5. Frontend feedback (`livecode.js`, `app.css`)

- `tab.isolated` → render a small `⑂` marker in the tab chip (`title` = worktree path).
- `closeTab`: pass the response through; if `worktreeKept`, toast
  `Worktree kept (${worktreeReason}): ${worktreePath}`; else if a worktree existed, toast
  `Worktree removed.`
- CSS: `.lc-tab-wt` marker style (muted, small).

## Data flow (Start with conflict → worktree)

1. User clicks Start on tab B; folder matches running tab A's `activeFolder`.
2. Frontend `livecode.folderInfo` → git repo → `App.choose` 3-way dialog.
3. User picks **worktree** → `livecode.start { tabId, folder, isolation:'worktree', … }`.
4. Backend `GitWorktree.Create` makes `livecode/<ticket>-<hex>` at `<repo>-worktrees/<…>`,
   launches the shell there, links the ticket to the worktree transcript, stores `Worktree`.
5. Response `{ isolated:true, worktreePath, folder:<cwd> }` → frontend sets `tab.activeFolder`
   (= worktree cwd), marks `tab.isolated`, toasts.
6. Later, closing tab B → `TryRemoveIfClean`: removed if clean, else kept with a reason toast.

## Error handling

- Non-git folder → worktree option omitted; only same-folder / cancel.
- `git worktree add` failure → error toast, session not started.
- `folderInfo` and cleanup git calls are best-effort and never throw out of the handler.
- Path comparison normalized (case/slashes/trailing slash) to avoid false matches on Windows.
- Reset never creates a second worktree (reuses the tab's current directory).

## Out of scope (YAGNI)

- Detecting/among external (non-app) Claude sessions.
- Auto-merging or PR-ing worktree branches.
- Configurable branch names / worktree locations.
- Removing worktrees on app exit (kept by design — the "remove if clean" runs only on explicit
  tab close).

## Docs to update

`CLAUDE.md` (Live Code paragraph), `.claude/STRUCTURE.md` (new `livecode.folderInfo`,
`GitWorktree.cs`, `isolation` param, `App.choose`, `LiveSession.Worktree`), and `PROGRESS.md`.
