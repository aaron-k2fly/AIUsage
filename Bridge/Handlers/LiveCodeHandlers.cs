using System.Text.Json;
using AIUsage.Jira;
using AIUsage.Platform;
using AIUsage.Settings;
using AIUsage.Terminal;
using Photino.NET;

namespace AIUsage.Bridge.Handlers;

/// <summary>
/// Backs the Live Code page. M1 scope: ticket picker (latest 3 assigned to the user),
/// working-folder picker, agent catalog, and persisted last-used selections. The live
/// terminal (ConPTY) and metrics land in later milestones.
/// </summary>
public static class LiveCodeHandlers
{
    /// <summary>Latest tickets assigned to the current user (independent of the user's Fetch JQL).</summary>
    private const string AssignedJql = "assignee = currentUser() ORDER BY updated DESC";

    public static void Register(MessageRouter router, PhotinoWindow window)
    {
        // Synchronous handlers return Task.FromResult (no Task.Run) — see the note in
        // SessionHandlers.Register on the null-return / unwrap-overload cancellation trap.

        router.Register("livecode.config", _ => Task.FromResult<object?>(new
        {
            jiraConfigured = JiraClient.FromSettings() is not null,
            lastFolder = SettingsStore.Get("livecode_last_folder") ?? "",
            lastShell = SettingsStore.Get("livecode_last_shell") ?? "powershell",
            lastModel = SettingsStore.Get("livecode_last_model") ?? "",
            autoApprove = SettingsStore.Get("livecode_auto_approve") == "1"
        }));

        router.Register("livecode.saveConfig", payload =>
        {
            SetIfPresent(payload, "folder", "livecode_last_folder");
            SetIfPresent(payload, "shell", "livecode_last_shell");
            SetIfPresent(payload, "model", "livecode_last_model");
            if (TryGetBool(payload, "autoApprove", out var auto))
                SettingsStore.Set("livecode_auto_approve", auto ? "1" : "0");
            return Task.FromResult<object?>(null);
        });

        router.Register("livecode.tickets", async _ =>
        {
            var client = JiraClient.FromSettings();
            if (client is null)
                return new { configured = false, tickets = Array.Empty<object>() };

            var page = await client.SearchIssuesAsync(AssignedJql, nextPageToken: null, maxResults: 3);
            var tickets = page.Issues.Select(i => new
            {
                key = i.Key,
                summary = i.Summary,
                status = i.Status,
                issueType = i.IssueType,
                priority = i.Priority
            }).ToList();
            return new { configured = true, tickets };
        });

        router.Register("livecode.listAgents", payload =>
        {
            var folder = SessionHandlers.GetString(payload, "folder");
            var agents = AgentCatalog.List(folder)
                .Select(a => new { name = a.Name, description = a.Description, scope = a.Scope });
            return Task.FromResult<object?>(agents);
        });

        router.Register("livecode.pickFolder", payload =>
        {
            var current = SessionHandlers.GetString(payload, "current");
            var path = FolderDialog.Pick(window, "Select working folder", current);
            return Task.FromResult<object?>(new { path });
        });
    }

    private static void SetIfPresent(JsonElement payload, string jsonName, string settingKey)
    {
        var value = SessionHandlers.GetString(payload, jsonName);
        if (value is not null) SettingsStore.Set(settingKey, value.Trim());
    }

    private static bool TryGetBool(JsonElement payload, string name, out bool value)
    {
        value = false;
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var p))
            return false;
        switch (p.ValueKind)
        {
            case JsonValueKind.True: value = true; return true;
            case JsonValueKind.False: value = false; return true;
            default: return false;
        }
    }
}
