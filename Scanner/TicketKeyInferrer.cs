using System.Text.RegularExpressions;

namespace AIUsage.Scanner;

public sealed partial class TicketKeyInferrer(HashSet<string> projectKeyAllowlist)
{
    [GeneratedRegex(@"\b[A-Z][A-Z0-9]{1,9}-\d{1,6}\b")]
    private static partial Regex KeyRegex();

    private static readonly HashSet<string> NonBranches = ["", "HEAD", "main", "master"];

    public bool IsRealBranch(string? branch) =>
        branch is not null && !NonBranches.Contains(branch.Trim());

    /// <summary>Extract ticket keys from arbitrary text, filtered by the project-key allowlist.</summary>
    public IEnumerable<string> Extract(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (Match m in KeyRegex().Matches(text))
        {
            var key = m.Value;
            var project = key[..key.IndexOf('-')];
            if (projectKeyAllowlist.Count == 0 || projectKeyAllowlist.Contains(project))
                yield return key;
        }
    }
}
