# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A single-user, local-only desktop app that tracks which JIRA tickets were worked on with AI assistance. It scans Claude Code session transcripts (and accepts manual entries), infers ticket keys, enriches them from JIRA (read-only), and visualizes the relations with charts. Photino.NET host (.NET 10) + OS-native WebView + vanilla-JS frontend + SQLite. No server, no team sync, no Electron.

## Commands

```bash
dotnet run                              # launch the app (Photino window)
dotnet build                            # build only
dotnet run -- --scan                    # headless: run the transcript scanner, print counts
dotnet run -- --sql "SELECT ..."        # headless: read-only query (PRAGMA query_only=ON enforced)
dotnet run -- --set <key> <value>       # headless: write a Settings row (use jira_token for the DPAPI-protected token)
dotnet run -- --route <page>            # open the app directly on a page (dashboard|sessions|manual|tickets|livecode|settings) — used for headless UI verification
dotnet run -- --pty-test                # headless: spawn a pseudo-console and verify continuous output streaming (Live Code terminal)
dotnet run -- --envtest                 # headless: verify ANTHROPIC_API_KEY is stripped from a session's child env
dotnet run -- --shelltest               # headless: print resolved PowerShell / Git Bash executables
dotnet run -- --accounttest             # headless: print subscription plan + usage-reset date from ~/.claude.json

# Publish the single-file, self-contained exe (see PROGRESS.md for the deliverable path):
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false
```

There is no test project — verification is done via the `--scan`/`--sql`/`--route`/`--pty-test`/`--envtest` CLI verbs and window screenshots.

Packages: Photino.NET, Microsoft.Data.Sqlite, System.Security.Cryptography.ProtectedData, SQLitePCLRaw.bundle_e_sqlite3, **Porta.Pty** (ConPTY wrapper for the Live Code terminal; managed-only, so the single-file publish is unaffected).

## Architecture

**Message bridge.** The WebView and .NET communicate over Photino's string message bus as JSON. Request `{ id, action, payload }` → response `{ id, ok, data | error }`. `Bridge/MessageRouter.cs` parses, dispatches by `action` to a registered handler on a pool thread, and serializes the reply (camelCase). Frontend side is `wwwroot/js/bridge.js` — `Bridge.call(action, payload, timeoutMs)` returns a Promise; pass `timeoutMs: 0` for unbounded operations (e.g. JIRA sync, Excel export) so the client doesn't strand a still-running backend call.

The bridge also has an **unsolicited event channel** for streaming (used by the Live Code terminal): `MessageRouter.PushEvent(event, data)` sends `{ type:"event", event, data }` with no request id, and the frontend subscribes via `Bridge.on(event, handler)`. Regular request/response is unchanged.

**Handlers** live in `Bridge/Handlers/*.cs`, each with a static `Register(router)` called from `Program.cs`. **Critical trap** (documented in `SessionHandlers.Register`): synchronous handlers must run inline and return `Task.FromResult<object?>(...)`. Do NOT wrap the body in `Task.Run(() => { ...; return null; })` — it binds to the `Func<Task<object?>>` unwrap overload, and unwrapping a null task yields a *canceled* task, surfacing as a spurious "A task was canceled" error even though the DB write committed. `MessageRouter.OnMessage` already provides the pool thread.

**Scanner pipeline** (`Scanner/`): `TranscriptScanner` walks `%USERPROFILE%\.claude\projects\**\*.jsonl` (append-only JSONL), incrementally by remembered byte offset (`ScanState` table); it reads only complete lines (stops before a partial trailing line a writer may still be appending) and uses `BEGIN IMMEDIATE` transactions so concurrent scanners (even other processes) don't double-count. Counters are **additive** across incremental parses; a shrunk/rewritten file triggers a full reparse that first zeroes counters. → `SessionAggregator` is the **ONLY** place that knows the undocumented Claude Code transcript schema — put all format-drift fixes here; unknown/malformed lines are skipped, never fatal. → `TicketKeyInferrer` extracts ticket keys with source priority **branch → cwd → prompt_text**, filtered by a project-key allowlist. Sidechain/subagent transcripts (in session-named subdirs) are skipped in v1.

**Data layer** (`Data/`): raw ADO.NET via `Microsoft.Data.Sqlite`, no ORM. `Db.Open()` sets WAL + foreign_keys. `Migrations.cs` is idempotent (`CREATE TABLE IF NOT EXISTS` + `AddColumnIfMissing` for post-ship columns), seeds `ActivityCategories`, and stamps `SchemaVersion` (currently 5). Repositories in `Data/Repositories/`. **DB location is portable-first**: `aiusage.db` next to the exe when that dir is writable (so the whole folder is copyable), falling back to `%APPDATA%\AIUsage\` only under Program Files; an existing %APPDATA% DB is copied over once (with `-wal`/`-shm`).

**Frontend** (`wwwroot/`): classic scripts + globals — **no ES modules** (they don't load over `file://` in WebView2). `app.js` is a hashchange router; each view self-registers on `window.Views` (`window.Views.<name> = ...`) and exposes `render(container)`. Shared helpers live on `window.App` (`toast`, `esc`, `fmtNum`, `fmtDate`, `refresh`, `exportExcel`). Chart.js is **vendored** at `wwwroot/lib/chart.umd.js` (no CDN — corporate network blocks it and the app runs offline). Dashboard charts use a fixed category→color palette (color follows the activity category, not its rank).

