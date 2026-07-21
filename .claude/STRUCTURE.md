# STRUCTURE.md

Detailed file-by-file map of the AI Usage Tracker. This complements `CLAUDE.md` (which
covers the big-picture architecture and conventions) with the concrete inventory: every
source file's responsibility, the full bridge-action catalog, the DB schema, and the
settings keys.

> **Keep this in sync.** When you add, remove, rename, or repurpose a file, a bridge
> action, a DB table/column, or a settings key, **update this file AND `CLAUDE.md` in the
> same change.** `PROGRESS.md` remains the feature-history log; these two are the reference.

---

## Directory tree (source only — `bin/`, `obj/` are build output)

```
AIUsage/
├── AIUsage.csproj            # net10.0 WinExe; embeds wwwroot + appicon; NuGet refs
├── Program.cs                # entry point, window setup, handler registration, CLI verbs
├── WebAssets.cs              # embedded-resource extraction (web assets + icon)
├── CLAUDE.md                 # architecture & conventions (read first)
├── PROGRESS.md               # feature history / decisions / first-run checklist
├── PLAN-AI-USAGE-TRACKER.md  # original implementation plan
├── REVIEW-AI-USAGE.md        # plan review notes
├── .claude/
│   ├── STRUCTURE.md          # this file
│   └── settings.local.json   # Claude Code local settings
├── Bridge/
│   ├── MessageRouter.cs      # JSON request/response bus (WebView ↔ .NET)
│   └── Handlers/             # one static Register(router) class per domain
│       ├── SessionHandlers.cs
│       ├── ManualHandlers.cs
│       ├── JiraHandlers.cs
│       ├── SettingsHandlers.cs
│       ├── StatsHandlers.cs
│       ├── ExportHandlers.cs
│       └── LiveCodeHandlers.cs   # Live Code page: tickets, folder, agents, terminal, metrics
├── Scanner/
│   ├── TranscriptScanner.cs  # incremental JSONL walk, offset/WAL bookkeeping
│   ├── SessionAggregator.cs  # SOLE owner of the transcript schema; aggregates + ReadLive/ContextWindow/ReadDetail
│   ├── ActiveSessions.cs     # top-N recently-active Claude Code sessions (for the metrics panel)
│   ├── FolderSessions.cs     # existing sessions in one folder (Resume Sessions picker)
│   └── TicketKeyInferrer.cs  # branch/cwd/prompt → ticket keys, allowlist filter
├── Terminal/                 # Live Code terminal backend
│   ├── ConPtySession.cs      # pseudo-console session (Porta.Pty wrapper): Start/Write/Resize + Output/Exited
│   ├── ShellResolver.cs      # PowerShell / Git Bash resolution (fallback to PowerShell)
│   ├── AgentCatalog.cs       # lists .claude/agents (project + user + custom dir) with name/description
│   ├── ClaudeCli.cs          # resolves the claude CLI on PATH (install check)
│   ├── GitWorktree.cs        # git-worktree isolation: IsGitRepo / Create / TryRemoveIfClean
│   └── PromptWatcher.cs      # best-effort auto-approve: detects prompts, injects Enter
├── Platform/
│   └── FolderDialog.cs       # UI-thread-marshalled Photino folder picker (+ manual fallback)
├── Data/
│   ├── Db.cs                 # connection open (WAL+FK), portable-first DB path
│   ├── Migrations.cs         # idempotent schema, seeds, SchemaVersion (v5)
│   ├── Rows.cs               # generic Query→dictionaries / Scalar helpers
│   └── Repositories/
│       ├── SessionRepo.cs
│       ├── TicketRepo.cs
│       └── ManualEntryRepo.cs
├── Jira/
│   ├── JiraClient.cs         # read-only JIRA Cloud REST client (+ ADF description parse)
│   └── JiraSync.cs           # lazy background fetch orchestration
├── Settings/
│   └── SettingsStore.cs      # typed settings + DPAPI-protected secrets
├── Export/
│   └── XlsxWriter.cs         # hand-rolled minimal OOXML .xlsx writer
├── Resources/
│   └── appicon.ico           # multi-size icon (BMP frames; exe + window icon)
└── wwwroot/                  # frontend (embedded into the exe at build time)
    ├── index.html            # shell: sidebar nav + <main id="content"> + script tags
    ├── css/app.css
    ├── lib/chart.umd.js      # vendored Chart.js 4.4.9 (no CDN)
    ├── lib/xterm.js          # vendored xterm.js 5.3.0 (Live Code terminal; no CDN)
    ├── lib/xterm.css
    ├── lib/xterm-addon-fit.js
    └── js/
        ├── bridge.js         # Bridge.call() + Bridge.on() event channel
        ├── app.js            # hash router + window.App helpers (toast/confirm/…) + scan button
        └── views/            # one self-registering module per page (window.Views.*)
            ├── dashboard.js
            ├── sessions.js
            ├── session.js        # session detail page (#session/<id>)
            ├── manual.js
            ├── tickets.js
            ├── livecode.js
            └── settings.js
```

