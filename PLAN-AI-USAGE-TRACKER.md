# PLAN — AI Usage Tracker (Photino.NET Desktop App)

## Purpose

Track AI usage across JIRA work: **which tickets** were done with AI assistance, **what the AI did** (generated code, tests, debugging, review...), and **charts** of those relations. Lightweight, multiplatform desktop app built with Photino.NET in `C:\Projects\AIUsage`. Primary target: Windows 11, .NET 8.

## Confirmed decisions

1. **Hybrid capture**: automatically parse Claude Code session transcripts on this machine, plus manual log entries (pick ticket, describe AI activity).
2. **Single-user, local-only**: SQLite at `%APPDATA%\AIUsage\aiusage.db`. No server, no team sync.
3. **Read-only JIRA Cloud REST**: enrich ticket keys with summary/status/issue-type; API token configured in settings.
4. **Dashboard leads with both**: tickets-with-AI-involvement AND AI activity volume/breakdown.

## Key research finding (verified against real transcripts on this machine)

Transcripts live at `C:\Users\aaron\.claude\projects\<sanitized-cwd>\<sessionId>.jsonl`, one JSON object per line. Useful fields:

- All message lines: `sessionId`, `timestamp` (ISO 8601), `cwd`, `gitBranch`, `version`, `isSidechain`
- `assistant` lines: `message.model`, `message.usage.{input_tokens, output_tokens, cache_creation_input_tokens, cache_read_input_tokens}`, `tool_use` content blocks with tool `name` (Read, Edit, Write, Grep, Glob, Bash, PowerShell, Agent, ...)
- `user` lines: `message.content` — string (real prompt) or array of `tool_result` blocks
- `ai-title` lines: `aiTitle` — human-readable session title (used in review-queue UI)

**Empirical constraint:** `gitBranch` is `"HEAD"` in ~94% of lines on this machine (detached worktrees), but **33 of 71 sessions contain ticket keys (e.g. `SFTY-1164`) in user prompt text**. Ticket inference must therefore check branch → cwd → prompt text, with a configurable project-key allowlist (e.g. `SFTY`) to eliminate false positives like `UTF-8`.

## Solution structure

Single project `AIUsage.csproj` (`net8.0`, WinExe; NuGet: `Photino.NET`, `Microsoft.Data.Sqlite`, `System.Security.Cryptography.ProtectedData`). No ORM — hand-written SQL. Frontend: static `wwwroot` (vanilla JS, hash-routed views) + vendored `chart.umd.js` (Chart.js v4; Photino loads local files only, no CDN).

```
C:\Projects\AIUsage\
├── Program.cs                      # PhotinoWindow setup + message router wiring
├── Bridge\MessageRouter.cs         # {id, action, payload} → {id, ok, data|error}
├── Bridge\Handlers\                # one handler class per action group
├── Data\Db.cs                      # connection factory, PRAGMA journal_mode=WAL
├── Data\Migrations.cs              # idempotent CREATE TABLE IF NOT EXISTS + schema_version
├── Data\Repositories\              # SessionRepo, TicketRepo, ManualEntryRepo, LinkRepo
├── Scanner\TranscriptScanner.cs    # directory walk + incremental offsets
├── Scanner\SessionAggregator.cs    # per-line JSON → per-session aggregate (ALL schema mapping isolated here)
├── Scanner\TicketKeyInferrer.cs    # regex [A-Z][A-Z0-9]+-\d+ + source priority + allowlist
├── Jira\JiraClient.cs              # HttpClient, basic auth, GET issue
├── Settings\SettingsStore.cs       # settings table + DPAPI token protection
└── wwwroot\
    ├── index.html, css\app.css
    ├── js\bridge.js                # promise-based sendRequest(action, payload) over window.external
    ├── js\app.js + js\views\{dashboard,sessions,manual,tickets,settings}.js
    └── lib\chart.umd.js            # vendored Chart.js v4 (downloaded once during M1 setup)
```

**Bridge protocol**: JS calls `window.external.sendMessage(json)` with `{id: crypto.randomUUID(), action, payload}`; C# `RegisterWebMessageReceivedHandler` dispatches on `action`, replies via `SendWebMessage` with `{id, ok, data|error}`; JS resolves a pending Promise by id.
Actions: `scan.run`, `sessions.list/assignTicket/dismiss`, `manual.create/list/delete`, `tickets.list/sync/fetch`, `settings.get/set`, `stats.dashboard`.

## Data model (SQLite)