**Embedded web assets** (`WebAssets.cs`): `wwwroot/**` is compiled in as embedded resources (`LogicalName=web/...`) so the published exe is one self-contained file. On startup they're extracted to `%LOCALAPPDATA%\AIUsage\web` and loaded over `file://` (keeps `crypto.randomUUID` working). **Extraction overwrites on every launch**, so editing `wwwroot` and re-running (or re-launching the exe) picks up changes. Dev fallback: on-disk `wwwroot` next to the exe if no embedded resources.

**JIRA** (`Jira/`): read-only JIRA Cloud REST. Token is DPAPI-protected at rest (`SettingsStore.SetProtected`, `dpapi:` prefix; non-Windows falls back to `plain:`) and write-only in the UI. The DPAPI token does NOT survive copying the folder to another machine/user — it degrades to "not set" by design. Fetches are lazy/background on link creation (`JiraSync.TryFetchInBackground`). **Never paste a real JIRA token into Claude** — use `--set jira_token` or the Settings UI. `FetchIssueAsync` also fetches the description and flattens the ADF (Atlassian Document Format) tree to plain text.

**Live Code page** (`Terminal/`, `Bridge/Handlers/LiveCodeHandlers.cs`, `wwwroot/js/views/livecode.js`): an interactive Claude Code session embedded in the app. `ConPtySession` hosts a shell in a pseudo-console — via the **Porta.Pty** library, NOT raw ConPTY: a hand-rolled `CreatePseudoConsole` implementation ran the child fine but conhost wouldn't stream output continuously on this Windows build (only flushed on resize/close), so we use the library (see PROGRESS.md). Output bytes stream to the WebView over the event channel as `pty.output` and render in **xterm.js** (vendored at `wwwroot/lib/xterm.*`, same no-CDN constraint as Chart.js); keystrokes go back via `pty.input`. On start, the chosen shell (`ShellResolver`: PowerShell, or Git Bash with fallback) is spawned in the selected folder, then a `claude …` command is *typed in* to work the selected ticket (built in `BuildClaudeCommand`, flattened to one line + shell-quoted). `ANTHROPIC_API_KEY` is stripped so Claude Code uses subscription auth (Porta.Pty inherits the parent env and ignores dict removal, so `ConPtySession` also unsets the var in this process). Confirmations: `--permission-mode acceptEdits` (auto-approve) or `bypassPermissions` (behind a danger confirm), plus a best-effort `PromptWatcher` that injects Enter on detected prompts. The metrics panel (`livecode.metrics`) shows tokens (from the DB) and a live context-window estimate (`SessionAggregator.LastContextTokens`).

## Conventions & gotchas

- **`net10.0`**, nullable + implicit usings enabled. Namespaces mirror folders (`AIUsage.Bridge.Handlers`, etc.).
- Ticket keys are validated against `^[A-Z][A-Z0-9]{1,9}-\d{1,6}$` and uppercased. Auto-links carry a `source`; manual/confirmed links are preserved when the allowlist purge runs (`SettingsHandlers.PurgeDisallowedAutoLinks`).
- Headline token figures **exclude cache-read tokens** by design.
- Do NOT use Photino `ShowSaveFile` off the UI thread — it returns null (reports a false "cancelled"). Excel export (`Export/XlsxWriter.cs`, minimal hand-rolled OOXML, no new dependency) saves directly to Downloads and reveals via `explorer /select`.
- The startup background scan only re-renders the input-free dashboard, never a view the user may be typing into (re-render wipes form state via `innerHTML`).
- **ConPTY streaming**: use `Porta.Pty`, not raw `CreatePseudoConsole` — the raw path doesn't stream continuously on this machine (see the Live Code paragraph). Stripping a child env var also unsets it in the app process (Porta.Pty inherits parent env); only `ANTHROPIC_API_KEY` is stripped today, which the app never reads.
- Claude Code encodes a session's cwd into its `~/.claude/projects/<name>` folder by replacing `:`, `\`, `/` with `-`. Live Code launches `claude --session-id <guid>` so `FindActiveTranscript` reads exactly `<guid>.jsonl` — matching by "newest file in the dir" is wrong because other concurrent Claude Code sessions (even this one) write to the same folder.
- Git Bash is resolved from the Git-for-Windows install paths, **never** a bare `bash.exe` on PATH — on Windows that's `C:\Windows\System32\bash.exe` (the WSL launcher), which fails with `execvpe(/bin/bash)` when there's no distro (`ShellResolver.IsSystemShim`).

## Companion docs — keep them in sync

- **`.claude/STRUCTURE.md`** — the detailed file-by-file inventory, full bridge-action catalog, DB schema, and settings keys that this file deliberately omits. **When you add/remove/rename/repurpose a file, bridge action, DB table/column, or settings key, update `.claude/STRUCTURE.md` AND this `CLAUDE.md` in the same change.**
- **`PROGRESS.md`** — the source of truth for project state, decisions, and the first-run checklist. **Update it after every meaningful change** so the project can be resumed from it; it's far more detailed than this file for feature history. Read it before starting work.
