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
/// Backs the Live Code page. M1 scope: ticket picker (latest 3 assigned to the user),
/// working-folder picker, agent catalog, and persisted last-used selections. The live
/// terminal (ConPTY) and metrics land in later milestones.
/// </summary>
public static class LiveCodeHandlers
{
    /// <summary>Latest tickets assigned to the current user (independent of the user's Fetch JQL).</summary>
    private const string AssignedJql = "assignee = currentUser() ORDER BY updated DESC";

    // One live terminal at a time (v1). Guarded because output/exit callbacks fire on the
    // ConPTY read thread while handlers run on bridge pool threads.
    private static readonly object Gate = new();
    private static ConPtySession? _session;
    private static string? _activeFolder;       // cwd of the running session (to locate its transcript)
    private static string? _activeSessionId;    // claude --session-id we launched (exact transcript file)
    private static string? _activeModel;        // selected model (drives the context-window size)
    private static string? _lastSessionId;      // survives Stop, so Resume can `claude --resume <id>`
    private static string? _lastFolder;

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
                lastAgentsDir = SettingsStore.Get("livecode_agents_dir") ?? "",
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
            SetIfPresent(payload, "agentsDir", "livecode_agents_dir");
            if (TryGetBool(payload, "autoApprove", out var auto))
                SettingsStore.Set("livecode_auto_approve", auto ? "1" : "0");
            return Task.FromResult<object?>(null);
        });

        router.Register("livecode.tickets", async _ =>
        {
            var client = JiraClient.FromSettings();
            if (client is null)
                return new { configured = false, tickets = Array.Empty<object>() };

            var page = await client.SearchIssuesAsync(AssignedJql, nextPageToken: null, maxResults: 3);
            var tickets = page.Issues.Select(i => new
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
            var agentsDir = SessionHandlers.GetString(payload, "agentsDir");
            var agents = AgentCatalog.List(folder, agentsDir)
                .Select(a => new { name = a.Name, description = a.Description, scope = a.Scope });
            return Task.FromResult<object?>(agents);
        });

        router.Register("livecode.pickFolder", payload =>
        {
            var current = SessionHandlers.GetString(payload, "current");
            var path = FolderDialog.Pick(window, "Select working folder", current);
            return Task.FromResult<object?>(new { path });
        });

        // --- live terminal ---
        // Spawns the chosen shell in a pseudo-console, then (if a ticket is selected) types a
        // `claude …` command that works the ticket. ANTHROPIC_API_KEY is stripped from the child
        // env so Claude Code uses the subscription, never metered API billing.
        router.Register("livecode.start", async payload =>
        {
            var shellReq = SessionHandlers.GetString(payload, "shell") ?? "powershell";
            var folder = SessionHandlers.GetString(payload, "folder");
            var model = SessionHandlers.GetString(payload, "model");
            var agent = SessionHandlers.GetString(payload, "agent");
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

            // Pin an explicit session id so metrics read exactly this session's transcript
            // (the folder's project dir can hold other concurrent Claude Code sessions).
            var sessionId = Guid.NewGuid().ToString();
            var kickoff = string.IsNullOrWhiteSpace(ticketKey)
                ? null
                : BuildClaudeCommand(shell.Kind, ticketKey!, ticketSummary, description, model, agent, permissionMode, sessionId);

            // Record the ticket ↔ session link now (before the transcript exists) so working a
            // ticket via Live Code is captured automatically.
            if (kickoff is not null && !string.IsNullOrWhiteSpace(folder))
            {
                try
                {
                    using var conn = Db.Open();
                    SessionRepo.LinkLiveCodeSession(conn, sessionId, TranscriptPath(folder, sessionId), folder, ticketKey!);
                }
                catch { /* best-effort; never block the session on a link failure */ }
            }

            return LaunchInPty(router, shell, folder, cols, rows, kickoff, permissionMode, sessionId, model,
                trackSession: kickoff is not null); // only track the session id when we launched claude
        });

        // Resume the previous session's Claude conversation via `claude --resume <id>` (works after
        // Stop or after the process exited — the conversation history lives in the transcript).
        router.Register("livecode.resume", payload =>
        {
            var shellReq = SessionHandlers.GetString(payload, "shell") ?? "powershell";
            var folder = SessionHandlers.GetString(payload, "folder");
            var model = SessionHandlers.GetString(payload, "model");
            var agent = SessionHandlers.GetString(payload, "agent");
            TryGetBool(payload, "autoApprove", out var autoApprove);
            TryGetBool(payload, "bypass", out var bypass);
            var cols = (short)GetInt(payload, "cols", 120);
            var rows = (short)GetInt(payload, "rows", 30);
            var permissionMode = bypass ? "bypassPermissions" : autoApprove ? "acceptEdits" : null;

            string? resumeId, resumeFolder;
            lock (Gate) { resumeId = _lastSessionId; resumeFolder = _lastFolder; }
            if (string.IsNullOrWhiteSpace(resumeId))
                throw new InvalidOperationException("No previous session to resume.");

            folder = string.IsNullOrWhiteSpace(folder) ? resumeFolder : folder;
            if (!string.IsNullOrWhiteSpace(folder) && !Directory.Exists(folder))
                throw new ArgumentException($"Folder not found: {folder}");

            var shell = ShellResolver.Resolve(shellReq);
            var kickoff = BuildResumeCommand(shell.Kind, resumeId!, model, agent, permissionMode);
            return Task.FromResult<object?>(
                LaunchInPty(router, shell, folder, cols, rows, kickoff, permissionMode, resumeId!, model, trackSession: true));
        });

        // Re-attach after navigating away and back: returns the running session's buffered output
        // (base64) to replay into a fresh terminal, or whether a stopped session can be resumed.
        router.Register("livecode.attach", _ =>
        {
            ConPtySession? s;
            string? lastId;
            lock (Gate) { s = _session; lastId = _lastSessionId; }
            if (s is null)
                return Task.FromResult<object?>(new { running = false, canResume = lastId is not null });
            return Task.FromResult<object?>(new { running = true, canResume = true, data = Convert.ToBase64String(s.Snapshot()) });
        });

        router.Register("pty.input", payload =>
        {
            var b64 = SessionHandlers.GetString(payload, "data");
            if (b64 is not null)
            {
                ConPtySession? s;
                lock (Gate) s = _session;
                s?.Write(Convert.FromBase64String(b64));
            }
            return Task.FromResult<object?>(null);
        });

        router.Register("pty.resize", payload =>
        {
            var cols = (short)GetInt(payload, "cols", 120);
            var rows = (short)GetInt(payload, "rows", 30);
            ConPtySession? s;
            lock (Gate) s = _session;
            s?.Resize(cols, rows);
            return Task.FromResult<object?>(null);
        });

        router.Register("livecode.stop", _ =>
        {
            lock (Gate) StopSession();
            return Task.FromResult<object?>(null);
        });

        // Reset: gracefully quit Claude (/exit), then tear down and open a fresh shell.
        router.Register("livecode.reset", async payload =>
        {
            var shellReq = SessionHandlers.GetString(payload, "shell") ?? "powershell";
            var folder = SessionHandlers.GetString(payload, "folder");
            var model = SessionHandlers.GetString(payload, "model");
            var cols = (short)GetInt(payload, "cols", 120);
            var rows = (short)GetInt(payload, "rows", 30);

            if (!string.IsNullOrWhiteSpace(folder) && !Directory.Exists(folder))
                throw new ArgumentException($"Folder not found: {folder}");

            ConPtySession? current;
            lock (Gate) current = _session;
            if (current is not null)
            {
                try { current.Write(Encoding.UTF8.GetBytes("/exit\r")); } catch { /* quitting anyway */ }
                await Task.Delay(800); // give claude a moment to exit cleanly before the tree-kill
            }

            // Fresh shell, no kickoff — LaunchInPty stops the old session (tree-kill) then opens anew.
            return LaunchInPty(router, shell: ShellResolver.Resolve(shellReq), folder, cols, rows,
                kickoff: null, permissionMode: null, sessionId: Guid.NewGuid().ToString(), model: model, trackSession: false);
        });

        // Lightweight status for the sidebar indicator (green when a session is running).
        router.Register("livecode.running", _ =>
        {
            bool running;
            lock (Gate) running = _session is not null;
            return Task.FromResult<object?>(new { running });
        });

        // Cheap, scan-free active-sessions list so the panel can refresh in near-real-time.
        router.Register("livecode.activeSessions", _ =>
        {
            var list = ActiveSessions.Top(2, TimeSpan.FromMinutes(5))
                .Select(a => new { folder = a.Folder, contextTokens = a.ContextTokens, contextSize = a.ContextSize, contextPct = a.Percent });
            return Task.FromResult<object?>(new { activeSessions = list });
        });

        // Usage metrics for the bottom panel. Tokens come from the transcript DB (a light
        // incremental scan picks up the live session's new lines); context % is read live from
        // the active session's transcript. Plan/tier is not shown (not exposed by the CLI).
        router.Register("livecode.metrics", _ =>
        {
            try { new TranscriptScanner().Run(); } catch { /* best-effort refresh */ }

            long weekTokens;
            using (var conn = Db.Open())
                weekTokens = Rows.Scalar(conn,
                    "SELECT COALESCE(SUM(input_tokens + output_tokens), 0) FROM Sessions " +
                    "WHERE strftime('%Y-%W', started_at) = strftime('%Y-%W', 'now')");

            long sessionTokens = 0, contextTokens = 0;
            // Prefer the selected model for the window size (per the UI); fall back to the actual
            // model recorded in the transcript (covers the "Default" selection).
            string? sizeModel;
            lock (Gate) sizeModel = _activeModel;
            var file = FindActiveTranscript();
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
            var activeSessions = ActiveSessions.Top(2, TimeSpan.FromMinutes(5))
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

    /// <summary>The launched session's exact transcript file, or null if we didn't launch claude
    /// (so we don't know the session id). Claude Code stores it at
    /// ~/.claude/projects/&lt;encoded-cwd&gt;/&lt;session-id&gt;.jsonl, where the cwd is encoded by replacing
    /// ':', '\\' and '/' with '-'. Using the exact session id avoids matching other concurrent
    /// Claude Code sessions running in the same folder.</summary>
    private static string? FindActiveTranscript()
    {
        string? folder, sessionId;
        lock (Gate) { folder = _activeFolder; sessionId = _activeSessionId; }
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

    private static void StopSession()
    {
        _session?.Dispose();
        _session = null;
        _activeFolder = null;
        _activeSessionId = null;
        _activeModel = null;
    }

    /// <summary>Spawn the shell in a pseudo-console, wire output/exit/kickoff/auto-approve, and record
    /// the active + last session. Shared by start and resume.</summary>
    private static object LaunchInPty(MessageRouter router, ResolvedShell shell, string? folder,
        short cols, short rows, string? kickoff, string? permissionMode, string sessionId, string? model, bool trackSession)
    {
        // Best-effort auto-answer only when auto-approving (bypass never prompts; default = manual).
        var watcher = permissionMode == "acceptEdits" ? new PromptWatcher() : null;

        lock (Gate)
        {
            StopSession();
            var session = new ConPtySession();
            var kicked = 0;
            session.Output += bytes =>
            {
                router.PushEvent("pty.output", new { data = Convert.ToBase64String(bytes) });
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
                router.PushEvent("pty.exit", new { code });
                lock (Gate) { _session?.Dispose(); _session = null; }
            };
            // Strip the API key so Claude Code falls back to subscription auth.
            var env = new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = null };
            session.Start(shell.Exe, Array.Empty<string>(), folder, env, cols, rows);
            _session = session;
            _activeFolder = folder;
            _activeModel = model;
            _activeSessionId = trackSession ? sessionId : null;
            if (trackSession) { _lastSessionId = sessionId; _lastFolder = folder; }
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
        string? description, string? model, string? agent, string? permissionMode, string sessionId)
    {
        var prompt = string.IsNullOrWhiteSpace(summary)
            ? $"Work on JIRA ticket {key}."
            : $"Work on JIRA ticket {key}: {summary}.";
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
        if (!string.IsNullOrWhiteSpace(agent)) sb.Append(" --agent ").Append(ShellQuote(shellKind, agent));
        if (permissionMode is not null) sb.Append(" --permission-mode ").Append(permissionMode);
        sb.Append(' ').Append(ShellQuote(shellKind, prompt));
        return sb.ToString();
    }

    /// <summary>Single-quote a value for the target shell (newlines survive inside single quotes in
    /// both PowerShell and bash, so multi-line prompts pass through intact).</summary>
    private static string ShellQuote(string shellKind, string s) => shellKind == "bash"
        ? "'" + s.Replace("'", "'\\''") + "'"
        : "'" + s.Replace("'", "''") + "'";

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
