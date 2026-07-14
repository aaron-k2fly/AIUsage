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
