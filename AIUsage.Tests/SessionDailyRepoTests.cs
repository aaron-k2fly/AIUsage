using AIUsage.Data.Repositories;
using AIUsage.Scanner;
using AIUsage.Tests.Helpers;

namespace AIUsage.Tests;

/// <summary>
/// Per-day token buckets — the source of the Live Code "last 7 days" figure. The property that
/// matters is that they stay in lockstep with the flat Sessions counters (same additive semantics,
/// same reset points), and that a rolling window sees only the days inside it.
/// </summary>
public class SessionDailyRepoTests
{
    private const string File1 = "/transcripts/f1.jsonl";
    private const string File2 = "/transcripts/f2.jsonl";

    /// <summary>Local day N days before today — the buckets and the rolling query both work in
    /// local time, so tests must too (otherwise they'd flip near midnight or in a non-UTC zone).</summary>
    private static string DaysAgo(int n) => DateTime.Now.AddDays(-n).ToString("yyyy-MM-dd");

    private static SessionAggregate Agg(string id, string file, params (string Day, long In, long Out)[] days)
    {
        var a = new SessionAggregate { SessionId = id, FilePath = file };
        foreach (var (day, input, output) in days)
        {
            a.AddDaily(day, input, output);
            a.InputTokens += input;
            a.OutputTokens += output;
        }
        return a;
    }

    private static void Store(TestDb db, SessionAggregate a)
    {
        SessionRepo.Upsert(db.Conn, a);          // the FK parent
        SessionDailyRepo.Accumulate(db.Conn, a);
    }

    [Fact]
    public void RollingTokens_counts_only_days_inside_the_window()
    {
        using var db = new TestDb();
        Store(db, Agg("s1", File1,
            (DaysAgo(0), 10, 1),
            (DaysAgo(6), 100, 2),     // last day still inside a 7-day window
            (DaysAgo(7), 1000, 3)));  // one day too old

        Assert.Equal(113, SessionDailyRepo.RollingTokens(db.Conn, 7));
        Assert.Equal(11, SessionDailyRepo.RollingTokens(db.Conn, 1));   // today only
    }

    [Fact]
    public void RollingTokens_counts_a_session_that_began_before_the_window()
    {
        // The bug this table exists to fix: attributing the whole session to its start date meant a
        // session begun outside the window contributed nothing, however much it spent inside it.
        using var db = new TestDb();
        Store(db, Agg("long-runner", File1,
            (DaysAgo(30), 500, 0),
            (DaysAgo(1), 40, 2)));

        Assert.Equal(42, SessionDailyRepo.RollingTokens(db.Conn, 7));
    }

    [Fact]
    public void Accumulate_adds_to_existing_buckets_like_the_flat_counters()
    {
        using var db = new TestDb();
        var today = DaysAgo(0);
        Store(db, Agg("s1", File1, (today, 10, 1)));
        Store(db, Agg("s1", File1, (today, 5, 2)));   // a later incremental slice of the same file

        Assert.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM SessionDailyTokens"));
        Assert.Equal(18, SessionDailyRepo.RollingTokens(db.Conn, 7));
        // in lockstep with Sessions, which accumulated the same two slices
        Assert.Equal(18L, db.Scalar<long>("SELECT input_tokens + output_tokens FROM Sessions WHERE id='s1'"));
    }

    [Fact]
    public void DeleteForFile_drops_only_that_files_buckets()
    {
        using var db = new TestDb();
        Store(db, Agg("s1", File1, (DaysAgo(0), 10, 0)));
        Store(db, Agg("s2", File2, (DaysAgo(0), 7, 0)));

        SessionDailyRepo.DeleteForFile(db.Conn, File1);

        Assert.Equal(7, SessionDailyRepo.RollingTokens(db.Conn, 7));
    }

    [Fact]
    public void ReplaceForFile_overwrites_rather_than_adds()
    {
        // What the v7 backfill relies on: re-deriving a file the incremental scan already consumed
        // must not double the stored numbers.
        using var db = new TestDb();
        var today = DaysAgo(0);
        Store(db, Agg("s1", File1, (today, 10, 0)));

        SessionDailyRepo.ReplaceForFile(db.Conn, File1, [Agg("s1", File1, (today, 10, 0))]);

        Assert.Equal(10, SessionDailyRepo.RollingTokens(db.Conn, 7));
    }

    [Fact]
    public void Buckets_cascade_delete_with_their_session()
    {
        using var db = new TestDb();
        Store(db, Agg("s1", File1, (DaysAgo(0), 10, 0)));

        SessionRepo.DeleteSessionsNotIn(db.Conn, File1, []);

        Assert.Equal(0L, db.Scalar<long>("SELECT COUNT(*) FROM SessionDailyTokens"));
    }

    [Fact]
    public void Accumulate_skips_a_session_that_has_no_row_in_Sessions()
    {
        using var db = new TestDb();
        SessionDailyRepo.Accumulate(db.Conn, Agg("orphan", File1, (DaysAgo(0), 10, 0)));

        Assert.Equal(0L, db.Scalar<long>("SELECT COUNT(*) FROM SessionDailyTokens"));
    }
}