- `Sessions(id PK = sessionId, file_path, project_dir, git_branch, title, model, started_at, ended_at, input_tokens, output_tokens, cache_creation_tokens, cache_read_tokens, edit_count, write_count, read_count, bash_count, other_tool_count, user_message_count, cc_version, review_state DEFAULT 'pending')` — `review_state`: `pending | linked | not_ticket_related`
- `ScanState(file_path PK, last_offset, last_mtime, last_size)` — incremental scans
- `Tickets(key PK, summary, status, issue_type, last_synced, fetch_failed DEFAULT 0)`
- `SessionTicketLinks(session_id, ticket_key, source, inferred_from, category_id NULL, PK(session_id, ticket_key))` — `source`: `auto | manual | confirmed`; `inferred_from`: `branch | cwd | prompt_text`; `category_id` = user override of inferred activity
- `ManualEntries(id PK AUTOINCREMENT, ticket_key, entry_date, category_id, description, tool_used, created_at)`
- `ActivityCategories(id PK, name UNIQUE)` — seed: generated code, wrote tests, refactored, debugged, reviewed, wrote docs, investigated
- `Settings(key PK, value)` — jira_site_url, jira_email, jira_token (DPAPI-protected), scan_paths, project_key_allowlist, backfill_from

Derived "what the AI did" (computed, not stored): edit+write dominant → "generated/modified code"; read/grep dominant → "investigated". User overrides via category on the link or a ManualEntry. **De-dup rule:** a ManualEntry for the same ticket whose `entry_date` falls within a session's start/end supersedes that session's inferred category in activity charts (prevents double counting).

## Transcript scanner

Runs on startup (background, non-blocking) + "Scan now" button.

1. Enumerate scan roots (default `%USERPROFILE%\.claude\projects`, configurable) → top-level `*.jsonl` only. Session-named subdirectories are sidechain/subagent transcripts — skipped in v1 (known token undercount); `isSidechain: true` lines skipped defensively.
2. **Incremental**: compare `(mtime, size)` to `ScanState`; grown → seek `last_offset`, parse new lines only and **accumulate deltas into the existing session row** (all counters are additive; `started_at`/`ended_at` are min/max); shrunk or rewritten → reparse from 0, replacing that file's contribution.
3. Per line `try JsonDocument.Parse catch skip` — one malformed line never kills a scan; unknown `type` values ignored (forward compatibility with Claude Code format changes).
4. Aggregate per `sessionId`: min/max timestamps, token sums from `assistant.message.usage`, tool_use counts by name (`PowerShell` → bash_count), `ai-title`, count of string-content user lines.
5. **Ticket inference** (`[A-Z][A-Z0-9]+-\d+`, allowlist-filtered):
   - `gitBranch` (unless `HEAD`/`main`/`master`) → source `auto`, `inferred_from = branch` (high confidence)
   - `cwd` path segments → `auto` / `cwd`
   - user prompt text (string `message.content` only — never tool_results) → `auto` / `prompt_text` (lower confidence); multiple keys ⇒ multiple links (multi-ticket sessions)
6. ≥1 auto link → `review_state = 'linked'`; zero links → `pending` review queue. User actions: assign ticket (source `manual`), confirm auto link (→ `confirmed`), or mark `not_ticket_related`.

## JIRA client

- `GET {site}/rest/api/3/issue/{key}?fields=summary,status,issuetype` with `Authorization: Basic base64(email:token)`.
- 200 → upsert Tickets; 404 → `fetch_failed = 1` (row kept, dead-key badge in UI); 401 → "check credentials" toast; timeout/offline → non-fatal, ticket stays unenriched.
- Lazy fetch on first reference + manual "Sync tickets" button (sequential with small delay; volumes are tiny).
- Optional denominator for the "share of tickets AI-assisted" chart: `POST /rest/api/3/search/approximate-count` with JQL (default `assignee = currentUser() AND resolved >= -90d`), user-editable in Settings. (The legacy `/rest/api/3/search` `total` field is deprecated on JIRA Cloud.) If the call fails, hide the chart rather than error.
- **Token security**: DPAPI (`ProtectedData`) on Windows; plaintext-with-warning fallback on Linux/macOS in v1. Token never sent to the JS side — Settings UI is write-only, shows only "token set: yes/no".

## UI pages (hash-routed, single index.html)

