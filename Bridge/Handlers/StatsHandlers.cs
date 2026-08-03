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

    /// <summary>
    /// Tokens per calendar week for the dashboard's "Token usage per week" line chart. Extracted as a
    /// constant so the (fiddly) attribution rules are covered by tests.
    /// </summary>
    public const string TokensWeeklySql = """
        WITH RECURSIVE spend(week, tokens) AS (
            SELECT strftime('%Y-W%W', day), input_tokens + output_tokens
            FROM SessionDailyTokens
            UNION ALL
            SELECT strftime('%Y-W%W', s.started_at), s.input_tokens + s.output_tokens
            FROM Sessions s
            WHERE s.started_at IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM SessionDailyTokens d WHERE d.session_id = s.id)
        ),
        span(d, hi) AS (
            SELECT MIN(x), MAX(x) FROM (
                SELECT day AS x FROM SessionDailyTokens
                UNION ALL
                SELECT date(started_at) FROM Sessions WHERE started_at IS NOT NULL)
            UNION ALL
            SELECT date(d, '+1 day'), hi FROM span WHERE d < hi
        )
        SELECT w.week AS week, COALESCE(SUM(sp.tokens), 0) AS tokens
        FROM (SELECT DISTINCT strftime('%Y-W%W', d) AS week FROM span) w
        LEFT JOIN spend sp ON sp.week = w.week
        -- On an empty DB the MIN/MAX base case is a single (NULL, NULL) row, which would otherwise
        -- plot one null-labelled point; same guard covers an unparseable started_at.
        WHERE w.week IS NOT NULL
        GROUP BY w.week ORDER BY w.week
        """;

    /// <summary>
    /// The dashboard's "Non-ticket sessions" bar chart: spend that never got attributed to a
    /// ticket, grouped by the project folder it happened in. A session counts as non-ticket when
    /// it has no <c>SessionTicketLinks</c> row at all — auto-inferred, manual and confirmed links
    /// alike — so the chart shrinks as work gets linked. Extracted as a constant so it's testable.
    /// </summary>
    public const string NonTicketProjectsSql = """
        SELECT MIN(COALESCE(NULLIF(s.project_dir, ''), '(unknown folder)')) AS project,
               COUNT(*) AS sessions,
               COALESCE(SUM(s.input_tokens + s.output_tokens), 0) AS tokens
        FROM Sessions s
        WHERE NOT EXISTS (SELECT 1 FROM SessionTicketLinks l WHERE l.session_id = s.id)
        -- Grouped case-insensitively: transcripts record the cwd as the shell reported it, so the
        -- same Windows folder shows up as both "C:\..." and "c:\..." and would otherwise draw two
        -- bars with an identical label. MIN() picks one spelling to display.
        GROUP BY lower(COALESCE(NULLIF(s.project_dir, ''), '(unknown folder)'))
        ORDER BY tokens DESC, project
        LIMIT 10
        """;

    public static void Register(MessageRouter router)
    {
        router.Register("stats.dashboard", _ =>
        {
            object tiles, weekly, tokensWeekly, modelWeekly, activity, topTickets, typeMatrix;
            object nonTicketProjects;
            object agentUsage, skillUsage, mcpUsage, hookUsage;

            using (var conn = Db.Open())
            {
                // Automation & extensions — total uses across all sessions, per category (top 12).
                List<Dictionary<string, object?>> UsageByCategory(string category) => Rows.Query(conn, """
                    SELECT name, SUM(count) AS count
                    FROM ToolUsage WHERE category = $cat
                    GROUP BY name ORDER BY count DESC, name
                    LIMIT 12
                    """, ("$cat", category));
                agentUsage = UsageByCategory("agent");
                skillUsage = UsageByCategory("skill");
                mcpUsage = UsageByCategory("mcp");
                hookUsage = UsageByCategory("hook");

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

                // Tokens land in the week they were actually SPENT, from the per-day buckets — not in
                // the week their session happened to start. A session running Sun→Fri used to dump
                // its whole total on the start week (on real data that moved ~1.5M tokens out of one
                // week and into its neighbour). Sessions with no buckets (transcripts older than the
                // backfill horizon, so never day-split) fall back to the old start-week attribution
                // via NOT EXISTS, which keeps the grand total identical instead of silently dropping
                // that history. `span` is a day-by-day spine over the whole data range so weeks with
                // no activity plot as a real zero — a line chart that just omits them implies a
                // gradual slope between non-adjacent weeks.
                tokensWeekly = Rows.Query(conn, TokensWeeklySql);

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

                nonTicketProjects = Rows.Query(conn, NonTicketProjectsSql);

                typeMatrix = Rows.Query(conn, $"""
                    SELECT COALESCE(t.issue_type, 'Unknown') AS issueType, u.act AS category, SUM(u.c) AS count
                    FROM ({ActivityUnion}) u
                    LEFT JOIN Tickets t ON t.key = u.k
                    GROUP BY 1, 2
                    ORDER BY 1, 2
                    """);
            }

            return Task.FromResult<object?>(new
            {
                tiles, weekly, tokensWeekly, modelWeekly, activity, topTickets, nonTicketProjects, typeMatrix,
                agentUsage, skillUsage, mcpUsage, hookUsage
            });
        });
    }

}
