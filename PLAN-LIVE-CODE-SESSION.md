# PLAN — Live Code Session

_Status: DESIGN APPROVED — not yet implemented. Created 2026-07-14. Branch: `LIVE-CODE-SESSION`._

## 1. Goal

Add a new **"Live Code"** page (new sidebar item, positioned **above Settings**) that lets the user
pick a JIRA ticket, a working folder, a shell, and a model/agent, then drives a **live, interactive
Claude Code session** embedded in the page. The session runs under the user's **Claude subscription
auth** (no Anthropic API key), so it draws on the existing plan rather than metered API billing. A
bottom panel shows live token usage and context-window consumption.

This is an **experimental feature**. It is built on the `LIVE-CODE-SESSION` branch; `main` holds the
stable baseline.

## 2. Design decisions (from brainstorming, 2026-07-14)

| # | Decision |
|---|---|
| Interaction model | **Live interactive terminal** — the real `claude` interactive TUI, rendered in the page, that the user can watch and type into. |
| Confirmations | **Manual/Auto toggle.** Off (default): user answers prompts in the terminal. On: launch in a low-friction permission mode + best-effort keystroke injection. |
| `bypassPermissions` | Selecting the bypass mode **requires an explicit confirmation dialog** first (destructive-command risk). |
| `ANTHROPIC_API_KEY` present | If the env var is set when starting a session, **warn and require confirmation** before continuing (the key is stripped from the child env so the subscription is used, not metered API). |
| Agent selector | **Model + optional subagent.** Model → `--model`; subagent (from `.claude/agents`) → `--agent <name>`, treated as a workflow agent that knows how to work a ticket. |
| Session kickoff | **Auto-work the selected ticket.** Launch `claude` with an initial prompt built from the ticket; if an agent is selected, run as that agent so it follows its own workflow. |
| Bottom panel | **Usage only** — tokens (session + week) and context-window %. **No subscription tier line** (not exposed by Claude Code). |
| Shells | **PowerShell + Git Bash.** If Git Bash is selected but not installed, notify and **fall back to PowerShell**. |
| PTY mechanism | **Raw ConPTY via P/Invoke, in-repo** (approach B). Fall back to a maintained NuGet ConPTY wrapper (approach A) only if the interop proves too costly. |
| Terminal renderer | **xterm.js**, vendored as a UMD bundle (same constraint as Chart.js: no CDN, no ES modules over `file://`). |
| Transport | **Extend the existing message bus** with an unsolicited event channel; no WebSocket/local server. |

## 3. Constraints & findings (Claude Code CLI)

Verified against current Claude Code behavior; items marked ⚠️ are version-dependent and must be
re-checked during implementation (M2/M3).

- **Subscription auth is automatic** when `ANTHROPIC_API_KEY` is unset. The app must launch `claude`
  with that variable cleared in the child environment so subscription billing is used, not API.
- **Interactive mode with an initial prompt:** `claude [flags] "initial prompt"` starts the
  interactive REPL and immediately submits the prompt. This avoids timing-sensitive keystroke
  injection for the kickoff. `--model`, `--agent`, `--permission-mode` apply to interactive runs.
- **Permission modes:** `default`, `acceptEdits`, `plan`, `bypassPermissions` (alias
  `--dangerously-skip-permissions`). Set at launch. In interactive mode the mode can also be cycled
  with Shift+Tab (possible runtime enhancement, not relied upon).
- **Subscription plan/tier is NOT exposed** via any non-interactive command (`/usage`, `/cost` are
  interactive-only). → panel shows usage only.
- **Per-turn token counts / context size are not in a stable machine-readable CLI output** ⚠️, but the
  live session writes a transcript JSONL under `~/.claude/projects/<encoded-cwd>/<session-id>.jsonl`
  containing per-message `usage` — the same format this app's scanner already parses. That transcript
  is our source of truth for the metrics panel.

## 4. Architecture

