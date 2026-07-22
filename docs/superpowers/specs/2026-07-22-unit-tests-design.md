# Design: unit test suite for AIUsage

_2026-07-22 — branch `AIUSAGE-UNIT-TEST`_

## Goal

Add the project's first automated tests. Cover the main/high-value scenarios (not 100%).
Framework: **xUnit**. Scope: **pure-logic tests + SQLite integration tests**.

## Project layout

- New `AIUsage.Tests/AIUsage.Tests.csproj` (`net10.0`, xUnit 2.9.3), `ProjectReference` → `AIUsage.csproj`.
- `Microsoft.Data.Sqlite` + `SQLitePCLRaw.bundle_e_sqlite3` referenced directly so the native
  provider lands in the test output for the data-layer tests.
- **No `.sln`** — a solution would make the documented root `dotnet run` / `dotnet build` ambiguous.
  Root commands keep targeting the single app csproj; tests run via **`dotnet test AIUsage.Tests`**.
- Data-layer tests use a **fresh in-memory SQLite DB** per test (single kept-open connection,
  migrated to the current schema) via a `TestDb` helper — no temp files, parallel-safe.

## Coverage

### Pure logic (no I/O)
1. **`TicketKeyInferrer`** — allowlist filtering (in-list kept / out-of-list dropped / empty = allow-all),
   key-shape regex, project extraction; `IsRealBranch` rejects `main`/`master`/`HEAD`/empty and trims.
2. **`SessionAggregator.Aggregate`** (fed line arrays, no filesystem) — session grouping by id,
   sidechain skip, tool categorisation (Edit/Write/Read/Bash/Other), token accumulation
   (input/output/cache), model capture, timestamp min/max, title precedence (custom beats AI),
   user-message counting (string content only; array `tool_result` content ignored), malformed-line
   skip, and ticket-key **source priority** (branch > cwd > prompt_text; non-real branch not mined).
3. **`SessionAggregator.ContextWindow`** — haiku → 200k (case-insensitive); opus/sonnet/fable/full
   ids/null/"" → 1M.
4. **`XlsxWriter.Build`** — valid zip with expected parts, header + data cells, numeric-vs-string
   cell typing, null → self-closing cell, XML escaping (`<` `&` `"`), column-letter mapping
   (…Z, AA, AB), sheet-name sanitisation (illegal chars stripped, 31-char truncation).

### SQLite integration (in-memory DB)
5. **`Migrations.Run`** — stamps SchemaVersion 6, all tables exist, `ActivityCategories` seeded (7),
   idempotent (second run no-op), fresh DB has no `toolusage_backfill_pending`.
6. **`SessionRepo`** — `Upsert` → `Get`/`List`; additive token accumulation on re-upsert;
   `AddAutoLink` (creates ticket+link, review_state→linked) → `ConfirmLink` → `RemoveLink`
   (review_state→pending); `ResetCountersForFile` zeroes; `DeleteSessionsNotIn` prunes + cascades links.
7. **`TicketRepo` / `ManualEntryRepo`** — basic upsert/create → list round-trips.

### Out of scope
Photino bridge wiring, ConPTY/Porta.Pty terminal, live JIRA HTTP, DPAPI, the `oauth/usage` call,
ADF flatten (private; would need a production visibility change — declined), filesystem-walking
scanner internals (the logic they call — `Aggregate` — is covered instead).

## Doc sync
Update `CLAUDE.md` (the "no test project" line → the test command), `.claude/STRUCTURE.md`, `PROGRESS.md`.
