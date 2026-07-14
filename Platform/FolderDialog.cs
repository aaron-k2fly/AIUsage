using Photino.NET;

namespace AIUsage.Platform;

/// <summary>
/// Native "choose folder" dialog. Photino's file dialogs must run on the UI thread —
/// calling them from a bridge handler's pool thread returns null (its internal Invoke
/// posts async and returns before the result is set; see PROGRESS.md ShowSaveFile note).
/// We marshal onto the UI thread with window.Invoke and block the caller until it returns.
/// If this proves flaky on a given machine, the Live Code page also lets the user type the
/// path directly, so folder selection never depends solely on the dialog.
/// </summary>
public static class FolderDialog
{
    public static string? Pick(PhotinoWindow window, string title, string? initialDir)
    {
        string? result = null;
        using var done = new ManualResetEventSlim(false);

        window.Invoke(() =>
        {
            try
            {
                var start = !string.IsNullOrWhiteSpace(initialDir) && Directory.Exists(initialDir)
                    ? initialDir!
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var picked = window.ShowOpenFolder(title, start, multiSelect: false);
                result = picked is { Length: > 0 } ? picked[0] : null;
            }
            catch
            {
                result = null; // fall back to the manual text field on the page
            }
            finally
            {
                done.Set();
            }
        });

        done.Wait();
        return result;
    }
}
