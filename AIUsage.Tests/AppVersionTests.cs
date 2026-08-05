using AIUsage.Platform;

namespace AIUsage.Tests;

public class AppVersionTests
{
    [Fact]
    public void Parse_splits_semver_and_commit_hash()
    {
        var (semver, commit) = AppVersion.Parse("1.0.0+7c7e4f5");

        Assert.Equal("1.0.0", semver);
        Assert.Equal("7c7e4f5", commit);
    }

    [Fact]
    public void Parse_bare_semver_yields_null_commit()
    {
        var (semver, commit) = AppVersion.Parse("1.0.0");

        Assert.Equal("1.0.0", semver);
        Assert.Null(commit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_missing_version_falls_back_to_zero(string? raw)
    {
        var (semver, commit) = AppVersion.Parse(raw);

        Assert.Equal("0.0.0", semver);
        Assert.Null(commit);
    }

    [Fact]
    public void Static_fields_are_populated_from_the_built_assembly()
    {
        // The test project references AIUsage.csproj, so the app assembly is built with the
        // same SetGitCommitHash/Version stamping — verifies the wiring end to end, not just Parse.
        Assert.False(string.IsNullOrWhiteSpace(AppVersion.Semver));
        Assert.False(string.IsNullOrWhiteSpace(AppVersion.Short));
        Assert.Contains(AppVersion.Semver, AppVersion.Short);
    }
}
