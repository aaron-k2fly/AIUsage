using AIUsage.Data;
using AIUsage.Data.Repositories;

namespace AIUsage.Jira;

public static class JiraSync
{
    /// <summary>
    /// Fire-and-forget enrichment of a newly referenced ticket. Quietly does nothing
    /// when JIRA isn't configured or the network is down — enrichment is optional.
    /// </summary>
    public static void TryFetchInBackground(string ticketKey)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var client = JiraClient.FromSettings();
                if (client is null) return;

                using (var conn = Db.Open())
                {
                    if (!TicketRepo.UnsyncedKeys(conn).Contains(ticketKey)) return;
                }

                await FetchOneAsync(client, ticketKey);
            }
            catch
            {
                // lazy enrichment must never surface errors
            }
        });
    }

    /// <summary>Fetch a single key and record the result. Returns true when the key exists.</summary>
    public static async Task<bool> FetchOneAsync(JiraClient client, string ticketKey)
    {
        var issue = await client.FetchIssueAsync(ticketKey);
        using var conn = Db.Open();
        if (issue is null)
        {
            TicketRepo.MarkFailed(conn, ticketKey);
            return false;
        }
        TicketRepo.UpsertFetched(conn, issue.Key, issue.Summary, issue.Status, issue.IssueType,
            issue.Project, issue.Sprint, issue.Priority, issue.Updated);
        return true;
    }
}
