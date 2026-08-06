using Photino.NET;

namespace AIUsage.Platform;

/// <summary>
/// Native OS confirmation dialog, used where a security decision must NOT be made by the WebView.
/// Every other confirmation in this app is an in-page modal (`App.confirm`), which is fine for
/// "are you sure?" UX — but the backend can only see the resulting payload flag, so a script in the
/// document could set it without any user involvement (2026-08 audit, AIU-07). A native dialog is
/// drawn by the host, cannot be answered from the page, and is visible even when the request came
/// from something the user never clicked.
///
/// Same UI-thread marshalling as <see cref="FolderDialog"/>: Photino dialogs must run on the UI
/// thread, so we <c>window.Invoke</c> and block the calling bridge pool thread until it returns.
/// Callers must use an unbounded client timeout (<c>Bridge.call(..., 0)</c>) because the dialog waits
/// on a human.
/// </summary>
public static class MessageDialog
{
    /// <summary>
    /// Yes/No confirmation. Returns false if the user declines **or** if the dialog cannot be shown
    /// — the caller is granting a privilege, so an unanswerable question must fail closed.
    /// </summary>
    public static bool Confirm(PhotinoWindow window, string title, string message)
    {
        var granted = false;
        using var done = new ManualResetEventSlim(false);

        window.Invoke(() =>
        {
            try
            {
                granted = window.ShowMessage(title, message,
                    PhotinoDialogButtons.YesNo, PhotinoDialogIcon.Warning) == PhotinoDialogResult.Yes;
            }
            catch
            {
                granted = false;   // fail closed — never grant on a dialog failure
            }
            finally
            {
                done.Set();
            }
        });

        done.Wait();
        return granted;
    }
}