---

## Backend files (C#)

| File | Responsibility |
|---|---|
| `Program.cs` | `[STAThread] Main`: `Db.Initialize()`, parse args (`--route` opens the window on a page; anything else → `RunCli`), build the `PhotinoWindow` (1280×860 restore size, maximized, DevTools on), set icon via `WebAssets.ExtractIcon`, register all handler groups (incl. `LiveCodeHandlers.Register(router, window)`), load `index.html` from the extracted web dir over `file://`. `RunCli` implements `--scan`, `--sql` (read-only), `--set`, `--pty-test` (ConPTY streaming smoke test), `--envtest` (API-key strip check), `--shelltest` (print resolved shells), `--accounttest` (print plan + usage reset), `--detailtest <sessionId>` (print the session-detail deep re-parse — per-tool/per-model/timing — as fed to the detail page). `--route` accepts a `page/<param>` form (e.g. `session/<id>`). |
| `WebAssets.cs` | `EnsureExtracted()` copies embedded `web/**` resources to `%LOCALAPPDATA%\AIUsage\web` (overwrites each launch) and returns the path; dev fallback to on-disk `wwwroot`. `ExtractIcon()` writes the embedded `appicon.ico` to `%LOCALAPPDATA%\AIUsage`. |
| `Bridge/MessageRouter.cs` | Owns the handler dictionary and JSON (camelCase) (de)serialization. `OnMessage` parses `{id,action,payload}` on a `Task.Run` pool thread, dispatches, replies `{id,ok,data|error}`. `PushEvent(event, data)` sends unsolicited `{type:"event",…}` messages (streaming channel). Registers built-in `ping`. |
| `Bridge/Handlers/SessionHandlers.cs` | Actions `scan.run`, `sessions.list/detail/assignTicket/confirmLink/removeLink/dismiss/reopen`. `sessions.detail {sessionId}` loads the stored row (`SessionRepo.Get`) + does an on-demand deep re-parse of that one transcript (`SessionAggregator.ReadDetail`) for the exact per-tool/per-model/timing breakdown, folds in `SubagentTokens`, derives the activity category (link category, else edit-vs-read guess — matching the dashboard), builds the `agents`/`skills`/`hooks` lists and an `mcps` list (grouped `{server,tool,count}` parsed from the `mcp__server__tool` tool names), and returns everything the detail page renders (falls back to stored counters if the transcript file is gone → `transcriptAvailable:false`). Validates ticket keys (`^[A-Z][A-Z0-9]{1,9}-\d{1,6}$`, uppercased). Documents the `Task.Run` null-unwrap → canceled-task trap (see CLAUDE.md). |
| `Bridge/Handlers/ManualHandlers.cs` | Actions `categories.list`, `manual.list/create/delete`. |
| `Bridge/Handlers/JiraHandlers.cs` | Actions `tickets.list/fetch/sync/fetchMore`, `jira.test`. Builds a `JiraClient` from stored settings; `fetchMore` uses token-paginated `/search/jql`. |
| `Bridge/Handlers/SettingsHandlers.cs` | Actions `settings.get` (never returns the token value — write-only), `settings.set` (routes token to `SetProtected`; on allowlist change calls `PurgeDisallowedAutoLinks`). `PurgeDisallowedAutoLinks()` removes auto links whose key is outside the allowlist, keeping manual/confirmed links, and cleans orphan tickets. |
| `Bridge/Handlers/StatsHandlers.cs` | Action `stats.dashboard` (tiles + all chart datasets: weekly tickets, activity doughnut, top-tickets, token/model weekly, type×activity). |
| `Bridge/Handlers/ExportHandlers.cs` | Actions `export.sessions/manual/tickets`. `BuildWorkbook(what)` (public/testable) builds the same dataset as the page; saves directly to Downloads (fallback Documents) and reveals via `explorer /select` — **not** Photino `ShowSaveFile` (returns null off the UI thread). |
| `Bridge/Handlers/LiveCodeHandlers.cs` | `Register(router, window)`. **Multiple concurrent sessions, one per tab**: a `Dictionary<string, LiveSession>` keyed by a frontend-minted `tabId` replaces the old singleton statics (all reads/writes under `Gate`). `LiveSession` holds `{ Session, ActiveFolder, ActiveSessionId, ActiveModel, LastSessionId, LastFolder, TicketKey, Worktree }`. Actions `livecode.config/saveConfig/tickets/listAgents/pickFolder/pickAgentFile/folderInfo/start/stop/closeTab/resume/reset/attach/metrics/list/running/activeSessions`, `pty.input/resize` — every per-session action requires `tabId` (`RequireTabId`). `start` spawns the shell, strips `ANTHROPIC_API_KEY`, types the `claude --session-id <guid> …` kickoff (`BuildClaudeCommand` + `ShellQuote`), wires a `PromptWatcher` in auto-approve mode, and — when a ticket is selected — auto-links it via `SessionRepo.LinkLiveCodeSession`. `LaunchInPty` captures the `tabId` in the `pty.output`/`pty.exit` events; the exit closure identity-checks (`ReferenceEquals`) so a superseded/stopped session doesn't clobber a newer one (an intentional Stop/Reset disposes with `_disposed=true`, which suppresses the exit event). `tickets` returns the latest 3 assigned, excluding finished statuses (`ExcludedTicketStatuses`: Closed/Done/Ready for Release, filtered client-side). A picked **Custom Agent** file (`pickAgentFile`) is installed into `.claude/agents` (`InstallAgentFile`) and the kickoff becomes "Use the &lt;agent&gt; agent to work on &lt;ticket&gt;" (no `--agent` flag — prompt-based invocation); returns `agentUsed`. `config` returns global page state (plan/usage/`claudeInstalled`/`apiKeyPresent`/`lastCustomAgent`+`lastCustomAgentName`); `listAgents` lists project + user agents; `metrics {tabId}` returns week tokens (global) + that tab's session tokens & live context % (via `FindActiveTranscript`→`<guid>.jsonl`) + `activeSessions` (top 5). `resume {tabId}` re-launches `claude --resume <lastId> … 'continue'`; `reset {tabId}` sends `/exit`, tree-kills, then restarts a fresh Claude session on the same ticket (reuses `StartTicketSession`); `stop {tabId}` disposes the session but keeps the entry (Resume); `closeTab {tabId}` disposes, removes the entry, and (if the tab used a worktree) runs `GitWorktree.TryRemoveIfClean` → `{worktreeKept, worktreeReason, worktreePath}`; `attach {tabId}` returns that tab's buffered output to replay after navigation; `list` returns all live tabs; `running` returns `{running, count}` for the sidebar dot; `activeSessions` is a scan-free top-5 list. **Resume Sessions**: `sessionsInFolder {folder}` → `{sessions:[{sessionId,label,updated}]}` (via `FolderSessions.List`); `resumeSession {tabId,folder,sessionId,shell,autoApprove,bypass,cols,rows}` types `claude --resume <id>` (interactive, `BuildResumeSessionCommand`, no prompt) into the tab's terminal. **Same-folder isolation**: `folderInfo {folder}` → `{isGitRepo}`; `start`/`reset` accept an `isolation` param — `"worktree"` calls `GitWorktree.Create` and launches in the worktree cwd (transcript + auto-link use it), stores `WorktreeInfo` on the entry, and returns `{isolated, worktreePath, folder}`. `StopSession` preserves `Worktree` (reset reuse + close cleanup). `start`/`reset` share `StartTicketSession`; all launches share `LaunchInPty`; per-tab `LastSessionId`/`LastFolder` survive Stop for Resume. |
| `Scanner/TranscriptScanner.cs` | `Run()` (lock-guarded) walks each scan root's project dirs for `*.jsonl`, skips files older than `backfill_from`, cheap-prechecks `ScanState` (size+mtime), then inside `BEGIN IMMEDIATE` re-reads state, reads complete lines from the saved offset (`ReadCompleteLines` stops before a partial trailing line), aggregates, upserts sessions + auto links, handles shrink/rewrite via full reparse (`ResetCountersForFile` + `DeleteSessionsNotIn`), saves the new offset, commits. Returns `ScanResult(Sessions, NewFiles, UpdatedFiles, SkippedFiles)`. |
| `Scanner/SessionAggregator.cs` | `SessionAggregate` (all counters additive) + `Aggregate(lines, filePath)`. **The only code that knows the undocumented Claude Code transcript JSONL schema** — put format-drift fixes here; malformed lines are skipped. Ticket-key source priority: branch(0) → cwd(1) → prompt_text(2). Also `ReadLive(file)` (cwd/model/context tokens), `LastContextTokens(file)`, `ContextWindow(model)` (1M, or 200k for Haiku), `SubagentTokens(mainTranscriptPath)` → `SubagentUsage{InOut, CacheCreation, CacheRead}` (summed recursively across `<sessionId>/subagents/agent-*.jsonl`, the sidechain files the scanner skips; powers the Live Code session Tokens (in+out) + Cache split incl. agents), and `FirstUserPrompt(file, maxLen)` (first string-content user prompt, one line, trimmed — labels the Resume Sessions picker) for the Live Code panels. `ReadDetail(file, sessionId)` → `SessionDetail` powers the session **detail** page: a deep single-file re-parse giving exact per-tool counts (`ToolCounts`), per-model token usage (`Models`→`ModelUsage`), reply/prompt/tool-call counts, an Agent/Active/Idle time split (`AgentMs`/`ActiveMs`/`IdleMs` — each inter-event gap classified: before a human prompt = active, before an assistant reply or tool-result = agent, any gap >5 min = idle; the three partition ended−started), plus the sub-agents / skills / hooks used: `Agents` (from Agent/Task tool_use `subagent_type`), `Skills` (from Skill tool_use `skill`), and `Hooks` (from `type:"attachment"` lines whose `attachment.type` is `hook_success`/`hook_error`, keyed by `hookName`). MCP tools aren't separate — they're the `mcp__…` entries in `ToolCounts`. |
| `Scanner/FolderSessions.cs` | `List(folder, max=25)` → `FolderSession(SessionId, Label, UpdatedIso)` for the transcripts in a folder's encoded project dir (`~/.claude/projects/<encoded-cwd>`), newest-first. Label = `SessionAggregator.FirstUserPrompt`. Empty for a folder with no transcripts. Powers `livecode.sessionsInFolder`. |
| `Scanner/TicketKeyInferrer.cs` | Extracts/validates ticket keys against the project-key allowlist; `IsRealBranch` filters out detached-`HEAD`/empty branches. |
| `Data/Db.cs` | `Initialize(path?)`, `Open()` (WAL + foreign_keys), `DbPath`. `ResolveDefaultPath` is portable-first (next to exe when writable, else `%APPDATA%\AIUsage\`, one-time copy of an existing %APPDATA% DB incl. `-wal`/`-shm`). |
| `Data/Migrations.cs` | Idempotent `CREATE TABLE IF NOT EXISTS` for all tables + indexes, `AddColumnIfMissing` for post-ship columns (incl. `Tickets.description`), `Seed` (ActivityCategories), `SetVersion` (currently **5**). |
| `Data/Rows.cs` | `Query(conn, sql, params (name,value)[])` → `List<Dictionary<string,object?>>` (JSON-friendly) and `Scalar(conn, sql)`. |
| `Data/Repositories/SessionRepo.cs` | `ResetCountersForFile`, `DeleteSessionsNotIn`, `Upsert`, `AddAutoLink`, `AssignTicket`, `ConfirmLink`, `RemoveLink`, `SetReviewState`, `List(conn, filter)`, `Get(conn, id)` (full stored row + ticket links + explicit category name, for the detail page; null if unknown), `LinkLiveCodeSession` (placeholder row + `livecode`-source link, before the transcript is scanned). |
| `Data/Repositories/TicketRepo.cs` | `UpsertFetched` (all JIRA fields incl. `description`, COALESCEd so bulk search doesn't wipe it), `MarkFailed`, `UnsyncedKeys`, `AllKeys`, `List` (ordered `updated DESC, key DESC`). |
| `Data/Repositories/ManualEntryRepo.cs` | `Create` (auto-creates the Ticket row), `Delete`, `List`, `Categories`. |
| `Jira/JiraClient.cs` | `FetchIssueAsync` (summary/status/type/project/priority/sprint/updated + **description**, flattening the ADF tree to text; discovers the Sprint custom-field id once, cached in `jira_sprint_field`), `SearchIssuesAsync` (token-paginated `POST /rest/api/3/search/jql`; no description), `TestConnectionAsync` (`/myself`). 404→dead-key, 401→credential error. |
| `Jira/JiraSync.cs` | `TryFetchInBackground(key)` fire-and-forget enrichment on link/entry creation; `FetchOneAsync(client, key)`. |
| `Settings/SettingsStore.cs` | Typed accessors over the `Settings` table; `SetProtected`/`GetProtected` (DPAPI `dpapi:` on Windows, `plain:` fallback elsewhere). Helpers: `ScanRoots`, `ProjectKeyAllowlist`, `BackfillFrom`. |
| `Export/XlsxWriter.cs` | Minimal OOXML `.xlsx` via `System.IO.Compression` (inline strings + numeric cells, XML-escaping, sheet-name sanitising) — no external dependency. |
| `Terminal/ConPtySession.cs` | Pseudo-console session via **Porta.Pty** (raw ConPTY didn't stream on this build — see PROGRESS.md). `Start(app,args,cwd,envOverrides,cols,rows)`, `Write`, `Resize`, `Snapshot` (rolling 512KB output buffer for terminal re-attach), `Dispose`; `Output`/`Exited` events. `BuildEnvironment` applies overrides and ALSO unsets null-valued keys in this process (Porta.Pty inherits parent env). `Dispose` kills the whole process **tree** via `taskkill /T /F` (so Stop halts `claude`/`node`, not just the shell). |
| `Terminal/ShellResolver.cs` | `Resolve("powershell"\|"bash")` → exe + kind + `FellBack`. Git Bash probed on Git-for-Windows install paths + `git.exe` derivation; a bare PATH `bash.exe` is used only as a last resort and **never** the System32/SysWOW64 WSL shim (`IsSystemShim`). Falls back to PowerShell if not found. |
| `Terminal/AgentCatalog.cs` | `List(projectDir)` → agents from `<projectDir>/.claude/agents` + `~/.claude/agents` (name/description frontmatter; first-seen wins). `ReadAgentName(mdPath)` returns an agent file's name; `InstallAgentFile(mdPath, workingFolder)` copies a chosen agent `.md` into the working folder's `.claude/agents` (so Claude finds it) and returns its name. |
| `Terminal/PromptWatcher.cs` | Best-effort auto-approve: ANSI-strips the output stream, detects Claude confirmation prompts (`❯ 1.`, `(y/n)`, …) and returns Enter to inject (1.5s cooldown). Fragile by nature. |
| `Terminal/GitWorktree.cs` | Git-worktree isolation for same-folder sessions. `IsGitRepo(folder)` (never throws); `Create(folder, suffix)` → `WorktreeInfo(WorktreePath, Cwd, Branch, BaseSha, Toplevel)` via `git worktree add -b livecode/<suffix>-<hex>` in a sibling `<toplevel>-worktrees/<…>` off HEAD (throws on git error so the caller doesn't launch); `TryRemoveIfClean(info)` removes the worktree + branch only if `git status --porcelain` is empty AND `rev-list <base>..<branch>` is 0, else `(false, reason)`. All git runs via `Process`. |
| `Platform/FolderDialog.cs` | `Pick(window, title, initial)` — Photino `ShowOpenFolder`; `PickFile(window, title, filterName, exts, initialDir)` — `ShowOpenFile`. Both marshalled onto the UI thread (block the caller); return null on failure (page has manual path fields as fallback). |
| `Platform/ClaudeAccount.cs` | `Read()` → `{ Plan, UsageResetsAt }` from `~/.claude.json` (`organizationType` → Team/Enterprise, `userRateLimitTier` → Max 5x/Pro, `planLimitsEndDate` → reset). Reads ONLY non-secret enums/date — never org name, email, or tokens. |
| `Platform/ClaudeUsage.cs` | `ReadAsync()` → `ClaudeUsageInfo { SessionPct, SessionResetsAt, WeekPct, WeekResetsAt }` for the Live Code usage bars. GETs Anthropic's `oauth/usage` endpoint (the same one Claude Code's `/usage` reads), authed with the access token from `~/.claude/.credentials.json` (`claudeAiOauth.accessToken`; token used only to sign the request, never stored/logged/returned; skipped if expired). Maps `five_hour`→session, `seven_day`→week `utilization`+`resets_at` (server-computed %, no local quota math). Cached 5 min (SemaphoreSlim-guarded); best-effort — offline/signed-out returns last-good or null. |

---

## Frontend files (`wwwroot/`)

- **No ES modules** (they don't load over `file://` in WebView2). Classic `<script>` tags in `index.html`, loaded in order: `chart.umd.js` → `bridge.js` → view modules → `app.js`.
- Each view is an IIFE assigning `window.Views.<name> = { render(container) }`.
- Shared state lives on the globals `window.Bridge`, `window.Views`, `window.App`.

| File | Responsibility |
|---|---|
| `index.html` | Shell: `#sidebar` nav links (`data-route`), `<main id="content">`, `#toast`, "Scan now" button + status, script tags. |
| `js/bridge.js` | `Bridge.call(action, payload, timeoutMs=120000)` → Promise over `window.external.sendMessage/receiveMessage`; correlates by `crypto.randomUUID()` id; `timeoutMs:0` disables the timeout. `Bridge.on(event, handler)` subscribes to server-pushed `{type:"event"}` messages (returns an unsubscribe fn). |
| `js/app.js` | Hash router (`navigate()` on `hashchange`, wipes `#content` and calls the view's `render(container, param)`; the hash may carry a `/param` — `#session/<id>` → route `session`, param `<id>` — and the `session` route keeps the "Sessions" nav item highlighted), `window.App` helpers (`toast`, `esc`, `fmtNum`, `fmtDate`, `refresh`, `exportExcel`, `confirm` [promise modal], `choose` [multi-button promise modal → chosen key]), the scan button + startup ping-then-scan, and a global 3s poll of `livecode.running` that colours the sidebar "Live Code" dot (green = `count>0`, red = none). `setupNavPopover()` shows a hover panel over the Live Code nav item listing live tabs (`livecode.list`); clicking a row calls `Views.livecode.focusTab(tabId)`. Background (auto) scan only re-renders the dashboard so form state elsewhere survives. |
| `js/views/dashboard.js` | Stat tiles + Chart.js charts; fixed `CATEGORY_COLORS` palette (color follows the category, not chart rank); consumes `stats.dashboard`. |
| `js/views/sessions.js` | Sessions table with All / Needs review / Not-ticket-related tabs, tool-mix bar, ticket-badge confirm/remove, inline assign, dismiss/reopen; Export button. Each row's title is a link to `#session/<id>` (detail page). |
| `js/views/session.js` | Session **detail** page (`#session/<id>`, reached from the Sessions list). Renders cards from `sessions.detail`: **Overview** (started/ended, Agent·Active·Idle time split, primary model, category, review state, total tokens with in/out/cache split + a "+ sub-agents" note, prompt/reply/tool-call counts), **Tools** (one coloured segment per tool + a name×count list), **Models** (per-model output bar), **Agents & extensions** (four labelled chip groups: Agents / MCP tools / Skills / Hooks; "—" per empty group), **Token cost** (cost derived here from a model-family `$/Mtok` rate table → est. cost, cache-hit %, output share, and cache-read/write·output·input breakdown bars), and **Tickets** last (reuses `Views.sessions` confirm/unlink/assign). `back()` = `history.back()` (fallback `#sessions`). |
| `js/views/manual.js` | Manual-entry form (category/tool/date/description) + recent-entries list with delete; Export button. |
| `js/views/tickets.js` | Tickets table (Key/Summary/Project/Type/Priority dot/Status/Sprint/Sessions/Manual/Last synced), status row tint, All / AI-touched tabs, "✨ AI" badge, "Sync all" + "Fetch more"; Export button. |
| `js/views/livecode.js` | Live Code page — **multiple sessions as tabs**. Module-closure `tabs[]` + `activeTabId` (survive navigation). A tab bar (`＋ New tab`, soft cap 6, `×` closes with confirm-if-running) sits above a per-active-tab control panel (ticket picker, folder, shell/model/agent, Custom Agent, Start/Stop/Resume/Reset + Auto-approve + Bypass, plus a per-tab "this session" tokens/context readout). One xterm per tab lives in a persistent `#lc-terminals` container (only the active tab shown); a single `pty.output`/`pty.exit` subscription routes by `tabId`, so background tabs keep streaming. `newTab()` inherits last-used defaults; `reconcile()` merges `tabs[]` with `livecode.list` on load; `reattachAll()` replays each running tab's buffer; `focusTab(tabId)` (called by the sidebar popover) activates a tab. Shared bottom panel keeps Plan + week-tokens + active-sessions. Pollers: `pollMetrics` (4s, active tab) + `pollActive` (2s). **Same-folder safeguard**: each tab tracks `activeFolder`/`isolated`; `conflictingTab()` (normalized paths) detects another running tab in the chosen folder, and `resolveIsolation()` shows the git-repo-aware `App.choose` warning (worktree / same folder / cancel) → passes `isolation` to start; isolated tabs show a `⑂` marker and closing them toasts kept-vs-removed. **Agent lock + Resume Sessions**: `refreshControlLocks(t)` is the single authority — Custom Agent disabled when an Agent is picked OR a picked-resume runs; Shell + Model disabled while a picked-resume runs. `loadFolderSessions` enables/disables the Resume Sessions button by folder session count; `openResumeSessions` (modal) → `resumePickedSession` calls `livecode.resumeSession` (confirm-replace if running) and sets `resumedPick`; stop/exit clear it. |
| `js/views/settings.js` | Settings form: JIRA site URL/email/token (write-only), scan paths, allowlist, backfill date, fetch JQL; Test connection. |

---

## Database schema (SQLite, `SchemaVersion` = 5)

| Table | Purpose / key columns |
|---|---|
| `Sessions` | One row per Claude Code session (`id` PK). Metadata (file_path, project_dir, git_branch, title[/_is_custom], model, started/ended_at, cc_version), additive token & tool counters, `review_state` (`pending`/`not_ticket_related`/...). |
| `ScanState` | `file_path` PK → `last_offset`, `last_mtime`, `last_size` for incremental scanning. |
| `Tickets` | `key` PK; JIRA fields summary/status/issue_type/project/sprint/priority/updated/**description**, `last_synced`, `fetch_failed`. |
| `ActivityCategories` | Seeded list: Generated code, Wrote tests, Refactored, Debugged, Reviewed, Wrote docs, Investigated. |
| `SessionTicketLinks` | (`session_id`→Sessions ON DELETE CASCADE, `ticket_key`) PK; `source` (auto/manual/confirmed/livecode), `inferred_from` (branch/cwd/prompt_text), `category_id`. |
| `ManualEntries` | `id` PK; ticket_key, entry_date, category_id, description, tool_used, created_at. |
| `Settings` | `key` PK → `value` (see settings keys below). |
| `SchemaVersion` | single `version` row. |

Indexes: `idx_links_ticket`, `idx_manual_ticket`, `idx_sessions_started`.

---

## Bridge action catalog

`scan.run` · `sessions.list` · `sessions.detail` · `sessions.assignTicket` · `sessions.confirmLink` ·
`sessions.removeLink` · `sessions.dismiss` · `sessions.reopen` · `categories.list` ·
`manual.list` · `manual.create` · `manual.delete` · `tickets.list` · `tickets.fetch` ·
`tickets.sync` · `tickets.fetchMore` · `jira.test` · `settings.get` · `settings.set` ·
`stats.dashboard` · `export.sessions` · `export.manual` ·
`export.tickets` · `livecode.config` · `livecode.saveConfig` · `livecode.tickets` ·
`livecode.listAgents` · `livecode.pickFolder` · `livecode.pickAgentFile` · `livecode.folderInfo` · `livecode.sessionsInFolder` · `livecode.start` · `livecode.stop` ·
`livecode.closeTab` · `livecode.metrics` · `livecode.resume` · `livecode.resumeSession` · `livecode.reset` · `livecode.attach` · `livecode.list` ·
`livecode.running` · `livecode.activeSessions` · `livecode.usage` · `pty.input` · `pty.resize` · `ping` (built-in).

Live Code supports **multiple concurrent sessions, one per UI tab**. Every per-session action
(`start`/`resume`/`reset`/`stop`/`closeTab`/`attach`/`metrics`, `pty.input`, `pty.resize`) takes a
frontend-minted **`tabId`** (a GUID, stable across a tab's Stop→Resume/Reset). `livecode.list`
returns all live tabs `[{tabId, folder, ticketKey, running, canResume, model}]` (rebuilds tabs
after navigation; feeds the sidebar hover panel). `livecode.running` returns `{running, count}`.
`livecode.closeTab` disposes a tab's session and drops its entry; `livecode.stop` keeps the entry
(so Resume still works). The stream **events** `pty.output`/`pty.exit` carry `{tabId, …}` so the
right tab's terminal renders them.

`livecode.usage` returns the rolling usage-limit bars `{available, sessionPct, sessionResetsAt, weekPct, weekResetsAt}` (server-computed % from `ClaudeUsage`; `available:false` when signed out/offline → page hides the bars). The Live Code bottom panel polls it every 60s (backend caches 5 min) and renders a SESSION (5h) + WEEK (7d) bar beside Plan/Tokens, colour-graded ≥80% warn / ≥95% crit.

`livecode.metrics {tabId}` returns `{weekTokens, sessionTokens, mainTokens, agentTokens, cacheTokens, cacheCreation, cacheRead, contextTokens, contextSize, contextPct, active, activeSessions}`. `sessionTokens` is `mainTokens + agentTokens` = `input + output` **including sub-agents** — the SAME formula as the dashboard and `weekTokens` (all count in+out only), so the numbers stay consistent; sub-agents (Task-tool sidechains under `<sessionId>/subagents/*.jsonl`, which the scanner skips) are re-added here for this readout only. `cacheTokens` (`cacheCreation + cacheRead`, incl. sub-agents) is shown as a **separate** "Cache" field with a created/read tooltip, so cache never inflates the headline Tokens. The UI adds an "incl. N agents" suffix + Main/agents tooltip on Tokens when `agentTokens > 0`. The DB/dashboard/`weekTokens` exclude cache and sub-agents (v1 design).

**Server-pushed events** (via `MessageRouter.PushEvent` → `Bridge.on`): `pty.output` (base64 terminal bytes), `pty.exit` (exit code).

---

## Settings keys (`Settings` table)

| Key | Meaning |
|---|---|
| `scan_paths` | Transcript scan roots (defaults to `%USERPROFILE%\.claude\projects`). |
| `projects` | (project list setting). |
| `project_key_allowlist` | Allowed JIRA project prefixes for ticket inference (e.g. `SFTY`); changing it purges disallowed auto-links. |
| `backfill_from` | Ignore transcript files older than this date. |
| `jira_site_url` | JIRA Cloud base URL. |
| `jira_email` | JIRA account email (basic-auth user). |
| `jira_token` | API token — **DPAPI-protected** (`dpapi:` prefix), write-only in the UI. Never paste a real token into Claude. |
| `jira_fetch_jql` | JQL for "Fetch more" (default `assignee = currentUser() ORDER BY updated DESC`). |
| `jira_sprint_field` | Discovered Sprint custom-field id (cache; `-` = instance has no Sprint field). |
| `livecode_last_folder` | Live Code: last-used working folder (default). |
| `livecode_last_shell` | Live Code: last-used shell (`powershell`/`bash`). |
| `livecode_last_model` | Live Code: last-used model (`''`/`opus`/`sonnet`/`haiku`). |
| `livecode_auto_approve` | Live Code: auto-approve toggle state (`1`/`0`). (Bypass is never persisted.) |
| `livecode_custom_agent` | Live Code: path to a chosen agent `.md` file to use for the session. |
| `livecode_ticket_count` | Live Code: how many assigned tickets the picker lists (default `3`, clamped 1–20). Set in Settings → Live Code; `livecode.tickets` and `livecode.config` (`ticketCount`) read it. |

---

## Dependencies (NuGet)

Photino.NET · Microsoft.Data.Sqlite · System.Security.Cryptography.ProtectedData ·
SQLitePCLRaw.bundle_e_sqlite3 · **Porta.Pty** (ConPTY wrapper for the Live Code terminal; managed-only).

---

## Build output (not source, do not edit)

`bin/` and `obj/` — MSBuild output. The single-file published exe lands under
`bin/Release/net10.0/win-x64/publish/AIUsage.exe` (see `CLAUDE.md` for the publish command).
