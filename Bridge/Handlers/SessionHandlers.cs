using System.Text.Json;
using System.Text.RegularExpressions;
using AIUsage.Data;
using AIUsage.Data.Repositories;
using AIUsage.Scanner;

namespace AIUsage.Bridge.Handlers;

public static partial class SessionHandlers
{
    [GeneratedRegex(@"^[A-Z][A-Z0-9]{1,9}-\d{1,6}$")]
    private static partial Regex TicketKeyRegex();

    public static void Register(MessageRouter router)
    {
        // Handlers run synchronously on the pool thread MessageRouter.OnMessage already
        // provides, returning a completed Task. They deliberately do NOT wrap the body in
        // Task.Run: `Task.Run<object?>(() => { ...; return null; })` binds to the
        // Func<Task<object?>> unwrap overload (null is assignable to Task<object?>), and
        // unwrapping a null task yields a *canceled* task — surfacing as a spurious
        // "A task was canceled" error even though the write committed.
        router.Register("scan.run", _ =>
        {
            var result = new TranscriptScanner().Run();
            return Task.FromResult<object?>(new
            {
                sessions = result.Sessions,
                newFiles = result.NewFiles,
                updatedFiles = result.UpdatedFiles,
                skippedFiles = result.SkippedFiles
            });
        });

        router.Register("sessions.list", payload =>
        {
            var filter = GetString(payload, "filter") ?? "all";
            using var conn = Db.Open();
            return Task.FromResult<object?>(SessionRepo.List(conn, filter));
        });

        router.Register("sessions.detail", payload =>
        {
            var sessionId = GetString(payload, "sessionId")
                ?? throw new ArgumentException("sessionId is required");
            using var conn = Db.Open();
            var row = SessionRepo.Get(conn, sessionId)
                ?? throw new ArgumentException($"Session '{sessionId}' not found");

            static long L(object? o) => o is null ? 0 : Convert.ToInt64(o);
            var filePath = row.GetValueOrDefault("filePath") as string;

            SessionAggregator.SessionDetail? d = null;
            SessionAggregator.SubagentUsage sub = default;
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                d = SessionAggregator.ReadDetail(filePath, sessionId);
                sub = SessionAggregator.SubagentTokens(filePath);
            }

            // Category matches the dashboard's derivation: an explicit link category if set, else a
            // guess from the edit/read balance (see StatsHandlers.ActivityUnion).
            var editN = L(row.GetValueOrDefault("editCount")) + L(row.GetValueOrDefault("writeCount"));
            var readN = L(row.GetValueOrDefault("readCount"));
            var category = row.GetValueOrDefault("categoryName") as string
                           ?? (editN >= readN ? "Generated code" : "Investigated");

            // Prefer the deep re-parse; fall back to stored counters if the transcript is gone.
            var tools = d is null
                ? null
                : d.ToolCounts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                    .Select(kv => new { name = kv.Key, count = kv.Value }).ToList();
            var models = d is null
                ? null
                : d.Models.OrderByDescending(kv => kv.Value.Output).ThenByDescending(kv => kv.Value.Input)
                    .Select(kv => new
                    {
                        model = kv.Key,
                        input = kv.Value.Input,
                        output = kv.Value.Output,
                        cacheCreation = kv.Value.CacheCreation,
                        cacheRead = kv.Value.CacheRead
                    }).ToList();

            // name/count lists for the "Agents & extensions" panel.
            static List<object> ByCountDesc(Dictionary<string, int> src) =>
                src.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                    .Select(kv => (object)new { name = kv.Key, count = kv.Value }).ToList();

            var agents = d is null ? null : ByCountDesc(d.Agents);
            var skills = d is null ? null : ByCountDesc(d.Skills);
            var hooks = d is null ? null : ByCountDesc(d.Hooks);
            // MCP tools are just tool_use names prefixed "mcp__<server>__<tool>" — group by server.
            var mcps = d is null ? null : d.ToolCounts
                .Where(kv => kv.Key.StartsWith("mcp__", StringComparison.Ordinal))
                .Select(kv =>
                {
                    var parts = kv.Key["mcp__".Length..].Split("__", 2);
                    return new { server = parts[0], tool = parts.Length > 1 ? parts[1] : "", count = kv.Value };
                })
                .OrderByDescending(x => x.count).ThenBy(x => x.server)
                .Select(x => (object)x).ToList();

            return Task.FromResult<object?>(new
            {
                id = sessionId,
                title = row.GetValueOrDefault("title"),
                projectDir = row.GetValueOrDefault("projectDir"),
                gitBranch = row.GetValueOrDefault("gitBranch"),
                model = row.GetValueOrDefault("model"),
                startedAt = row.GetValueOrDefault("startedAt"),
                endedAt = row.GetValueOrDefault("endedAt"),
                reviewState = row.GetValueOrDefault("reviewState"),
                ccVersion = row.GetValueOrDefault("ccVersion"),
                category,
                links = row.GetValueOrDefault("links"),

                // Token totals: deep re-parse when available, else stored counters.
                inputTokens = d?.InputTokens ?? L(row.GetValueOrDefault("inputTokens")),
                outputTokens = d?.OutputTokens ?? L(row.GetValueOrDefault("outputTokens")),
                cacheCreationTokens = d?.CacheCreationTokens ?? L(row.GetValueOrDefault("cacheCreationTokens")),
                cacheReadTokens = d?.CacheReadTokens ?? L(row.GetValueOrDefault("cacheReadTokens")),

                promptCount = d?.PromptCount ?? (int)L(row.GetValueOrDefault("userMessageCount")),
                replyCount = d?.ReplyCount ?? 0,
                toolCallCount = d?.ToolCallCount ?? 0,

                agentMs = d?.AgentMs ?? 0,
                activeMs = d?.ActiveMs ?? 0,
                idleMs = d?.IdleMs ?? 0,

                tools,
                models,
                agents,
                skills,
                hooks,
                mcps,
                subagentTokens = new { inOut = sub.InOut, cacheCreation = sub.CacheCreation, cacheRead = sub.CacheRead },
                transcriptAvailable = d is not null
            });
        });

