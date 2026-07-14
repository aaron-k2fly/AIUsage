namespace AIUsage.Terminal;

/// <summary>Resolved shell to host a session, plus whether we fell back from the request.</summary>
public sealed record ResolvedShell(string Exe, string Kind, bool FellBack);

/// <summary>
/// Picks the executable for the requested shell. "bash" resolves to Git Bash; if Git Bash
/// isn't installed we fall back to PowerShell and flag it so the UI can notify the user.
/// </summary>
public static class ShellResolver
{
    public static ResolvedShell Resolve(string requested)
    {
        if (string.Equals(requested, "bash", StringComparison.OrdinalIgnoreCase))
        {
            var bash = FindGitBash();
            if (bash is not null) return new ResolvedShell(bash, "bash", FellBack: false);
            return new ResolvedShell(ResolvePowerShell(), "powershell", FellBack: true);
        }
        return new ResolvedShell(ResolvePowerShell(), "powershell", FellBack: false);
    }

    private static string ResolvePowerShell() =>
        FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe") ?? "powershell.exe";

    private static string? FindGitBash()
    {
        // Git for Windows install locations come first. We deliberately do NOT prefer a bare
        // "bash.exe" on PATH: on Windows that resolves to C:\Windows\System32\bash.exe — the WSL
        // launcher — which fails with "execvpe(/bin/bash) failed" when there's no Linux distro.
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "bin", "bash.exe"),
        };

        // Also derive from git.exe on PATH: <git>\cmd\git.exe -> <git>\bin\bash.exe
        var git = FindOnPath("git.exe");
        if (git is not null)
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(git)); // strip \cmd
            if (root is not null) candidates.Add(Path.Combine(root, "bin", "bash.exe"));
        }

        // A bash.exe on PATH is a last resort, and only if it isn't the WSL/system shim.
        var onPath = FindOnPath("bash.exe");
        if (onPath is not null && !IsSystemShim(onPath)) candidates.Add(onPath);

        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool IsSystemShim(string path) =>
        path.Contains(@"\System32\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains(@"\SysWOW64\", StringComparison.OrdinalIgnoreCase);

    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var full = Path.Combine(dir, exe);
                if (File.Exists(full)) return full;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }
}