### 4.1 PTY host (.NET) — `Terminal/ConPtySession.cs`
- Wraps Win32 **ConPTY**: `CreatePseudoConsole` + input/output pipes + `CreateProcess` with
  `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`. Working directory = selected folder; environment inherits the
  user's, minus `ANTHROPIC_API_KEY`.
- Exposes: `Start(shellExe, args, cwd, cols, rows)`, `Write(bytes)` (stdin), an output callback
  (raw bytes → forwarded to the WebView), `Resize(cols, rows)`, `Stop()`/dispose, and process-exit
  notification.
- One active session at a time in v1 (documented limitation). A second `Start` stops the first.
- **Fallback plan:** if raw interop stalls, swap the internals for a maintained ConPTY NuGet wrapper
  behind the same interface — callers/UI unchanged.

### 4.2 Shell resolution — `Terminal/ShellResolver.cs`
- PowerShell: prefer `pwsh.exe` on PATH, else `powershell.exe`.
- Git Bash: probe common install locations + PATH for `bash.exe` (Git for Windows). If absent, return
  a "not found" result so the handler can toast and fall back to PowerShell.
- The shell hosts the session; the app writes the `claude …` command line into it on start (or spawns
  `claude` directly with the shell as parent — decided in M2 based on which renders cleaner).

### 4.3 Terminal renderer (frontend) — xterm.js
- Vendored at `wwwroot/lib/xterm.js` (+ CSS). Mounted into the wrapper area; `onData` (keystrokes) →
  `pty.input`; PTY output events → `term.write()`. `FitAddon`-style sizing drives `pty.resize`.

### 4.4 Streaming transport (extends the bridge)
- Today `MessageRouter` is strictly request/response keyed by `id`; `bridge.js` drops messages with no
  matching pending `id`.
- Add an **event channel**: server pushes `{ type: "event", event: "pty.output", data: <base64> }`
  (no `id`). `bridge.js` gains `Bridge.on(event, handler)` and routes id-less `type:"event"` messages
  to registered handlers.
- Keystrokes/resize go back as normal fire-and-forget actions (`pty.input`, `pty.resize`).
- Rationale: keeps the single-exe, `file://`, no-network model intact; terminal I/O volume is well
  within the string bus's capacity.

## 5. UI layout (maps to `LIVE-CODE.jpg`, top → bottom)

New view `wwwroot/js/views/livecode.js`, registered on `window.Views.livecode`; nav link added to
`index.html` above the Settings link.

1. **Ticket picker** — latest 3 tickets assigned to the user. Source: `JiraClient.SearchIssuesAsync`
   with JQL `assignee = currentUser() ORDER BY updated DESC`, `maxResults = 3`. Cards show key +
   summary + status; one is selectable. Empty/needs-config state when JIRA isn't set up.
2. **Working folder** — text field + "Choose…" button → native folder dialog invoked **on the UI
   thread** (avoids the off-thread dialog bug documented for `ShowSaveFile` in PROGRESS.md). Defaults
   to the last-used folder (persisted in Settings).
3. **Shell** — PowerShell / Git Bash toggle.
4. **Model + Agent** — Model dropdown (Opus / Sonnet / Haiku → `--model`); Agent dropdown listing
   `.claude/agents/*.md` from the selected project and the user's `~/.claude/agents`, default
   "(none)".
5. **Controls** — Start / Stop; **Auto-approve** toggle (see §7).
6. **Terminal wrapper** — the xterm.js surface (the large middle box in the drawing).
7. **Metrics panel** — Tokens this session / this week; Context window % used (see §8).

## 6. Session lifecycle

**Start:**
1. Validate: ticket selected, folder exists, JIRA reachable for description fetch.
2. **`ANTHROPIC_API_KEY` guard:** if the variable is present in the current environment, show a
   warning dialog **before continuing** — it explains that a key is set, that this feature is meant to
   run on the Claude **subscription** (not metered API billing), and that the app will launch the
   session with the key removed from the child environment. The user must confirm to proceed; Cancel
   aborts the start. (When the key is absent, no dialog — start proceeds directly.)
