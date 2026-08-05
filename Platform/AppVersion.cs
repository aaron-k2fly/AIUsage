using System.Reflection;

namespace AIUsage.Platform;

/// <summary>
/// The app's own build-time version: semver from AIUsage.csproj's &lt;Version&gt;, plus the git
/// short commit hash and UTC build date stamped in by the csproj's SetGitCommitHash target (see
/// comments there). Read once from this assembly's attributes. The commit/build date are
/// best-effort — a build made outside a git checkout simply omits them.
/// </summary>
public static class AppVersion
{
    public static string Semver { get; }
    public static string? Commit { get; }
    public static string? BuildDate { get; }

    /// <summary>Compact form for the UI, e.g. "v1.0.0 · 7c7e4f5" (or "v1.0.0" without a commit).</summary>
    public static string Short { get; }

    /// <summary>Multi-line detail for a tooltip.</summary>
    public static string Detail { get; }

    static AppVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        BuildDate = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(m => m.Key == "BuildDate")?.Value;

        (Semver, Commit) = Parse(informational);
        Short = Commit is null ? $"v{Semver}" : $"v{Semver} · {Commit}";
        Detail = "AI Usage Tracker " + Semver
            + (Commit is null ? "" : $"\ncommit {Commit}")
            + (BuildDate is null ? "" : $"\nbuilt {BuildDate}");
    }

    /// <summary>
    /// Splits an AssemblyInformationalVersion like "1.0.0+7c7e4f5" into ("1.0.0", "7c7e4f5").
    /// A bare "1.0.0" (no git commit at build time) yields a null commit. Public so this
    /// parsing logic is unit-testable without needing a real stamped assembly.
    /// </summary>
    public static (string Semver, string? Commit) Parse(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return ("0.0.0", null);
        var plus = informationalVersion.IndexOf('+');
        return plus < 0
            ? (informationalVersion, null)
            : (informationalVersion[..plus], informationalVersion[(plus + 1)..]);
    }
}
