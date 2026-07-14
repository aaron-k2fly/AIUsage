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

    /// <summary>
    /// Live context-window estimate for the Live Code metrics panel: the tokens sent as context on
    /// the most recent assistant turn (input + cache read + cache creation; output doesn't count
    /// toward the window). Schema-aware, so it lives here with the rest of the transcript parsing.
    /// Returns (0, null) if the file has no assistant turn yet.
    /// </summary>
    public static (long ContextTokens, string? Model) LastContextTokens(string filePath)
    {
        long ctx = 0;
        string? model = null;
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
                    if (TryGetString(msg, "model", out var m)) model = m;
                    if (msg.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        var c = GetLong(usage, "input_tokens")
                              + GetLong(usage, "cache_read_input_tokens")
                              + GetLong(usage, "cache_creation_input_tokens");
                        if (c > 0) ctx = c; // most recent non-zero turn wins
                    }
                }
                catch (JsonException) { /* skip truncated/partial line */ }
            }
        }
        catch (IOException) { /* file busy — caller falls back to previous value */ }
        return (ctx, model);
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
