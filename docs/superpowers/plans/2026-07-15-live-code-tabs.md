# Live Code Tabs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the Live Code page run multiple independent Claude Code sessions at once, each in its own tab, with a shared bottom panel and a sidebar icon that reflects/lists all sessions.

**Architecture:** Replace the singleton session state in the backend (`LiveCodeHandlers.cs`) with a `Dictionary<string, LiveSession>` keyed by a frontend-minted `tabId`; every per-session bridge action and the `pty.output`/`pty.exit` events carry `tabId`. Rewrite `livecode.js` from one `state` object to a `tabs[]` array + `activeTabId`, one xterm per tab, routing events by `tabId`. Update the sidebar dot to reflect session count and add a hover popover.

**Tech Stack:** .NET 10 / C#, Photino.NET message bridge, Porta.Pty ConPTY, vanilla JS (no modules), xterm.js.

## Global Constraints

- `net10.0`, nullable + implicit usings enabled; namespaces mirror folders.
- No test project — verify via `dotnet build`, `dotnet run -- --route livecode`, and window inspection.
- Synchronous handlers return `Task.FromResult<object?>(...)` — NEVER `Task.Run(() => {...; return null;})` (canceled-task trap).
- Frontend is classic scripts + globals — no ES modules. Views self-register on `window.Views`; shared helpers on `window.App`.
- `pty.output`/`pty.exit` stream over the unsolicited event channel (`router.PushEvent` → `Bridge.on`).
- Soft cap: 6 concurrent tabs. Tab label = ticket key once picked, else `Session N`.
- Keep `CLAUDE.md`, `.claude/STRUCTURE.md`, `PROGRESS.md` in sync (repo rule).
- ANTHROPIC_API_KEY is stripped per session (unchanged); DPAPI/JIRA-token rules unchanged.

---

## File Structure

- **Modify** `Bridge/Handlers/LiveCodeHandlers.cs` — singleton → `Dictionary<tabId, LiveSession>`; all per-session actions take `tabId`; events carry `tabId`; add `livecode.list`; `livecode.running` returns `{running,count}`.
- **Modify** `wwwroot/js/views/livecode.js` — `tabs[]` + `activeTabId`; tab bar; one xterm per tab; per-tab controls + per-tab metrics readout; event routing by `tabId`; reconcile with `livecode.list` on load.
- **Modify** `wwwroot/js/app.js` — `updateLiveDot()` uses `{running,count}` (green/red); hover popover listing sessions; expose `Views.livecode.focusTab`.
- **Modify** `wwwroot/index.html` — nav item wrapper for the popover; (tab-bar markup is created by JS).
- **Modify** `wwwroot/css/app.css` — tab bar styles, per-tab metric readout, `.nav-dot.off` (red), `.lc-nav-popover`.
- **Modify** `CLAUDE.md`, `.claude/STRUCTURE.md`, `PROGRESS.md` — doc sync.

---

### Task 1: Backend — per-tab session dictionary

**Files:**
- Modify: `Bridge/Handlers/LiveCodeHandlers.cs`

**Interfaces produced (consumed by Task 2/3):**
- `livecode.start|resume|reset|stop|attach|metrics` payloads now include `tabId` (string, required for these).
- `pty.input|pty.resize` payloads include `tabId`.
- Events: `pty.output { tabId, data }`, `pty.exit { tabId, code }`.
- `livecode.list` → `{ tabs: [{ tabId, folder, ticketKey, running, canResume, model }] }`.
- `livecode.running` → `{ running: bool, count: int }`.
- `livecode.attach { tabId }` → `{ running, canResume, data? }`.
- `livecode.metrics { tabId }` → `{ weekTokens, sessionTokens, contextTokens, contextSize, contextPct, active, activeSessions }`.

**Design:**
- Add a private `sealed class LiveSession { ConPtySession? Session; string? ActiveFolder, ActiveSessionId, ActiveModel, LastSessionId, LastFolder, TicketKey; }`.
- Replace the six static session fields with `private static readonly Dictionary<string, LiveSession> Tabs = new();` still guarded by the existing `Gate`.
- Helper `LiveSession Entry(string tabId)` (under `Gate`) → get-or-create.
- `StopSession(LiveSession e)` disposes `e.Session`, nulls `Session`/`ActiveSessionId`/`ActiveFolder`/`ActiveModel` but KEEPS the entry (Resume needs `LastSessionId`/`LastFolder`).
- Closing a tab is frontend-driven via `stop`; a dedicated remove isn't required because `stop` leaves the entry resumable and the dictionary is tiny — but add a `livecode.closeTab { tabId }` that fully removes the entry (dispose + `Tabs.Remove`) so the dictionary/list/count don't retain closed tabs.
- `StartTicketSession` / `LaunchInPty` / `resume` / `reset` take `tabId`, look up the entry, and write into it instead of the statics.
- `LaunchInPty` output/exit closures capture `tabId`: `PushEvent("pty.output", new { tabId, data })`; exit: `PushEvent("pty.exit", new { tabId, code })` then under `Gate` dispose+null that entry's `Session` (mark not-running, keep entry).
- `FindActiveTranscript(LiveSession e)` uses the entry's folder/sessionId.
- `metrics`: resolve entry by `tabId`; `sizeModel` from `e.ActiveModel`; `sessionTokens`/`contextTokens` from that entry's transcript; `weekTokens` + `activeSessions` stay global.
- `running`: `count = Tabs.Values.Count(t => t.Session is not null)`; `running = count > 0`.
- `list`: project each entry → `{ tabId, folder = e.ActiveFolder ?? e.LastFolder, ticketKey = e.TicketKey, running = e.Session is not null, canResume = e.LastSessionId is not null, model = e.ActiveModel }`.