3. Resolve shell (Git Bash → PowerShell fallback with a toast if missing).
4. Fetch the ticket's summary + **description** from JIRA (requires storing `description` — see §10).
5. Build the launch command:
   - Base: `claude --model <model>` and `--agent <name>` if an agent is selected.
   - Permission mode per the Auto-approve state (§7).
   - Environment: current env **minus `ANTHROPIC_API_KEY`** (see step 2).
   - Initial prompt (positional arg):
     - No agent: `Work on JIRA ticket <KEY>: <summary>.\n\n<description>`
     - With agent: same ticket task, run under `--agent <name>` so the workflow agent drives the steps.
   - cwd: selected folder.
6. Spawn via ConPTY; stream output to the terminal; show "running" state.

**During:** keystrokes ↔ PTY; resize on panel resize; metrics panel polls (§8).

**Stop:** `Stop()` tears down the process + pseudoconsole; terminal shows the exit state. Closing the
page or starting a new session also stops the current one.

## 7. Confirmations (manual / auto)

- **Auto-approve OFF (default):** launch in `default` permission mode; the user answers any prompt
  directly in the live terminal.
- **Auto-approve ON:** launch in `acceptEdits` (edits auto-accepted; other tools still prompt), and run
  a **best-effort watcher** that detects known prompt patterns in the output stream and injects the
  confirming keystroke (Enter / `y` / select option 1).
- **`bypassPermissions`:** offered as a separate, clearly-labelled danger option. **Toggling it on
  requires an explicit confirmation dialog** ("This lets the agent run commands, including destructive
  ones, with no confirmation. Continue?"). Only after confirmation does the next session launch with
  `--permission-mode bypassPermissions`.
- **Honesty note:** permission modes are the *robust* mechanism; TUI prompt-pattern scraping is
  inherently fragile and is a best-effort convenience, not a guarantee. The permission mode is fixed at
  launch; flipping the toggle mid-session changes the watcher only (a full mode change needs a restart;
  Shift+Tab cycling is a possible later enhancement).

## 8. Metrics panel data sources

- **Tokens (session & week):** from the existing `Sessions` table populated by `TranscriptScanner`.
  Week = existing weekly aggregation; session = the active session's row. A light re-scan (or a tail of
  the active transcript) refreshes the current session's counts while it runs. Headline figures exclude
  cache-read tokens, consistent with the rest of the app.
- **Context window %:** tail the active session's `.jsonl`, take the latest cumulative input token
  count for the current turn, divide by the model's context size (200,000 default; 1,000,000 when the
  model id indicates a 1M-context variant). Labelled **"approx."** — derived, not an official figure.
- Polling cadence: ~2–3 s while a session is active; stop when idle.

## 9. New / changed files

**New**
- `Terminal/ConPtySession.cs` (+ any `Terminal/NativeMethods.cs` interop)
- `Terminal/ShellResolver.cs`
- `Bridge/Handlers/LiveCodeHandlers.cs`
- `wwwroot/js/views/livecode.js`
- `wwwroot/lib/xterm.js`, `wwwroot/lib/xterm.css` (vendored)

**Changed**
- `wwwroot/index.html` — nav item above Settings; xterm + livecode script tags
- `wwwroot/js/bridge.js` — `Bridge.on(event, handler)` event channel
- `Bridge/MessageRouter.cs` — helper to push id-less `type:"event"` messages
- `Program.cs` — register `LiveCodeHandlers`
- `Jira/JiraClient.cs`, `Data/Repositories/TicketRepo.cs`, `Data/Migrations.cs` — fetch + store ticket
  `description`
- `Settings/SettingsStore.cs` — last-used folder / model / shell defaults
- `wwwroot/css/app.css` — Live Code page + terminal styling
- `AIUsage.csproj` — (only if ConPTY fallback A is adopted) the ConPTY NuGet package

## 10. Data & settings changes

