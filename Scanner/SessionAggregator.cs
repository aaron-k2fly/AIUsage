using System.Text.Json;

namespace AIUsage.Scanner;

/// <summary>
/// Per-session aggregate built from transcript lines. All counters are additive so
/// incremental parses can be accumulated into existing DB rows.
/// </summary>
public sealed class SessionAggregate
{
    public required string SessionId { get; init; }
    public required string FilePath { get; init; }
    public string? ProjectDir;
    public string? GitBranch;
    public string? Title;
    public bool TitleIsCustom;
    public string? Model;
    public string? StartedAt;
    public string? EndedAt;
    public long InputTokens, OutputTokens, CacheCreationTokens, CacheReadTokens;
    public int EditCount, WriteCount, ReadCount, BashCount, OtherToolCount, UserMessageCount;
    public string? CcVersion;
    /// <summary>key → inferred_from (branch|cwd|prompt_text), highest-priority source wins.</summary>
    public Dictionary<string, string> TicketKeys { get; } = [];
}

/// <summary>
/// Maps raw Claude Code transcript JSONL lines to session aggregates.
/// This is the ONLY place that knows the (undocumented) transcript schema —
/// contain any format-drift fixes here. Unknown line types and malformed lines
/// are skipped, never fatal.
/// </summary>
public sealed class SessionAggregator(TicketKeyInferrer inferrer)
{
    private static int SourcePriority(string source) =>
        source switch { "branch" => 0, "cwd" => 1, _ => 2 };

