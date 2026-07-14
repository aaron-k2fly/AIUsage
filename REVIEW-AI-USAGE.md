# Code Review — AIUsage v1

_Reviewed 2026-07-10 (high-effort multi-agent review: independent finders per bug category, each finding adversarially verified before inclusion). Scope: all C# and app JS; vendored Chart.js excluded. 10 findings, all verified CONFIRMED._

## Findings (most severe first)

### 1. Cross-process scan can double-count usage permanently — `Scanner/TranscriptScanner.cs`
`ScanLock` is a process-local `static object`, but two *processes* can scan concurrently (GUI auto-scan at startup + `AIUsage --scan` CLI, or two app instances). Both read the same `ScanState` offset, both parse the same new bytes, and both apply the **additive** token/tool-count upserts. WAL mode happily lets both writers proceed → inflated numbers, permanent, undetectable.
**Fix applied:** each file's read-state → parse → upsert → save-state now runs inside a single immediate SQLite transaction (Microsoft.Data.Sqlite's default `BeginTransaction()` issues `BEGIN IMMEDIATE`). A second scanner blocks on the write lock, then re-reads the updated offset and skips the already-parsed range. The in-process lock is kept as a fast path.

### 2. Crash window between commit and offset save re-adds the same bytes — `Scanner/TranscriptScanner.cs`
`SaveScanState` ran *after and outside* the transaction that committed the session counters. A crash (or `database is locked`) between the two left a stale offset, and the next scan re-added the same token counts.
**Fix applied:** same transaction restructure as #1 — the offset save commits atomically with the counters it describes.

### 3. Truncated/rewritten transcript leaves ghost sessions — `Scanner/TranscriptScanner.cs`
The shrink/rewrite path zeroed counters for **all** sessions of that file, but sessions absent from the rewritten file were never re-populated: permanent 0-token ghost rows with no dates, still holding stale auto ticket-links that inflate per-ticket session counts.
**Fix applied:** after a full reparse, sessions of that file whose id no longer appears in the parsed content are deleted (`SessionTicketLinks` rows cascade via the existing FK).

### 4. A `;`-only scan path bricks the Settings page — `Settings/SettingsStore.cs` / `Bridge/Handlers/SettingsHandlers.cs`
Saving `";"` as the scan path made `ScanRoots()` return an empty array; `settings.get` then threw `IndexOutOfRangeException` on `[0]` on every load, so the Settings page could never render again to let the user fix the value.
**Fix applied:** `ScanRoots()` falls back to the default `~/.claude/projects` root whenever the configured value parses to zero entries.

### 5. `--sql` is documented read-only but executes anything — `Program.cs`
`AIUsage --sql "DELETE FROM Sessions"` silently destroyed data despite the documented "read-only query" contract.
**Fix applied:** the CLI now sets `PRAGMA query_only=ON` on the connection before executing, so writes fail with an explicit error.

### 6. One 404 marks a ticket dead forever with no recovery — `Data/Repositories/TicketRepo.cs`
A ticket linked before it existed in JIRA (or during a transient 404) was branded `fetch_failed=1` and excluded from every future fetch path — no UI remedy.
**Fix applied:** "Sync all from JIRA" now retries *all* keys including previously-dead ones (volumes are tiny); a success clears the flag, a genuine dead key just stays flagged. Lazy background fetch still skips dead keys to avoid hammering.

### 7. Custom session titles silently revert to AI titles — `Scanner/SessionAggregator.cs` / `Data/Repositories/SessionRepo.cs`
`TitleIsCustom` only lived within one parse batch. An `ai-title` line arriving in a later incremental chunk overwrote a previously stored custom title via `COALESCE`.
**Fix applied:** new `title_is_custom` column (added via idempotent migration); the upsert never lets an AI title replace a stored custom title, and the custom flag is sticky.

### 8. Fixed 120 s bridge timeout breaks long ticket syncs — `wwwroot/js/bridge.js`
`tickets.sync` over a few hundred tickets exceeds 120 s: the promise rejected while the backend kept syncing, the button re-enabled, and a second click started a *concurrent* sync loop.
**Fix applied:** `Bridge.call` accepts a per-call timeout (0 = none); `tickets.sync` is called without a timeout, so the button stays disabled until the real result arrives.

### 9. Offline JIRA holds the whole dashboard hostage for 20 s — `Bridge/Handlers/StatsHandlers.cs`
`stats.dashboard` awaited the JIRA approximate-count inline, so with credentials configured but the host unreachable (off VPN), every dashboard render blocked ~20 s on purely-local data.
**Fix applied:** the share widget moved to its own `stats.share` action. The dashboard renders local stats immediately; the share panel appears asynchronously when/if JIRA answers.

### 10. Background scan completion wipes in-progress form input — `wwwroot/js/app.js`
The startup scan's completion called `App.refresh()` unconditionally, re-rendering the current view via `innerHTML` and destroying anything the user had typed (manual entry form, assign-ticket box).
**Fix applied:** scan completion now auto-refreshes only the Dashboard (which has no inputs); other views show the updated scan status in the sidebar and pick up fresh data on their next navigation.

## Post-review finding (2026-07-13) — "A task was cancelled" on confirm/remove
Found while investigating a user-reported bug the review missed. Every bridge handler written as `Task.Run<object?>(() => { …; return null; })` bound to the `Task.Run(Func<Task<object?>>)` **unwrap** overload (`null` is assignable to `Task<object?>`, and the more-derived overload wins). Unwrapping a *null* task produces a **Canceled** task, so `await handler(payload)` threw `TaskCanceledException` — reported to the UI as `ok:false "A task was cancelled"` **even though the DB write had already committed** (matching the symptom: "error toast shown, but it's actually updated"). Affected: confirmLink, removeLink, assignTicket, dismiss, reopen, manual.delete, settings.set (value-returning handlers dodged it because a `List`/anonymous object isn't `Task`-assignable).
**Fix applied:** all synchronous handlers now run inline and return `Task.FromResult<object?>(…)` — no per-handler `Task.Run` (`MessageRouter.OnMessage` already offloads to a pool thread). This removes the unwrap trap entirely.

## Not addressed (explicitly out of scope)
The review's cleanup-tier findings (duplicated row-reading code, redundant `Task.Run` wrappers, minor allocation churn) were deprioritized by the review itself beyond its 10-finding cap and are not correctness issues; candidates for a later `/simplify` pass.
