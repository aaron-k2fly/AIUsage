using AIUsage.Data;
using AIUsage.Data.Repositories;
using AIUsage.Jira;
using AIUsage.Settings;

namespace AIUsage.Bridge.Handlers;

public static class JiraHandlers
{
    /// <summary>Default JQL for "Fetch more from JIRA" when the user hasn't set one.</summary>
    public const string DefaultFetchJql = "assignee = currentUser() ORDER BY updated DESC";

    public static void Register(MessageRouter router)
    {
        router.Register("tickets.list", _ =>
        {
            using var conn = Db.Open();
            return Task.FromResult<object?>(TicketRepo.List(conn));
        });

        router.Register("tickets.fetch", async payload =>
        {
            var key = SessionHandlers.GetString(payload, "ticketKey")
                ?? throw new ArgumentException("ticketKey is required");
            var client = JiraClient.FromSettings()
                ?? throw new InvalidOperationException("JIRA is not configured — set site URL, email and token in Settings");
            var found = await JiraSync.FetchOneAsync(client, key);
            return new { found };
        });

        router.Register("tickets.sync", async _ =>
        {
            var client = JiraClient.FromSettings()
                ?? throw new InvalidOperationException("JIRA is not configured — set site URL, email and token in Settings");

            List<string> keys;
            using (var conn = Db.Open())
                keys = TicketRepo.AllKeys(conn);

            int ok = 0, dead = 0, failed = 0;
            foreach (var key in keys)
            {
                try
                {
                    if (await JiraSync.FetchOneAsync(client, key)) ok++;
                    else dead++;
                }
                catch
                {
                    failed++;
                }
                await Task.Delay(150); // gentle on rate limits; volumes are tiny
            }
            return new { synced = ok, dead, failed, total = keys.Count };
        });

        // Import additional tickets from JIRA by JQL, one page per call. The client passes
        // back the previous nextPageToken to page through results ("Fetch more").
        router.Register("tickets.fetchMore", async payload =>
        {
            var client = JiraClient.FromSettings()
                ?? throw new InvalidOperationException("JIRA is not configured — set site URL, email and token in Settings");

            var jql = SettingsStore.Get("jira_fetch_jql");
            if (string.IsNullOrWhiteSpace(jql)) jql = DefaultFetchJql;

            var token = SessionHandlers.GetString(payload, "nextPageToken");
            var page = await client.SearchIssuesAsync(jql, token);

            using (var conn = Db.Open())
                foreach (var iss in page.Issues)
                    TicketRepo.UpsertFetched(conn, iss.Key, iss.Summary, iss.Status, iss.IssueType,
                        iss.Project, iss.Sprint, iss.Priority, iss.Updated);

            return new { imported = page.Issues.Count, nextPageToken = page.NextPageToken, isLast = page.IsLast };
        });

        router.Register("jira.test", async _ =>
        {
            var client = JiraClient.FromSettings()
                ?? throw new InvalidOperationException("Fill in site URL, email and token first (and Save)");
            var user = await client.TestConnectionAsync();
            return new { user };
        });
    }
}