    public Dictionary<string, SessionAggregate> Aggregate(IEnumerable<string> lines, string filePath)
    {
        var sessions = new Dictionary<string, SessionAggregate>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                ParseLine(doc.RootElement, filePath, sessions);
            }
            catch (JsonException)
            {
                // malformed / truncated line — skip
            }
        }

        foreach (var agg in sessions.Values)
        {
            if (inferrer.IsRealBranch(agg.GitBranch))
                AddKeys(agg, inferrer.Extract(agg.GitBranch), "branch");
            AddKeys(agg, inferrer.Extract(agg.ProjectDir), "cwd");
        }

        return sessions;
    }

    private void ParseLine(JsonElement root, string filePath, Dictionary<string, SessionAggregate> sessions)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (!TryGetString(root, "sessionId", out var sessionId)) return;
        if (root.TryGetProperty("isSidechain", out var sc) && sc.ValueKind == JsonValueKind.True) return;

        if (!sessions.TryGetValue(sessionId, out var agg))
            sessions[sessionId] = agg = new SessionAggregate { SessionId = sessionId, FilePath = filePath };

        if (TryGetString(root, "timestamp", out var ts))
        {
            if (agg.StartedAt is null || string.CompareOrdinal(ts, agg.StartedAt) < 0) agg.StartedAt = ts;
            if (agg.EndedAt is null || string.CompareOrdinal(ts, agg.EndedAt) > 0) agg.EndedAt = ts;
        }
        if (TryGetString(root, "cwd", out var cwd)) agg.ProjectDir = cwd;
        if (TryGetString(root, "gitBranch", out var branch) && branch.Length > 0) agg.GitBranch = branch;
        if (TryGetString(root, "version", out var version)) agg.CcVersion = version;

        if (!TryGetString(root, "type", out var type)) return;
        switch (type)
        {
            case "assistant": ParseAssistant(root, agg); break;
            case "user": ParseUser(root, agg); break;
            case "ai-title":
                if (!agg.TitleIsCustom && TryGetString(root, "aiTitle", out var aiTitle))
                    agg.Title = aiTitle;
                break;
            case "custom-title":
                if (TryGetString(root, "customTitle", out var customTitle))
                {
                    agg.Title = customTitle;
                    agg.TitleIsCustom = true;
                }
                break;
        }
    }

    private static void ParseAssistant(JsonElement root, SessionAggregate agg)
    {
        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return;

        if (TryGetString(msg, "model", out var model)) agg.Model = model;

        if (msg.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            agg.InputTokens += GetLong(usage, "input_tokens");
            agg.OutputTokens += GetLong(usage, "output_tokens");
            agg.CacheCreationTokens += GetLong(usage, "cache_creation_input_tokens");
            agg.CacheReadTokens += GetLong(usage, "cache_read_input_tokens");
        }

        if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (!TryGetString(block, "type", out var blockType) || blockType != "tool_use") continue;
                TryGetString(block, "name", out var tool);
                switch (tool)
                {
                    case "Edit" or "MultiEdit" or "NotebookEdit": agg.EditCount++; break;
                    case "Write": agg.WriteCount++; break;
                    case "Read" or "Glob" or "Grep": agg.ReadCount++; break;
                    case "Bash" or "PowerShell" or "BashOutput": agg.BashCount++; break;
                    default: agg.OtherToolCount++; break;
                }
            }
        }
    }

    private void ParseUser(JsonElement root, SessionAggregate agg)
    {
        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return;
        // string content = a real user prompt; array content = tool_result noise (never
        // mine those for ticket keys — they contain things like UTF-8, ISO-8859 etc.)
        if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        {
            agg.UserMessageCount++;
            AddKeys(agg, inferrer.Extract(content.GetString()), "prompt_text");
        }
    }

    private static void AddKeys(SessionAggregate agg, IEnumerable<string> keys, string source)
    {
        foreach (var key in keys)
        {
            if (!agg.TicketKeys.TryGetValue(key, out var existing) ||
                SourcePriority(source) < SourcePriority(existing))
            {
                agg.TicketKeys[key] = source;
            }
        }
    }

    /// <summary>Live view of a transcript for the Live Code panels: the working dir, the model of
    /// the latest assistant turn, and the tokens sent as context on that turn (input + cache read +
    /// cache creation; output doesn't count toward the window).</summary>
    public sealed record LiveInfo(string? Cwd, string? Model, long ContextTokens);

    public static LiveInfo ReadLive(string filePath)
    {
        string? cwd = null, model = null;
        long ctx = 0;
        try
        {
            foreach (var line in File.ReadLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (TryGetString(root, "cwd", out var c)) cwd = c;
                    if (!TryGetString(root, "type", out var type) || type != "assistant") continue;
                    if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
                    if (TryGetString(msg, "model", out var m)) model = m;
                    if (msg.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        var t = GetLong(usage, "input_tokens")
                              + GetLong(usage, "cache_read_input_tokens")
                              + GetLong(usage, "cache_creation_input_tokens");
                        if (t > 0) ctx = t; // most recent non-zero turn wins
                    }
                }
                catch (JsonException) { /* skip truncated/partial line */ }
            }
        }
        catch (IOException) { /* file busy — caller falls back to previous value */ }
        return new LiveInfo(cwd, model, ctx);
    }

    /// <summary>Context-window size for a model: 1M for current Claude models, 200k for Haiku. Accepts
    /// a full transcript model id or a dropdown alias (opus/sonnet/haiku/fable).</summary>
    public static long ContextWindow(string? model) =>
        !string.IsNullOrEmpty(model) && model.Contains("haiku", StringComparison.OrdinalIgnoreCase)
            ? 200_000 : 1_000_000;

    /// <summary>Latest assistant turn's context tokens + model (Live Code metrics). See <see cref="ReadLive"/>.</summary>
    public static (long ContextTokens, string? Model) LastContextTokens(string filePath)
    {
        var info = ReadLive(filePath);
        return (info.ContextTokens, info.Model);
    }

    /// <summary>Token usage a session's sub-agents consumed, split into the headline in+out figure
    /// (<see cref="InOut"/>, matches the dashboard formula) and the cache portion
    /// (<see cref="CacheCreation"/> + <see cref="CacheRead"/>), so the Live Code readout can show
    /// "Tokens … · cache …". <see cref="Cache"/> is the sum of both cache fields.</summary>
    public readonly record struct SubagentUsage(long InOut, long CacheCreation, long CacheRead)
    {
        public long Cache => CacheCreation + CacheRead;
    }

    /// <summary>Sum sub-agent usage for a session. Claude Code writes each sub-agent to
    /// ~/.claude/projects/&lt;encoded-cwd&gt;/&lt;session-id&gt;/subagents/agent-*.jsonl (sidechain turns that
    /// carry the parent's sessionId, so the scanner and the main-file token query both exclude them).
    /// Given the main transcript path &lt;dir&gt;/&lt;session-id&gt;.jsonl, this sums every agent-*.jsonl found
    /// anywhere under &lt;dir&gt;/&lt;session-id&gt;/ (recursive, so nested sub-agents are counted too). Returns
    /// zeroes if the session has no sub-agent dir. Best-effort: unreadable files/lines are skipped.</summary>
    public static SubagentUsage SubagentTokens(string mainTranscriptPath)
    {
        var dir = Path.GetDirectoryName(mainTranscriptPath);
        var id = Path.GetFileNameWithoutExtension(mainTranscriptPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(id)) return default;
        var sessionDir = Path.Combine(dir, id);
        if (!Directory.Exists(sessionDir)) return default;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(sessionDir, "agent-*.jsonl", SearchOption.AllDirectories); }
        catch (IOException) { return default; }
        catch (UnauthorizedAccessException) { return default; }

        long inOut = 0, cc = 0, cr = 0;
        foreach (var file in files)
        {
            var u = SumUsage(file);
            inOut += u.InOut; cc += u.CacheCreation; cr += u.CacheRead;
        }
        return new SubagentUsage(inOut, cc, cr);
    }

    /// <summary>Sum a transcript's assistant-turn usage, split into in+out and the two cache fields.
    /// Best-effort — IO/parse errors yield a partial sum.</summary>
    private static SubagentUsage SumUsage(string filePath)
    {
        long inOut = 0, cc = 0, cr = 0;
        try
        {
            foreach (var line in File.ReadLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (!TryGetString(root, "type", out var type) || type != "assistant") continue;
                    if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
                    if (msg.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        inOut += GetLong(usage, "input_tokens") + GetLong(usage, "output_tokens");
                        cc += GetLong(usage, "cache_creation_input_tokens");
                        cr += GetLong(usage, "cache_read_input_tokens");
                    }
                }
                catch (JsonException) { /* skip truncated/partial line */ }
            }
        }
        catch (IOException) { /* file busy — return partial */ }
        return new SubagentUsage(inOut, cc, cr);
    }

    // ── Session detail (on-demand deep re-parse of one transcript) ────────────────────────
    // The list/scan path buckets tools into edit/write/read/bash/other and keeps a single model;
    // the detail page wants the exact breakdown, so it re-reads that one file (same pattern as
    // ReadLive/SubagentTokens above). Nothing here touches the DB.

    /// <summary>Per-model token usage within a session (assistant turns tagged with that model id).</summary>
    public sealed record ModelUsage(long Input, long Output, long CacheCreation, long CacheRead);

    /// <summary>Rich, display-only aggregate for the Session Detail page. Built from the main transcript
    /// only (sub-agent totals are reported separately by <see cref="SubagentTokens"/>).</summary>
    public sealed class SessionDetail
    {
        public long InputTokens, OutputTokens, CacheCreationTokens, CacheReadTokens;
        /// <summary>Assistant turns.</summary>
        public int ReplyCount;
        /// <summary>Real user prompts (string message.content — array content is tool-result noise).</summary>
        public int PromptCount;
        /// <summary>Total tool_use blocks across all assistant turns.</summary>
        public int ToolCallCount;
        /// <summary>Exact tool name → count (e.g. Bash, Grep, Skill, mcp__atlassian__getJiraIssue).</summary>
        public Dictionary<string, int> ToolCounts { get; } = [];
        /// <summary>Sub-agent type → launch count (from Agent/Task tool_use <c>subagent_type</c>).</summary>
        public Dictionary<string, int> Agents { get; } = [];
        /// <summary>Skill name → invocation count (from Skill tool_use <c>skill</c>).</summary>
        public Dictionary<string, int> Skills { get; } = [];
        /// <summary>Hook name → fire count (from <c>attachment.type = hook_success|hook_error</c> lines).</summary>
        public Dictionary<string, int> Hooks { get; } = [];
        /// <summary>Model id → token usage.</summary>
        public Dictionary<string, ModelUsage> Models { get; } = [];
        /// <summary>Time split (ms): assistant/tool working, human active, and idle. These partition
        /// (ended − started) exactly. See <see cref="ReadDetail"/> for the classification rule.</summary>
        public long AgentMs, ActiveMs, IdleMs;
        public string? StartedAt, EndedAt;
    }

    /// <summary>Gaps longer than this between transcript events count as idle (the user stepped away)
    /// rather than agent/active time.</summary>
    private const double IdleGapSeconds = 300;

    /// <summary>Deep-parse a single transcript into a <see cref="SessionDetail"/>: exact per-tool counts,
    /// per-model token usage, reply/prompt/tool-call counts, and an Agent/Active/Idle time split.
    /// Only lines for <paramref name="sessionId"/> are counted; sidechains are skipped (they're summed
    /// separately by <see cref="SubagentTokens"/>). Best-effort — malformed lines/IO errors yield a
    /// partial result.</summary>
    public static SessionDetail ReadDetail(string filePath, string sessionId)
    {
        var d = new SessionDetail();
        var modelUsage = new Dictionary<string, long[]>(); // model → [in, out, cacheCreation, cacheRead]
        var events = new List<(DateTimeOffset Ts, int Kind)>(); // Kind: 0 = prompt, 1 = assistant, 2 = tool-result

        try
        {
            foreach (var line in File.ReadLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (!TryGetString(root, "sessionId", out var sid) || sid != sessionId) continue;
                    if (root.TryGetProperty("isSidechain", out var scv) && scv.ValueKind == JsonValueKind.True) continue;
                    if (!TryGetString(root, "type", out var type)) continue;

                    DateTimeOffset? ts = null;
                    if (TryGetString(root, "timestamp", out var tsStr) &&
                        DateTimeOffset.TryParse(tsStr, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                        ts = parsed;

                    if (type == "assistant")
                    {
                        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
                        d.ReplyCount++;
                        if (ts is { } at) events.Add((at, 1));

                        var model = TryGetString(msg, "model", out var m) && m.Length > 0 ? m : "unknown";
                        if (msg.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                        {
                            long i = GetLong(usage, "input_tokens"), o = GetLong(usage, "output_tokens"),
                                 cc = GetLong(usage, "cache_creation_input_tokens"), cr = GetLong(usage, "cache_read_input_tokens");
                            d.InputTokens += i; d.OutputTokens += o; d.CacheCreationTokens += cc; d.CacheReadTokens += cr;
                            if (!modelUsage.TryGetValue(model, out var arr)) modelUsage[model] = arr = new long[4];
                            arr[0] += i; arr[1] += o; arr[2] += cc; arr[3] += cr;
                        }
                        if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var block in content.EnumerateArray())
                            {
                                if (block.ValueKind != JsonValueKind.Object) continue;
                                if (!TryGetString(block, "type", out var bt) || bt != "tool_use") continue;
                                d.ToolCallCount++;
                                var name = TryGetString(block, "name", out var n) && n.Length > 0 ? n : "(unknown)";
                                d.ToolCounts[name] = d.ToolCounts.GetValueOrDefault(name) + 1;

                                // Drill into the tool input for the *which* behind agents & skills.
                                var hasInput = block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object;
                                if (hasInput && (name == "Agent" || name == "Task")
                                    && TryGetString(input, "subagent_type", out var agent) && agent.Length > 0)
                                    d.Agents[agent] = d.Agents.GetValueOrDefault(agent) + 1;
                                else if (hasInput && name == "Skill"
                                    && TryGetString(input, "skill", out var skill) && skill.Length > 0)
                                    d.Skills[skill] = d.Skills.GetValueOrDefault(skill) + 1;
                            }
                        }
                    }
                    else if (type == "user")
                    {
                        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
                        // string content = a real prompt (Active time); array content = a tool_result
                        // arriving after the tool ran (Agent time).
                        var isPrompt = msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String;
                        if (isPrompt) { d.PromptCount++; if (ts is { } pt) events.Add((pt, 0)); }
                        else if (ts is { } rt) events.Add((rt, 2));
                    }
                    else if (type == "attachment")
                    {
                        // Hook executions land as attachment lines; hook_success/hook_error carry the
                        // hook's name (e.g. "SessionStart:startup") + exit code.
                        if (!root.TryGetProperty("attachment", out var att) || att.ValueKind != JsonValueKind.Object) continue;
                        if (!TryGetString(att, "type", out var at) || (at != "hook_success" && at != "hook_error")) continue;
                        var hook = TryGetString(att, "hookName", out var hn) && hn.Length > 0 ? hn
                                 : (TryGetString(att, "hookEvent", out var he) && he.Length > 0 ? he : null);
                        if (hook is not null) d.Hooks[hook] = d.Hooks.GetValueOrDefault(hook) + 1;
                    }
                }
            }
        }
        catch (IOException) { /* file busy — return partial */ }

        foreach (var (model, a) in modelUsage)
            d.Models[model] = new ModelUsage(a[0], a[1], a[2], a[3]);

        // Time split: each gap between consecutive events is the wall time *before* the later event.
        // A gap before an assistant reply or a tool-result = the agent/tool working; before a human
        // prompt = the user active; any gap over the idle threshold = idle. These sum to (last − first).
        events.Sort((x, y) => x.Ts.CompareTo(y.Ts));
        if (events.Count > 0) { d.StartedAt = events[0].Ts.ToString("o"); d.EndedAt = events[^1].Ts.ToString("o"); }
        for (var i = 1; i < events.Count; i++)
        {
            var gap = events[i].Ts - events[i - 1].Ts;
            if (gap <= TimeSpan.Zero) continue;
            var ms = (long)gap.TotalMilliseconds;
            if (gap.TotalSeconds > IdleGapSeconds) d.IdleMs += ms;
            else if (events[i].Kind == 0) d.ActiveMs += ms; // before a human prompt
            else d.AgentMs += ms;                            // before an assistant reply or tool-result
        }

        return d;
    }

    /// <summary>Which sub-agents / skills / MCP servers / hooks a session used, each as name→count.
    /// <see cref="Flatten"/> yields <c>(category, name, count)</c> rows for the ToolUsage table.</summary>
    public sealed class ToolUsageCounts
    {
        public Dictionary<string, int> Agents { get; } = [];
        public Dictionary<string, int> Skills { get; } = [];
        public Dictionary<string, int> Mcp { get; } = [];
        public Dictionary<string, int> Hooks { get; } = [];

        public IEnumerable<(string Category, string Name, int Count)> Flatten() =>
            Agents.Select(kv => ("agent", kv.Key, kv.Value))
            .Concat(Skills.Select(kv => ("skill", kv.Key, kv.Value)))
            .Concat(Mcp.Select(kv => ("mcp", kv.Key, kv.Value)))
            .Concat(Hooks.Select(kv => ("hook", kv.Key, kv.Value)));
    }

    /// <summary>Full-file parse of a transcript into per-session sub-agent/skill/MCP/hook counts
    /// (keyed by sessionId; sidechains skipped) for the ToolUsage table. Set semantics — the caller
    /// replaces a session's rows with these. Best-effort: malformed lines/IO errors yield partials.
    /// Agents ← Agent/Task tool_use <c>subagent_type</c>; skills ← Skill tool_use <c>skill</c>;
    /// MCP ← the server in a <c>mcp__server__tool</c> tool name; hooks ← <c>hook_success</c>/
    /// <c>hook_error</c> attachment lines (keyed by <c>hookName</c>, fallback <c>hookEvent</c>).</summary>
    public static Dictionary<string, ToolUsageCounts> ReadToolUsage(string filePath)
    {
        var bySession = new Dictionary<string, ToolUsageCounts>();
        ToolUsageCounts For(string sid) =>
            bySession.TryGetValue(sid, out var c) ? c : bySession[sid] = new ToolUsageCounts();

        try
        {
            foreach (var line in File.ReadLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (!TryGetString(root, "sessionId", out var sid) || sid.Length == 0) continue;
                    if (root.TryGetProperty("isSidechain", out var scv) && scv.ValueKind == JsonValueKind.True) continue;
                    if (!TryGetString(root, "type", out var type)) continue;

                    if (type == "assistant")
                    {
                        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
                        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                        foreach (var block in content.EnumerateArray())
                        {
                            if (block.ValueKind != JsonValueKind.Object) continue;
                            if (!TryGetString(block, "type", out var bt) || bt != "tool_use") continue;
                            if (!TryGetString(block, "name", out var name) || name.Length == 0) continue;
                            var hasInput = block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object;

                            if ((name == "Agent" || name == "Task") && hasInput
                                && TryGetString(input, "subagent_type", out var agent) && agent.Length > 0)
                                Bump(For(sid).Agents, agent);
                            else if (name == "Skill" && hasInput
                                && TryGetString(input, "skill", out var skill) && skill.Length > 0)
                                Bump(For(sid).Skills, skill);
                            else if (name.StartsWith("mcp__", StringComparison.Ordinal))
                            {
                                var server = name["mcp__".Length..].Split("__", 2)[0];
                                if (server.Length > 0) Bump(For(sid).Mcp, server);
                            }
                        }
                    }
                    else if (type == "attachment")
                    {
                        if (!root.TryGetProperty("attachment", out var att) || att.ValueKind != JsonValueKind.Object) continue;
                        if (!TryGetString(att, "type", out var at) || (at != "hook_success" && at != "hook_error")) continue;
                        var hook = TryGetString(att, "hookName", out var hn) && hn.Length > 0 ? hn
                                 : (TryGetString(att, "hookEvent", out var he) && he.Length > 0 ? he : null);
                        if (hook is not null) Bump(For(sid).Hooks, hook);
                    }
                }
            }
        }
        catch (IOException) { /* file busy — return partial */ }
        return bySession;

        static void Bump(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;
    }

    /// <summary>The first real user prompt in a transcript (string message.content — array content is
    /// tool-result noise), collapsed to one line and trimmed to <paramref name="maxLen"/> chars. Null
    /// if none/unreadable. Used to label sessions in the Resume Sessions picker.</summary>
    public static string? FirstUserPrompt(string filePath, int maxLen = 90)
    {
        try
        {
            foreach (var line in File.ReadLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (!TryGetString(root, "type", out var type) || type != "user") continue;
                    if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
                    if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) continue;
                    var text = content.GetString();
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var collapsed = System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
                    return collapsed.Length <= maxLen ? collapsed : collapsed[..maxLen].TrimEnd() + "…";
                }
            }
        }
        catch { /* IO error — best effort */ }
        return null;
    }

    private static bool TryGetString(JsonElement el, string name, out string value)
    {
        if (el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
        {
            value = p.GetString()!;
            return true;
        }
        value = "";
        return false;
    }

    private static long GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
}
