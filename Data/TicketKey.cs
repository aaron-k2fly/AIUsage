using System.Text.RegularExpressions;

namespace AIUsage.Data;

/// <summary>
/// The single definition of a valid JIRA ticket key (uppercase project key + number), shared by
/// every writer of <c>SessionTicketLinks.ticket_key</c>: the bridge handlers, the manual-entry
/// handler, the Live Code launcher and the repositories themselves. Centralised deliberately —
/// the Live Code path used to be the only unconstrained writer of that column, and its value
/// comes from a remote JIRA server (2026-08 audit, AIU-04).
/// </summary>
public static partial class TicketKey
{
    [GeneratedRegex(@"^[A-Z][A-Z0-9]{1,9}-\d{1,6}$")]
    private static partial Regex Pattern();

    /// <summary>True for an already-normalised (trimmed, uppercase) key such as "SFTY-1234".</summary>
    public static bool IsValid(string? key) => key is not null && Pattern().IsMatch(key);

    /// <summary>Trim + uppercase, the form the DB stores.</summary>
    public static string Normalize(string? raw) => (raw ?? "").Trim().ToUpperInvariant();

    /// <summary>Normalise and validate, throwing the message the UI toasts on failure.</summary>
    public static string Require(string? raw)
    {
        var key = Normalize(raw);
        if (!IsValid(key))
            throw new ArgumentException($"'{key}' is not a valid ticket key (expected e.g. SFTY-1234)");
        return key;
    }
}
