using AIUsage.Jira;

namespace AIUsage.Tests;

/// <summary>
/// The JIRA site URL is the address a reversible Basic credential (email:token) is sent to on every
/// request, so it is validated rather than merely trimmed: https only, real host, no embedded
/// credentials (2026-08 audit, AIU-06).
/// </summary>
public class JiraSiteUrlTests
{
    [Theory]
    [InlineData("https://acme.atlassian.net", "https://acme.atlassian.net")]
    [InlineData("https://acme.atlassian.net/", "https://acme.atlassian.net")]
    [InlineData("  https://acme.atlassian.net///  ", "https://acme.atlassian.net")]
    [InlineData("https://jira.internal:8443/jira/", "https://jira.internal:8443/jira")]
    [InlineData("HTTPS://Acme.Atlassian.NET", "https://acme.atlassian.net")]
    public void Normalize_accepts_https_and_trims_trailing_slashes(string input, string expected) =>
        Assert.Equal(expected, JiraSiteUrl.Normalize(input));

    [Theory]
    [InlineData("http://jira.internal")]              // the finding: cleartext Basic credential
    [InlineData("HTTP://jira.internal")]
    [InlineData("ftp://jira.internal")]
    [InlineData("file:///c:/jira")]
    [InlineData("jira.internal")]                     // no scheme → not absolute
    [InlineData("//jira.internal")]
    [InlineData("https://")]
    [InlineData("https://user:pass@jira.internal")]   // credentials in the URL
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    public void Normalize_rejects_anything_but_https(string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => JiraSiteUrl.Normalize(input));
        Assert.Contains("https", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://acme.atlassian.net", true)]
    [InlineData("https://acme.atlassian.net/", true)]
    [InlineData("http://acme.atlassian.net", false)]
    [InlineData("acme.atlassian.net", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSecure_reports_whether_a_stored_value_is_safe_to_send_a_credential_to(string? stored, bool expected) =>
        Assert.Equal(expected, JiraSiteUrl.IsSecure(stored));

    [Theory]
    [InlineData("https://acme.atlassian.net", "acme.atlassian.net:443")]
    [InlineData("https://jira.internal:8443/jira", "jira.internal:8443")]
    [InlineData("garbage", null)]
    [InlineData(null, null)]
    public void Authority_identifies_the_host_a_credential_would_go_to(string? stored, string? expected) =>
        Assert.Equal(expected, JiraSiteUrl.Authority(stored));

    [Theory]
    // Same host (even with cosmetic differences) → the stored token still belongs to it.
    [InlineData("https://acme.atlassian.net", "https://acme.atlassian.net/", false)]
    [InlineData("https://acme.atlassian.net", "https://ACME.atlassian.net", false)]
    [InlineData("https://acme.atlassian.net/jira", "https://acme.atlassian.net/other", false)]
    // Different host → the credential must not be replayable to it.
    [InlineData("https://acme.atlassian.net", "https://evil.example", true)]
    [InlineData("https://acme.atlassian.net", "https://acme.atlassian.net:8443", true)]
    // Nothing configured before, or nothing valid now → nothing to protect / nothing to compare.
    [InlineData(null, "https://acme.atlassian.net", false)]
    [InlineData("", "https://acme.atlassian.net", false)]
    public void PointsAtADifferentHost_decides_whether_to_drop_the_stored_token(
        string? oldUrl, string newUrl, bool expected) =>
        Assert.Equal(expected, JiraSiteUrl.PointsAtADifferentHost(oldUrl, newUrl));
}
