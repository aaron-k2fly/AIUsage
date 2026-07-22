# PROGRESS — AI Usage Tracker

_Last updated: 2026-07-22_

## 2026-07-22: Live Code — add **Fable** to the model dropdown

The Live Code model picker only listed Opus/Sonnet/Haiku (+ Default). Added **Fable** (`claude-fable-5`,
Claude 5 family). Two hardcoded spots drive this and both were updated:
- **`wwwroot/js/views/livecode.js`** — `MODELS` array now includes `{ value: 'fable', label: 'Fable' }`.
- **`Bridge/Handlers/LiveCodeHandlers.cs`** — both `--model` whitelists (`BuildClaudeCommand`,
  `BuildResumeCommand`) now accept `"fable"`; without this the backend silently dropped the flag and
  launched with the session default.
- `SessionAggregator.ContextWindow` already defaults non-Haiku models to 1M, so Fable's context bar is
  correct with no logic change (doc comment updated to mention fable). Verified: build clean.

## 2026-07-21: Dashboard — **Automation & extensions** charts — branch `SESSION-DETAIL`

Added four dashboard bar charts (below the existing charts, under an "Automation & extensions" header)
showing, across ALL sessions: **Sub-agents used**, **Skills used**, **MCP servers used**, **Hooks fired**.

**Design decision — persist, don't re-parse.** This data lives only in the transcripts (the session
detail page extracts it per-session on demand). Re-parsing every transcript on each dashboard load
would be far too slow, so the counts are **persisted** in a new `ToolUsage` table and the dashboard
stays a fast DB query. Chosen approach (**B**): `ToolUsage` is **set-semantics**, derived from a
**full-file parse** (`SessionAggregator.ReadToolUsage`), and kept **decoupled from the incremental
token-counting pipeline** (which stays untouched — no reset, no ScanState games, so existing
token/ticket data is never disturbed).

- **Schema v6**: new `ToolUsage(session_id→Sessions ON DELETE CASCADE, category, name, count)` PK
  (category ∈ agent/skill/mcp/hook) + `idx_toolusage_cat`.
- **Scanner**: on each new/changed file, after the token upsert, `ToolUsageRepo.ReplaceForFile` replaces
  that file's rows (delete-then-insert, FK-guarded). A one-time **backfill** (flagged by the v6 migration
  via `toolusage_backfill_pending`, since pre-v6 sessions have no rows) re-parses every transcript for
  ToolUsage on the next scan, then clears the flag.
- **Extraction** (`ReadToolUsage`): agents ← Agent/Task `subagent_type`; skills ← Skill `skill`; MCP ←
  the **server** in `mcp__server__tool` names; hooks ← `hook_success`/`hook_error` attachment lines.
- **stats.dashboard** returns `agentUsage`/`skillUsage`/`mcpUsage`/`hookUsage` (top-12 name→total each).
- **dashboard.js**: `renderExtCharts` draws four horizontal bar charts (one hue each via `EXT_COLORS`);
  long labels truncate on the axis with the full name in the tooltip; the whole section hides if there's
  no such data.

