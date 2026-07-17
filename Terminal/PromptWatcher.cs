using System.Text;
using System.Text.RegularExpressions;

namespace AIUsage.Terminal;

/// <summary>
/// Best-effort auto-approve for the live terminal. Watches the (ANSI-stripped) output stream and,
/// when it looks like Claude Code is showing a confirmation prompt, returns the keystroke to accept
/// it (Enter — selects the highlighted default, normally "Yes"/"proceed").
///
/// This is deliberately conservative and inherently fragile: scraping a rich TUI is not reliable, so
/// it's a convenience layered on top of the real mechanism (launching with a low-friction
/// --permission-mode). A cooldown prevents rapid double-answers.
/// </summary>
public sealed class PromptWatcher
{
    private static readonly Regex Ansi =
        new(@"\x1b\[[0-9;?]*[A-Za-z]|\x1b\][^\x07]*(\x07|\x1b\\)", RegexOptions.Compiled);

    private readonly StringBuilder _tail = new();
    private DateTime _lastInject = DateTime.MinValue;
    private static readonly TimeSpan Cooldown = TimeSpan.FromMilliseconds(1500);

    /// <summary>Feed a raw output chunk; returns bytes to write back (Enter) when a prompt is detected, else null.</summary>
    public byte[]? Observe(byte[] chunk)
    {
        var text = Ansi.Replace(Encoding.UTF8.GetString(chunk), "");
        _tail.Append(text);
        if (_tail.Length > 4000) _tail.Remove(0, _tail.Length - 4000);

        var now = DateTime.UtcNow;
        if (now - _lastInject < Cooldown) return null;

        if (LooksLikePrompt(_tail.ToString()))
        {
            _lastInject = now;
            _tail.Clear(); // don't re-match the same prompt text on the next chunk
            return "\r"u8.ToArray();
        }
        return null;
    }

    private static bool LooksLikePrompt(string s)
    {
        // Only inspect the tail — a prompt is the most recent thing drawn.
        var tail = s.Length > 500 ? s[^500..] : s;
        return tail.Contains("❯ 1.")            // Claude's arrow-selected menu, option 1 highlighted
            || tail.Contains("❯ 1 ")
            || tail.Contains("(y/n)")
            || tail.Contains("[y/N]")
            || tail.Contains("[Y/n]")
            || tail.Contains("Do you want to proceed");
    }
}
