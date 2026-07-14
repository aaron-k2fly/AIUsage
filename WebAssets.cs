using System.Reflection;

namespace AIUsage;

/// <summary>
/// The wwwroot files are embedded in the assembly (see AIUsage.csproj). On startup they are
/// extracted to a per-user cache directory so the WebView can load them over file:// — this
/// keeps the app a single portable .exe with no wwwroot folder to ship alongside.
/// </summary>
public static class WebAssets
{
    private const string Prefix = "web/";

    public static string EnsureExtracted()
    {
        var asm = Assembly.GetExecutingAssembly();
        var resources = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .ToList();

        if (resources.Count == 0)
        {
            // Dev fallback: assets present on disk next to the exe.
            var disk = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (File.Exists(Path.Combine(disk, "index.html"))) return disk;
            throw new InvalidOperationException("Web assets not found (neither embedded nor on disk).");
        }

        var target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsage", "web");

        foreach (var name in resources)
        {
            var rel = name[Prefix.Length..]
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var rs = asm.GetManifestResourceStream(name)!;
            using var fs = File.Create(dest); // overwrite each launch so updates take effect
            rs.CopyTo(fs);
        }
        return target;
    }

    /// <summary>Extract the embedded window icon and return its path (null if unavailable).</summary>
    public static string? ExtractIcon()
    {
        var asm = Assembly.GetExecutingAssembly();
        var rs = asm.GetManifestResourceStream("appicon.ico");
        if (rs is null)
        {
            var disk = Path.Combine(AppContext.BaseDirectory, "appicon.ico");
            return File.Exists(disk) ? disk : null;
        }
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsage");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "appicon.ico");
        using (rs)
        using (var fs = File.Create(path))
            rs.CopyTo(fs);
        return path;
    }
}
