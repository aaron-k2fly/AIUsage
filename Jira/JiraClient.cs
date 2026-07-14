using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIUsage.Settings;

namespace AIUsage.Jira;

public sealed record JiraIssue(
    string Key, string? Summary, string? Status, string? IssueType,
    string? Project, string? Sprint, string? Priority, string? Updated,
    string? Description = null);

public sealed record JiraSearchPage(List<JiraIssue> Issues, string? NextPageToken, bool IsLast);

public sealed class JiraClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly string _site;
    private readonly AuthenticationHeaderValue _auth;

    private JiraClient(string site, string email, string token)
    {
        _site = site.TrimEnd('/');
        _auth = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{token}")));
    }

    /// <summary>Null when site URL, email, or token is not configured.</summary>
    public static JiraClient? FromSettings()
    {
        var site = SettingsStore.Get("jira_site_url");
        var email = SettingsStore.Get("jira_email");
        var token = SettingsStore.GetProtected("jira_token");
        if (string.IsNullOrWhiteSpace(site) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
            return null;
        return new JiraClient(site, email, token);
    }

    /// <summary>Fetch one issue. Returns null for a dead key (404). Throws for auth/network errors.</summary>
    public async Task<JiraIssue?> FetchIssueAsync(string key)
    {
        // Sprint lives in an instance-specific custom field; discover its id once (cached).
        var sprintField = await GetSprintFieldIdAsync();
        var fieldList = "summary,status,issuetype,project,priority,updated,description";
        if (sprintField is not null) fieldList += "," + sprintField;

        using var response = await SendAsync(HttpMethod.Get,
            $"/rest/api/3/issue/{Uri.EscapeDataString(key)}?fields={fieldList}");
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureOkAsync(response);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return ParseIssueFields(key, doc.RootElement.GetProperty("fields"), sprintField);
    }

    /// <summary>
    /// Run a JQL search and return one page of issues plus the token for the next page.
    /// Uses JIRA Cloud's enhanced token-paginated search (/rest/api/3/search/jql).
    /// </summary>
    public async Task<JiraSearchPage> SearchIssuesAsync(string jql, string? nextPageToken, int maxResults = 50)
    {
        var sprintField = await GetSprintFieldIdAsync();
        var fields = new List<string> { "summary", "status", "issuetype", "project", "priority", "updated" };
        if (sprintField is not null) fields.Add(sprintField);

        var body = new Dictionary<string, object?>
        {
            ["jql"] = jql,
            ["maxResults"] = maxResults,
            ["fields"] = fields
        };
        if (!string.IsNullOrEmpty(nextPageToken)) body["nextPageToken"] = nextPageToken;

        using var response = await SendAsync(HttpMethod.Post, "/rest/api/3/search/jql", JsonSerializer.Serialize(body));
        await EnsureOkAsync(response);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        var issues = new List<JiraIssue>();
        if (root.TryGetProperty("issues", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var iss in arr.EnumerateArray())
            {
                var key = GetString(iss, "key");
                if (key is null || !iss.TryGetProperty("fields", out var f)) continue;
                issues.Add(ParseIssueFields(key, f, sprintField));
            }
        }

        var token = GetString(root, "nextPageToken");
        var isLast = (root.TryGetProperty("isLast", out var il) && il.ValueKind == JsonValueKind.True)
                     || string.IsNullOrEmpty(token);
        return new JiraSearchPage(issues, token, isLast);
    }

    private static JiraIssue ParseIssueFields(string key, JsonElement fields, string? sprintField) =>
        new(key,
            GetString(fields, "summary"),
            GetNestedName(fields, "status"),
            GetNestedName(fields, "issuetype"),
            GetNestedName(fields, "project"),
            sprintField is not null ? ParseSprint(fields, sprintField) : null,
            GetNestedName(fields, "priority"),
            GetString(fields, "updated"),
            ParseDescription(fields)); // null unless the request asked for the description field

    /// <summary>
    /// JIRA Cloud returns description as an Atlassian Document Format (ADF) tree. Flatten it to
    /// plain text: concatenate all text nodes, break lines on paragraph/heading/list-item and
    /// hardBreak nodes. Tolerates the legacy plain-string encoding.
    /// </summary>
    private static string? ParseDescription(JsonElement fields)
    {
        if (!fields.TryGetProperty("description", out var d)) return null;
        if (d.ValueKind == JsonValueKind.String) return d.GetString();
        if (d.ValueKind != JsonValueKind.Object) return null;

        var sb = new StringBuilder();
        AdfWalk(d, sb);
        var text = sb.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    private static void AdfWalk(JsonElement node, StringBuilder sb)
    {
        if (node.ValueKind != JsonValueKind.Object) return;

        var type = GetString(node, "type");
        if (type == "text")
            sb.Append(GetString(node, "text"));
        else if (type == "hardBreak")
            sb.Append('\n');

        if (node.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var child in content.EnumerateArray())
                AdfWalk(child, sb);

        // Block-level nodes end with a line break so paragraphs/list items don't run together.
        if (type is "paragraph" or "heading" or "listItem" or "blockquote" or "codeBlock" or "rule")
            sb.Append('\n');
    }

    /// <summary>
    /// Find the "Sprint" custom-field id for this JIRA instance and cache it in settings.
    /// Cached value "-" means the instance has no Sprint field (no agile boards).
    /// </summary>
    private async Task<string?> GetSprintFieldIdAsync()
    {
        var cached = SettingsStore.Get("jira_sprint_field");
        if (!string.IsNullOrEmpty(cached)) return cached == "-" ? null : cached;

        using var response = await SendAsync(HttpMethod.Get, "/rest/api/3/field");
        if (!response.IsSuccessStatusCode) return null; // don't cache on transient failure

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var f in doc.RootElement.EnumerateArray())
        {
            if (string.Equals(GetString(f, "name"), "Sprint", StringComparison.OrdinalIgnoreCase))
            {
                var id = GetString(f, "id");
                SettingsStore.Set("jira_sprint_field", id ?? "-");
                return id;
            }
        }
        SettingsStore.Set("jira_sprint_field", "-");
        return null;
    }

    /// <summary>
    /// Sprint field value is an array of sprint objects (active/closed/future). Prefer the
    /// active sprint, else the most recent. Tolerates the legacy string encoding.
    /// </summary>
    private static string? ParseSprint(JsonElement fields, string sprintField)
    {
        if (!fields.TryGetProperty(sprintField, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;

        string? active = null, last = null;
        foreach (var s in arr.EnumerateArray())
        {
            if (s.ValueKind == JsonValueKind.Object)
            {
                var name = GetString(s, "name");
                if (name is null) continue;
                last = name;
                if (string.Equals(GetString(s, "state"), "active", StringComparison.OrdinalIgnoreCase))
                    active = name;
            }
            else if (s.ValueKind == JsonValueKind.String)
            {
                // legacy: "...Sprint@1[id=5,name=Sprint 5,state=ACTIVE,...]"
                var raw = s.GetString() ?? "";
                var m = Regex.Match(raw, @"name=([^,\]]+)");
                if (m.Success) last = m.Groups[1].Value.Trim();
                if (raw.Contains("state=ACTIVE", StringComparison.OrdinalIgnoreCase) && m.Success)
                    active = m.Groups[1].Value.Trim();
            }
        }
        return active ?? last;
    }

    /// <summary>Verify credentials; returns the authenticated user's display name.</summary>
    public async Task<string> TestConnectionAsync()
    {
        using var response = await SendAsync(HttpMethod.Get, "/rest/api/3/myself");
        await EnsureOkAsync(response);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return GetString(doc.RootElement, "displayName") ?? "(unknown user)";
    }

    /// <summary>Approximate issue count for a JQL query (denominator of the AI-share chart).</summary>
    public async Task<long?> ApproximateCountAsync(string jql)
    {
        using var response = await SendAsync(HttpMethod.Post, "/rest/api/3/search/approximate-count",
            JsonSerializer.Serialize(new { jql }));
        if (!response.IsSuccessStatusCode) return null; // optional feature — degrade quietly
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetInt64()
            : null;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? jsonBody = null)
    {
        using var request = new HttpRequestMessage(method, _site + path);
        request.Headers.Authorization = _auth;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (jsonBody is not null)
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return await Http.SendAsync(request);
    }

    private static async Task EnsureOkAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "authentication failed — check email and API token",
            HttpStatusCode.Forbidden => "access denied — check permissions",
            _ => (await response.Content.ReadAsStringAsync()) is { Length: > 0 and < 300 } body
                ? body
                : response.ReasonPhrase ?? "request failed"
        };
        throw new HttpRequestException($"JIRA {(int)response.StatusCode}: {detail}");
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string? GetNestedName(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object
            ? GetString(p, "name")
            : null;
}
