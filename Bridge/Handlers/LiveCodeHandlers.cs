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
    }

    // All live tabs, keyed by tabId. Guarded by Gate for every read/write of a session's fields.
    private static readonly object Gate = new();
    private static readonly Dictionary<string, LiveSession> Tabs = new();

    /// <summary>Get-or-create the entry for a tab. Call under <see cref="Gate"/>.</summary>
    private static LiveSession Entry(string tabId)
    {
        if (!Tabs.TryGetValue(tabId, out var e)) { e = new LiveSession(); Tabs[tabId] = e; }
        return e;
    }

    public static void Register(MessageRouter router, PhotinoWindow window)
    {
        // Synchronous handlers return Task.FromResult (no Task.Run) — see the note in
        // SessionHandlers.Register on the null-return / unwrap-overload cancellation trap.

        router.Register("livecode.config", _ =>
        {
            var account = ClaudeAccount.Read();
            return Task.FromResult<object?>(new
            {
                jiraConfigured = JiraClient.FromSettings() is not null,
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

            // Fetch a larger page (newest first) then drop finished tickets by status name and take 3.
            // Filtering client-side (vs JQL) avoids errors if a status name doesn't exist in this instance.
            var page = await client.SearchIssuesAsync(AssignedJql, nextPageToken: null, maxResults: 25);
            var tickets = page.Issues
                .Where(i => i.Status is null || !ExcludedTicketStatuses.Contains(i.Status.Trim()))
                .Take(3)
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
            var permissionMode = bypass ? "bypassPermissions" : autoApprove ? "acceptEdits" : null;

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

            var shell = ShellResolver.Resolve(shellReq);
            var kickoff = BuildResumeCommand(shell.Kind, resumeId!, model, agent, permissionMode);
            return Task.FromResult<object?>(
                LaunchInPty(router, tabId, shell, folder, cols, rows, kickoff, permissionMode, resumeId!, model, ticketKey, trackSession: true));
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
            lock (Gate)
            {
                if (Tabs.TryGetValue(tabId, out var e)) { e.Session?.Dispose(); Tabs.Remove(tabId); }
            }
            return Task.FromResult<object?>(null);
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

        // Usage metrics for a specific tab's readout. Tokens come from the transcript DB (a light
        // incremental scan picks up the live session's new lines); context % is read live from the
        // tab's transcript. weekTokens + activeSessions are global (shared bottom panel).
        router.Register("livecode.metrics", payload =>
        {
            var tabId = RequireTabId(payload);
            try { new TranscriptScanner().Run(); } catch { /* best-effort refresh */ }

            long weekTokens;
            using (var conn = Db.Open())
                weekTokens = Rows.Scalar(conn,
                    "SELECT COALESCE(SUM(input_tokens + output_tokens), 0) FROM Sessions " +
                    "WHERE strftime('%Y-%W', started_at) = strftime('%Y-%W', 'now')");

            LiveSession? entry;
            lock (Gate) Tabs.TryGetValue(tabId, out entry);

            long sessionTokens = 0, contextTokens = 0;
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
                var rows = Rows.Query(conn,
                    "SELECT COALESCE(input_tokens + output_tokens, 0) AS t FROM Sessions WHERE file_path = $f",
                    ("$f", file));
                if (rows.Count > 0) sessionTokens = Convert.ToInt64(rows[0]["t"] ?? 0L);
            }

            var contextSize = ContextSizeFor(sizeModel);
            var activeSessions = ActiveSessions.Top(5, TimeSpan.FromMinutes(5))
                .Select(a => new { folder = a.Folder, contextTokens = a.ContextTokens, contextSize = a.ContextSize, contextPct = a.Percent });
            return Task.FromResult<object?>(new
            {
                weekTokens,
                sessionTokens,
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
        var ticketKey = SessionHandlers.GetString(payload, "ticketKey");
        var ticketSummary = SessionHandlers.GetString(payload, "ticketSummary");
        TryGetBool(payload, "autoApprove", out var autoApprove);
        TryGetBool(payload, "bypass", out var bypass);
        var cols = (short)GetInt(payload, "cols", 120);
        var rows = (short)GetInt(payload, "rows", 30);

        // bypass (confirmed in the UI) > auto-approve (acceptEdits) > default (manual prompts).
        var permissionMode = bypass ? "bypassPermissions" : autoApprove ? "acceptEdits" : null;

        if (!string.IsNullOrWhiteSpace(folder) && !Directory.Exists(folder))
            throw new ArgumentException($"Folder not found: {folder}");

        // Agent to use: a picked Custom Agent file (installed into .claude/agents so Claude finds it)
        // takes precedence over the dropdown selection.
        var agentName = !string.IsNullOrWhiteSpace(customAgent)
            ? AgentCatalog.InstallAgentFile(customAgent, folder)
            : null;
        agentName ??= agent;

        var shell = ShellResolver.Resolve(shellReq);

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
        var kickoff = string.IsNullOrWhiteSpace(ticketKey)
            ? null
            : BuildClaudeCommand(shell.Kind, ticketKey!, ticketSummary, description, model, agentName, permissionMode, sessionId);

        // Record the ticket ↔ session link now (before the transcript exists).
        if (kickoff is not null && !string.IsNullOrWhiteSpace(folder))
        {
            try
            {
                using var conn = Db.Open();
                SessionRepo.LinkLiveCodeSession(conn, sessionId, TranscriptPath(folder, sessionId), folder, ticketKey!);
            }
            catch { /* best-effort; never block the session on a link failure */ }
        }

        LaunchInPty(router, tabId, shell, folder, cols, rows, kickoff, permissionMode, sessionId, model, ticketKey,
            trackSession: kickoff is not null); // only track the session id when we launched claude
        return new { shell = shell.Kind, fellBack = shell.FellBack, kickoff = kickoff is not null, agentUsed = agentName };
    }

    /// <summary>Spawn the shell in a pseudo-console for the tab, wire output/exit/kickoff/auto-approve,
    /// and record the active + last session on the tab's entry. Shared by start and resume.</summary>
    private static object LaunchInPty(MessageRouter router, string tabId, ResolvedShell shell, string? folder,
        short cols, short rows, string? kickoff, string? permissionMode, string sessionId, string? model,
        string? ticketKey, bool trackSession)
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
        return new { shell = shell.Kind, fellBack = shell.FellBack, kickoff = kickoff is not null };
    }

    /// <summary>`claude --resume <id>` (+ model/agent/permission flags) — continues the prior conversation.</summary>
    private static string BuildResumeCommand(string shellKind, string sessionId, string? model, string? agent, string? permissionMode)
    {
        var sb = new StringBuilder("claude --resume ").Append(sessionId);
        if (model is "opus" or "sonnet" or "haiku") sb.Append(" --model ").Append(model);
        if (!string.IsNullOrWhiteSpace(agent)) sb.Append(" --agent ").Append(ShellQuote(shellKind, agent));
        if (permissionMode is not null) sb.Append(" --permission-mode ").Append(permissionMode);
        // Positional prompt: resume AND immediately tell Claude to continue the work.
        sb.Append(' ').Append(ShellQuote(shellKind, "continue"));
        return sb.ToString();
    }

    /// <summary>Build the interactive `claude` invocation typed into the shell to kick off a ticket.</summary>
    private static string BuildClaudeCommand(string shellKind, string key, string? summary,
        string? description, string? model, string? agentName, string? permissionMode, string sessionId)
    {
        var ticket = string.IsNullOrWhiteSpace(summary)
            ? $"JIRA ticket {key}"
            : $"JIRA ticket {key}: {summary}";
        // When an agent is chosen, tell Claude to USE that agent on the ticket (it invokes the
        // matching subagent from .claude/agents); otherwise work the ticket directly.
        var prompt = string.IsNullOrWhiteSpace(agentName)
            ? $"Work on {ticket}."
            : $"Use the {agentName} agent to work on {ticket}.";
        if (!string.IsNullOrWhiteSpace(description))
            prompt += " " + description.Trim();

        // Flatten to a single line: the command is TYPED into an interactive shell, and an
        // embedded newline would be read as Enter (submitting the command early).
        prompt = prompt.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        while (prompt.Contains("  ")) prompt = prompt.Replace("  ", " ");
        prompt = prompt.Trim();

        var sb = new StringBuilder("claude");
        sb.Append(" --session-id ").Append(sessionId);
        if (model is "opus" or "sonnet" or "haiku") sb.Append(" --model ").Append(model);
        if (permissionMode is not null) sb.Append(" --permission-mode ").Append(permissionMode);
        sb.Append(' ').Append(ShellQuote(shellKind, prompt));
        return sb.ToString();
    }

    /// <summary>Single-quote a value for the target shell (newlines survive inside single quotes in
    /// both PowerShell and bash, so multi-line prompts pass through intact).</summary>
    private static string ShellQuote(string shellKind, string s) => shellKind == "bash"
        ? "'" + s.Replace("'", "'\\''") + "'"
        : "'" + s.Replace("'", "''") + "'";

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
