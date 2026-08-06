namespace AIUsage.Jira;

/// <summary>
/// Validation for the `jira_site_url` setting. This value is the address a **reversible** Basic
/// credential (`base64(email:token)`) is sent to on every request — including every background sync
/// — so it is not merely trimmed: it must be an absolute `https://` URL with a real host and no
/// embedded userinfo. Previously `http://jira.internal` was accepted and the credential went out in
/// cleartext (2026-08 audit, AIU-06).
///
/// Used by every writer of the setting (`settings.set`, the `--set` CLI verb) and by
/// <see cref="JiraClient.FromSettings"/>, which refuses to build a client for an insecure value so a
/// pre-existing or hand-edited setting can never leak the credential either.
/// </summary>
public static class JiraSiteUrl
{
    private const string Requirement =
        "JIRA site URL must be an absolute https:// URL with a host and no embedded credentials " +
        "(e.g. https://yourcompany.atlassian.net)";

    /// <summary>Validate and canonicalise for storage (lowercased host, no trailing slash).
    /// Throws <see cref="ArgumentException"/> — the message is what the UI toasts.</summary>
    public static string Normalize(string? raw)
    {
        var uri = Parse(raw) ?? throw new ArgumentException(Requirement);
        // GetLeftPart lowercases the scheme+host and keeps a non-default port; the path is kept
        // (some instances live under /jira) minus any trailing slashes.
        var authority = uri.GetLeftPart(UriPartial.Authority);
        var path = uri.AbsolutePath.TrimEnd('/');
        return authority + path;
    }

    /// <summary>True when a stored value is a URL a credential may safely be sent to.</summary>
    public static bool IsSecure(string? stored) => Parse(stored) is not null;

    /// <summary>`host:port` of a stored value (the identity a credential would be handed to), or
    /// null if it isn't a usable https URL.</summary>
    public static string? Authority(string? stored)
    {
        var uri = Parse(stored);
        return uri is null ? null : uri.Host + ":" + uri.Port;
    }

    /// <summary>
    /// True when <paramref name="newUrl"/> would send the currently stored token to a different
    /// host than <paramref name="oldUrl"/> — the signal to drop the stored token instead of
    /// replaying it (blunts the token-exfiltration path noted under AIU-07). Cosmetic edits
    /// (trailing slash, casing, a different path on the same host) are not a host change.
    /// </summary>
    public static bool PointsAtADifferentHost(string? oldUrl, string? newUrl)
    {
        var before = Authority(oldUrl);
        var after = Authority(newUrl);
        if (before is null || after is null) return false;   // nothing configured / nothing valid
        return !string.Equals(before, after, StringComparison.OrdinalIgnoreCase);
    }

    private static Uri? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps) return null;          // never plain http
        if (string.IsNullOrEmpty(uri.Host)) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return null;       // no user:pass@host
        return uri;
    }
}
