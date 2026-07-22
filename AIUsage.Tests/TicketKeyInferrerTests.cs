using AIUsage.Scanner;

namespace AIUsage.Tests;

public class TicketKeyInferrerTests
{
    private static TicketKeyInferrer WithAllowlist(params string[] keys) => new([.. keys]);

    [Fact]
    public void Extract_keeps_keys_on_the_allowlist_and_drops_others()
    {
        var inferrer = WithAllowlist("ABC");
        var keys = inferrer.Extract("Fixing ABC-123 and XYZ-9 today").ToList();

        Assert.Contains("ABC-123", keys);
        Assert.DoesNotContain("XYZ-9", keys);
    }

    [Fact]
    public void Extract_with_empty_allowlist_returns_all_matches()
    {
        var inferrer = WithAllowlist(); // empty => allow all projects
        var keys = inferrer.Extract("ABC-1 and XYZ-9").ToList();

        Assert.Equal(new[] { "ABC-1", "XYZ-9" }, keys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no ticket keys here")]
    [InlineData("lowercase abc-1 is not a key")]
    public void Extract_returns_nothing_when_there_is_no_valid_key(string? text)
    {
        var inferrer = WithAllowlist(); // allow-all so filtering isn't the reason
        Assert.Empty(inferrer.Extract(text));
    }

    [Fact]
    public void Extract_finds_multiple_keys_across_text()
    {
        var inferrer = WithAllowlist();
        var keys = inferrer.Extract("branch feature/ABC-12 closes ABC-34").ToList();

        Assert.Equal(2, keys.Count);
        Assert.Contains("ABC-12", keys);
        Assert.Contains("ABC-34", keys);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("HEAD", false)]
    [InlineData("main", false)]
    [InlineData("master", false)]
    [InlineData(" main ", false)] // trimmed before comparison
    [InlineData("feature/ABC-1", true)]
    [InlineData("develop", true)]
    public void IsRealBranch_rejects_placeholder_and_default_branches(string? branch, bool expected)
    {
        var inferrer = WithAllowlist();
        Assert.Equal(expected, inferrer.IsRealBranch(branch));
    }
}
