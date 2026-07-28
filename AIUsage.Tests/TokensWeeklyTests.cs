using AIUsage.Bridge.Handlers;
using AIUsage.Data;
using AIUsage.Data.Repositories;
using AIUsage.Scanner;
using AIUsage.Tests.Helpers;

namespace AIUsage.Tests;

/// <summary>
/// The dashboard's "Token usage per week" series. The interesting behaviour is all in the
/// attribution: tokens belong to the week they were spent, sessions with no day buckets still
/// count, and quiet weeks are real zeroes rather than gaps.
/// </summary>
public class TokensWeeklyTests
{
    private const string File1 = "/transcripts/f1.jsonl";

    private static List<(string Week, long Tokens)> Weekly(TestDb db)
    {
        var rows = Rows.Query(db.Conn, StatsHandlers.TokensWeeklySql);
        return [.. rows.Select(r => ((string)r["week"]!, Convert.ToInt64(r["tokens"])))];
    }

    /// <summary>A session with day-resolved spend, stored the way the scanner stores it.</summary>
    private static void AddBucketed(TestDb db, string id, string startedAt, params (string Day, long Tokens)[] days)
    {
        var a = new SessionAggregate { SessionId = id, FilePath = File1, StartedAt = startedAt };
        foreach (var (day, tokens) in days)
        {
            a.AddDaily(day, tokens, 0);
            a.InputTokens += tokens;
        }
        SessionRepo.Upsert(db.Conn, a);
        SessionDailyRepo.Accumulate(db.Conn, a);
    }

    [Fact]
    public void A_session_spanning_two_weeks_splits_across_them()
    {
        // 2026-07-05 is a Sunday (still week 26 under %W); 2026-07-06 is the Monday of week 27.
        using var db = new TestDb();
        AddBucketed(db, "spanning", "2026-07-05T10:00:00Z",
            ("2026-07-05", 100),
            ("2026-07-08", 900));

        Assert.Equal([("2026-W26", 100), ("2026-W27", 900)], Weekly(db));
    }

    [Fact]
    public void A_session_with_no_buckets_still_counts_on_its_start_week()
    {
        // Transcripts older than the backfill horizon never got day-split; dropping them would
        // silently shrink the chart's history.
        using var db = new TestDb();
        SessionRepo.Upsert(db.Conn, new SessionAggregate
        {
            SessionId = "legacy",
            FilePath = File1,
            StartedAt = "2026-07-08T10:00:00Z",
            InputTokens = 400,
            OutputTokens = 100,
        });

        Assert.Equal([("2026-W27", 500)], Weekly(db));
    }

    [Fact]
    public void A_bucketed_session_is_not_also_counted_on_its_start_week()
    {
        using var db = new TestDb();
        AddBucketed(db, "s1", "2026-07-08T10:00:00Z", ("2026-07-08", 500));

        Assert.Equal([("2026-W27", 500)], Weekly(db));
    }

    [Fact]
    public void Weeks_with_no_activity_appear_as_zero_rather_than_being_skipped()
    {
        // A line chart that omits empty weeks draws a smooth slope between non-adjacent points.
        using var db = new TestDb();
        AddBucketed(db, "before", "2026-07-08T10:00:00Z", ("2026-07-08", 100));
        AddBucketed(db, "after", "2026-07-29T10:00:00Z", ("2026-07-29", 300));

        Assert.Equal(
            [("2026-W27", 100), ("2026-W28", 0), ("2026-W29", 0), ("2026-W30", 300)],
            Weekly(db));
    }

    [Fact]
    public void The_grand_total_is_preserved_when_buckets_and_legacy_sessions_are_mixed()
    {
        using var db = new TestDb();
        AddBucketed(db, "bucketed", "2026-07-05T10:00:00Z", ("2026-07-05", 100), ("2026-07-08", 900));
        SessionRepo.Upsert(db.Conn, new SessionAggregate
        {
            SessionId = "legacy", FilePath = File1,
            StartedAt = "2026-07-08T10:00:00Z", InputTokens = 500,
        });

        Assert.Equal(1500, Weekly(db).Sum(w => w.Tokens));
    }

    [Fact]
    public void An_empty_database_yields_no_rows()
    {
        using var db = new TestDb();
        Assert.Empty(Weekly(db));
    }
}
