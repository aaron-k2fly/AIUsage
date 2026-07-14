using System.Text.Json;
using System.Text.RegularExpressions;
using AIUsage.Data;
using AIUsage.Data.Repositories;
using AIUsage.Jira;

namespace AIUsage.Bridge.Handlers;

public static partial class ManualHandlers
{
    [GeneratedRegex(@"^[A-Z][A-Z0-9]{1,9}-\d{1,6}$")]
    private static partial Regex TicketKeyRegex();

    public static void Register(MessageRouter router)
    {
        // Synchronous handlers return Task.FromResult (no Task.Run) — see the note in
        // SessionHandlers.Register on the null-return / unwrap-overload cancellation trap.
        router.Register("categories.list", _ =>
        {
            using var conn = Db.Open();
            return Task.FromResult<object?>(ManualEntryRepo.Categories(conn));
        });

        router.Register("manual.list", _ =>
        {
            using var conn = Db.Open();
            return Task.FromResult<object?>(ManualEntryRepo.List(conn));
        });

        router.Register("manual.create", payload =>
        {
            var key = (SessionHandlers.GetString(payload, "ticketKey") ?? "").Trim().ToUpperInvariant();
            if (!TicketKeyRegex().IsMatch(key))
                throw new ArgumentException($"'{key}' is not a valid ticket key (expected e.g. SFTY-1234)");

            var date = SessionHandlers.GetString(payload, "entryDate");
            if (!DateOnly.TryParse(date, out _))
                throw new ArgumentException("entryDate must be a valid date (yyyy-MM-dd)");

            long? categoryId = payload.TryGetProperty("categoryId", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt64()
                : null;

            long id;
            using (var conn = Db.Open())
            {
                id = ManualEntryRepo.Create(conn, key, date!,
                    categoryId,
                    SessionHandlers.GetString(payload, "description"),
                    SessionHandlers.GetString(payload, "toolUsed"));
            }
            JiraSync.TryFetchInBackground(key);
            return Task.FromResult<object?>(new { id });
        });

        router.Register("manual.delete", payload =>
        {
            if (!payload.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                throw new ArgumentException("id is required");
            using var conn = Db.Open();
            ManualEntryRepo.Delete(conn, idEl.GetInt64());
            return Task.FromResult<object?>(null);
        });
    }
}