Verified: build clean; `dashboard.js` `node --check` clean; a scan backfilled all 106 sessions
(`--sql` shows agents e.g. Explore 52 / general-purpose 41, skills brainstorming 36, MCP Claude_Preview
65 / Atlassian 31, hooks SessionStart:startup 96); a `PrintWindow` screenshot of the dashboard confirms
all four charts render correctly with truncated labels and per-category colours. (Screenshot mechanics
note: on this mixed-DPI multi-monitor setup, `PrintWindow` with `PW_RENDERFULLCONTENT` is the reliable
way to capture the Photino window — `CopyFromScreen` at `GetWindowRect` coords caught the wallpaper when
the window wasn't foreground.)

## 2026-07-21: Sessions — new session **detail** page — branch `SESSION-DETAIL`

Clicking a session on the Sessions list now navigates to a detail page (`#session/<id>`) modelled on the
reference mockup, with a **← Back** link (`history.back()`, falling back to `#sessions`).

**Routing.** `app.js`'s `navigate()` now splits the hash at the first `/` — `#session/<id>` → route
`session`, `param = <id>` — and passes `param` to `view.render(container, param)` (existing views ignore
the extra arg). The `session` route keeps the "Sessions" sidebar item highlighted.

**Backend.** New action `sessions.detail {sessionId}` (`SessionHandlers`): loads the stored row via new
`SessionRepo.Get(conn, id)` (row + ticket links + explicit category name), then does an **on-demand deep
re-parse** of that one transcript via new `SessionAggregator.ReadDetail(file, sessionId)` — same
single-file pattern as `ReadLive`/`SubagentTokens`, no schema change. `ReadDetail` → `SessionDetail`
gives exact per-tool counts (`ToolCounts`, full names incl. `mcp__…`), per-model token usage
(`Models`→`ModelUsage`), reply/prompt/tool-call counts, and an **Agent/Active/Idle time split**: each
inter-event gap is classified — before a human prompt = Active, before an assistant reply or tool-result
= Agent, any gap **>5 min** = Idle — and the three partition (ended−started) exactly. The handler folds
in `SubagentTokens`, derives the category (link category else edit-vs-read guess, matching the
dashboard), and falls back to stored counters if the transcript file is gone (`transcriptAvailable:false`).

**Frontend.** New `wwwroot/js/views/session.js` renders a 2-column card layout: **Overview** (started/
ended, time split, primary model, category, review, total tokens with in/out/cache split + a
"+ sub-agents" note, prompt/reply/tool-call counts), **Tools** (one coloured segment per tool + a
name×count list), **Models** (per-model output bar), **Tickets** (reuses `Views.sessions`
confirm/unlink/assign), **Token cost**. Cost is derived **in the view** from a model-family `$/Mtok`
rate table (opus/sonnet/haiku) → est. cost, cache-hit % (`cacheRead/(cacheRead+cacheCreation)`), output
share, and cache-read/write·output·input breakdown bars, with a rates footnote for the primary model.
CSS added under "Session detail page" in `app.css`; the 2-column `.detail-grid` uses
`repeat(2, minmax(0,1fr))` + `min-width:0` panels and stacks below 820px.

Also: `#content` got `min-width: 0` (defensive against the flexbox min-content overflow trap) and a new
`--detailtest <sessionId>` CLI verb prints the deep re-parse headlessly.

Verified: `dotnet build` clean; `node --check` on the three touched JS files clean; `--detailtest` on
several sessions (opus + sonnet, linked + unlinked) shows correct per-tool/per-model/timing/cost;
DPI-aware window screenshots of two sessions confirm the layout matches the mockup (cost math checks out
— e.g. opus session out 1.9M × $75/Mtok ≈ $145). **Note the earlier "right column clipped" scare was a
DPI-unaware screenshot artifact (PowerShell captured only part of a 300%-scaled window), not a layout
bug — the 2-column grid never overflowed.** Not committed — left on branch `SESSION-DETAIL` for the user.

### Follow-up (same branch): **Agents & extensions** panel + Tickets moved last

Added a fifth card, **Agents & extensions**, with four labelled chip groups showing *which*
sub-agents / MCP tools / skills / hooks a session used (empty groups show "—"; all-empty shows a note).
All recovered from the transcript in `ReadDetail` (no schema change):
- **Agents** ← `Agent`/`Task` tool_use `input.subagent_type`.
- **Skills** ← `Skill` tool_use `input.skill`.
- **Hooks** ← `type:"attachment"` lines with `attachment.type` `hook_success`/`hook_error`, keyed by
  `attachment.hookName` (e.g. `SessionStart:startup`; fallback `hookEvent`).
- **MCP tools** ← the `mcp__server__tool` entries already in `ToolCounts`, grouped by server in the
  handler (`mcps` list). Note remote/claude.ai MCP servers show as a UUID server name (that's the real
  name in the transcript — no reliable UUID→friendly map).

`SessionDetail` gained `Agents`/`Skills`/`Hooks` dicts; `sessions.detail` now also returns
`agents`/`skills`/`hooks`/`mcps`; `--detailtest` prints them. Per the user's request the **Tickets**
panel (least info) moved to **last**, so the grid is now Overview | Tools/Models · Agents&extensions |
Token cost · Tickets.

Verified: build clean; `session.js` `node --check` clean; `--detailtest` on a session using all four
(agents `Explore ×3`, MCP atlassian+registry, skills, `SessionStart` hooks) shows correct extraction;
DPI-aware screenshot confirms the panel + reordering render cleanly.

## 2026-07-18: Live Code — session Tokens = dashboard formula + separate Cache field — branch `live-code-enhancement`

The session "Tokens" readout looked "too much": it used `input + output + cache_creation` while the
**dashboard** (tokens this month/week, top tickets, `weekTokens`) uses `input + output` only (all cache
excluded, per StatsHandlers). So the session number was ~5–25× the dashboard for the same work (e.g.
"Injury Summary CSV": dashboard 3,636 vs session 95,768).

**Resolution (user decision after review):** make session Tokens use the **same formula as the
dashboard** (`input + output`, sub-agents still included), and show **cache as a separate field**:
- `livecode.metrics` now returns `sessionTokens = mainTokens + agentTokens` where both are `input +
  output` (dashboard-consistent), plus `cacheTokens` (`cache_creation + cache_read`, incl. sub-agents)
  and its `cacheCreation`/`cacheRead` split.
- `SessionAggregator.SubagentTokens` now returns `SubagentUsage{InOut, CacheCreation, CacheRead}`
  (was a single cache-inclusive long) so the handler can split Tokens vs Cache for sub-agents too.
- UI (`livecode.js`): the "This session" strip is now `Tokens X · Cache Y · Context Z`. Tokens keeps the
  "incl. N agents" suffix + Main/agents tooltip; Cache has a "created … · read …" tooltip (read is
  re-counted each turn, so it's usually the bulk).

Now the session Tokens equals the dashboard figure for the same session (main part) and cache is
transparent but never inflates the headline. **Dashboard token method (answer to the other question):**
`SUM(input_tokens + output_tokens)` — excludes ALL cache (creation + read).

Verified: `dotnet build` clean; `livecode.js` `node --check` clean; DB spot-check confirmed session
Tokens(main) == dashboard `input+output` per session and cache = created+read shown separately.

## 2026-07-18: Live Code — session/week usage-limit progress bars — branch `live-code-enhancement`

Added two rolling usage-limit bars (SESSION = 5-hour window, WEEK = 7-day window) to the empty space in
the Live Code bottom panel, beside **Plan** and **Tokens — this week**, above Active sessions — ported
from the CommandCenter project's implementation.

**Data source** (the crux): Anthropic's `https://api.anthropic.com/api/oauth/usage` endpoint — the same
data Claude Code's `/usage` shows. Authed with the OAuth access token from
`~/.claude/.credentials.json` (`claudeAiOauth.accessToken`) + header `anthropic-beta: oauth-2025-04-20`.
The response's `five_hour.utilization`/`resets_at` → SESSION bar, `seven_day.*` → WEEK bar. Percentages
are **server-computed** (0–100) — no local quota table / plan-specific math (the server knows the plan
from the token).

**Backend** — new `Platform/ClaudeUsage.cs`:
- `ReadAsync()` → `ClaudeUsageInfo { SessionPct, SessionResetsAt, WeekPct, WeekResetsAt }`. Reads the
  token (skips if `expiresAt` passed), GETs the endpoint, parses the two windows.
- Token is used only to sign the request — never stored, logged, or returned. Consistent with
  `ClaudeAccount` reading `~/.claude.json` for the plan.
- Cached 5 min (SemaphoreSlim-guarded so concurrent pollers share one fetch); best-effort — any failure
  (no token / expired / offline / non-2xx) returns the last-good value or null.
- New bridge action `livecode.usage` → `{available, sessionPct, sessionResetsAt, weekPct, weekResetsAt}`;
  `available:false` when signed out/offline so the page hides the bars.

**Frontend** (`wwwroot/js/views/livecode.js` + `app.css`):
- `#lc-usage` block added to the `.lc-metrics` flex row (fills the space beside Plan/Tokens).
- `usageRow(label, pct, resetsAt)` — clamp 0–100, threshold class (`≥95% crit` red / `≥80% warn`
  amber / else accent), `N% · resets …` suffix. `fmtReset()` shows just the time if the reset is today,
  else `Fri 07:00 am`.
- `pollUsage()` on init + every 60s (`G.usageTimer`, cleared in `teardownPage`); backend 5-min cache
  means the API is hit at most once per 5 min regardless of poll rate.
- CSS `.lc-usage`/`.lc-usage-row`/`.lc-bar-track`/`.lc-bar-fill(.warn/.crit)` in the app's light-theme
  palette (`--accent`/`--warn`/`--danger`, `--accent-soft` track).

**Verified**: `dotnet build` clean; `ClaudeUsage.Parse` unit-checked against the known endpoint shape
(session 25% / week 4% / reset times parsed; empty JSON → `HasAny=false` → bars hidden); `livecode.js`
`node --check` clean; headless-Edge render (real `app.css`) confirmed the bars fill the space beside
Plan/Tokens above Active sessions, with correct normal/warn/crit colouring and reset formatting.
**Not verified**: the live API call — this sandbox has no outbound network (curl → HTTP 000 even with the
sandbox off; a proxy blocks it), but the real Photino app reaches the endpoint just as Claude Code does.
Worth a manual GUI confirmation that real numbers appear.

## 2026-07-18: Live Code — Re-fetch tickets button + configurable ticket count — branch `live-code-enhancement`

Two related additions to the Live Code ticket picker:
- **↻ Re-fetch button** beside the ticket list (`wwwroot/js/views/livecode.js`, header now
  `.lc-tickets-head` flex row). Shown only when JIRA is configured; wired in `wireTabPanel`; the new
  `refetchTickets()` shows a busy state and re-calls `loadTickets()` (which hits `livecode.tickets`,
  always a live JIRA fetch — no cache). CSS `.lc-tickets-head`/`.lc-refetch` in `app.css`.
- **Configurable ticket count** — new setting `livecode_ticket_count` (default 3, clamped 1–20):
  - `Settings → Live Code` panel with a number input (`wwwroot/js/views/settings.js`); saved via
    `settings.set` (`livecodeTicketCount`, parsed + clamped 1–20) and read back by `settings.get`.
  - `LiveCodeHandlers.TicketCount()` reads/clamps the setting; `livecode.tickets` uses it for `.Take(n)`
    and oversizes the JIRA page (`maxResults = clamp(n*3, 25, 60)`) so status-filtering still leaves
    enough; `livecode.config` returns `ticketCount` so the picker label ("latest N assigned to you")
    reflects it.

Verified: `dotnet build` clean; settings round-trip via `--set`/`--sql` (key `livecode_ticket_count`
consistent across all four call sites); both JS files `node --check` clean; and a headless-Edge render
(same engine as WebView2) of the real `settings.js` + the Live Code ticket-header markup confirmed the
new Settings panel and the right-aligned ↻ Re-fetch button with the dynamic "latest 5" label. The live
in-app terminal/JIRA path still needs a manual GUI check (interactive login).

## 2026-07-18: Live Code — terminal sizing (taller container, no spurious scrollbars) — branch `live-code-enhancement`

Tweaked the terminal panel so the common case shows neither scrollbar (`wwwroot/css/app.css`,
`.lc-terminal`):
- **Taller container:** `height 42vh → 60vh`, `min-height 320px → 440px`, so more rows fit and the
  initial view (welcome + prompt) doesn't need vertical scroll.
- **No horizontal scrollbar:** host `overflow: auto → hidden`. The bar came from sub-pixel width
  rounding of the xterm canvas — xterm wraps to its own column count and never needs the host to
  scroll horizontally, so hiding host overflow removes it without clipping real content.
- **Vertical scrollbar only when needed:** xterm's vendored css forces `.xterm-viewport
  { overflow-y: scroll }` (always-visible bar). Overrode it with `overflow-y: auto` +
  `scrollbar-gutter: stable`. The stable gutter permanently reserves the 12px column — so FitAddon's
  column count stays consistent and the last chars never tuck under the bar (the reason the old code
  forced an always-on bar) — while the scrollbar itself only appears once there's real scrollback.
  This also renders the terminal slightly narrower (the reserved gutter).

Verified in headless Edge (same Chromium engine as WebView2) with the real vendored xterm + these CSS
rules: short content → no vertical + no horizontal scrollbar (full-width line fits); 200 lines →
vertical scrollbar appears, still no horizontal. Note: the live in-app terminal with real Claude
output needs an interactive login to exercise fully, so that path is left to manual GUI check.

## 2026-07-18: Live Code — session Tokens counter = distinct tokens processed, incl. sub-agents — branch `live-code-enhancement`

**Problem.** The per-tab "This session → Tokens" readout only counted the main agent. When a
session spawns sub-agents (the Task tool — e.g. `safety-developer` spawning `safety-plan-critic`),
their token usage was invisible, so the counter under-reported the true session cost.

**Why they were missed.** Claude Code writes each sub-agent to
`~/.claude/projects/<encoded-cwd>/<sessionId>/subagents/agent-*.jsonl`. Those lines carry the
**parent's** `sessionId` and are marked `isSidechain:true`, so (a) the scanner skips them
(`SessionAggregator.ParseLine` returns on `isSidechain`, and the session-named subdirs are skipped),
and (b) the metrics query (`WHERE file_path = <main transcript>`) never sees them.

**Fix (display-only, live readout).**
- `SessionAggregator.SubagentTokens(mainTranscriptPath)` — sums **processed** tokens (input + output +
  cache-creation; cache-read excluded) across every `agent-*.jsonl` found recursively under
  `<sessionId>/`, so nested sub-agents count too. Best-effort (IO/parse errors → partial sum).
- `livecode.metrics` now returns `mainTokens` + `agentTokens`; `sessionTokens = mainTokens + agentTokens`.
  The **main** query uses the same formula (`input + output + cache_creation_tokens`).
- Frontend shows the total with an "incl. N agents" muted suffix + a "Main X + agents Y" tooltip when
  `agentTokens > 0` (`wwwroot/js/views/livecode.js`).
- **Metric choice — "distinct tokens processed" (user decision 2026-07-18, after testing):** the first
  cut counted only input+output (dashboard headline convention), which under-reported so badly it looked
  broken — a codebase-reading sub-agent showed ~2k while actually processing hundreds of thousands of
  tokens. A middle iteration used the full billed total (all four types incl. cache-read), but cache-read
  re-counts the same cached prefix every turn and inflated the number. Final: **input + output +
  cache-creation** (cache-read excluded) — reflects real distinct content processed without the per-turn
  re-read inflation.
- **Scope:** the DB, dashboard, and `weekTokens` still exclude *all* cache *and* sub-agents (v1 design is
  unchanged) — only the Live Code session readout keeps cache-creation and re-adds sub-agents.

Verified against a real running session (SFTY-1634, one background sub-agent): main ≈ 96k + agent ≈ 86k =
≈ 182k. `dotnet build` clean.

## 2026-07-16: Live Code — agent/custom-agent lock + Resume Sessions picker — branch `LIVE-CODE-SESSION`

Two refinements. Design/plan:
`docs/superpowers/specs/2026-07-16-live-code-agent-lock-resume-sessions-design.md` /
`docs/superpowers/plans/2026-07-16-live-code-agent-lock-resume-sessions.md`.

1. **Agent ↔ Custom Agent exclusion** — selecting an Agent from the dropdown clears + disables the
   Custom Agent input (re-enabled when set back to `(none)`). `refreshControlLocks(t)` in
   `livecode.js` is now the single authority for control disabled-states.
2. **Resume Sessions** — a button beside Browse lists the working folder's existing Claude Code
   sessions and resumes a chosen one:
   - **Backend**: `Scanner/FolderSessions.cs` (`List(folder,max)` enumerates
     `~/.claude/projects/<encoded-cwd>/*.jsonl`, newest-first) + `SessionAggregator.FirstUserPrompt`
     (first string-content user prompt as the label). New actions `livecode.sessionsInFolder`
     (`{sessions:[{sessionId,label,updated}]}`) and `livecode.resumeSession` (types
     `claude --resume <id>` interactively via `BuildResumeSessionCommand` — no prompt).
   - **Frontend**: the button is disabled when the folder has no sessions (`loadFolderSessions`); the
     modal (`openResumeSessions`) lists label + time + short id; picking a row (`resumePickedSession`)
     confirms-replace if the tab is running, resumes into the active tab, sets `resumedPick`, and
     locks Shell/Model/Custom Agent until the session stops (stop/exit clear it).

Decisions: resume into the current tab (confirm-replace); interactive `--resume` (no auto-continue);
disable Resume Sessions when the folder has none; agent→custom-agent exclusion is one-directional.
Build clean (0/0); backend verified headlessly (`sessionsInFolder` returns real first-prompt labels
for the last-used folder; no errors). **GUI click-through pending at the machine.** Docs updated in
the same change.

## 2026-07-15: Live Code — same-folder warning + git-worktree isolation — branch `LIVE-CODE-SESSION`

Safeguard for the new multi-tab sessions: when two tabs would run agents in the **same working
folder** at once, warn the user and offer to isolate the new session in a git worktree. Design:
`docs/superpowers/specs/2026-07-15-live-code-same-folder-worktree-design.md`; plan:
`docs/superpowers/plans/2026-07-15-live-code-same-folder-worktree.md`.

- **`Terminal/GitWorktree.cs`** (new): `IsGitRepo`, `Create` (new branch `livecode/<ticket>-<hex>`
  in a sibling `<repo>-worktrees/…` off HEAD, returns the launch cwd), `TryRemoveIfClean` (removes
  worktree + branch only if `git status --porcelain` empty AND no commits beyond base). All git via
  `Process`; verified the exact sequence against a scratch repo (clean → removed; dirty → kept).
- **Backend** (`LiveCodeHandlers.cs`): `LiveSession.Worktree`; new `livecode.folderInfo`
  (`{isGitRepo}`); `StartTicketSession` honors an `isolation` param (`"worktree"` → create worktree,
  launch/transcript/auto-link use its cwd, store on the entry, return `{isolated, worktreePath,
  folder}`); `closeTab` runs `TryRemoveIfClean` and returns `{worktreeKept, worktreeReason,
  worktreePath}`; `StopSession` preserves `Worktree` (so Reset reuses it and close can clean up).
- **Frontend** (`app.js`, `livecode.js`, `app.css`): `App.choose` (multi-button promise modal); each
  tab tracks `activeFolder`/`isolated`; on Start, `conflictingTab()` (normalized path compare) detects
  another running tab in the same folder and `resolveIsolation()` shows the git-repo-aware 3-way
  warning (worktree / same-folder own-risk / cancel), passing `isolation` to the backend. Reset reuses
  the tab's worktree (`isolation:'none'`, folder = activeFolder). Isolated tabs show a `⑂` marker;
  closing toasts kept-vs-removed.

Decisions: warn on Start vs currently-running tabs; 3-way dialog; remove-worktree-if-clean-else-keep;
omit the worktree option for non-git folders. Build clean (0/0); GitWorktree sequence verified in a
scratch repo; app boots with no errors. **GUI click-through pending at the machine.** Docs
(`CLAUDE.md`, `.claude/STRUCTURE.md`) updated in the same change.

## 2026-07-15: Live Code — multiple sessions as tabs — branch `LIVE-CODE-SESSION`

The Live Code page now runs **multiple independent Claude Code sessions at once**, one per tab.
Design/plan docs: `docs/superpowers/specs/2026-07-15-live-code-tabs-design.md` and
`docs/superpowers/plans/2026-07-15-live-code-tabs.md`.

**Backend (`Bridge/Handlers/LiveCodeHandlers.cs`)** — replaced the singleton session statics with a
`Dictionary<string, LiveSession>` keyed by a frontend-minted `tabId` (stable across a tab's
Stop→Resume/Reset), all guarded by the existing `Gate`. Every per-session action
(`start`/`resume`/`reset`/`stop`/`attach`/`metrics`, `pty.input`/`pty.resize`) now requires a
`tabId`, and `pty.output`/`pty.exit` events carry it. New actions: `livecode.list` (all live tabs,
for rebuild + hover panel) and `livecode.closeTab` (dispose + drop entry); `livecode.running` now
returns `{running, count}`. `stop` keeps the entry (so Resume works); `closeTab` removes it. The
exit closure identity-checks the session (`ReferenceEquals`) so a superseded/stopped session can't
clobber a newer one — and since `ConPtySession.Dispose` sets `_disposed=true` before killing (and
`Exited` only fires when not disposed), an intentional Stop/Reset/close emits **no** spurious
`pty.exit`.

**Frontend (`wwwroot/js/views/livecode.js`)** — rewrote from one `state` to a `tabs[]` array +
`activeTabId` (both in the module closure, so they survive navigation). A tab bar (`＋ New tab`,
soft cap **6**, `×` closes with a confirm when running) sits above a per-active-tab control panel
(ticket picker, folder, shell/model/agent, Custom Agent, Start/Stop/Resume/Reset, Auto-approve,
Bypass, and a per-tab "this session" tokens/context readout). One xterm per tab lives in a
persistent `#lc-terminals` container (only the active tab shown); a single `pty.output`/`pty.exit`
subscription routes by `tabId`, so background tabs keep streaming and switching is instant.
`newTab()` inherits last-used defaults; `reconcile()` merges `tabs[]` with `livecode.list` on load;
`reattachAll()` replays each running tab's buffer; `focusTab(tabId)` is exported for the sidebar.
Shared bottom panel keeps Plan + week-tokens + active-sessions.

**Sidebar (`wwwroot/js/app.js`, `index.html`, `app.css`)** — the nav dot reflects
`livecode.running.count` (green ≥1, red = none; the red default was already in CSS). Hovering the
Live Code nav item shows a popover (`setupNavPopover`) listing live tabs (ticket/`Session N`,
folder basename, running/stopped); clicking a row calls `Views.livecode.focusTab`.

**Decisions:** per-session metrics moved into each tab; closing a running tab confirms first; new
tabs inherit last-used defaults; labels = ticket key or `Session N`. Build clean (0/0); backend
verified headlessly (`livecode.list`/`running{count}`/`attach{tabId}`/`metrics{tabId}` all correct
in the boot log). **GUI click-through verification pending at the machine.** Docs (`CLAUDE.md`,
`.claude/STRUCTURE.md`) updated in the same change.

## 2026-07-14: Live Code Session feature — branch `LIVE-CODE-SESSION` (code-complete, GUI verification pending)

New experimental feature: a "Live Code" page (nav item above Settings) that drives an
interactive Claude Code session under the user's **subscription** auth (no API key),
kicked off from a selected JIRA ticket. Full design in `PLAN-LIVE-CODE-SESSION.md`.
All six milestones (M1–M6) are implemented and build clean; the backend is verified
headlessly. What remains is **GUI verification** at the machine (see the checklist at the
end of this section) and merging `LIVE-CODE-SESSION` → `main` once happy.

### Repo now under version control
- `git init` done; baseline commit of v1 on `main`; feature work on branch `LIVE-CODE-SESSION`.
- Added `.gitignore` (bin/obj, `aiusage.db*`, `.claude/settings.local.json`, OS cruft).
- Added `.claude/STRUCTURE.md` (detailed file/DB/action/settings reference) alongside `CLAUDE.md`;
  both carry a note to keep them in sync on structural changes.

### Design decisions (brainstormed 2026-07-14)
- Interaction: **live interactive terminal** (real `claude` TUI rendered in-page).
- Confirmations: **manual/auto toggle**; `bypassPermissions` requires an explicit confirm dialog.
- Agent selector: **model (`--model`) + optional subagent (`--agent`)** from `.claude/agents`.
- Kickoff: auto-work the selected ticket; if an agent is chosen, run as that workflow agent.
- Bottom panel: **usage only** (tokens session/week + context %); plan/tier not exposed by the CLI.
- Shells: **PowerShell + Git Bash**, with runtime Git-Bash detection → PowerShell fallback.
- Warn + confirm before starting if `ANTHROPIC_API_KEY` is set (it's stripped from the child env).

### M1 done — page scaffold (commit on branch)
- New `Live Code` nav item + `wwwroot/js/views/livecode.js`: ticket picker (latest 3 assigned),
  working-folder picker, shell/model/agent selectors, placeholder terminal + metrics areas.
- Backend `Bridge/Handlers/LiveCodeHandlers.cs`: `livecode.config/saveConfig/tickets/listAgents/pickFolder`.
- `Terminal/AgentCatalog.cs` (reads `.claude/agents` frontmatter), `Platform/FolderDialog.cs`
  (UI-thread-marshalled Photino folder dialog + manual-path fallback).

### M2 done (backend proven) — live terminal transport
- **Key finding:** the hand-rolled raw-ConPTY implementation (plan approach B) ran the child fine
  (correct exit codes) but **conhost would not stream output continuously** on this Windows build
  (26200.8655) — it only flushed on resize/close. Verified via a headless `--pty-test` harness
  (a `ping` run produced a single 16-byte handshake chunk, then nothing until close).
- **Resolution:** took the plan's pre-authorized fallback to a maintained library —
  **`Porta.Pty` 1.0.7** (managed-only NuGet, so the single-file publish story is unchanged).
  `Terminal/ConPtySession.cs` is now a thin wrapper (same public surface: `Start/Write/Resize/
  Dispose` + `Output`/`Exited`). `--pty-test` now shows continuous streaming (13 chunks at ~1s
  intervals for a 6-ping run, `streamed=True`).
- Streaming transport: `MessageRouter.PushEvent` sends unsolicited `{type:"event",…}` messages;
  `bridge.js` gained `Bridge.on(event, handler)`. Terminal I/O rides this as `pty.output`/`pty.exit`
  events + `pty.input`/`pty.resize`/`livecode.start`/`livecode.stop` actions.
- Frontend: vendored **xterm.js 5.3.0** + fit addon (`wwwroot/lib/xterm.*`, UMD, no CDN — same
  constraint as Chart.js); `livecode.js` mounts xterm, streams output, sends keystrokes, refits on resize.
- **Debug CLI:** added `--pty-test` (spawns a pseudo-console, verifies continuous output streaming).
- **Still to verify in the GUI** (needs a human at the machine): xterm rendering fidelity, typing,
  and resize. Backend streaming is proven headlessly.

### M3 done (backend verified) — launch Claude Code on the ticket
- `livecode.start` now spawns the chosen shell then **types a `claude …` command** that works the
  selected ticket: `claude [--model x] [--agent y] [--permission-mode acceptEdits] '<prompt>'`.
  Prompt = "Work on JIRA ticket KEY: summary. <description>", flattened to one line (embedded
  newlines would be read as Enter by the shell) and shell-quoted (single quotes, per-shell escaping).
- **Ticket description**: schema **v5** adds `Tickets.description`; `JiraClient.FetchIssueAsync`
  fetches it and flattens the ADF (Atlassian Document Format) tree to plain text;
  `TicketRepo.UpsertFetched` stores it (COALESCE so bulk search upserts don't wipe it). `livecode.start`
  fetches the description fresh for the prompt.
- **Subscription auth**: `ANTHROPIC_API_KEY` is stripped from the child env. Porta.Pty inherits the
  parent process env and ignores dict-based removal, so `ConPtySession` also **unsets the var in this
  process** (safe — the app never reads it). Verified headlessly via `--envtest` (`stripped=True`).
  The var is only stripped, so if a key is present the UI warns + confirms first (`App.confirm` modal;
  backend reports `apiKeyPresent`).
- `--envtest` debug verb added; it also incidentally confirms typed-command execution + streaming in
  interactive cmd.
- **Still GUI-pending** (needs the machine + a Claude login): real `claude` launch, subscription
  billing, and kickoff timing under PowerShell/PSReadLine.

### M4 done — confirmations toggle
- Permission mode at launch: **bypass** (confirmed) → `bypassPermissions` > **auto-approve** →
  `acceptEdits` > default (manual). `livecode.start` reads `autoApprove` + `bypass`.
- `bypassPermissions` requires an explicit danger confirm (`App.confirm(..., danger)`) before the box
  stays checked; bypass never persists (resets each page load).
- **Best-effort auto-answer** (`Terminal/PromptWatcher.cs`): only active in auto-approve (acceptEdits)
  mode. ANSI-strips output, detects Claude's confirmation prompts (`❯ 1.`, `(y/n)`, etc.) and injects
  Enter with a 1.5s cooldown. Documented as fragile — the permission mode is the robust part.

### M5 done — usage metrics panel
- `livecode.metrics`: runs a light incremental scan, then returns week tokens (DB, current ISO week),
  the active session's tokens (DB row for its transcript), and a live context-window estimate.
- Context %: `SessionAggregator.LastContextTokens(file)` (schema-aware) reads the most recent
  assistant turn's input+cache tokens; context size = 1M for `[1m]` models else 200k.
- Active transcript located by encoding the session's cwd the way Claude Code does
  (`:` `\` `/` → `-`) under `~/.claude/projects/<encoded>`, newest `*.jsonl` since start.
- Frontend polls `livecode.metrics` every 3s while a session runs; panel shows
  "Tokens this session / this week" and "≈ N% of <size>".

### Fixes 2026-07-14 (from first GUI test)
- **Git Bash launched WSL and failed** (`execvpe(/bin/bash) failed`): `ShellResolver.FindGitBash`
  preferred a bare `bash.exe` on PATH = `C:\Windows\System32\bash.exe` (the WSL launcher). Fixed to
  prefer Git-for-Windows install paths and never use the System32/SysWOW64 shim (`IsSystemShim`).
  Verified via `--shelltest` → `C:\Program Files\Git\bin\bash.exe`.
- **Context window showed the wrong value**: `FindActiveTranscript` picked the newest `.jsonl` in the
  folder's project dir, which is shared by other concurrent Claude Code sessions (incl. the dev's own).
  Fixed by launching `claude --session-id <guid>` and reading exactly `<guid>.jsonl`. Transcript files
  are confirmed named `<uuid>.jsonl`.
- Added `--shelltest` debug verb.

### Fixes 2026-07-14 (round 2, from GUI test)
- **Terminal text overlapped the scrollbar**: WebView2's overlay scrollbar has zero layout width, so
  xterm's FitAddon computed full-width columns and the last chars rendered under it. Fixed with CSS
  forcing a real 12px `::-webkit-scrollbar` on `.lc-terminal .xterm-viewport` (FitAddon then reserves
  a column for it).
- **Context-window % used the wrong max**: `ContextSizeFor` returned 200k unless the model string
  contained "1m", but current models don't encode that (`claude-opus-4-8` is 1M) — so % was ~2×.
  Per the claude-api reference, current Claude models are **1M except Haiku (200k)**. Now the size is
  driven by the **selected model** (opus/sonnet → 1M, haiku → 200k), falling back to the transcript's
  model for "Default". Matches Claude Code's own `Context: 30k/1M` indicator.

### Enhancements 2026-07-14 (round 3)
- **Stop now kills the whole process tree.** `Kill()` ended only the shell, orphaning `claude`/`node`.
  `ConPtySession.Dispose` now runs `taskkill /PID <pid> /T /F` (tree kill) so Stop halts everything.
- **Subscription package + usage reset in the metrics panel.** New `Platform/ClaudeAccount.cs` reads
  Claude Code's `~/.claude.json` for `organizationType` (→ Team/Enterprise/…), `userRateLimitTier`
  (→ Max 5x/Pro/…) and `planLimitsEndDate` (usage-limit reset). Surfaced via `livecode.config`
  (`plan`, `usageResetsAt`) → shown as a "Plan" tile ("Team · Max 5x") and an "usage limits reset
  <date>" sub-line under weekly tokens. **Only non-secret fields are read** — never org name, email,
  or tokens. Verified via `--accounttest` (`plan=Team · Max 5x`, `usageResetsAt=2026-07-20`).
- Added `--accounttest` debug verb.

### Enhancement 2026-07-14 (round 4): context window as "used of max"
- Investigated whether real per-session/weekly token **limits** are readable — they are **not**
  (`~/.claude.json`, `policy-limits.json`, `stats-cache.json` hold usage counts + the weekly reset,
  but no quota numbers or 5-hour session reset; Claude fetches those live as %s from its API).
- Per the user's choice ("context window only"): the **Context window** metric now shows the one
  real limit as "`<used> of <max> (N%)`" (e.g. `34K of 1M (3%)`). Session & week stay as plain
  counts; the weekly reset date stays; the unreadable 5-hour session reset is not shown.

### Enhancements 2026-07-14 (round 5): active sessions, CLI check, auto-link, agents folder
- **Top-2 active Claude Code sessions** in the metrics panel: `Scanner/ActiveSessions.cs` scans
  `~/.claude/projects/**` for transcripts written in the last 5 min, returns the 2 newest with folder
  (from transcript `cwd`) + context used/max/%. Surfaced via `livecode.metrics` (`activeSessions`).
  The metrics poller is now **page-level** (every 4s while on the page, cleared on navigate via a
  hashchange hook) so week tokens + active sessions stay live even with no session running.
- **Claude CLI install check**: `Terminal/ClaudeCli.cs` resolves `claude` on PATH (+ `~/.local/bin`).
  `livecode.config` returns `claudeInstalled`; the page shows a warning banner and disables Start when
  it's missing.
- **Auto-link ticket ↔ session**: on start with a ticket, `SessionRepo.LinkLiveCodeSession` inserts a
  placeholder Sessions row (keyed by the launched `--session-id`) + a `SessionTicketLinks` row with
  source `livecode` (kept by the allowlist purge, which only removes `auto`). The scanner later
  accumulates the real tokens into the same row (ON CONFLICT(id)). New `.badge.livecode` style.
- **Agents folder**: `AgentCatalog.List(projectDir, customDir)` now also scans a user-chosen folder
  (the folder itself and its `.claude/agents`), in addition to the working folder's `.claude/agents`
  and `~/.claude/agents`. New "Agents folder" input + Browse on the page; persisted as
  `livecode_agents_dir`; passed to `livecode.listAgents`.
- Shared `SessionAggregator.ContextWindow(model)` + `ReadLive(file)` (cwd/model/context); metrics and
  active-sessions both use them.

### Enhancements 2026-07-14 (round 6): persist session across navigation, Resume, Start disabled
- **Terminal now survives navigation.** Root cause of the "black terminal on return": the router wipes
  `#content` on navigation AND `load()` was calling `livecode.stop` on re-entry (killing the session).
  Fixed: navigation no longer stops the backend; `ConPtySession` keeps a rolling 512KB output buffer
  (`Snapshot()`); on return, `livecode.attach` returns the buffer and the UI replays it into a fresh
  xterm and reconnects (`reattach()`), so the running session continues.
- **Resume button** (next to Stop): `livecode.resume` re-launches `claude --resume <lastId>` (+ model/
  agent/permission flags) in the last folder to continue the prior conversation after Stop/exit.
  `_lastSessionId`/`_lastFolder` survive Stop; `start`/`resume` share a `LaunchInPty` helper.
- **Start disabled while running** (and Stop only enabled while running, Resume only when idle + a prior
  session exists) via a single `updateButtons()`.

### Enhancements 2026-07-14 (round 7): Resume-continue, Reset, sidebar dot, real-time active list
- **Resume now sends "continue"**: `claude --resume <id>` takes a positional prompt, so
  `BuildResumeCommand` appends `'continue'` — resumes AND tells Claude to continue in one command.
- **Reset button** (next to Resume, enabled only while running): `livecode.reset` writes `/exit` to the
  running Claude, waits ~800ms, then **restarts a fresh Claude session on the same ticket** (new
  session id). start + reset share `StartTicketSession` (ticket fetch + kickoff + auto-link + launch).
- **Sidebar dot** on the "Live Code" nav item (`#lc-nav-dot`): app.js polls `livecode.running` every 3s
  (global, all pages) → green when a session is running, red otherwise.
- **Real-time active sessions**: split out a scan-free `livecode.activeSessions`; the panel now polls it
  every 2s (`pollActive`) while `livecode.metrics` (with the DB scan) stays at 4s for tokens/context.

### Enhancement 2026-07-14 (round 8): hide finished tickets from the picker
- The "tickets to work on" picker now fetches 25 assigned tickets (newest first) and drops any with
  status **Closed / Done / Ready for Release** (`ExcludedTicketStatuses`, case-insensitive,
  filtered client-side to avoid JQL errors on instances lacking a status name), then shows the top 3.

### Enhancements 2026-07-14 (round 9): ticket required + custom agents actually run
- **Start now requires a selected ticket** (in addition to a folder + CLI). `updateButtons` gates it;
  the JIRA-not-configured note says a ticket must be selected.
- **Custom Agents folder is now actually usable.** `--agent <name>` only resolves agents in
  `.claude/agents`, so `StartTicketSession`/reset call `AgentCatalog.SyncCustomAgents(agentsDir, folder)`
  BEFORE the kickoff — copying the folder's `*.md` (and its `.claude/agents`) into the working folder's
  `.claude/agents` (never overwriting existing ones). Returns `agentsCopied` (toasted). Then the
  selected agent runs against the ticket via `--agent`.

### Enhancements 2026-07-14 (round 10): Custom Agent file + "use the agent" prompt
- Replaced the "Agents folder" section with **"Custom Agent"**: Browse now opens a **file picker**
  (`livecode.pickAgentFile` → `FolderDialog.PickFile`/Photino `ShowOpenFile`) to select an agent `.md`
  directly; the resolved agent name is shown as a badge next to it (the confirmation that was missing).
  Persisted as `livecode_custom_agent`; `config` returns `lastCustomAgent` + `lastCustomAgentName`.
- On start/reset, the chosen agent file is installed into the working folder's `.claude/agents`
  (`AgentCatalog.InstallAgentFile`, returns its name), and the kickoff prompt is now
  **"Use the &lt;agent&gt; agent to work on JIRA ticket &lt;KEY&gt;: …"** (prompt-based; the `--agent` flag
  was removed). Returns `agentUsed` → toasted. The dropdown agent is the fallback when no file is set.
- Removed `AgentCatalog.SyncCustomAgents` / `livecode_agents_dir` (superseded by the single-file flow).

### Enhancement 2026-07-14 (round 11): removed the "Share of tickets AI-assisted" chart
- Removed the dashboard AI-share doughnut and its whole backend chain (didn't show useful info):
  `stats.share` handler + `ComputeShareAsync`, `JiraClient.ApproximateCountAsync`, the Settings
  "share JQL" input, `jiraShareJql`/`DefaultShareJql`, and unused usings. The `jira_share_jql`
  settings row (if present in a DB) is now just an unreferenced orphan.

### M6 done — docs & polish
- `CLAUDE.md` + `.claude/STRUCTURE.md` updated: Live Code architecture, Porta.Pty dependency,
  ConPTY finding, xterm vendoring, event channel, schema v5, new files/actions/events/settings.
- Git Bash fallback UX: toast when Git Bash isn't found and PowerShell is used instead.
- Debug verbs `--pty-test` / `--envtest` documented.

### GUI verification still outstanding (needs a human + Claude login)
xterm render/typing/resize (M2), real `claude` launch + subscription billing + kickoff timing
under PowerShell (M3), auto-approve injection hitting real prompts (M4), live metrics values (M5).

---

## Status: v1 complete + all 2026-07-13 features + published single-file exe

### Feature 2026-07-14: application icon
- Custom icon representing "AI Usage Tracker": rounded blue tile + white ascending bars (usage/analytics) + gold 4-point AI sparkle. Source at `Resources/appicon.ico` (multi-size 16–256, uncompressed BMP frames for max compatibility; generated by scratchpad `makeicon.ps1`).
- **Exe/taskbar/Explorer icon**: `<ApplicationIcon>Resources\appicon.ico` in csproj (verified via `ExtractAssociatedIcon` on the published exe).
- **Window title-bar icon**: icon embedded as resource `appicon.ico`, extracted at startup by `WebAssets.ExtractIcon()` to `%LOCALAPPDATA%\AIUsage\appicon.ico`, applied via `window.SetIconFile(...)` before load (verified in both debug and the standalone published exe).
- Gotcha noted: PowerShell variable names are case-insensitive ($R/$r are the same var) — bit the sparkle geometry initially; and `System.Drawing.Icon` can't decode PNG-in-ICO frames, so the .ico uses BMP frames.
- Re-published the single-file exe with the icon baked in.

### Publish 2026-07-13: single-file, self-contained, copy-and-run
- **Deliverable**: `C:\Projects\AIUsage\bin\Release\net10.0\win-x64\publish\AIUsage.exe` (~37 MB, one file, nothing else).
- Command: `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false`
- **wwwroot is now embedded** (csproj `EmbeddedResource … LogicalName=web/%(RecursiveDir)…`) and extracted at startup to `%LOCALAPPDATA%\AIUsage\web` by `WebAssets.EnsureExtracted()`; the WebView loads it over file:// (keeps crypto.randomUUID working). Dev fallback: on-disk `wwwroot` next to the exe if no embedded resources.
- Requires only the **WebView2 Runtime** on the target (pre-installed on Win11); no .NET install needed (self-contained).
- **Verified**: copied ONLY the exe to an empty folder and ran it — full dashboard rendered (self-extracted runtime + native libs + web assets; fresh scan into portable `aiusage.db` beside the exe).
- To re-publish after changes: rerun the command above.

## Status: v1 complete + code-review fixes + confirm/remove bug fixed + Tickets project/sprint/priority (2026-07-13)

### Feature 2026-07-13 (f): Export to Excel (Sessions, Manual entry, Tickets)
- "⬇ Export to Excel" button on the top-right of each of the three tables (Sessions tabs row, Manual "Recent entries" heading, Tickets controls row).
- **Real .xlsx, no new dependency**: `Export/XlsxWriter.cs` writes minimal OOXML (zip via `System.IO.Compression`; inline strings + numeric cells; XML-escaping strips illegal chars; sheet-name sanitised).
- `Bridge/Handlers/ExportHandlers.cs`: `export.sessions|manual|tickets` → builds workbook from the same repo queries as the pages (full dataset), writes the file, reveals it in the file manager. `BuildWorkbook(what)` is public/testable.
- **NOTE — do not use Photino `ShowSaveFile`**: it returns `null` when called off the UI thread (its internal `Invoke` posts async then returns before the result is set), so the dialog approach reported "Export cancelled". Replaced with a **direct save to the Downloads folder** (fallback Documents) + `explorer /select` reveal. Reliable and verified.
- `app.js`: shared `App.exportExcel(action)` helper (no client timeout — the save dialog may stay open); registered in `Program.cs` with the window.
- **Verified end-to-end**: clicked Export on the Tickets page → `{saved:true, path:…\Downloads\tickets-….xlsx, rows:69}`, file appeared in Downloads, structurally valid, and **opened in real Excel via COM** (sheet "Tickets", A1=Key, A2=SFTY-1583, Sessions cell typed as a number). Buttons render on all 3 pages.

### Feature 2026-07-13 (e): Claude model usage-over-time chart on Dashboard
- New "Claude model usage per week" stacked-bar chart (2nd chart): sessions per model per week from `Sessions.model` (`strftime('%Y-W%W', started_at)`, `claude-` prefix stripped in the legend, NULL/empty → 'unknown').
- `StatsHandlers.cs`: added `modelWeekly` query (GROUP BY week, model).
- `dashboard.js`: new `ch-models` stacked bar; pivots modelWeekly into one dataset per model; colors from a fixed `PALETTE` assigned by sorted model name (stable per model).
- Verified: matches SQL (W25 sonnet-4-6 16 etc.), no regressions, 0 errors.

### Feature 2026-07-13 (d): Token-usage-over-time chart on Dashboard
- New "Token usage per week" line chart (first chart on the Dashboard): weekly total of `input_tokens + output_tokens` (cache reads excluded), `strftime('%Y-W%W', started_at)`.
- `StatsHandlers.cs stats.dashboard`: added `tokensWeekly` query to the returned object.
- `dashboard.js`: new `ch-tokens` panel + Chart.js line (filled, BLUE, y-axis via `App.fmtNum`). Reuses existing palette/`makeChart`; covered by the existing `hasData` guard.
- Verified: chart shape matches SQL (W22 0.8M → W27 peak 5.6M → W28), no regressions, 0 errors.

### Feature 2026-07-13 (c): Tickets sorted latest-first
- Added `updated` column (JIRA `fields.updated`; migration → schema v4). Fetched in both `FetchIssueAsync` and `SearchIssuesAsync`; stored by `UpsertFetched`.
- `TicketRepo.List` now `ORDER BY t.updated DESC, t.key DESC` — most recently updated first (SQLite sorts NULLs last in DESC; the key-DESC fallback keeps unsynced tickets newest-first per project before a re-sync). Verified live: after a sync, SFTY-1572 (updated today 14:25) sorts to the top.
- Existing tickets need a "Sync all"/"Fetch more" to populate `updated`.

### Feature 2026-07-13 (b): Tickets status colors, scroll, fetch-more, AI filter
- **Status row tint** (`app.css` `.st-green/.st-blue/.st-orange` + `tickets.js statusRowClass`): Closed/Done/Resolved → light green, Open/To-Do/Backlog → light blue, In Progress/In Review → light orange (hover keeps the tint). Other statuses stay neutral.
- **Scrollable list**: table wrapped in `.table-scroll` (bounded height, own scrollbar, sticky header).
- **"Fetch more from JIRA"** (on-demand import of tickets beyond AI-touched ones): `JiraClient.SearchIssuesAsync` uses the enhanced token-paginated search `POST /rest/api/3/search/jql` (`nextPageToken`); handler `tickets.fetchMore` upserts a page per click; JQL is configurable in Settings (`jira_fetch_jql`, default `assignee = currentUser() ORDER BY updated DESC`). Verified live against real JIRA: imported 50/page, real pagination token, count 24→69.
- **AI-touched filter**: All / AI-touched tabs on the Tickets page (client-side; AI-touched = sessionCount>0 || manualCount>0). Verified: All 69 / AI-touched 23.
- **AI mark**: AI-touched tickets show a violet "✨ AI" badge next to the key (`.badge.ai`), so they stand out in the All view. Verified: SFTY tickets with sessions show it; SFTY-1572 (0 sessions) and imported DEC/CLS tickets do not.
- Priority dot colors cover both the Highest…Lowest and Blocker…Trivial schemes.
- Files: `Jira/JiraClient.cs`, `Bridge/Handlers/{Jira,Settings}Handlers.cs`, `wwwroot/js/views/{tickets,settings}.js`, `wwwroot/css/app.css`.

### Feature 2026-07-13: project, sprint & priority in Tickets
- Added `project`, `sprint`, `priority` columns to the `Tickets` table (migration bumps schema to v3; `AddColumnIfMissing` handles existing DBs).
- `JiraClient.FetchIssueAsync` now also fetches `project`, `priority`, and **sprint**. Sprint is an instance-specific custom field, so its id is discovered once via `/rest/api/3/field` (name == "Sprint") and cached in Settings key `jira_sprint_field` (`-` sentinel = instance has no Sprint field). Sprint value is parsed defensively: array of sprint objects → prefer the `active` one, else most recent; also tolerates the legacy string encoding.
- Tickets page shows new columns: Key, Summary, **Project**, Type, **Priority** (colored dot by Highest/High/Medium/Low/Lowest), Status, **Sprint**, Sessions, Manual, Last synced. Table scrolls horizontally if narrow.
- **Populated by "Sync all from JIRA"** — existing tickets show blank project/sprint/priority until re-synced. Verified storage→display end-to-end with seeded data (since removed); real values require the user's JIRA token + a sync.

### Fixes 2026-07-13
- **Window now starts maximized** (`Program.cs`: `.SetMaximized(true)`; keeps 1280×860 as the restore size).
- **Fixed "A task was cancelled" error toast on confirm/remove (and assign/dismiss/reopen/manual-delete/settings-save).** Root cause: handlers written as `Task.Run<object?>(() => { …; return null; })` bound to the `Task.Run(Func<Task<object?>>)` **unwrap** overload (because `null` is assignable to `Task<object?>` and the more-derived target wins); `Task.Run` unwrapping a *null* task yields a **Canceled** task, so `await` threw `TaskCanceledException` even though the DB write committed — hence "updated, but error shown". Fix: all synchronous bridge handlers now run inline and return `Task.FromResult<object?>(…)` (no per-handler `Task.Run`; `OnMessage` already runs them on a pool thread). Files: `Bridge/Handlers/{Session,Manual,Settings,Jira,Stats}Handlers.cs`. A code comment in `SessionHandlers.Register` documents the trap.
- Reproduced end-to-end (confirm turned a link green + returned ok:true, zero `ok:false`); verified via headless MessageRouter instrumentation (since removed).
- Note: testing rebuilt the local DB a few times and toggled some links (SFTY-1230/1234/1424 etc.); harmless — re-scan/re-review as needed. Session count is 62 (Claude Code rotated old transcripts; 62 files on disk = 62 rows, verified).

### M4 done (2026-07-10)
- Dashboard: 4 stat tiles (sessions/tickets/tokens this month, pending review) + 5 charts (weekly AI-assisted tickets, activity doughnut, top-tickets bar with tokens/sessions toggle, AI-share doughnut [hidden until JIRA configured], type×activity stacked bar).
- Charts use the validated dataviz reference palette (fixed category→color mapping); Chart.js renders fully offline from the vendored bundle.
- Verified visually via window screenshots: all 5 pages render with real data. Dashboard SQL dry-run matched UI numbers (19 sessions this month, 39 pending, weekly W22–W27 counts).
- `--route <page>` CLI arg added (opens the app on a given page — used for headless UI verification).

### Post-v1 change: portable database (2026-07-10)
- DB resolution is now portable-first: `aiusage.db` next to the exe when that directory is writable; falls back to `%APPDATA%\AIUsage\` only when it isn't (Program Files case). One-time migration copies an existing %APPDATA% DB (+`-wal`/`-shm`) next to the exe; the %APPDATA% copy stays behind as a backup and can be deleted once happy.
- Verified: 71 sessions + settings intact at `bin\Debug\net10.0\aiusage.db`.
- Caveats: (1) the DPAPI-encrypted JIRA token does NOT survive copying the folder to another machine/user — it degrades to "not set" and must be re-entered (by design); (2) during development the DB sits in `bin\Debug\net10.0`, so deleting `bin` deletes it (the %APPDATA% backup still exists until removed).

### Remaining for Aaron (first-run checklist)
1. Open Settings → enter JIRA site URL, email, API token → "Test connection" → Save. (Token never entered via Claude; DPAPI-encrypted at rest.)
2. Tickets → "Sync all from JIRA" → summaries/statuses/types populate; `SFTY-123` will likely show a dead-key badge (false positive that survived the allowlist since it's an SFTY key).
3. Add one manual entry end-to-end and confirm it appears on the dashboard (UI click-through was not automatable headlessly).
4. Review the 39 pending sessions in Sessions → Needs review (assign/dismiss).
5. Optional: `git init` — the project is not under version control yet.

### M3 done (2026-07-10)
- JiraClient (read-only): GET issue with summary/status/issuetype, `/rest/api/3/myself` test-connection, `POST /rest/api/3/search/approximate-count` for the AI-share denominator. 404 → dead-key badge; 401 → clear credential error. Lazy background fetch on link/entry creation (`JiraSync`).
- **Live JIRA calls NOT yet tested** — needs Aaron's real API token entered in Settings, then "Test connection" + "Sync all" (never paste the token into Claude).
- SettingsStore: DPAPI-protected token (verified: stored as `dpapi:...`, decrypt round-trip works, write-only in UI); scan paths, allowlist, backfill date, share-JQL settings.
- Allowlist set to `SFTY` and purge verified on real data: false positives (UTF-8, PROJ-123, MC-001) removed, 2 sessions demoted to review queue, orphan tickets cleaned (25 SFTY tickets remain).
- Manual entry page (category/tool/date/description), Tickets page (sync all, dead-key badges), Settings page. UI click-through pending final verification.
- CLI verbs now: `--scan`, `--sql "..."`, `--set <key> <value>`.

### M2 done (2026-07-10)
- Transcript scanner verified against real data: **71 sessions** parsed from `C:\Users\aaron\.claude\projects`; re-scan idempotent and incremental (live session file grew mid-test and was picked up by offset without double counting).
- Inference results: 34 sessions auto-linked / 37 pending review; 1 branch link (`SFTY-1230`), 77 prompt-text links; top tickets SFTY-1164, SFTY-1424, SFTY-1230.
- Predicted false positives confirmed with empty allowlist (`UTF-8`, `PROJ-123`, `MC-001`) → set project-key allowlist to `SFTY` in Settings once M3 lands; settings handler must retroactively purge non-matching **auto** links (keep manual/confirmed).
- Sessions page: tabs (All / Needs review / Not ticket-related), tool-mix bar, ticket badges with confirm (✓) and remove (×), inline assign input, dismiss/reopen.
- Debug CLI added: `AIUsage.exe --scan` and `AIUsage.exe --sql "SELECT ..."` for headless verification.

### M1 done (2026-07-10)
- Project builds and runs: `dotnet run` opens the Photino window, WebView loads `wwwroot/index.html`, JS↔C# ping round-trip verified, SQLite DB + schema created at `%APPDATA%\AIUsage\aiusage.db`.
- Targets **net10.0** (no .NET 8 runtime installed on this machine). Packages: Photino.NET 4.0.16, Microsoft.Data.Sqlite 10.0.9, System.Security.Cryptography.ProtectedData 10.0.9, SQLitePCLRaw.bundle_e_sqlite3 (pinned to fix NU1903 advisory).
- Chart.js 4.4.9 vendored at `wwwroot/lib/chart.umd.js`. Note: corporate network requires `curl --ssl-no-revoke` for CDN downloads.
- Views are stubs; classic scripts + globals (`window.Bridge`, `window.Views`, `window.App`) because ES modules don't load over `file://` in WebView2.

## Done so far

### Requirements clarified (2026-07-10)
- **Goal**: track which JIRA tickets are worked on with AI assistance, what the AI did, and visualize the relations with charts.
- **Data capture**: hybrid — automatic parsing of Claude Code session transcripts + manual log entries. (Decided over git-commit-evidence and JIRA-labels approaches.)
- **Scope**: single user, local-only SQLite. No server or team sync.
- **JIRA**: read-only JIRA Cloud REST integration to enrich ticket keys with summary/status/type.
- **Dashboard**: leads with both tickets-with-AI-involvement and AI activity volume/breakdown.

### Research completed (2026-07-10)
- Verified the real Claude Code transcript format against ~71 sessions on this machine (`C:\Users\aaron\.claude\projects\**\*.jsonl`): confirmed field names for session id, timestamps, cwd, git branch, model, token usage, and tool calls.
- **Key finding**: `gitBranch` is detached (`"HEAD"`) in ~94% of transcript lines here, so ticket inference from branch names mostly fails; however 33/71 sessions contain ticket keys (e.g. `SFTY-1164`) in user prompt text. The scanner design therefore infers tickets from branch → cwd → prompt text with a project-key allowlist.
- Confirmed Photino.NET architecture: .NET host + OS-native WebView, static wwwroot frontend, JS↔C# string message bridge, chart library must be vendored locally (no CDN).

### Plan written and reviewed (2026-07-10)
- Full implementation plan: `PLAN-AI-USAGE-TRACKER.md` (this directory).
- Review pass fixed three issues: incremental scans must accumulate (not overwrite) session aggregates; JIRA count uses the non-deprecated `approximate-count` endpoint; manual entries supersede inferred categories to avoid double counting in activity charts.
- Known blockers and open questions are documented in the plan (transcript-format drift, Claude-Code-only auto-capture, ticket-mapping ambiguity, cross-platform token storage, coarse activity heuristics).

## Next up — milestones

- [x] **M1 — Skeleton + bridge**: csproj (`net10.0`, Photino.NET, Microsoft.Data.Sqlite), Photino window, wwwroot shell, vendored Chart.js, JSON message bridge with `ping` round-trip, SQLite migrations. ✅ verified 2026-07-10
- [x] **M2 — Scanner + DB**: transcript scanner (incremental), session aggregator, ticket-key inferrer, Sessions page with review queue. ✅ verified against 71 real sessions 2026-07-10
- [x] **M3 — Manual entry + JIRA**: manual entry CRUD, JiraClient (read-only), DPAPI-protected token in Settings, Tickets page. ✅ 2026-07-10 (live JIRA call pending Aaron's token)
- [x] **M4 — Dashboard**: stats queries, 5 charts, stat tiles, empty-state placeholders. ✅ verified visually 2026-07-10

## Decisions log

| Date | Decision |
|---|---|
| 2026-07-10 | Photino.NET + .NET 8 + vanilla JS + vendored Chart.js (lightweight, no Electron) |
| 2026-07-10 | Hybrid capture: Claude Code transcript scanning + manual entries |
| 2026-07-10 | Local-only SQLite; no ORM |
| 2026-07-10 | Portable-first DB location: next to exe when writable, %APPDATA% fallback; one-time auto-migration |
| 2026-07-10 | Read-only JIRA Cloud API; token DPAPI-protected on Windows, write-only in UI |
| 2026-07-10 | Ticket inference priority: git branch → cwd → prompt text, with project-key allowlist |
| 2026-07-10 | Sidechain/subagent transcripts skipped in v1 (documented undercount) |
| 2026-07-10 | Headline token figures exclude cache-read tokens |
