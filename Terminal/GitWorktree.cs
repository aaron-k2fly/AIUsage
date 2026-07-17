using System.Diagnostics;
using System.Text;

namespace AIUsage.Terminal;

/// <summary>
/// Isolated git-worktree operations for Live Code session isolation. All git is run via the
/// `git` CLI. <see cref="IsGitRepo"/> and <see cref="TryRemoveIfClean"/> never throw;
/// <see cref="Create"/> throws on git failure so the caller can surface the error and not launch
/// a session in a broken state.
/// </summary>
public sealed record WorktreeInfo(string WorktreePath, string Cwd, string Branch, string BaseSha, string Toplevel);

public static class GitWorktree
{
    /// <summary>True when <paramref name="folder"/> is inside a git work tree.</summary>
    public static bool IsGitRepo(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return false;
        var (ok, stdout, _) = Run(folder, "rev-parse", "--is-inside-work-tree");
        return ok && stdout.Trim() == "true";
    }

    /// <summary>Create a new worktree off HEAD on a fresh branch, in a sibling
    /// <c>&lt;toplevel&gt;-worktrees/&lt;suffix&gt;-&lt;hex&gt;</c> folder (outside the repo so the agent
    /// won't scan it). Returns the info incl. the cwd to launch in (re-applying any subfolder the
    /// user selected beneath the repo root). Throws <see cref="InvalidOperationException"/> on git
    /// error.</summary>
    public static WorktreeInfo Create(string folder, string suffix)
    {
        var toplevel = MustRun(folder, "top-level", "rev-parse", "--show-toplevel").Trim();
        var baseSha = MustRun(toplevel, "HEAD sha", "rev-parse", "HEAD").Trim();
        var hex = Guid.NewGuid().ToString("N")[..8];
        var safe = Sanitize(suffix);
        var branch = $"livecode/{safe}-{hex}";

        var parent = Path.GetDirectoryName(toplevel.TrimEnd('/', '\\')) ?? toplevel;
        var baseName = Path.GetFileName(toplevel.TrimEnd('/', '\\'));
        var path = Path.Combine(parent, $"{baseName}-worktrees", $"{safe}-{hex}");

        MustRun(toplevel, "worktree add", "worktree", "add", "-b", branch, path);

        var rel = Path.GetRelativePath(toplevel, folder);
        var cwd = (rel == "." || rel.StartsWith("..")) ? path : Path.Combine(path, rel);
        return new WorktreeInfo(path, cwd, branch, baseSha, toplevel);
    }

    /// <summary>Remove the worktree + branch only if clean: no uncommitted changes AND no commits
    /// on the branch beyond its base. Otherwise keep it and return the reason.</summary>
    public static (bool removed, string? keptReason) TryRemoveIfClean(WorktreeInfo info)
    {
        try
        {
            var (statusOk, status, _) = Run(info.WorktreePath, "status", "--porcelain");
            if (statusOk && status.Trim().Length > 0) return (false, "has uncommitted changes");

            var (aheadOk, ahead, _) = Run(info.Toplevel, "rev-list", $"{info.BaseSha}..{info.Branch}", "--count");
            if (aheadOk && int.TryParse(ahead.Trim(), out var n) && n > 0) return (false, "has unmerged commits");

            var (rmOk, _, rmErr) = Run(info.Toplevel, "worktree", "remove", info.WorktreePath);
            if (!rmOk) return (false, string.IsNullOrWhiteSpace(rmErr) ? "worktree is locked or in use" : rmErr.Trim());

            Run(info.Toplevel, "branch", "-D", info.Branch); // best-effort; branch is only ours
            return (true, null);
        }
        catch { return (false, "cleanup failed"); }
    }

    /// <summary>Keep only branch/path-safe characters; fall back to "session" when nothing is left.</summary>
    private static string Sanitize(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-');
        var r = sb.ToString().Trim('-');
        return r.Length == 0 ? "session" : r;
    }

    private static string MustRun(string cwd, string what, params string[] args)
    {
        var (ok, stdout, stderr) = Run(cwd, args);
        if (!ok) throw new InvalidOperationException($"git {what} failed: {(stderr.Trim().Length > 0 ? stderr.Trim() : "unknown error")}");
        return stdout;
    }

    private static (bool ok, string stdout, string stderr) Run(string cwd, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return (false, "", "could not start git");
            var so = p.StandardOutput.ReadToEnd();
            var se = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return (p.ExitCode == 0, so, se);
        }
        catch (Exception ex) { return (false, "", ex.Message); }
    }
}
