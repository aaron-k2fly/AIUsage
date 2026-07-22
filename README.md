# AI Usage Tracker

A single-user, **local-only** desktop app that tracks which JIRA tickets you worked on with AI
assistance — how much, and what the AI actually did. It scans your Claude Code session
transcripts (and accepts manual entries), infers ticket keys, enriches them from JIRA
(read-only), and visualizes the relationships with charts.

No server, no team sync, no Electron, no cloud. Everything stays on your machine in a local
SQLite file.

> **Platform:** Windows (Photino.NET + WebView2). Built and run with .NET 10.

---

## What it does

- **Scans Claude Code transcripts** (`%USERPROFILE%\.claude\projects\**\*.jsonl`) incrementally and
  infers the JIRA ticket each session worked on (from the git branch → working directory → prompt
  text, filtered by a project-key allowlist).
- **Dashboard** — token usage per week, AI-assisted tickets per week, a breakdown of *what* the AI
  did (edit/write/read/shell/other), Claude model usage over time, top tickets, and ticket-type ×
  activity — plus **Automation & extensions** charts (sub-agents, skills, MCP servers, and hooks
  used across all sessions).
- **Sessions** — a review queue of detected sessions; confirm, reassign, or dismiss the inferred
  ticket link. Each session opens a **detail page** with per-tool and per-model breakdowns, reply /
  tool-call counts, an agent / active / idle time split, token cost, and the agents, skills, MCP
  tools, and hooks that session used.
- **Manual entry** — log AI-assisted work that wasn't captured automatically.
- **Tickets** — a JIRA-enriched ticket list (status, type, project, sprint, priority, last
  updated), with status colouring, an "AI-touched" filter, and on-demand import of more tickets
  from JIRA.
- **Export to Excel** — one-click `.xlsx` export of Sessions, Manual entries, and Tickets.
- **Read-only JIRA integration** — enrich ticket keys with summary/status/type/etc. The app never
  writes to JIRA.
- **Live Code** — drive interactive Claude Code sessions right inside the app, kicked off from a
  selected ticket (see below).

---

## Privacy & data

- **Local-first.** All data lives in a portable `aiusage.db` (SQLite) next to the executable,
  falling back to `%APPDATA%\AIUsage\` only when the install directory isn't writable. It is
  git-ignored and never leaves your machine.
- **JIRA token** is stored **DPAPI-encrypted** for your Windows user (write-only in the UI) and does
  not survive copying the folder to another machine or user — by design it degrades to "not set".
- **JIRA access is read-only.**
- Headline token figures exclude cache-read tokens.

---

## Live Code sessions

The **Live Code** page runs interactive Claude Code sessions inside the app, under your Claude
**subscription** auth (`ANTHROPIC_API_KEY` is stripped so you're never billed for metered API
usage), started from a selected JIRA ticket. Starting a session auto-links the ticket to the work.

- **A real terminal** — the chosen shell (PowerShell or Git Bash) is hosted in a Windows
  pseudo-console (ConPTY) and streamed to an embedded [xterm.js](https://xtermjs.org/) terminal;
  a `claude` session is launched on the ticket.
- **Multiple sessions as tabs** — each tab is an independent session with its own terminal,
  controls, and token/context readout. A sidebar dot shows whether any session is running (green /
  red), and hovering it lists the live sessions.
- **Lifecycle** — Stop (kills the process tree, keeps the session resumable), Resume, and Reset
  (restart fresh on the same ticket). Sessions survive navigating away and back.
- **Same-folder safeguard + git-worktree isolation** — if two tabs would run agents in the same
  folder at once, the app warns you and can run the new session in an isolated `git worktree`
  (auto-removed on close only if it's clean), so concurrent agents don't collide.
- **Agents** — pick an agent from `.claude/agents` (project + user), or point to a custom agent
  `.md` file.
- **Resume Sessions** — browse the existing Claude Code sessions for a folder (labelled by their
  first prompt) and resume any one of them.
- **Metrics** — per-session tokens (with cache shown separately) and context-window usage
  (used-of-max), plus your plan, weekly tokens, rolling **usage-limit bars** (5-hour session &
  7-day week windows, from Claude's own `/usage` data), and the top active Claude Code sessions.

> Live Code is **Windows-only** (it relies on ConPTY) and requires the
> [Claude Code CLI](https://claude.ai/code) to be installed and on `PATH`.

---

## Getting started

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/) (to build/run from source)
- **WebView2 Runtime** (pre-installed on Windows 11)

### Run from source
```bash
dotnet run          # launch the app (Photino window)
dotnet build        # build only
```

On first launch, open **Settings** and add your JIRA site URL, email, and an API token
(create one at id.atlassian.com → Security → API tokens) to enable ticket enrichment.

### Headless / diagnostic commands
```bash
dotnet run -- --scan                 # run the transcript scanner, print counts
dotnet run -- --sql "SELECT ..."     # read-only query (PRAGMA query_only=ON enforced)
dotnet run -- --set <key> <value>    # write a Settings row (use jira_token for the DPAPI secret)
dotnet run -- --route <page>         # open directly on a page (dashboard|sessions|manual|tickets|livecode|settings)
```

### Build a single-file, self-contained executable
```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false
```
Produces a single `AIUsage.exe` (~37 MB) that runs on any Windows 11 machine (WebView2 only; no
.NET install needed). The web assets are embedded and self-extracted at startup.

---

## Architecture

A Photino.NET host (.NET 10) opens an OS-native WebView2 window over a vanilla-JS frontend, and the
two halves talk over Photino's string message bus as JSON.

- **Message bridge** — `Bridge/MessageRouter.cs`: request `{ id, action, payload }` → response
  `{ id, ok, data | error }`. Handlers live in `Bridge/Handlers/*.cs`, one static `Register` per
  domain.
- **Scanner** (`Scanner/`) — `TranscriptScanner` walks the append-only JSONL transcripts
  incrementally (by remembered byte offset, `BEGIN IMMEDIATE` transactions so concurrent scanners
  never double-count). `SessionAggregator` is the single owner of the (undocumented) transcript
  schema; `TicketKeyInferrer` extracts ticket keys.
- **Data** (`Data/`) — raw ADO.NET over `Microsoft.Data.Sqlite` (no ORM), WAL mode, idempotent
  migrations.
- **Frontend** (`wwwroot/`) — classic scripts + globals (no ES modules — they don't load over
  `file://` in WebView2); a hashchange router with one self-registering module per page. Chart.js
  is vendored locally (no CDN).
- **JIRA** (`Jira/`) — read-only JIRA Cloud REST; DPAPI-protected token.
- **Live Code** (`Terminal/`, `Bridge/Handlers/LiveCodeHandlers.cs`) — hosts shells in a
  pseudo-console (ConPTY via Porta.Pty), streams output to xterm.js over an event channel, and
  manages per-tab sessions, git-worktree isolation, and session resume.

Two companion docs carry the details:
- `CLAUDE.md` — big-picture architecture and conventions.
- `.claude/STRUCTURE.md` — file-by-file inventory, bridge-action catalog, DB schema, settings keys.

---

## Tech stack

.NET 10 · Photino.NET · WebView2 · Microsoft.Data.Sqlite · vanilla JS · Chart.js (vendored) ·
xterm.js (vendored) · Porta.Pty (ConPTY, for Live Code) ·
DPAPI (`System.Security.Cryptography.ProtectedData`).

---

*Personal tooling — provided as-is, for a single user's local use.*