1. **Dashboard** — stat tiles (sessions this month, distinct tickets touched, tokens this month, pending-review count) + charts.
2. **Sessions** — table (title, date, project, model, tokens, tool-mix mini-bar, linked tickets); tabs All / Needs review / Not ticket-related; row actions assign / confirm / dismiss.
3. **Manual entry** — ticket key (regex-validated, triggers lazy JIRA fetch), date (default today), category dropdown, description, tool used (Claude Code / Copilot / Cursor / ChatGPT / free text — the only way non-Claude tools get counted).
4. **Tickets** — key, summary, status, type, session count, manual-entry count, last synced, dead-key badge; "Sync all" button.
5. **Settings** — JIRA site URL / email / token + "Test connection", scan paths, project-key allowlist, backfill cutoff date.

## Charts (Chart.js, data shaped in C# via `stats.dashboard`)

1. **AI-assisted tickets over time** — weekly bar: distinct ticket keys from links + manual entries.
2. **Activity category breakdown** — doughnut: manual categories + inferred session categories (with the de-dup rule above).
3. **Top-N tickets by tokens / sessions** — horizontal bar with metric toggle.
4. **Share of tickets AI-assisted** — doughnut; hidden if the JIRA denominator is unavailable.
5. **Ticket-type × activity matrix** — stacked bar; degrades to an "Unknown type" bucket when issue types aren't synced.

Headline token figures exclude `cache_read_input_tokens` (kept in DB; they distort totals).

## Milestones & verification

- **M1 — Skeleton + bridge (~½–1 day)**: csproj, Photino window, wwwroot shell, vendored Chart.js, `ping` action round-trip, migrations run.
  *Verify:* `dotnet run` opens a WebView2 window; a ping button shows a pong from C#.
- **M2 — Scanner + DB (~1–2 days)**: scanner, aggregator, inferrer, Sessions page, review queue.
  *Verify on real data:* scan finds the ~71 sessions under `C:\Users\aaron\.claude\projects`; `SFTY-*` links inferred from prompt text; the `SFTY-1230-...` branch session links via branch; re-scan is fast (offsets) and idempotent (no duplicate rows or inflated totals).
- **M3 — Manual entry + JIRA (~1 day)**: ManualEntries CRUD, JiraClient, DPAPI token, Tickets page.
  *Verify:* entry persists across restart; with a real token, `SFTY-1230` fetch populates summary/status/type; a garbage key shows the dead-key badge without crashing; token is not plaintext in the DB (inspect with sqlite3).
- **M4 — Dashboard (~1 day)**: stats queries, 5 charts, stat tiles.
  *Verify:* charts render fully offline (no CDN); numbers cross-check against the Sessions/Tickets pages; an empty DB shows friendly placeholders.

## Blockers / risks

1. **Undocumented transcript format** — 21 distinct Claude Code versions already present in the local store; internal fields (`gitBranch`, `message.usage`) can drift between releases. *Mitigation:* tolerant parser (skip unknown lines/fields, never fail a scan), record `cc_version` per session for diagnosing drift, isolate all schema mapping in `SessionAggregator.cs`.
2. **Claude-Code-only auto-capture** — Copilot/Cursor/ChatGPT usage is invisible to the scanner; charts systematically undercount unless manual entries are diligent.
3. **Ticket-mapping ambiguity (confirmed empirically)** — branches are mostly detached `HEAD` on this machine; prompt-text inference covers ~46% of sessions and has false-positive risk (allowlist mitigates); multi-ticket sessions attribute full token totals to every linked ticket (double counting in per-ticket charts — footnoted in v1).
4. **Cross-platform token security** — DPAPI is Windows-only; Linux/macOS fall back to plaintext in v1 (Keychain/libsecret out of scope).
5. **Photino non-Windows quirks** — WebKitGTK dependency install on Linux, conservative JS required for macOS WKWebView; irrelevant for the primary Windows 11 target but keep the JS conservative anyway.
6. **"What the AI did" heuristics are coarse** — tool counts show *how much editing* happened, not *what kind of work* (a debugging session and a feature session look similar). Inference is a default the user overrides; manual categories are the trustworthy signal.
7. **Sidechain/subagent transcripts skipped in v1** — token totals undercount agent-heavy sessions.

## Open questions (defaults chosen, revisit anytime)

1. **Backfill horizon** — default: scan everything in `.claude/projects`; a Settings cutoff date is available.
2. **Denominator JQL** for "share of tickets AI-assisted" — default `assignee = currentUser() AND resolved >= -90d`, user-editable; the *right* definition is a business question.
3. **Multi-machine usage** — out of scope (local-only DB); state it in the README.
4. **Multi-ticket session token attribution** — v1 duplicates totals per ticket with a footnote; per-ticket charts can toggle to session-count, which doesn't double count.
