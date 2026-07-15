# Live Code — multiple sessions as tabs

**Date:** 2026-07-15
**Branch:** LIVE-CODE-SESSION
**Status:** Approved design

## Goal

Let the Live Code page run **multiple independent Claude Code sessions at once**, each in
its own tab. Everything currently inside the page's working area (the "red box": ticket
picker, working folder, shell, model, agent, custom agent, action buttons, and the
terminal) becomes **per-tab**. The bottom metrics panel stays shared, except the
per-session figures which move into each tab.

## Current state (what we're changing)

The Live Code backend is a **singleton**. `Bridge/Handlers/LiveCodeHandlers.cs` holds
static fields — `_session`, `_activeFolder`, `_activeSessionId`, `_activeModel`,
`_lastSessionId`, `_lastFolder` — all guarded by one `Gate`. Every per-session action
(`livecode.start` / `stop` / `resume` / `reset` / `attach` / `metrics`, `pty.input`,
`pty.resize`) operates on that single session, and the `pty.output` / `pty.exit` events
are broadcast with no session identifier.

`wwwroot/js/views/livecode.js` mirrors this with one `state` object and one `term`
terminal handle. The session survives navigation because the backend keeps running and
`livecode.attach` replays a 512 KB rolling buffer into a fresh xterm.

## Decisions (locked)

- **Per-session metrics move into each tab.** Each tab shows its own "Tokens — this
  session" and "Context window". The shared bottom panel keeps **Plan**, **Tokens — this
  week**, and the **Active Claude Code sessions** list.
- **Closing a running tab confirms first.** The tab's `×` on a live session shows
  `App.confirm`; on OK it tree-kills the session and removes the tab.
- **New tabs inherit last-used defaults** — the same saved folder / shell / model the page
  uses today (`livecode_last_*` settings). No ticket selected.
- **Tab labels:** the ticket key once picked (e.g. `SFTY-1572`), otherwise `Session N`.
- **Soft cap of 6 concurrent tabs.** "＋ New tab" disables at the cap. Each tab is a real
  shell process plus an xterm instance.
- **Background tabs stay live.** Only the active tab's terminal is visible (others hidden
  via CSS), but every tab's session keeps running and its terminal keeps receiving output,
  so switching tabs is instant. Replay from the buffer is only needed on page re-entry.

## Architecture

### Backend: singleton → per-tab dictionary

Replace the static singleton fields with a **`ConcurrentDictionary<string, LiveSession>`
keyed by `tabId`** — a GUID minted by the frontend when a tab is created. `tabId` is a
*stable* key that survives Stop → Resume/Reset (each mints a new Claude `--session-id`
inside the same tab).

A small `LiveSession` class holds what the statics held per session:

```
sealed class LiveSession
{
    public ConPtySession? Session;
    public string? ActiveFolder;      // cwd of the running session (locate its transcript)
    public string? ActiveSessionId;   // claude --session-id we launched (exact transcript file)
    public string? ActiveModel;       // selected model (drives context-window size)
    public string? LastSessionId;     // survives Stop, so Resume can `claude --resume <id>`
    public string? LastFolder;
    public string? TicketKey;         // for labels + the hover panel
}
```

Concurrency: `ConPtySession` output/exit callbacks fire on the PTY read thread while
handlers run on bridge pool threads. Keep per-`LiveSession` mutation under a lock (either a
lock per entry or reuse a single `Gate` around dictionary + entry mutations — a single
`Gate` is simplest and contention is negligible for ≤6 tabs). The dictionary itself is
concurrent for lookups.

### Bridge actions

Every per-session action gains a **`tabId`** field:

| Action | Change |
|---|---|
| `livecode.start` / `resume` / `reset` / `stop` | take `tabId`; operate on that tab's `LiveSession` (create the entry on start) |
| `pty.input` / `pty.resize` | take `tabId` → route to that tab's `ConPtySession` |
| `livecode.attach` | takes `tabId`; returns that tab's `{ running, canResume, data }` |
| `livecode.metrics` | takes `tabId` (+ model/folder for that tab) → returns that session's `sessionTokens` / `contextTokens` / `contextSize` / `contextPct`. Week-tokens + active-sessions still returned (global). Polled only for the **active** tab |
| **`livecode.list`** (new) | returns `[{ tabId, folder, ticketKey, running, canResume, model }]` for all live tabs — rebuilds tabs after navigation and feeds the hover panel |
| `livecode.running` | now returns `{ running, count }` — green when `count > 0` |
| `livecode.config` / `saveConfig` / `tickets` / `listAgents` / `pickFolder` / `pickAgentFile` / `activeSessions` | unchanged (global or stateless) |

Events carry the tab id:

- `PushEvent("pty.output", { tabId, data })`
- `PushEvent("pty.exit", { tabId, code })`

On exit, the handler disposes only that tab's `ConPtySession` and marks the entry
not-running (`Session = null`, `ActiveSessionId = null`) but **keeps the `LiveSession` in
the dictionary**, retaining `LastSessionId`/`LastFolder` so Resume still works (see
below). The entry is only removed from the dictionary when the tab is closed.