- [ ] **Step 1: Add `LiveSession` class + `Tabs` dictionary + `Entry`/`StopSession(e)` helpers; remove the six static session fields.**
- [ ] **Step 2: Thread `tabId` through `start`/`resume`/`reset`/`stop`, `StartTicketSession`, `LaunchInPty`; capture `tabId` in the output/exit closures and add it to both events.**
- [ ] **Step 3: Update `attach`/`metrics`/`pty.input`/`pty.resize` to resolve the entry by `tabId`; add `livecode.list`, `livecode.closeTab`; change `livecode.running` to `{running,count}`.**
- [ ] **Step 4: Build.**

Run: `dotnet build`
Expected: `Build succeeded` with 0 errors. (If the app is running, MSB3021 copy-lock is fine — only CSxxxx are real; close the app and rebuild.)

- [ ] **Step 5: Headless sanity — routes and verbs unaffected.**

Run: `dotnet run -- --route livecode` (opens page; close it) and `dotnet run -- --shelltest`
Expected: page opens without a bridge error; shelltest prints resolved shells.

- [ ] **Step 6: Commit.**

```bash
git add Bridge/Handlers/LiveCodeHandlers.cs
git commit -m "Live Code: per-tab session dictionary (tabId on all session actions)"
```

---

### Task 2: Frontend — tabs model, tab bar, per-tab terminals & metrics

**Files:**
- Modify: `wwwroot/js/views/livecode.js`
- Modify: `wwwroot/css/app.css`

**Interfaces:**
- Consumes: all Task 1 actions/events (`tabId` on everything; `livecode.list`; `pty.output/exit` carry `tabId`).
- Produces: `Views.livecode.focusTab(tabId)` (used by Task 3 hover popover) — activates `#livecode` route then selects that tab.

**Design:**
- Replace `state` with `tabs = []` (module closure) and `activeTabId`. Each tab: `{ tabId, ticket, tickets, folder, shell, model, agent, customAgent, customAgentName, autoApprove, bypass, running, canResume, term:{inst,fit,unsub,ro}, metrics:{} }`.
- `newTab()` mints `crypto.randomUUID()`, seeds folder/shell/model from `cfg.last*` (last-used), pushes, activates. Disabled at 6.
- Global config (`plan`, `usageResetsAt`, `apiKeyPresent`, `claudeInstalled`, `jiraConfigured`) stays page-level (one `livecode.config` call).
- Render: a **tab bar** (chips + "＋ New tab"), then a single **active-tab panel** containing the existing controls (ticket picker, folder, shell, model, agent, custom agent, buttons, and a per-tab tokens/context readout), then the shared bottom panel (Plan, week-tokens, active-sessions).
- **Terminals:** keep one `<div class="lc-terminal" data-tab="<id>">` per tab, all in a container; only the active one is visible (`display` toggled). Each tab's xterm is created on first Start/attach and stays mounted. `pty.output`/`pty.exit` global subscription (once) dispatches by `d.tabId` to `tabs.find(...).term.inst`.
- **Event routing:** subscribe ONCE (page-level) to `pty.output`/`pty.exit`; look up the tab by `tabId`; ignore unknown ids.
- **Per-tab metrics:** poll `livecode.metrics {tabId: activeTabId}` on the 4s timer → write into the active tab's readout. `livecode.activeSessions` (global) on the 2s timer → shared list.
- **Close tab (`×`):** if running, `App.confirm` → `livecode.stop {tabId}` then `livecode.closeTab {tabId}` + remove from `tabs[]`; else `livecode.closeTab` + remove. Activate a neighbour. Always keep ≥1 tab (re-create an empty one if the last is closed).
- **On load:** dispose old DOM terminals; render from closure `tabs[]`; call `livecode.list` and merge — any backend tab not in the closure becomes a reconstructed tab; for each running tab call `livecode.attach {tabId}` and replay its buffer into that tab's xterm. If `tabs[]` ends up empty, create one default tab.
- All the existing helpers (`loadTickets`, `loadAgents`, `browse`, `browseCustomAgent`, `start`, `stop`, `resume`, `reset`, `updateButtons`, `saveConfig`, `mountTerminal`, `refit`) become **tab-scoped** — operate on the active tab and pass its `tabId`.