- **Migration → schema v5:** add `Tickets.description TEXT` (via `AddColumnIfMissing`, idempotent).
  `JiraClient.FetchIssueAsync` fetches the description (plain-text rendering of the ADF body);
  `TicketRepo.UpsertFetched` stores it. Existing tickets get it on next sync.
- **New Settings keys:** `livecode_last_folder`, `livecode_last_model`, `livecode_last_shell`,
  `livecode_auto_approve` (last-used defaults; all non-secret).

## 11. New bridge actions & events

Actions: `livecode.tickets` (latest 3 assigned), `livecode.pickFolder` (UI-thread folder dialog),
`livecode.listAgents`, `livecode.start`, `livecode.stop`, `livecode.metrics`, `pty.input`,
`pty.resize`.
Events (server→client push): `pty.output`, `pty.exit`.

## 12. Security & safety

- `ANTHROPIC_API_KEY` is stripped from the child environment so the subscription is used and no API key
  is ever required or handled. If the variable is present in the current environment, the user is
  **warned and must confirm before the session starts** (§6, step 2) — guarding against accidental
  metered API billing.
- `bypassPermissions` is gated behind an explicit confirmation (§7) and visually marked as dangerous.
- The app runs whatever the user launches; the working folder is user-chosen. No elevation, no network
  service opened. Consistent with the app's local-only, single-user model.
- No secrets are added; JIRA token handling is unchanged (DPAPI, write-only in UI).

## 13. Risks & open questions

1. **Auto-approve reliability** — mitigated by leaning on permission modes; keystroke injection is
   best-effort. (Accepted.)
2. **ConPTY rendering fidelity** — Claude Code's TUI uses alt-screen + cursor addressing; xterm.js
   handles this, but resize/reflow needs testing. Prove in M2 before building on it.
3. **Context-% accuracy** — derived from the transcript; may lag or mis-estimate on 1M-context models.
   Labelled approximate.
4. **Initial-prompt-as-arg behavior** ⚠️ — confirm `claude "prompt" --agent X` submits correctly in the
   installed Claude Code version; fall back to post-launch keystroke injection if not.
5. **Single concurrent session** — v1 limitation; documented.
6. **Git Bash path mapping** — Git Bash uses POSIX paths; the cwd is passed as a Windows path to
   ConPTY (fine), but confirm `claude` starts cleanly under Git Bash.

## 14. Milestones (incremental, verify each before the next)

1. **Page scaffold** — nav item above Settings, static Live Code page, ticket list (3 assigned),
   folder pick, shell/model/agent selectors. No terminal yet.
2. **PTY proof (riskiest)** — ConPTY spawns a plain shell; bidirectional I/O through the event channel;
   xterm.js renders; resize works. Prove fidelity here.
3. **Launch Claude Code** — subscription auth (no API key), `--model`/`--agent`, ticket kickoff prompt
   (fetch + store description first).
4. **Confirmations** — Auto-approve toggle, permission modes, bypass confirmation dialog.
5. **Metrics panel** — tokens (session + week) + context % from the transcript.
6. **Polish & docs** — Git Bash fallback UX, styling, empty/error states; update `CLAUDE.md` and
   `.claude/STRUCTURE.md` (new files, actions, settings, schema v5) and `PROGRESS.md`.

## 15. Out of scope (YAGNI for v1)

- Multiple concurrent sessions / session tabs.
- Persisting/replaying past Live Code sessions beyond what the normal scanner already records.
- Subscription tier/quota display (not available programmatically).
- Non-Windows PTY (macOS/Linux) — app is Windows-only today.
- WSL bash (path-mapping friction); Git Bash only.

## 16. Verification approach

- M2 verified by driving a plain shell (echo, `dir`/`ls`, a curses-ish program) through the embedded
  terminal and confirming correct rendering + input + resize.
- M3+ verified by starting a real session against a scratch folder and a test ticket, watching Claude
  Code run under subscription auth (confirm no API key needed), and checking the metrics panel against
  the transcript via the existing `--sql` CLI.
- Keep `main` clean; all work on `LIVE-CODE-SESSION`.
