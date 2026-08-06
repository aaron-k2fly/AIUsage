using System.Text;
using System.Text.Json;
using AIUsage.Data;
using AIUsage.Data.Repositories;
using AIUsage.Jira;
using AIUsage.Platform;
using AIUsage.Scanner;
using AIUsage.Settings;
using AIUsage.Terminal;
using Photino.NET;

namespace AIUsage.Bridge.Handlers;

/// <summary>
/// Backs the Live Code page. Supports MULTIPLE concurrent Claude Code sessions, one per UI tab.
/// Each tab is identified by a frontend-minted <c>tabId</c> (a GUID that is stable across the
/// tab's Stop → Resume/Reset cycle) and owns its own <see cref="LiveSession"/> (ConPTY session,
/// working folder, launched Claude session id, model, and last-session metadata for Resume).
/// The <c>pty.output</c>/<c>pty.exit</c> stream events carry the <c>tabId</c> so the right
/// terminal renders them.
/// </summary>
public static class LiveCodeHandlers
{
    /// <summary>Latest tickets assigned to the current user (independent of the user's Fetch JQL).</summary>
    private const string AssignedJql = "assignee = currentUser() ORDER BY updated DESC";

    /// <summary>Finished statuses hidden from the "tickets to work on" picker (case-insensitive).</summary>
    private static readonly HashSet<string> ExcludedTicketStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "Closed", "Done", "Ready for Release" };

    /// <summary>How many assigned tickets the Live Code picker lists (setting
    /// <c>livecode_ticket_count</c>; default 3, clamped 1..20).</summary>
    private static int TicketCount() =>
        int.TryParse(SettingsStore.Get("livecode_ticket_count"), out var n) ? Math.Clamp(n, 1, 20) : 3;

    /// <summary>One <see cref="ConPtySession"/> per tab plus the metadata needed to locate its
    /// transcript and to Resume it after Stop. Mutated only under <see cref="Gate"/> because the
    /// ConPTY output/exit callbacks fire on the PTY read thread while handlers run on bridge pool
    /// threads.</summary>
    private sealed class LiveSession
    {
        public ConPtySession? Session;      // null when not running (stopped/exited but resumable)
        public string? ActiveFolder;        // cwd of the running session (to locate its transcript)
        public string? ActiveSessionId;     // claude --session-id we launched (exact transcript file)
        public string? ActiveModel;         // selected model (drives the context-window size)
        public string? LastSessionId;       // survives Stop, so Resume can `claude --resume <id>`
        public string? LastFolder;
        public string? TicketKey;           // for tab labels + the sidebar hover panel
        public WorktreeInfo? Worktree;      // set when the session runs in an isolated git worktree
    }

    // All live tabs, keyed by tabId. Guarded by Gate for every read/write of a session's fields.
    private static readonly object Gate = new();
    private static readonly Dictionary<string, LiveSession> Tabs = new();

    /// <summary>Elevated permission modes the USER has granted, per tab (see
    /// <see cref="GrantPermissionMode"/>). Guarded by <see cref="Gate"/>.</summary>
    private static readonly Dictionary<string, HashSet<string>> PermissionGrants = new();

    /// <summary>The app window, for host-side (native) confirmation dialogs. Null in headless CLI
    /// runs, in which case an elevated permission mode is never granted.</summary>
    private static PhotinoWindow? _window;

    /// <summary>Get-or-create the entry for a tab. Call under <see cref="Gate"/>.</summary>
    private static LiveSession Entry(string tabId)
    {
        if (!Tabs.TryGetValue(tabId, out var e)) { e = new LiveSession(); Tabs[tabId] = e; }
        return e;
    }

    public static void Register(MessageRouter router, PhotinoWindow window)
    {
        _window = window;   // for host-side permission confirmation (GrantPermissionMode)

        // Synchronous handlers return Task.FromResult (no Task.Run) — see the note in
        // SessionHandlers.Register on the null-return / unwrap-overload cancellation trap.

        router.Register("livecode.config", _ =>
        {
            var account = ClaudeAccount.Read();
            return Task.FromResult<object?>(new
            {
                jiraConfigured = JiraClient.FromSettings() is not null,
                ticketCount = TicketCount(),
                lastFolder = SettingsStore.Get("livecode_last_folder") ?? "",
                lastShell = SettingsStore.Get("livecode_last_shell") ?? "powershell",
                lastModel = SettingsStore.Get("livecode_last_model") ?? "",
                autoApprove = SettingsStore.Get("livecode_auto_approve") == "1",
                lastCustomAgent = SettingsStore.Get("livecode_custom_agent") ?? "",
                lastCustomAgentName = AgentCatalog.ReadAgentName(SettingsStore.Get("livecode_custom_agent")),
                // Warn on the page if the Claude Code CLI isn't installed.
                claudeInstalled = ClaudeCli.IsInstalled(),
                // If a key is set, the child env will have it stripped (subscription auth); the UI
                // warns and asks for confirmation before starting.
                apiKeyPresent = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")),
                // Subscription package + usage-limit reset, from Claude Code's own config.
                plan = account.Plan,
                usageResetsAt = account.UsageResetsAt?.ToString("o")
            });
        });

        router.Register("livecode.saveConfig", payload =>
        {
            SetIfPresent(payload, "folder", "livecode_last_folder");
            SetIfPresent(payload, "shell", "livecode_last_shell");
            SetIfPresent(payload, "model", "livecode_last_model");
            SetIfPresent(payload, "customAgent", "livecode_custom_agent");
            if (TryGetBool(payload, "autoApprove", out var auto))
                SettingsStore.Set("livecode_auto_approve", auto ? "1" : "0");
            return Task.FromResult<object?>(null);
        });

        router.Register("livecode.tickets", async _ =>
        {
            var client = JiraClient.FromSettings();
            if (client is null)
                return new { configured = false, tickets = Array.Empty<object>() };

            // Fetch a larger page (newest first) then drop finished tickets by status name and take the
            // configured count. Filtering client-side (vs JQL) avoids errors if a status name doesn't
            // exist in this instance; the page is oversized so filtering still leaves enough to take.
            var count = TicketCount();
            var page = await client.SearchIssuesAsync(AssignedJql, nextPageToken: null,
                maxResults: Math.Clamp(count * 3, 25, 60));
            var tickets = page.Issues
                .Where(i => i.Status is null || !ExcludedTicketStatuses.Contains(i.Status.Trim()))
                .Take(count)
                .Select(i => new
                {
                    key = i.Key,
                    summary = i.Summary,
                    status = i.Status,
                    issueType = i.IssueType,
                    priority = i.Priority
                }).ToList();
            return new { configured = true, tickets };
        });

        router.Register("livecode.listAgents", payload =>
        {
            var folder = SessionHandlers.GetString(payload, "folder");
            var agents = AgentCatalog.List(folder)
                .Select(a => new { name = a.Name, description = a.Description, scope = a.Scope });
            return Task.FromResult<object?>(agents);
        });

        router.Register("livecode.pickFolder", payload =>
        {
            var current = SessionHandlers.GetString(payload, "current");
            var path = FolderDialog.Pick(window, "Select working folder", current);
            return Task.FromResult<object?>(new { path });
        });

        // Pick a single agent .md file directly; also return its resolved agent name (confirmation).
        router.Register("livecode.pickAgentFile", payload =>
        {
            var current = SessionHandlers.GetString(payload, "current");
            var initialDir = !string.IsNullOrWhiteSpace(current) ? Path.GetDirectoryName(current) : null;
            var path = FolderDialog.PickFile(window, "Select an agent .md file", "Agent markdown", new[] { "*.md" }, initialDir);
            return Task.FromResult<object?>(new { path, agentName = AgentCatalog.ReadAgentName(path) });
        });

        // Whether a folder is a git repo — decides if the same-folder conflict dialog can offer
        // worktree isolation. Best-effort; never throws.
        router.Register("livecode.folderInfo", payload =>
        {
            var folder = SessionHandlers.GetString(payload, "folder");
            return Task.FromResult<object?>(new { isGitRepo = GitWorktree.IsGitRepo(folder) });
        });

        // Existing Claude Code sessions whose transcript lives in a folder — for the Resume Sessions
        // picker. Empty when the folder has no transcripts.
        router.Register("livecode.sessionsInFolder", payload =>
        {
            var folder = SessionHandlers.GetString(payload, "folder");
            var sessions = FolderSessions.List(folder)
                .Select(s => new { sessionId = s.SessionId, label = s.Label, updated = s.UpdatedIso });
            return Task.FromResult<object?>(new { sessions });
        });

        // --- live terminal (per tab) ---
        // Spawns the chosen shell in a pseudo-console for the tab, then (if a ticket is selected)
        // types a `claude …` command that works the ticket. ANTHROPIC_API_KEY is stripped from the
        // child env so Claude Code uses the subscription, never metered API billing.
        router.Register("livecode.start", payload => StartTicketSession(router, payload));

        // Resume this tab's previous Claude conversation via `claude --resume <id>` (works after Stop
        // or after the process exited — the conversation history lives in the transcript).
        router.Register("livecode.resume", payload =>
        {
            var tabId = RequireTabId(payload);
            var shellReq = SessionHandlers.GetString(payload, "shell") ?? "powershell";
            var folder = SessionHandlers.GetString(payload, "folder");
            var model = SessionHandlers.GetString(payload, "model");
            var agent = SessionHandlers.GetString(payload, "agent");
            TryGetBool(payload, "autoApprove", out var autoApprove);
            TryGetBool(payload, "bypass", out var bypass);
            var cols = (short)GetInt(payload, "cols", 120);
            var rows = (short)GetInt(payload, "rows", 30);
            var requestedMode = bypass ? "bypassPermissions" : autoApprove ? "acceptEdits" : null;

            string? resumeId, resumeFolder, ticketKey;
            lock (Gate)
            {
                Tabs.TryGetValue(tabId, out var e);
                resumeId = e?.LastSessionId;
                resumeFolder = e?.LastFolder;
                ticketKey = e?.TicketKey;
            }
            if (string.IsNullOrWhiteSpace(resumeId))
                throw new InvalidOperationException("No previous session to resume.");

            folder = string.IsNullOrWhiteSpace(folder) ? resumeFolder : folder;
            if (!string.IsNullOrWhiteSpace(folder) && !Directory.Exists(folder))
                throw new ArgumentException($"Folder not found: {folder}");

            // Elevated modes need the user's confirmation at a native dialog (AIU-07), same as start.
            var permissionMode = GrantPermissionMode(tabId, requestedMode, folder, ticketKey);
            var shell = ShellResolver.Resolve(shellReq);
            var kickoff = ClaudeCommand.BuildResume(shell.Kind, resumeId!, model, agent, permissionMode);
            return Task.FromResult<object?>(
                LaunchInPty(router, tabId, shell, folder, cols, rows, kickoff, permissionMode, resumeId!, model, ticketKey,
                            trackSession: true, requestedMode: requestedMode));
        });

        // Resume a specific past session chosen in the Resume Sessions picker, in the tab's terminal
        // (interactive `claude --resume <id>`, no continue prompt).
        router.Register("livecode.resumeSession", payload =>
        {
            var tabId = RequireTabId(payload);
            var shellReq = SessionHandlers.GetString(payload, "shell") ?? "powershell";
            var folder = SessionHandlers.GetString(payload, "folder");
            var sessionId = SessionHandlers.GetString(payload, "sessionId");
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("sessionId is required.");
            if (!string.IsNullOrWhiteSpace(folder) && !Directory.Exists(folder))
                throw new ArgumentException($"Folder not found: {folder}");
            TryGetBool(payload, "autoApprove", out var autoApprove);
            TryGetBool(payload, "bypass", out var bypass);
            var cols = (short)GetInt(payload, "cols", 120);
            var rows = (short)GetInt(payload, "rows", 30);
            var requestedMode = bypass ? "bypassPermissions" : autoApprove ? "acceptEdits" : null;
            var permissionMode = GrantPermissionMode(tabId, requestedMode, folder, ticketKey: null);

            var shell = ShellResolver.Resolve(shellReq);
            var kickoff = ClaudeCommand.BuildResumeSession(shell.Kind, sessionId!, permissionMode);
            return Task.FromResult<object?>(
                LaunchInPty(router, tabId, shell, folder, cols, rows, kickoff, permissionMode, sessionId!,
                            model: null, ticketKey: null, trackSession: true, requestedMode: requestedMode));
        });

        // Re-attach after navigating away and back: returns the tab's buffered output (base64) to
        // replay into a fresh terminal, or whether a stopped session can be resumed.
        router.Register("livecode.attach", payload =>
        {
            var tabId = RequireTabId(payload);
            ConPtySession? s;
            bool canResume;
            lock (Gate)
            {
                Tabs.TryGetValue(tabId, out var e);
                s = e?.Session;
                canResume = e?.LastSessionId is not null;
            }
            if (s is null)
                return Task.FromResult<object?>(new { running = false, canResume });
            return Task.FromResult<object?>(new { running = true, canResume = true, data = Convert.ToBase64String(s.Snapshot()) });
        });

        // All live tabs — lets the page rebuild tabs after navigation and feeds the sidebar hover
        // panel. Ordered by insertion (dictionary preserves insertion order in practice; the UI
        // does not rely on ordering beyond stability).
        router.Register("livecode.list", _ =>
        {
            List<object> tabs;
            lock (Gate)
                tabs = Tabs.Select(kv => (object)new
                {
                    tabId = kv.Key,
                    folder = kv.Value.ActiveFolder ?? kv.Value.LastFolder,
                    ticketKey = kv.Value.TicketKey,
                    running = kv.Value.Session is not null,
                    canResume = kv.Value.LastSessionId is not null,
                    model = kv.Value.ActiveModel
                }).ToList();
            return Task.FromResult<object?>(new { tabs });
        });

        router.Register("pty.input", payload =>
        {
            var tabId = RequireTabId(payload);
            var b64 = SessionHandlers.GetString(payload, "data");
            if (b64 is not null)
            {
                ConPtySession? s;
                lock (Gate) { Tabs.TryGetValue(tabId, out var e); s = e?.Session; }
                s?.Write(Convert.FromBase64String(b64));
            }
            return Task.FromResult<object?>(null);
        });

        router.Register("pty.resize", payload =>
        {
            var tabId = RequireTabId(payload);
            var cols = (short)GetInt(payload, "cols", 120);
            var rows = (short)GetInt(payload, "rows", 30);
            ConPtySession? s;
            lock (Gate) { Tabs.TryGetValue(tabId, out var e); s = e?.Session; }
            s?.Resize(cols, rows);
            return Task.FromResult<object?>(null);
        });

        // Stop the tab's session (tree-kill) but KEEP the entry so Resume still works.
        router.Register("livecode.stop", payload =>
        {
            var tabId = RequireTabId(payload);
            lock (Gate) { if (Tabs.TryGetValue(tabId, out var e)) StopSession(e); }
            return Task.FromResult<object?>(null);
        });

        // Close the tab entirely: stop the session (if any) and drop the entry from the dictionary
        // so it no longer counts toward the sidebar dot / list.
        router.Register("livecode.closeTab", payload =>
        {
            var tabId = RequireTabId(payload);
            WorktreeInfo? wt;
            lock (Gate)
            {
                // A closed tab's permission grant dies with it: a new tab (even one reusing the id)
                // must ask the user again.
                PermissionGrants.Remove(tabId);
                if (!Tabs.TryGetValue(tabId, out var e))
                    return Task.FromResult<object?>(new { worktreeKept = false, worktreeReason = (string?)null, worktreePath = (string?)null });
                e.Session?.Dispose();
                wt = e.Worktree;
                Tabs.Remove(tabId);
            }
            if (wt is null)
                return Task.FromResult<object?>(new { worktreeKept = false, worktreeReason = (string?)null, worktreePath = (string?)null });

            // Remove the worktree only if it's clean; otherwise keep it so no agent work is lost.
            var (removed, reason) = GitWorktree.TryRemoveIfClean(wt);
            return Task.FromResult<object?>(new { worktreeKept = !removed, worktreeReason = reason, worktreePath = wt.WorktreePath });
        });

        // Reset: gracefully quit the running Claude (/exit), then restart a FRESH Claude session on
        // the same ticket (new session id) in the same tab. Same payload as start.
        router.Register("livecode.reset", async payload =>
        {
            var tabId = RequireTabId(payload);
            ConPtySession? current;
            lock (Gate) { Tabs.TryGetValue(tabId, out var e); current = e?.Session; }
            if (current is not null)
            {
                try { current.Write(Encoding.UTF8.GetBytes("/exit\r")); } catch { /* quitting anyway */ }
                await Task.Delay(800); // give claude a moment to exit cleanly before the tree-kill
            }
            return await StartTicketSession(router, payload); // StopSession (tree-kill) + fresh ticket session
        });

        // Lightweight status for the sidebar indicator: green when any tab has a running session.
        router.Register("livecode.running", _ =>
        {
            int count;
            lock (Gate) count = Tabs.Values.Count(t => t.Session is not null);
            return Task.FromResult<object?>(new { running = count > 0, count });
        });

        // Cheap, scan-free active-sessions list so the panel can refresh in near-real-time.
        router.Register("livecode.activeSessions", _ =>
        {
            var list = ActiveSessions.Top(5, TimeSpan.FromMinutes(5))
                .Select(a => new { folder = a.Folder, contextTokens = a.ContextTokens, contextSize = a.ContextSize, contextPct = a.Percent });
            return Task.FromResult<object?>(new { activeSessions = list });
        });

        // Rolling usage-limit bars (session 5h + week 7d) from Anthropic's oauth/usage endpoint —
        // server-computed percentages, cached 5 min in ClaudeUsage so polling is cheap. Best-effort:
        // returns available:false when signed out / offline so the page just hides the bars.
        router.Register("livecode.usage", async _ =>
        {
            var u = await ClaudeUsage.ReadAsync();
            if (u is null || !u.HasAny) return new { available = false };
            return new
            {
                available = true,
                sessionPct = u.SessionPct,
                sessionResetsAt = u.SessionResetsAt?.ToString("o"),
                weekPct = u.WeekPct,
                weekResetsAt = u.WeekResetsAt?.ToString("o")
            };
        });

        // Usage metrics for a specific tab's readout. Tokens come from the transcript DB (a light
        // incremental scan picks up the live session's new lines); context % is read live from the
        // tab's transcript. weekTokens + activeSessions are global (shared bottom panel).
        router.Register("livecode.metrics", payload =>
        {
            var tabId = RequireTabId(payload);
            try { new TranscriptScanner().Run(); } catch { /* best-effort refresh */ }

            // Rolling last 7 days, summed from the per-day buckets — NOT from Sessions grouped by
            // started_at, which credited a multi-day session's whole spend to the week it began in
            // (so the figure collapsed to near-zero every Monday while long sessions kept burning
            // tokens). 7 days also matches the WEEK usage bar sitting beside this readout.
            long weekTokens;
            using (var conn = Db.Open())
                weekTokens = SessionDailyRepo.RollingTokens(conn, 7);

            LiveSession? entry;
            lock (Gate) Tabs.TryGetValue(tabId, out entry);

            long mainTokens = 0, agentTokens = 0, contextTokens = 0;
            long cacheCreation = 0, cacheRead = 0;
            // Prefer the selected model for the window size (per the UI); fall back to the actual
            // model recorded in the transcript (covers the "Default" selection).
            string? sizeModel = entry?.ActiveModel;
            var file = entry is null ? null : FindActiveTranscript(entry);
            if (file is not null)
            {
                var (ctx, transcriptModel) = SessionAggregator.LastContextTokens(file);
                contextTokens = ctx;
                if (string.IsNullOrEmpty(sizeModel)) sizeModel = transcriptModel;
                using var conn = Db.Open();
                // "Tokens" is input + output — the SAME formula as the dashboard/weekly figures, so the
                // two stay consistent. Cache is reported SEPARATELY (created + read) as its own field so
                // the readout can show "Tokens … · cache …" without inflating the headline number.
                var rows = Rows.Query(conn,
                    "SELECT COALESCE(input_tokens + output_tokens, 0) AS t, " +
                    "       COALESCE(cache_creation_tokens, 0) AS cc, COALESCE(cache_read_tokens, 0) AS cr " +
                    "FROM Sessions WHERE file_path = $f",
                    ("$f", file));
                if (rows.Count > 0)
                {
                    mainTokens = Convert.ToInt64(rows[0]["t"] ?? 0L);
                    cacheCreation = Convert.ToInt64(rows[0]["cc"] ?? 0L);
                    cacheRead = Convert.ToInt64(rows[0]["cr"] ?? 0L);
                }
                // Sub-agents (Task tool) write to <sessionId>/subagents/*.jsonl with the parent's
                // sessionId — the scanner skips them, so add their usage here for the true session total.
                var agent = SessionAggregator.SubagentTokens(file);
                agentTokens = agent.InOut;
                cacheCreation += agent.CacheCreation;
                cacheRead += agent.CacheRead;
            }
            var sessionTokens = mainTokens + agentTokens;   // in+out, dashboard-consistent, incl. agents
            var cacheTokens = cacheCreation + cacheRead;     // shown separately as "cache …"

            var contextSize = ContextSizeFor(sizeModel);
            var activeSessions = ActiveSessions.Top(5, TimeSpan.FromMinutes(5))
                .Select(a => new { folder = a.Folder, contextTokens = a.ContextTokens, contextSize = a.ContextSize, contextPct = a.Percent });
            return Task.FromResult<object?>(new
            {
                weekTokens,
                sessionTokens,
                mainTokens,
                agentTokens,
                cacheTokens,
                cacheCreation,
                cacheRead,
                contextTokens,
                contextSize,
                contextPct = contextSize > 0 ? (int)Math.Round(100.0 * contextTokens / contextSize) : 0,
                active = file is not null,
                activeSessions
            });
        });
    }

    /// <summary>The tab's exact transcript file, or null if we didn't launch claude (so we don't
    /// know the session id). Claude Code stores it at
    /// ~/.claude/projects/&lt;encoded-cwd&gt;/&lt;session-id&gt;.jsonl, where the cwd is encoded by replacing
    /// ':', '\\' and '/' with '-'. Using the exact session id avoids matching other concurrent
    /// Claude Code sessions running in the same folder.</summary>
    private static string? FindActiveTranscript(LiveSession e)
    {
        string? folder, sessionId;
        lock (Gate) { folder = e.ActiveFolder; sessionId = e.ActiveSessionId; }
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(sessionId)) return null;
        var file = TranscriptPath(folder, sessionId);
        return File.Exists(file) ? file : null;
    }

    /// <summary>Path Claude Code writes a session's transcript to: cwd encoded (':' '\\' '/' → '-')
    /// under ~/.claude/projects, file named &lt;session-id&gt;.jsonl.</summary>
    private static string TranscriptPath(string folder, string sessionId)
    {
        var encoded = folder.Replace(':', '-').Replace('\\', '-').Replace('/', '-');
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects", encoded, sessionId + ".jsonl");
    }

    private static long ContextSizeFor(string? model) => SessionAggregator.ContextWindow(model);

    /// <summary>Stop a tab's session (tree-kill) but keep the entry: clears the running/active fields
    /// while retaining LastSessionId/LastFolder so Resume still works. Call under <see cref="Gate"/>.</summary>
    private static void StopSession(LiveSession e)
    {
        e.Session?.Dispose();
        e.Session = null;
        e.ActiveFolder = null;
        e.ActiveSessionId = null;
        e.ActiveModel = null;
        // NOTE: e.Worktree is intentionally preserved here (needed for reset reuse + close cleanup).
    }

    /// <summary>Start (or restart) a Claude session on the selected ticket in a tab: resolve shell,
    /// fetch the ticket description, build the kickoff, auto-link the ticket, and launch. Shared by
    /// livecode.start and livecode.reset (reset first /exits the current session).</summary>
    private static async Task<object?> StartTicketSession(MessageRouter router, JsonElement payload)
    {
        var tabId = RequireTabId(payload);
        var shellReq = SessionHandlers.GetString(payload, "shell") ?? "powershell";
        var folder = SessionHandlers.GetString(payload, "folder");
        var model = SessionHandlers.GetString(payload, "model");
        var agent = SessionHandlers.GetString(payload, "agent");
        var customAgent = SessionHandlers.GetString(payload, "customAgent");
        // A ticket is optional (no ticket = a bare shell, no kickoff), but if one is given it must be
        // a real key: this used to be the ONLY unconstrained writer of SessionTicketLinks.ticket_key,
        // and the value originates from a remote JIRA server (2026-08 audit, AIU-04).
        var ticketKeyRaw = SessionHandlers.GetString(payload, "ticketKey");
        var ticketKey = string.IsNullOrWhiteSpace(ticketKeyRaw) ? null : TicketKey.Require(ticketKeyRaw);
        var ticketSummary = SessionHandlers.GetString(payload, "ticketSummary");
        TryGetBool(payload, "autoApprove", out var autoApprove);
        TryGetBool(payload, "bypass", out var bypass);
        var cols = (short)GetInt(payload, "cols", 120);
        var rows = (short)GetInt(payload, "rows", 30);

        if (!string.IsNullOrWhiteSpace(folder) && !Directory.Exists(folder))
            throw new ArgumentException($"Folder not found: {folder}");

        // bypass > auto-approve (acceptEdits) > default (manual prompts) — but an elevated mode only
        // takes effect once the USER confirms it at a native dialog; see GrantPermissionMode.
        var requestedMode = bypass ? "bypassPermissions" : autoApprove ? "acceptEdits" : null;
        var permissionMode = GrantPermissionMode(tabId, requestedMode, folder, ticketKey);
        var permissionDenied = requestedMode is not null && permissionMode is null;

        // Agent to use: a picked Custom Agent file (installed into .claude/agents so Claude finds it)
        // takes precedence over the dropdown selection.
        var agentName = !string.IsNullOrWhiteSpace(customAgent)
            ? AgentCatalog.InstallAgentFile(customAgent, folder)
            : null;
        agentName ??= agent;

        var shell = ShellResolver.Resolve(shellReq);

        // Optional git-worktree isolation (chosen in the same-folder conflict dialog): run this
        // session in a fresh worktree so concurrent agents can't collide. The launch folder,
        // transcript path, and auto-link all use the worktree cwd. A git failure propagates out
        // (the frontend toasts and the session doesn't start).
        var isolation = SessionHandlers.GetString(payload, "isolation");
        WorktreeInfo? worktree = null;
        var launchFolder = folder;
        if (string.Equals(isolation, "worktree", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(folder))
        {
            worktree = GitWorktree.Create(folder!, ticketKey ?? ("tab-" + tabId[..Math.Min(8, tabId.Length)]));
            launchFolder = worktree.Cwd;
        }

        // Best-effort fresh fetch of the ticket description for the kickoff prompt.
        string? description = null;
        if (!string.IsNullOrWhiteSpace(ticketKey))
        {
            try
            {
                var client = JiraClient.FromSettings();
                if (client is not null)
                {
                    var iss = await client.FetchIssueAsync(ticketKey);
                    if (iss is not null)
                    {
                        ticketSummary ??= iss.Summary;
                        description = iss.Description;
                        using var c = Db.Open();
                        TicketRepo.UpsertFetched(c, iss.Key, iss.Summary, iss.Status, iss.IssueType,
                            iss.Project, iss.Sprint, iss.Priority, iss.Updated, iss.Description);
                    }
                }
            }
            catch { /* kickoff proceeds with the summary the UI already has */ }
        }

        // Pin an explicit session id so metrics read exactly this session's transcript.
        var sessionId = Guid.NewGuid().ToString();
        var kickoff = ticketKey is null
            ? null
            : ClaudeCommand.BuildTicket(shell.Kind, ticketKey, ticketSummary, description, model, agentName, permissionMode, sessionId);

        // Record the ticket ↔ session link now (before the transcript exists).
        if (kickoff is not null && !string.IsNullOrWhiteSpace(launchFolder))
        {
            try
            {
                using var conn = Db.Open();
                SessionRepo.LinkLiveCodeSession(conn, sessionId, TranscriptPath(launchFolder, sessionId), launchFolder, ticketKey!);
            }
            catch { /* best-effort; never block the session on a link failure */ }
        }

        LaunchInPty(router, tabId, shell, launchFolder, cols, rows, kickoff, permissionMode, sessionId, model, ticketKey,
            trackSession: kickoff is not null); // only track the session id when we launched claude

        // Record the worktree on the tab entry so close can remove-if-clean and reset can reuse it.
        if (worktree is not null)
            lock (Gate) { if (Tabs.TryGetValue(tabId, out var e)) e.Worktree = worktree; }

        return new { shell = shell.Kind, fellBack = shell.FellBack, kickoff = kickoff is not null,
                     agentUsed = agentName, isolated = worktree is not null, worktreePath = worktree?.WorktreePath,
                     folder = launchFolder,
                     permissionMode, permissionDenied, permissionRequested = requestedMode };
    }

    /// <summary>Spawn the shell in a pseudo-console for the tab, wire output/exit/kickoff/auto-approve,
    /// and record the active + last session on the tab's entry. Shared by start and resume.</summary>
    private static object LaunchInPty(MessageRouter router, string tabId, ResolvedShell shell, string? folder,
        short cols, short rows, string? kickoff, string? permissionMode, string sessionId, string? model,
        string? ticketKey, bool trackSession, string? requestedMode = null)
    {
        // Best-effort auto-answer only when auto-approving (bypass never prompts; default = manual).
        var watcher = permissionMode == "acceptEdits" ? new PromptWatcher() : null;

        lock (Gate)
        {
            var e = Entry(tabId);
            StopSession(e); // stop any prior session in THIS tab (disposing sets _disposed → no exit event)
            var session = new ConPtySession();
            var kicked = 0;
            session.Output += bytes =>
            {
                router.PushEvent("pty.output", new { tabId, data = Convert.ToBase64String(bytes) });
                // Type the command once, after the shell has drawn its first prompt.
                if (kickoff is not null && Interlocked.Exchange(ref kicked, 1) == 0)
                {
                    var cmd = kickoff;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(600);
                        session.Write(Encoding.UTF8.GetBytes(cmd + "\r"));
                    });
                }
                var inject = watcher?.Observe(bytes);
                if (inject is not null) session.Write(inject);
            };
            session.Exited += code =>
            {
                router.PushEvent("pty.exit", new { tabId, code });
                // Mark only THIS tab's session stopped, and only if it hasn't been superseded by a
                // newer session in the same tab (identity check). Keep the entry (LastSessionId) for
                // Resume. Note: an intentional Stop/Reset disposes with _disposed=true, which
                // suppresses this event, so we only get here on a genuine self-exit.
                lock (Gate)
                {
                    if (Tabs.TryGetValue(tabId, out var ee) && ReferenceEquals(ee.Session, session))
                    {
                        ee.Session = null;
                        ee.ActiveSessionId = null;
                    }
                }
            };
            // Strip the API key so Claude Code falls back to subscription auth.
            var env = new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = null };
            session.Start(shell.Exe, Array.Empty<string>(), folder, env, cols, rows);
            e.Session = session;
            e.ActiveFolder = folder;
            e.ActiveModel = model;
            e.ActiveSessionId = trackSession ? sessionId : null;
            if (ticketKey is not null) e.TicketKey = ticketKey;
            if (trackSession) { e.LastSessionId = sessionId; e.LastFolder = folder; }
        }
        return new
        {
            shell = shell.Kind,
            fellBack = shell.FellBack,
            kickoff = kickoff is not null,
            permissionMode,
            permissionRequested = requestedMode,
            permissionDenied = requestedMode is not null && permissionMode is null
        };
    }

    /// <summary>
    /// Decide the permission mode a launch may actually use. The page can *ask* for `acceptEdits` or
    /// `bypassPermissions`, but only the user — at a **native** OS dialog the WebView cannot answer —
    /// can grant it, once per tab per mode. Returns the requested mode when granted, otherwise null
    /// (the session still starts, with normal manual prompts).
    ///
    /// Why: the bridge dispatches purely by action name and every other confirmation lives in the
    /// frontend, so the backend used to take `bypass`/`autoApprove` straight off the payload —
    /// meaning any script in the document could start an auto-approving agent with no user
    /// involvement (2026-08 audit, AIU-07). Moving just this decision to the host keeps the
    /// one-click flow for ordinary (manual-prompt) sessions while making the dangerous modes
    /// impossible to arm silently. Callers must be reachable only via unbounded-timeout bridge calls
    /// — the dialog waits on a human.
    /// </summary>
    private static string? GrantPermissionMode(string tabId, string? requested, string? folder, string? ticketKey)
    {
        if (requested is null) return null;                    // manual prompts need no grant

        lock (Gate)
            if (PermissionGrants.TryGetValue(tabId, out var already) && already.Contains(requested))
                return requested;                              // already confirmed for this tab

        var window = _window;
        if (window is null) return null;                       // no host UI → never elevate

        var where = string.IsNullOrWhiteSpace(folder) ? "(the app's current folder)" : folder!;
        var what = string.IsNullOrWhiteSpace(ticketKey) ? "none" : ticketKey!;
        // Both texts name file edits AND shell commands: auto-approve answers whatever prompt comes
        // up, including a Bash execution prompt, so describing it as "file edits" would understate it.
        var message = requested == "bypassPermissions"
            ? "Bypass ALL permission checks for this Live Code session?\n\n" +
              "Claude Code will run every action — editing files AND running shell commands — with NO confirmation.\n\n" +
              $"Folder: {where}\nTicket: {what}\n\n" +
              "Only allow this in a folder you trust. If you did not just start a session, choose No."
            : "Auto-approve confirmations for this Live Code session?\n\n" +
              "Claude Code will automatically answer the prompts it raises — including file edits AND shell commands — " +
              "so it can keep working without waiting for you.\n\n" +
              $"Folder: {where}\nTicket: {what}\n\n" +
              "Only allow this in a folder you trust. If you did not just start a session, choose No.";

        if (!MessageDialog.Confirm(window, "Live Code permissions", message))
            return null;

        lock (Gate)
        {
            if (!PermissionGrants.TryGetValue(tabId, out var granted))
            {
                granted = new HashSet<string>(StringComparer.Ordinal);
                PermissionGrants[tabId] = granted;
            }
            granted.Add(requested);
        }
        return requested;
    }

    /// <summary>Read the required tabId from a payload (throws with a clear message if absent).</summary>
    private static string RequireTabId(JsonElement payload)
    {
        var tabId = SessionHandlers.GetString(payload, "tabId");
        if (string.IsNullOrWhiteSpace(tabId))
            throw new ArgumentException("tabId is required.");
        return tabId;
    }

    private static int GetInt(JsonElement payload, string name, int dflt) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : dflt;

    private static void SetIfPresent(JsonElement payload, string jsonName, string settingKey)
    {
        var value = SessionHandlers.GetString(payload, jsonName);
        if (value is not null) SettingsStore.Set(settingKey, value.Trim());
    }

    private static bool TryGetBool(JsonElement payload, string name, out bool value)
    {
        value = false;
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var p))
            return false;
        switch (p.ValueKind)
        {
            case JsonValueKind.True: value = true; return true;
            case JsonValueKind.False: value = false; return true;
            default: return false;
        }
    }
}
