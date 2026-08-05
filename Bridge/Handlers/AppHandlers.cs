using AIUsage.Platform;

namespace AIUsage.Bridge.Handlers;

public static class AppHandlers
{
    public static void Register(MessageRouter router)
    {
        // Synchronous handler returning Task.FromResult (no Task.Run) — see the note in
        // SessionHandlers.Register on the null-return / unwrap-overload cancellation trap.
        router.Register("app.info", _ => Task.FromResult<object?>(new
        {
            version = AppVersion.Semver,
            commit = AppVersion.Commit,
            buildDate = AppVersion.BuildDate,
            @short = AppVersion.Short,
            detail = AppVersion.Detail
        }));
    }
}
