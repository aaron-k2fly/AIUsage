using AIUsage.Data;
using AIUsage.Jira;
using AIUsage.Settings;

namespace AIUsage.Bridge.Handlers;

public static class SettingsHandlers
{
    public static void Register(MessageRouter router)
    {
        // Synchronous handlers return Task.FromResult (no Task.Run) — see the note in
        // SessionHandlers.Register on the null-return / unwrap-overload cancellation trap.
        router.Register("settings.get", _ => Task.FromResult<object?>(new
        {
            jiraSiteUrl = SettingsStore.Get("jira_site_url") ?? "",
            jiraEmail = SettingsStore.Get("jira_email") ?? "",
            jiraTokenSet = SettingsStore.GetProtected("jira_token") is not null,
            scanPaths = SettingsStore.Get("scan_paths") ?? "",
            defaultScanPath = SettingsStore.ScanRoots()[0],
            projectKeyAllowlist = SettingsStore.Get("project_key_allowlist") ?? "",
            backfillFrom = SettingsStore.Get("backfill_from") ?? "",
            jiraFetchJql = SettingsStore.Get("jira_fetch_jql") ?? JiraHandlers.DefaultFetchJql
        }));

        router.Register("settings.set", payload =>
        {
            SetIfPresent(payload, "jiraSiteUrl", "jira_site_url");
            SetIfPresent(payload, "jiraEmail", "jira_email");
            SetIfPresent(payload, "scanPaths", "scan_paths");
            SetIfPresent(payload, "backfillFrom", "backfill_from");
            SetIfPresent(payload, "jiraFetchJql", "jira_fetch_jql");

            // token is write-only: only overwrite when a new non-empty value arrives
            var token = SessionHandlers.GetString(payload, "jiraToken");
            if (!string.IsNullOrWhiteSpace(token))
                SettingsStore.SetProtected("jira_token", token.Trim());

            var newAllowlist = SessionHandlers.GetString(payload, "projectKeyAllowlist");
            if (newAllowlist is not null)
            {
                var before = SettingsStore.Get("project_key_allowlist") ?? "";
                SettingsStore.Set("project_key_allowlist", newAllowlist.Trim());
                if (!string.Equals(before.Trim(), newAllowlist.Trim(), StringComparison.OrdinalIgnoreCase))
                    PurgeDisallowedAutoLinks();
            }
            return Task.FromResult<object?>(null);
        });
    }

    private static void SetIfPresent(System.Text.Json.JsonElement payload, string jsonName, string settingKey)
    {
        var value = SessionHandlers.GetString(payload, jsonName);
        if (value is not null)
            SettingsStore.Set(settingKey, value.Trim());
    }

    /// <summary>
    /// After the allowlist changes, drop auto-inferred links whose project key no longer
    /// matches (manual and confirmed links are user statements — never touched), demote
    /// sessions left without links back to the review queue, and remove orphaned,
    /// never-synced ticket rows.
    /// </summary>
    public static void PurgeDisallowedAutoLinks()
    {
        var allowed = SettingsStore.ProjectKeyAllowlist();
        if (allowed.Count == 0) return; // empty allowlist = allow everything

        using var conn = Db.Open();
        using var tx = conn.BeginTransaction();

        var placeholders = string.Join(",", allowed.Select((_, i) => $"$p{i}"));
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                DELETE FROM SessionTicketLinks
                WHERE source = 'auto'
                  AND substr(ticket_key, 1, instr(ticket_key, '-') - 1) NOT IN ({placeholders});

                UPDATE Sessions SET review_state = 'pending'
                WHERE review_state = 'linked'
                  AND NOT EXISTS (SELECT 1 FROM SessionTicketLinks l WHERE l.session_id = Sessions.id);

                DELETE FROM Tickets
                WHERE last_synced IS NULL AND fetch_failed = 0
                  AND NOT EXISTS (SELECT 1 FROM SessionTicketLinks l WHERE l.ticket_key = Tickets.key)
                  AND NOT EXISTS (SELECT 1 FROM ManualEntries m WHERE m.ticket_key = Tickets.key);
                """;
            for (var i = 0; i < allowed.Count; i++)
                cmd.Parameters.AddWithValue($"$p{i}", allowed.ElementAt(i));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