        router.Register("sessions.assignTicket", payload =>
        {
            var (sessionId, ticketKey) = RequireSessionAndKey(payload);
            using (var conn = Db.Open())
                SessionRepo.AssignTicket(conn, sessionId, ticketKey);
            Jira.JiraSync.TryFetchInBackground(ticketKey);
            return Task.FromResult<object?>(null);
        });

        router.Register("sessions.confirmLink", payload =>
        {
            var (sessionId, ticketKey) = RequireSessionAndKey(payload);
            using var conn = Db.Open();
            SessionRepo.ConfirmLink(conn, sessionId, ticketKey);
            return Task.FromResult<object?>(null);
        });

        router.Register("sessions.removeLink", payload =>
        {
            var (sessionId, ticketKey) = RequireSessionAndKey(payload);
            using var conn = Db.Open();
            SessionRepo.RemoveLink(conn, sessionId, ticketKey);
            return Task.FromResult<object?>(null);
        });

        router.Register("sessions.dismiss", payload =>
        {
            var sessionId = GetString(payload, "sessionId")
                ?? throw new ArgumentException("sessionId is required");
            using var conn = Db.Open();
            SessionRepo.SetReviewState(conn, sessionId, "not_ticket_related");
            return Task.FromResult<object?>(null);
        });

        router.Register("sessions.reopen", payload =>
        {
            var sessionId = GetString(payload, "sessionId")
                ?? throw new ArgumentException("sessionId is required");
            using var conn = Db.Open();
            SessionRepo.SetReviewState(conn, sessionId, "pending");
            return Task.FromResult<object?>(null);
        });
    }

    private static (string SessionId, string TicketKey) RequireSessionAndKey(JsonElement payload)
    {
        var sessionId = GetString(payload, "sessionId")
            ?? throw new ArgumentException("sessionId is required");
        var ticketKey = (GetString(payload, "ticketKey") ?? "").Trim().ToUpperInvariant();
        if (!TicketKeyRegex().IsMatch(ticketKey))
            throw new ArgumentException($"'{ticketKey}' is not a valid ticket key (expected e.g. SFTY-1234)");
        return (sessionId, ticketKey);
    }

    internal static string? GetString(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
}
