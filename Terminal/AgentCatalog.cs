namespace AIUsage.Terminal;

public sealed record AgentInfo(string Name, string? Description, string Scope);

/// <summary>
/// Lists Claude Code subagent definitions available for a session: those in the selected
/// project's <c>.claude/agents</c> and the user's <c>~/.claude/agents</c>. Each agent is a
/// markdown file with YAML-ish frontmatter carrying <c>name</c> and <c>description</c>;
/// the frontmatter <c>name</c> is what the CLI's <c>--agent</c> flag expects, falling back
/// to the file name when absent.
/// </summary>
public static class AgentCatalog
{
    /// <param name="projectDir">Working folder; its <c>.claude/agents</c> is scanned.</param>
    /// <param name="customDir">Optional user-chosen agents folder (scanned directly, and its
    /// <c>.claude/agents</c> if that's what they pointed at).</param>
    public static List<AgentInfo> List(string? projectDir, string? customDir = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AgentInfo>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var sources = new List<(string Dir, string Scope)>();
        if (!string.IsNullOrWhiteSpace(customDir))
        {
            sources.Add((customDir!, "custom"));                              // folder holds *.md directly
            sources.Add((Path.Combine(customDir!, ".claude", "agents"), "custom")); // …or is a project root
        }
        if (!string.IsNullOrWhiteSpace(projectDir))
            sources.Add((Path.Combine(projectDir, ".claude", "agents"), "project"));
        sources.Add((Path.Combine(home, ".claude", "agents"), "user"));

        foreach (var (dir, scope) in sources)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly))
            {
                var (name, desc) = ParseFrontmatter(file);
                name ??= Path.GetFileNameWithoutExtension(file);
                if (!seen.Add(name)) continue; // project agent shadows a same-named user agent
                result.Add(new AgentInfo(name, desc, scope));
            }
        }

        return result.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Make the custom Agents-folder agents usable in the session: copy their <c>*.md</c> into the
    /// working folder's <c>.claude/agents/</c> (which Claude Code discovers for <c>--agent</c>) before
    /// the session starts. Existing project agents are NOT overwritten (avoids clobbering the repo's
    /// own). Returns how many files were copied.
    /// </summary>
    public static int SyncCustomAgents(string? customDir, string? workingFolder)
    {
        if (string.IsNullOrWhiteSpace(customDir) || string.IsNullOrWhiteSpace(workingFolder)) return 0;

        var dest = Path.Combine(workingFolder!, ".claude", "agents");
        var sources = new[] { customDir!, Path.Combine(customDir!, ".claude", "agents") };
        var copied = 0;

        foreach (var src in sources)
        {
            if (!Directory.Exists(src)) continue;
            foreach (var file in Directory.EnumerateFiles(src, "*.md", SearchOption.TopDirectoryOnly))
            {
                var target = Path.Combine(dest, Path.GetFileName(file));
                try
                {
                    if (File.Exists(target)) continue; // never clobber an agent already in the project
                    if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                        continue; // custom folder already IS the project agents dir
                    Directory.CreateDirectory(dest);
                    File.Copy(file, target);
                    copied++;
                }
                catch { /* skip unreadable/locked files */ }
            }
        }
        return copied;
    }

    /// <summary>Pull name/description from a leading <c>---</c> frontmatter block. Tolerant of quotes and missing fields.</summary>
    private static (string? Name, string? Description) ParseFrontmatter(string file)
    {
        string? name = null, desc = null;
        try
        {
            using var reader = new StreamReader(file);
            var first = reader.ReadLine();
            if (first?.Trim() != "---") return (null, null);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Trim() == "---") break;
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var key = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim().Trim('"', '\'');
                if (value.Length == 0) continue;
                if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) name = value;
                else if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) desc = value;
            }
        }
        catch
        {
            // unreadable file — treat as no frontmatter
        }
        return (name, desc);
    }
}