- [ ] **Step 1: Rewrite `livecode.js`** to the tabs model per the design above (tab bar, per-tab panel, per-tab xterm keyed by `tabId`, event routing by `tabId`, per-tab metrics, close-with-confirm, load-time reconcile with `livecode.list`, `focusTab`).
- [ ] **Step 2: Add CSS** in `app.css`: `.lc-tabbar`, `.lc-tab`, `.lc-tab.active`, `.lc-tab .close`, `.lc-newtab`, `.lc-terminal[hidden]`, and a compact per-tab `.lc-tab-metrics` readout.
- [ ] **Step 3: Build** (embeds `wwwroot` assets).

Run: `dotnet build`
Expected: `Build succeeded`.

- [ ] **Step 4: JS syntax check.**

Run: `node --check wwwroot/js/views/livecode.js`
Expected: no output (valid).

- [ ] **Step 5: Manual UI verification.**

Run: `dotnet run` → Live Code page. Verify: create a 2nd tab (inherits folder), pick a ticket + Start in each, switch tabs (each shows its own live terminal), per-tab tokens/context update for the active tab, close a running tab → confirm dialog → session stops. Navigate away and back → both sessions reattach.

- [ ] **Step 6: Commit.**

```bash
git add wwwroot/js/views/livecode.js wwwroot/css/app.css
git commit -m "Live Code: multiple sessions as tabs (per-tab terminal, controls, metrics)"
```

---

### Task 3: Sidebar dot (green/red by count) + hover popover

**Files:**
- Modify: `wwwroot/js/app.js`
- Modify: `wwwroot/index.html`
- Modify: `wwwroot/css/app.css`

**Interfaces:**
- Consumes: `livecode.running` → `{running,count}`; `livecode.list`; `Views.livecode.focusTab(tabId)`.

**Design:**
- `updateLiveDot()`: `const {count} = await Bridge.call('livecode.running')`; toggle dot `on` (green) when `count>0`, else `off` (red). Cache last `list` for the popover.
- `index.html`: wrap the Live Code nav item so a `.lc-nav-popover` can anchor to it; keep `#lc-nav-dot`.
- Popover: on `mouseenter` of the nav item, `livecode.list` → render rows `[ticketKey||Session N] · <folder basename> · running/stopped`; click a row → `location.hash='#livecode'` then `Views.livecode.focusTab(tabId)`. Empty → "No active sessions". Hide on `mouseleave`.
- `app.css`: `.nav-dot.off` red; `.lc-nav-popover` styled like `.modal` (absolute, bg, border, shadow), `.lc-nav-popover .row`.

- [ ] **Step 1: Update `updateLiveDot()` for `{count}` and cache the list; add popover build + hover wiring in `app.js`.**
- [ ] **Step 2: Add nav-item wrapper in `index.html` and popover/red-dot CSS in `app.css`.**
- [ ] **Step 3: Build + JS check.**

Run: `dotnet build` and `node --check wwwroot/js/app.js`
Expected: build succeeds; JS valid.

- [ ] **Step 4: Manual verification.**

Run: `dotnet run`. With no session: dot red, hover → "No active sessions". Start a session: dot green, hover lists it; click a row focuses that tab.

- [ ] **Step 5: Commit.**

```bash
git add wwwroot/js/app.js wwwroot/index.html wwwroot/css/app.css
git commit -m "Live Code: sidebar dot green/red by session count + hover session list"
```

---

### Task 4: Docs sync + final verify

**Files:**
- Modify: `CLAUDE.md`, `.claude/STRUCTURE.md`, `PROGRESS.md`

- [ ] **Step 1:** Update the Live Code paragraph in `CLAUDE.md` (multiple sessions as tabs; per-tab `tabId`; events carry `tabId`; sidebar count + hover list).
- [ ] **Step 2:** Update `.claude/STRUCTURE.md` — bridge-action catalog (`tabId` on session actions; new `livecode.list`, `livecode.closeTab`; `running` shape; event shape), and the `livecode.js`/`app.js` file notes.
- [ ] **Step 3:** Append a round entry to `PROGRESS.md` describing the tabs feature.
- [ ] **Step 4:** Final build.

Run: `dotnet build`
Expected: `Build succeeded`.

- [ ] **Step 5: Commit.**

```bash
git add CLAUDE.md .claude/STRUCTURE.md PROGRESS.md
git commit -m "Docs: Live Code multiple-session tabs"
```

---

## Self-Review

- **Spec coverage:** per-tab metrics (Task 2 readout + Task 1 `metrics{tabId}`) ✓; confirm-then-close (Task 2 Step 1) ✓; inherit last-used defaults (Task 2 `newTab`) ✓; tab labels/cap 6 (Task 2) ✓; background tabs stay live (Task 2 event routing, terminals stay mounted) ✓; `tabId` dictionary + stable across Stop/Resume (Task 1) ✓; `livecode.list` reconcile (Task 2 load) ✓; sidebar green/red + hover (Task 3) ✓; Stop keeps entry resumable, closeTab removes (Task 1) ✓; docs (Task 4) ✓.
- **Type consistency:** `livecode.running` → `{running,count}` used identically in Task 1/3; `list` shape `{tabId,folder,ticketKey,running,canResume,model}` consistent Task 1↔2↔3; `focusTab(tabId)` produced in Task 2, consumed in Task 3.
- **Placeholders:** none — each task names exact files, actions, payload shapes, and verification commands.
