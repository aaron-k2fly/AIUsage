using AIUsage.Data.Repositories;
using AIUsage.Scanner;
using AIUsage.Tests.Helpers;

namespace AIUsage.Tests;

public class SessionRepoTests
{
    private const string File1 = "/transcripts/f1.jsonl";

    private static SessionAggregate NewAgg(string id, string file = File1) => new()
    {
        SessionId = id,
        FilePath = file,
        Title = "A title",
        ProjectDir = "/repo",
        GitBranch = "feature/ABC-1",
        Model = "claude-opus-4-8",
        StartedAt = "2026-07-01T10:00:00Z",
        EndedAt = "2026-07-01T10:10:00Z",
        InputTokens = 10,
        OutputTokens = 5,
        EditCount = 2,
        UserMessageCount = 1,
    };

    private static string? Links(IReadOnlyDictionary<string, object?> row) => row["links"] as string;

    [Fact]
    public void Upsert_then_Get_round_trips_the_row()
    {
        using var db = new TestDb();
        SessionRepo.Upsert(db.Conn, NewAgg("s1"));

        var row = SessionRepo.Get(db.Conn, "s1");

        Assert.NotNull(row);
        Assert.Equal("s1", row!["id"]);
        Assert.Equal("A title", row["title"]);
        Assert.Equal(10L, Convert.ToInt64(row["inputTokens"]));
        Assert.Equal(2L, Convert.ToInt64(row["editCount"]));
        Assert.Equal("pending", row["reviewState"]);
    }

    [Fact]
    public void Get_returns_null_for_an_unknown_session()
    {
        using var db = new TestDb();
        Assert.Null(SessionRepo.Get(db.Conn, "does-not-exist"));
    }

    [Fact]
    public void List_returns_pending_sessions()
    {
        using var db = new TestDb();
        SessionRepo.Upsert(db.Conn, NewAgg("s1"));

        var all = SessionRepo.List(db.Conn, "all");
        var pending = SessionRepo.List(db.Conn, "pending");

        Assert.Contains(all, r => (string?)r["id"] == "s1");
        Assert.Contains(pending, r => (string?)r["id"] == "s1");
    }

    [Fact]
    public void Upsert_accumulates_token_counters_for_the_same_session()
    {
        using var db = new TestDb();
        SessionRepo.Upsert(db.Conn, NewAgg("s1"));              // input 10
        SessionRepo.Upsert(db.Conn, new SessionAggregate        // input 5
        {
            SessionId = "s1",
            FilePath = File1,
            InputTokens = 5,
        });

        var row = SessionRepo.Get(db.Conn, "s1")!;
        Assert.Equal(15L, Convert.ToInt64(row["inputTokens"]));
    }

    [Fact]
    public void List_orders_by_last_activity_so_a_resumed_session_surfaces()
    {
        // Resuming a session (Live Code or `claude --resume`) leaves started_at at the original
        // date — only ended_at moves. Ordering on started_at buried today's work down the list and
        // made the Sessions page look like it hadn't picked the session up at all.
        using var db = new TestDb();
        SessionRepo.Upsert(db.Conn, new SessionAggregate
        {
            SessionId = "old-but-resumed",
            FilePath = File1,
            StartedAt = "2026-07-01T09:00:00Z",
            EndedAt = "2026-07-28T16:00:00Z",   // resumed today
        });
        SessionRepo.Upsert(db.Conn, new SessionAggregate
        {
            SessionId = "started-later",
            FilePath = File1,
            StartedAt = "2026-07-20T09:00:00Z",
            EndedAt = "2026-07-20T10:00:00Z",
        });

        var ids = SessionRepo.List(db.Conn, "all").Select(r => (string?)r["id"]).ToList();

        Assert.Equal(["old-but-resumed", "started-later"], ids);
    }

    [Fact]
    public void List_falls_back_to_started_at_when_a_session_has_no_end()
    {
        using var db = new TestDb();
        SessionRepo.Upsert(db.Conn, new SessionAggregate
        {
            SessionId = "no-end", FilePath = File1, StartedAt = "2026-07-25T09:00:00Z",
        });
        SessionRepo.Upsert(db.Conn, new SessionAggregate
        {
            SessionId = "ended-earlier", FilePath = File1,
            StartedAt = "2026-07-01T09:00:00Z", EndedAt = "2026-07-02T09:00:00Z",
        });

        var ids = SessionRepo.List(db.Conn, "all").Select(r => (string?)r["id"]).ToList();

        Assert.Equal(["no-end", "ended-earlier"], ids);
    }

    [Fact]
    public void AddAutoLink_confirm_and_remove_transition_the_link_and_review_state()
    {
        using var db = new TestDb();
        SessionRepo.Upsert(db.Conn, NewAgg("s1"));

        SessionRepo.AddAutoLink(db.Conn, "s1", "ABC-1", "branch");
        var linked = SessionRepo.Get(db.Conn, "s1")!;
        Assert.Equal("linked", linked["reviewState"]);
        Assert.Equal("ABC-1|auto", Links(linked));
        Assert.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM Tickets WHERE key='ABC-1'"));

        SessionRepo.ConfirmLink(db.Conn, "s1", "ABC-1");
        Assert.Equal("ABC-1|confirmed", Links(SessionRepo.Get(db.Conn, "s1")!));

        SessionRepo.RemoveLink(db.Conn, "s1", "ABC-1");
        var removed = SessionRepo.Get(db.Conn, "s1")!;
        Assert.Null(Links(removed));
        Assert.Equal("pending", removed["reviewState"]); // back to pending when no links remain
    }

    [Fact]
    public void ResetCountersForFile_zeroes_counters_and_timestamps()
    {
        using var db = new TestDb();
        SessionRepo.Upsert(db.Conn, NewAgg("s1"));

        SessionRepo.ResetCountersForFile(db.Conn, File1);

        var row = SessionRepo.Get(db.Conn, "s1")!;
        Assert.Equal(0L, Convert.ToInt64(row["inputTokens"]));
        Assert.Equal(0L, Convert.ToInt64(row["editCount"]));
        Assert.Null(row["startedAt"]);
    }

    [Fact]
    public void DeleteSessionsNotIn_prunes_missing_sessions_and_cascades_their_links()
    {
        using var db = new TestDb();
        SessionRepo.Upsert(db.Conn, NewAgg("s1"));
        SessionRepo.Upsert(db.Conn, NewAgg("s2"));
        SessionRepo.AddAutoLink(db.Conn, "s1", "ABC-1", "branch");

        SessionRepo.DeleteSessionsNotIn(db.Conn, File1, ["s2"]);

        Assert.Null(SessionRepo.Get(db.Conn, "s1"));      // pruned
        Assert.NotNull(SessionRepo.Get(db.Conn, "s2"));   // kept
        // link cascade-deleted with s1
        Assert.Equal(0L, db.Scalar<long>(
            "SELECT COUNT(*) FROM SessionTicketLinks WHERE session_id='s1'"));
    }
}
