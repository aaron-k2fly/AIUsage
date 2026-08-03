using AIUsage.Bridge.Handlers;
using AIUsage.Data;
using AIUsage.Data.Repositories;
using AIUsage.Scanner;
using AIUsage.Tests.Helpers;

namespace AIUsage.Tests;

/// <summary>
/// The dashboard's "Non-ticket sessions" bar chart. The behaviour worth pinning down is what
/// counts as non-ticket (no link of any source), how folders are grouped, and the top-10 cut.
/// </summary>
public class NonTicketProjectsTests
{
    private const string File1 = "/transcripts/f1.jsonl";

    private static List<(string Project, long Sessions, long Tokens)> Rows_(TestDb db) =>
        [.. Rows.Query(db.Conn, StatsHandlers.NonTicketProjectsSql)
            .Select(r => ((string)r["project"]!, Convert.ToInt64(r["sessions"]), Convert.ToInt64(r["tokens"])))];

    private static void AddSession(TestDb db, string id, string? projectDir, long tokens)
    {
        SessionRepo.Upsert(db.Conn, new SessionAggregate
        {
            SessionId = id,
            FilePath = File1,
            ProjectDir = projectDir,
            StartedAt = "2026-07-08T10:00:00Z",
            InputTokens = tokens,
        });
    }

    [Fact]
    public void Sessions_in_one_folder_are_summed_into_a_single_bar()
    {
        using var db = new TestDb();
        AddSession(db, "s1", "C:/Projects/AIUsage", 100);
        AddSession(db, "s2", "C:/Projects/AIUsage", 400);

        Assert.Equal([("C:/Projects/AIUsage", 2, 500)], Rows_(db));
    }

    [Fact]
    public void A_linked_session_is_excluded_whatever_the_link_source()
    {
        using var db = new TestDb();
        AddSession(db, "auto", "C:/Projects/A", 100);
        AddSession(db, "manual", "C:/Projects/B", 200);
        AddSession(db, "orphan", "C:/Projects/C", 300);
        SessionRepo.AddAutoLink(db.Conn, "auto", "ABC-1", "branch");
        SessionRepo.AssignTicket(db.Conn, "manual", "ABC-2");

        Assert.Equal([("C:/Projects/C", 1, 300)], Rows_(db));
    }

    [Fact]
    public void The_same_folder_in_different_case_is_one_bar()
    {
        // Real transcripts carry both "C:\Projects\X" and "c:\Projects\X" for the same folder.
        using var db = new TestDb();
        AddSession(db, "upper", @"C:\Projects\X", 100);
        AddSession(db, "lower", @"c:\Projects\X", 400);

        Assert.Equal([(@"C:\Projects\X", 2, 500)], Rows_(db));
    }

    [Fact]
    public void Folders_are_ordered_by_tokens_and_capped_at_ten()
    {
        using var db = new TestDb();
        // 12 folders, ascending spend — only the ten biggest survive, biggest first.
        for (var i = 1; i <= 12; i++) AddSession(db, $"s{i}", $"C:/Projects/P{i}", i * 100);

        var rows = Rows_(db);
        Assert.Equal(10, rows.Count);
        Assert.Equal("C:/Projects/P12", rows[0].Project);
        Assert.Equal("C:/Projects/P3", rows[^1].Project);
    }

    [Fact]
    public void A_session_with_no_project_dir_is_labelled_rather_than_dropped()
    {
        using var db = new TestDb();
        AddSession(db, "nodir", null, 700);

        Assert.Equal([("(unknown folder)", 1, 700)], Rows_(db));
    }

    [Fact]
    public void An_empty_database_yields_no_rows()
    {
        using var db = new TestDb();
        Assert.Empty(Rows_(db));
    }
}
