using AIUsage.Data.Repositories;
using AIUsage.Tests.Helpers;

namespace AIUsage.Tests;

/// <summary>Basic round-trips for the Ticket and Manual-entry repositories.</summary>
public class DataRepoTests
{
    [Fact]
    public void TicketRepo_upsert_then_list_round_trips()
    {
        using var db = new TestDb();

        TicketRepo.UpsertFetched(db.Conn, "ABC-1", "Fix the thing", "In Progress",
            "Bug", "ABC", "Sprint 3", "High", "2026-07-01T00:00:00Z");

        Assert.Contains("ABC-1", TicketRepo.AllKeys(db.Conn));
        var rows = TicketRepo.List(db.Conn);
        Assert.Contains(rows, r => (string?)r["key"] == "ABC-1");
    }

    [Fact]
    public void TicketRepo_upsert_updates_an_existing_ticket()
    {
        using var db = new TestDb();

        TicketRepo.UpsertFetched(db.Conn, "ABC-1", "First", "Open", "Task", "ABC", null, "Low", null);
        TicketRepo.UpsertFetched(db.Conn, "ABC-1", "Second", "Done", "Task", "ABC", null, "Low", null);

        Assert.Equal("Second", db.Scalar<string>("SELECT summary FROM Tickets WHERE key='ABC-1'"));
        Assert.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM Tickets WHERE key='ABC-1'"));
    }

    [Fact]
    public void ManualEntryRepo_create_list_delete_round_trips()
    {
        using var db = new TestDb();

        var id = ManualEntryRepo.Create(db.Conn, "ABC-1", "2026-07-01", null, "Did some work", "Claude Code");
        Assert.True(id > 0);

        var rows = ManualEntryRepo.List(db.Conn);
        Assert.Contains(rows, r => Convert.ToInt64(r["id"]) == id);

        ManualEntryRepo.Delete(db.Conn, id);
        Assert.DoesNotContain(ManualEntryRepo.List(db.Conn), r => Convert.ToInt64(r["id"]) == id);
    }
}
