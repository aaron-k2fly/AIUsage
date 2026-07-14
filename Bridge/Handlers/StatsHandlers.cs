using AIUsage.Data;

namespace AIUsage.Bridge.Handlers;

public static class StatsHandlers
{
    /// <summary>
    /// Activity rows drawn from two sources with a de-dup rule: a manual entry for the
    /// same ticket dated within a session's start/end supersedes that session's
    /// inferred category, so one piece of work isn't counted twice.
    /// </summary>
    private const string ActivityUnion = """
        SELECT m.ticket_key AS k, COALESCE(c.name, 'Uncategorised') AS act, COUNT(*) AS c
        FROM ManualEntries m
        LEFT JOIN ActivityCategories c ON c.id = m.category_id
        GROUP BY 1, 2
        UNION ALL
        SELECT l.ticket_key,
               COALESCE(ac.name,
                        CASE WHEN s.edit_count + s.write_count >= s.read_count
                             THEN 'Generated code' ELSE 'Investigated' END),
               COUNT(*)
        FROM SessionTicketLinks l
        JOIN Sessions s ON s.id = l.session_id
        LEFT JOIN ActivityCategories ac ON ac.id = l.category_id
        WHERE NOT EXISTS (
            SELECT 1 FROM ManualEntries m
            WHERE m.ticket_key = l.ticket_key
              AND s.started_at IS NOT NULL AND s.ended_at IS NOT NULL
              AND m.entry_date BETWEEN date(s.started_at) AND date(s.ended_at))
        GROUP BY 1, 2
        """;

    public static void Register(MessageRouter router)
    {
        router.Register("stats.dashboard", _ =>
        {
            object tiles, weekly, tokensWeekly, modelWeekly, activity, topTickets, typeMatrix;

            using (var conn = Db.Open())
            {
                tiles = new
                {
                    sessionsThisMonth = Rows.Scalar(conn, """
                        SELECT COUNT(*) FROM Sessions
                        WHERE started_at >= date('now', 'start of month')
                        """),
                    ticketsThisMonth = Rows.Scalar(conn, """
                        SELECT COUNT(DISTINCT k) FROM (
                            SELECT l.ticket_key AS k
                            FROM SessionTicketLinks l JOIN Sessions s ON s.id = l.session_id
                            WHERE s.started_at >= date('now', 'start of month')
                            UNION
                            SELECT ticket_key FROM ManualEntries
                            WHERE entry_date >= date('now', 'start of month'))
                        """),
                    tokensThisMonth = Rows.Scalar(conn, """
                        SELECT COALESCE(SUM(input_tokens + output_tokens), 0) FROM Sessions
                        WHERE started_at >= date('now', 'start of month')
                        """),
                    pendingReview = Rows.Scalar(conn,
                        "SELECT COUNT(*) FROM Sessions WHERE review_state = 'pending'")
                };

                weekly = Rows.Query(conn, """
                    SELECT w AS week, COUNT(DISTINCT k) AS tickets FROM (
                        SELECT strftime('%Y-W%W', s.started_at) AS w, l.ticket_key AS k
                        FROM SessionTicketLinks l JOIN Sessions s ON s.id = l.session_id
                        WHERE s.started_at IS NOT NULL
                        UNION
                        SELECT strftime('%Y-W%W', entry_date), ticket_key FROM ManualEntries)
                    GROUP BY w ORDER BY w
                    """);

                tokensWeekly = Rows.Query(conn, """
                    SELECT strftime('%Y-W%W', started_at) AS week,
                           SUM(input_tokens + output_tokens) AS tokens
                    FROM Sessions
                    WHERE started_at IS NOT NULL
                    GROUP BY week ORDER BY week
                    """);

                modelWeekly = Rows.Query(conn, """
                    SELECT strftime('%Y-W%W', started_at) AS week,
                           COALESCE(NULLIF(model, ''), 'unknown') AS model,
                           COUNT(*) AS sessions
                    FROM Sessions
                    WHERE started_at IS NOT NULL
                    GROUP BY week, model ORDER BY week, model
                    """);

                activity = Rows.Query(conn, $"""
                    SELECT act AS category, SUM(c) AS count
                    FROM ({ActivityUnion})
                    GROUP BY act ORDER BY count DESC
                    """);

                topTickets = Rows.Query(conn, """
                    SELECT l.ticket_key AS key,
                           COUNT(DISTINCT s.id) AS sessions,
                           SUM(s.input_tokens + s.output_tokens) AS tokens,
                           (SELECT COUNT(*) FROM ManualEntries m WHERE m.ticket_key = l.ticket_key) AS manual
                    FROM SessionTicketLinks l
                    JOIN Sessions s ON s.id = l.session_id
                    GROUP BY l.ticket_key
                    ORDER BY tokens DESC
                    LIMIT 10
                    """);

                typeMatrix = Rows.Query(conn, $"""
                    SELECT COALESCE(t.issue_type, 'Unknown') AS issueType, u.act AS category, SUM(u.c) AS count
                    FROM ({ActivityUnion}) u
                    LEFT JOIN Tickets t ON t.key = u.k
                    GROUP BY 1, 2
                    ORDER BY 1, 2
                    """);
            }

            return Task.FromResult<object?>(new { tiles, weekly, tokensWeekly, modelWeekly, activity, topTickets, typeMatrix });
        });
    }

}