**Stop/exit vs. Resume:** as today, after Stop the tab must still be able to Resume. Keep
the `LiveSession` entry alive with `Session = null` and `ActiveSessionId = null` but
`LastSessionId`/`LastFolder` retained, so `livecode.resume { tabId }` can
`claude --resume <LastSessionId>`. The entry is only fully removed when the tab is closed.

### Frontend: `tabs[]` + `activeTabId`

`livecode.js` changes from one `state` object to a **`tabs[]` array** plus `activeTabId`,
both held in the module closure (so they survive navigation exactly as the single `state`
does today). Each tab entry holds today's per-session fields:

```
{ tabId, ticket, folder, shell, model, agent, customAgent, customAgentName,
  autoApprove, bypass, running, canResume,
  term: { inst, fit, unsub, ro },   // this tab's xterm handles
  metrics: { sessionTokens, contextTokens, contextSize, contextPct } }
```

UI:

- A **tab bar** renders above the ticket picker: one chip per tab (label = ticket key or
  `Session N`, with the shell as a subtle hint) + a "＋ New tab" button (disabled at 6).
- Clicking a tab sets `activeTabId`, shows that tab's terminal, hides the others (CSS
  `display:none`), and refits the shown terminal.
- The `×` on a tab: if that tab is running, `App.confirm` → `livecode.stop { tabId }` →
  remove; if idle, remove directly. Removing the active tab activates a neighbour.
- Each tab's controls (folder / shell / model / agent / custom agent / buttons /
  auto-approve / bypass / **per-session tokens + context readout**) render inside the
  active tab's panel and read/write that tab's entry.
- **One xterm per tab.** `pty.output` / `pty.exit` handlers route by `tabId` to the right
  tab's terminal. Background tabs keep their terminal mounted and subscribed so output
  keeps flowing; only visibility toggles.

On load: rebuild the tab bar from the closure `tabs[]`, then reconcile with
`livecode.list` — any backend session not represented in the closure gets a reconstructed
tab; for each running tab call `livecode.attach { tabId }` and replay its buffer. If the
closure is empty (fresh page / after app restart), build tabs entirely from
`livecode.list`, defaulting to one empty tab when the list is empty.

Metrics polling: poll `livecode.metrics { tabId: activeTabId }` on the 4 s timer and write
into the active tab's readout; `livecode.activeSessions` (global) still polls on the 2 s
timer for the shared bottom list.

### Sidebar icon + hover panel

- `app.js` `updateLiveDot()` calls `livecode.running` → `{ running, count }`; the nav dot
  is **green** when `count > 0`, **red** when `count === 0` (today: green/off — red makes
  "no session" explicit; add a `.nav-dot.off` red state or a `.red` class).
- **Hover panel:** wrap the nav item in a container with a `.lc-nav-popover` (hidden by
  default, shown on `mouseenter`). On hover it calls `livecode.list` (cached from the 3 s
  poll so it appears instantly, then refreshes) and lists live tabs — each row shows the
  ticket key (or `Session N`), the folder basename, and running/stopped state. Clicking a
  row navigates to `#livecode` and focuses that tab (via a small hook on the livecode
  view, e.g. `Views.livecode.focusTab(tabId)`). Empty → "No active sessions". Styled like
  the existing `.modal`, no new dependency.

## Data flow (start a session in a tab)

1. User clicks "＋ New tab" → frontend mints `tabId`, pushes an entry pre-filled with
   last-used defaults, activates it.
2. User picks a ticket + folder, clicks Start → `livecode.start { tabId, ... }`.
3. Backend creates/updates `LiveSession[tabId]`, launches the shell in a `ConPtySession`,
   types the `claude --session-id <guid> …` kickoff, auto-links the ticket
   (`SessionRepo.LinkLiveCodeSession`), and streams `pty.output { tabId, data }`.
4. Frontend routes those events to the tab's xterm; the 4 s metrics poll (for the active
   tab) fills its per-session readout.
5. Sidebar dot goes green; the hover panel lists the new session.

## Error handling

- Unknown / missing `tabId` on a per-session action → return a clear error; frontend
  ignores stale events whose `tabId` isn't in `tabs[]`.
- Folder-not-found and shell-fallback behaviour is unchanged, but scoped to the tab.
- Exit event marks only that tab stopped (Resume stays available), matching today.
- Bypass-permissions confirm and API-key-present confirm are unchanged, per tab.

## Out of scope (YAGNI)

- Persisting sessions across an app restart (the ConPTY child dies with the app — same as
  today).
- Drag-to-reorder tabs, split-view (two terminals visible at once), or renaming tabs.
- Raising or configuring the 6-tab cap.

## Docs to update on implementation

`CLAUDE.md` (Live Code paragraph), `.claude/STRUCTURE.md` (bridge-action catalog, file
notes), and `PROGRESS.md` — per the repo's keep-in-sync rule.
