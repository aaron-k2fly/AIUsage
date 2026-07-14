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
│   ├── SessionAggregator.cs  # SOLE owner of the transcript schema; aggregates + ReadLive/ContextWindow
│   ├── ActiveSessions.cs     # top-N recently-active Claude Code sessions (for the metrics panel)
│   └── TicketKeyInferrer.cs  # branch/cwd/prompt → ticket keys, allowlist filter
├── Terminal/                 # Live Code terminal backend
│   ├── ConPtySession.cs      # pseudo-console session (Porta.Pty wrapper): Start/Write/Resize + Output/Exited
│   ├── ShellResolver.cs      # PowerShell / Git Bash resolution (fallback to PowerShell)
│   ├── AgentCatalog.cs       # lists .claude/agents (project + user + custom dir) with name/description
│   ├── ClaudeCli.cs          # resolves the claude CLI on PATH (install check)
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
            ├── manual.js
            ├── tickets.js
            ├── livecode.js
            └── settings.js
```

---

## Backend files (C#)

| File | Responsibility |
|---|---|
| `Program.cs` | `[STAThread] Main`: `Db.Initialize()`, parse args (`--route` opens the window on a page; anything else → `RunCli`), build the `PhotinoWindow` (1280×860 restore size, maximized, DevTools on), set icon via `WebAssets.ExtractIcon`, register all handler groups (incl. `LiveCodeHandlers.Register(router, window)`), load `index.html` from the extracted web dir over `file://`. `RunCli` implements `--scan`, `--sql` (read-only), `--set`, `--pty-test` (ConPTY streaming smoke test), `--envtest` (API-key strip check), `--shelltest` (print resolved shells), `--accounttest` (print plan + usage reset). |
| `WebAssets.cs` | `EnsureExtracted()` copies embedded `web/**` resources to `%LOCALAPPDATA%\AIUsage\web` (overwrites each launch) and returns the path; dev fallback to on-disk `wwwroot`. `ExtractIcon()` writes the embedded `appicon.ico` to `%LOCALAPPDATA%\AIUsage`. |
| `Bridge/MessageRouter.cs` | Owns the handler dictionary and JSON (camelCase) (de)serialization. `OnMessage` parses `{id,action,payload}` on a `Task.Run` pool thread, dispatches, replies `{id,ok,data|error}`. `PushEvent(event, data)` sends unsolicited `{type:"event",…}` messages (streaming channel). Registers built-in `ping`. |
| `Bridge/Handlers/SessionHandlers.cs` | Actions `scan.run`, `sessions.list/assignTicket/confirmLink/removeLink/dismiss/reopen`. Validates ticket keys (`^[A-Z][A-Z0-9]{1,9}-\d{1,6}$`, uppercased). Documents the `Task.Run` null-unwrap → canceled-task trap (see CLAUDE.md). |
| `Bridge/Handlers/ManualHandlers.cs` | Actions `categories.list`, `manual.list/create/delete`. |
| `Bridge/Handlers/JiraHandlers.cs` | Actions `tickets.list/fetch/sync/fetchMore`, `jira.test`. Builds a `JiraClient` from stored settings; `fetchMore` uses token-paginated `/search/jql`. |
| `Bridge/Handlers/SettingsHandlers.cs` | Actions `settings.get` (never returns the token value — write-only), `settings.set` (routes token to `SetProtected`; on allowlist change calls `PurgeDisallowedAutoLinks`). `PurgeDisallowedAutoLinks()` removes auto links whose key is outside the allowlist, keeping manual/confirmed links, and cleans orphan tickets. |
| `Bridge/Handlers/StatsHandlers.cs` | Actions `stats.dashboard` (tiles + all chart datasets: weekly tickets, activity doughnut, top-tickets, token/model weekly, type×activity) and `stats.share` (AI-share denominator via JIRA `approximate-count`, hidden until JIRA configured). |
| `Bridge/Handlers/ExportHandlers.cs` | Actions `export.sessions/manual/tickets`. `BuildWorkbook(what)` (public/testable) builds the same dataset as the page; saves directly to Downloads (fallback Documents) and reveals via `explorer /select` — **not** Photino `ShowSaveFile` (returns null off the UI thread). |
| `Bridge/Handlers/LiveCodeHandlers.cs` | `Register(router, window)`. Actions `livecode.config/saveConfig/tickets/listAgents/pickFolder/start/stop/metrics`, `pty.input/resize`. Holds the single `ConPtySession` (guarded). `start` spawns the shell, strips `ANTHROPIC_API_KEY`, types the `claude --session-id <guid> …` kickoff (`BuildClaudeCommand` + `ShellQuote`), wires a `PromptWatcher` in auto-approve mode, and — when a ticket is selected — auto-links it via `SessionRepo.LinkLiveCodeSession`. `config` also returns `claudeInstalled` (ClaudeCli) + `lastAgentsDir`; `listAgents` takes a custom `agentsDir`; `metrics` returns week/session tokens, live context % (via `FindActiveTranscript`→`<guid>.jsonl`), and `activeSessions` (top 2). `resume` re-launches `claude --resume <lastId> … 'continue'` (resumes AND continues); `reset` sends `/exit`, tree-kills, then restarts a fresh Claude session on the same ticket (reuses `StartTicketSession`); `attach` returns the running session's buffered output to replay after navigation; `running` is a cheap bool for the sidebar dot; `activeSessions` is a scan-free top-2 list for the real-time panel. `start`/`reset` share `StartTicketSession` (ticket fetch + kickoff + auto-link + launch); all launches share `LaunchInPty`; `_lastSessionId`/`_lastFolder` survive Stop for Resume. |
| `Scanner/TranscriptScanner.cs` | `Run()` (lock-guarded) walks each scan root's project dirs for `*.jsonl`, skips files older than `backfill_from`, cheap-prechecks `ScanState` (size+mtime), then inside `BEGIN IMMEDIATE` re-reads state, reads complete lines from the saved offset (`ReadCompleteLines` stops before a partial trailing line), aggregates, upserts sessions + auto links, handles shrink/rewrite via full reparse (`ResetCountersForFile` + `DeleteSessionsNotIn`), saves the new offset, commits. Returns `ScanResult(Sessions, NewFiles, UpdatedFiles, SkippedFiles)`. |
| `Scanner/SessionAggregator.cs` | `SessionAggregate` (all counters additive) + `Aggregate(lines, filePath)`. **The only code that knows the undocumented Claude Code transcript JSONL schema** — put format-drift fixes here; malformed lines are skipped. Ticket-key source priority: branch(0) → cwd(1) → prompt_text(2). Also `ReadLive(file)` (cwd/model/context tokens), `LastContextTokens(file)`, and `ContextWindow(model)` (1M, or 200k for Haiku) for the Live Code panels. |
| `Scanner/TicketKeyInferrer.cs` | Extracts/validates ticket keys against the project-key allowlist; `IsRealBranch` filters out detached-`HEAD`/empty branches. |
| `Data/Db.cs` | `Initialize(path?)`, `Open()` (WAL + foreign_keys), `DbPath`. `ResolveDefaultPath` is portable-first (next to exe when writable, else `%APPDATA%\AIUsage\`, one-time copy of an existing %APPDATA% DB incl. `-wal`/`-shm`). |
| `Data/Migrations.cs` | Idempotent `CREATE TABLE IF NOT EXISTS` for all tables + indexes, `AddColumnIfMissing` for post-ship columns (incl. `Tickets.description`), `Seed` (ActivityCategories), `SetVersion` (currently **5**). |
| `Data/Rows.cs` | `Query(conn, sql, params (name,value)[])` → `List<Dictionary<string,object?>>` (JSON-friendly) and `Scalar(conn, sql)`. |
| `Data/Repositories/SessionRepo.cs` | `ResetCountersForFile`, `DeleteSessionsNotIn`, `Upsert`, `AddAutoLink`, `AssignTicket`, `ConfirmLink`, `RemoveLink`, `SetReviewState`, `List(conn, filter)`, `LinkLiveCodeSession` (placeholder row + `livecode`-source link, before the transcript is scanned). |
| `Data/Repositories/TicketRepo.cs` | `UpsertFetched` (all JIRA fields incl. `description`, COALESCEd so bulk search doesn't wipe it), `MarkFailed`, `UnsyncedKeys`, `AllKeys`, `List` (ordered `updated DESC, key DESC`). |
| `Data/Repositories/ManualEntryRepo.cs` | `Create` (auto-creates the Ticket row), `Delete`, `List`, `Categories`. |
| `Jira/JiraClient.cs` | `FetchIssueAsync` (summary/status/type/project/priority/sprint/updated + **description**, flattening the ADF tree to text; discovers the Sprint custom-field id once, cached in `jira_sprint_field`), `SearchIssuesAsync` (token-paginated `POST /rest/api/3/search/jql`; no description), `TestConnectionAsync` (`/myself`), `ApproximateCountAsync`. 404→dead-key, 401→credential error. |
| `Jira/JiraSync.cs` | `TryFetchInBackground(key)` fire-and-forget enrichment on link/entry creation; `FetchOneAsync(client, key)`. |
| `Settings/SettingsStore.cs` | Typed accessors over the `Settings` table; `SetProtected`/`GetProtected` (DPAPI `dpapi:` on Windows, `plain:` fallback elsewhere). Helpers: `ScanRoots`, `ProjectKeyAllowlist`, `BackfillFrom`. |
| `Export/XlsxWriter.cs` | Minimal OOXML `.xlsx` via `System.IO.Compression` (inline strings + numeric cells, XML-escaping, sheet-name sanitising) — no external dependency. |
| `Terminal/ConPtySession.cs` | Pseudo-console session via **Porta.Pty** (raw ConPTY didn't stream on this build — see PROGRESS.md). `Start(app,args,cwd,envOverrides,cols,rows)`, `Write`, `Resize`, `Snapshot` (rolling 512KB output buffer for terminal re-attach), `Dispose`; `Output`/`Exited` events. `BuildEnvironment` applies overrides and ALSO unsets null-valued keys in this process (Porta.Pty inherits parent env). `Dispose` kills the whole process **tree** via `taskkill /T /F` (so Stop halts `claude`/`node`, not just the shell). |
| `Terminal/ShellResolver.cs` | `Resolve("powershell"\|"bash")` → exe + kind + `FellBack`. Git Bash probed on Git-for-Windows install paths + `git.exe` derivation; a bare PATH `bash.exe` is used only as a last resort and **never** the System32/SysWOW64 WSL shim (`IsSystemShim`). Falls back to PowerShell if not found. |
| `Terminal/AgentCatalog.cs` | `List(projectDir)` → agents from `<projectDir>/.claude/agents` + `~/.claude/agents`, parsing `name`/`description` frontmatter (project shadows user). |
| `Terminal/PromptWatcher.cs` | Best-effort auto-approve: ANSI-strips the output stream, detects Claude confirmation prompts (`❯ 1.`, `(y/n)`, …) and returns Enter to inject (1.5s cooldown). Fragile by nature. |
| `Platform/FolderDialog.cs` | `Pick(window, title, initial)` — Photino `ShowOpenFolder` marshalled onto the UI thread (blocks the caller); returns null on failure (page has a manual path field as fallback). |
| `Platform/ClaudeAccount.cs` | `Read()` → `{ Plan, UsageResetsAt }` from `~/.claude.json` (`organizationType` → Team/Enterprise, `userRateLimitTier` → Max 5x/Pro, `planLimitsEndDate` → reset). Reads ONLY non-secret enums/date — never org name, email, or tokens. |

---

## Frontend files (`wwwroot/`)

- **No ES modules** (they don't load over `file://` in WebView2). Classic `<script>` tags in `index.html`, loaded in order: `chart.umd.js` → `bridge.js` → view modules → `app.js`.
- Each view is an IIFE assigning `window.Views.<name> = { render(container) }`.
- Shared state lives on the globals `window.Bridge`, `window.Views`, `window.App`.

| File | Responsibility |
|---|---|
| `index.html` | Shell: `#sidebar` nav links (`data-route`), `<main id="content">`, `#toast`, "Scan now" button + status, script tags. |
| `js/bridge.js` | `Bridge.call(action, payload, timeoutMs=120000)` → Promise over `window.external.sendMessage/receiveMessage`; correlates by `crypto.randomUUID()` id; `timeoutMs:0` disables the timeout. `Bridge.on(event, handler)` subscribes to server-pushed `{type:"event"}` messages (returns an unsubscribe fn). |
| `js/app.js` | Hash router (`navigate()` on `hashchange`, wipes `#content` and calls the view's `render`), `window.App` helpers (`toast`, `esc`, `fmtNum`, `fmtDate`, `refresh`, `exportExcel`, `confirm` [promise modal]), the scan button + startup ping-then-scan, and a global 3s poll of `livecode.running` that colours the sidebar "Live Code" dot (green = session running). Background (auto) scan only re-renders the dashboard so form state elsewhere survives. |
| `js/views/dashboard.js` | Stat tiles + Chart.js charts; fixed `CATEGORY_COLORS` palette (color follows the category, not chart rank); consumes `stats.dashboard`/`stats.share`. |
| `js/views/sessions.js` | Sessions table with All / Needs review / Not-ticket-related tabs, tool-mix bar, ticket-badge confirm/remove, inline assign, dismiss/reopen; Export button. |
| `js/views/manual.js` | Manual-entry form (category/tool/date/description) + recent-entries list with delete; Export button. |
| `js/views/tickets.js` | Tickets table (Key/Summary/Project/Type/Priority dot/Status/Sprint/Sessions/Manual/Last synced), status row tint, All / AI-touched tabs, "✨ AI" badge, "Sync all" + "Fetch more"; Export button. |
| `js/views/livecode.js` | Live Code page: ticket picker (3 assigned), folder pick, shell/model/agent + agents-folder selectors, Start/Stop/Resume/Reset + Auto-approve + Bypass (danger confirm), xterm.js terminal (mounts/streams `pty.output`, sends `pty.input`, refits, replays buffer on re-attach). Two page-level pollers: `pollMetrics` (4s, tokens/context) + `pollActive` (2s, active-sessions list). `updateButtons` gates Start/Stop/Resume/Reset by running/canResume state. |
| `js/views/settings.js` | Settings form: JIRA site URL/email/token (write-only), scan paths, allowlist, backfill date, fetch/share JQL; Test connection. |

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

`scan.run` · `sessions.list` · `sessions.assignTicket` · `sessions.confirmLink` ·
`sessions.removeLink` · `sessions.dismiss` · `sessions.reopen` · `categories.list` ·
`manual.list` · `manual.create` · `manual.delete` · `tickets.list` · `tickets.fetch` ·
`tickets.sync` · `tickets.fetchMore` · `jira.test` · `settings.get` · `settings.set` ·
`stats.dashboard` · `stats.share` · `export.sessions` · `export.manual` ·
`export.tickets` · `livecode.config` · `livecode.saveConfig` · `livecode.tickets` ·
`livecode.listAgents` · `livecode.pickFolder` · `livecode.start` · `livecode.stop` ·
`livecode.metrics` · `livecode.resume` · `livecode.reset` · `livecode.attach` · `livecode.running` ·
`livecode.activeSessions` · `pty.input` · `pty.resize` · `ping` (built-in).

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
| `jira_share_jql` | JQL denominator for the AI-share chart. |
| `jira_sprint_field` | Discovered Sprint custom-field id (cache; `-` = instance has no Sprint field). |
| `livecode_last_folder` | Live Code: last-used working folder (default). |
| `livecode_last_shell` | Live Code: last-used shell (`powershell`/`bash`). |
| `livecode_last_model` | Live Code: last-used model (`''`/`opus`/`sonnet`/`haiku`). |
| `livecode_auto_approve` | Live Code: auto-approve toggle state (`1`/`0`). (Bypass is never persisted.) |
| `livecode_agents_dir` | Live Code: user-chosen folder to also scan for agent `.md` files. |

---

## Dependencies (NuGet)

Photino.NET · Microsoft.Data.Sqlite · System.Security.Cryptography.ProtectedData ·
SQLitePCLRaw.bundle_e_sqlite3 · **Porta.Pty** (ConPTY wrapper for the Live Code terminal; managed-only).

---

## Build output (not source, do not edit)

`bin/` and `obj/` — MSBuild output. The single-file published exe lands under
`bin/Release/net10.0/win-x64/publish/AIUsage.exe` (see `CLAUDE.md` for the publish command).
