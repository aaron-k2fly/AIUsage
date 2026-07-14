using System.Drawing;
using AIUsage.Bridge;
using AIUsage.Data;
using Photino.NET;

namespace AIUsage;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Db.Initialize();

        var route = "";
        if (args.Length > 0)
        {
            if (args[0] == "--route" && args.Length > 1)
            {
                route = "#" + args[1];
            }
            else
            {
                RunCli(args);
                return;
            }
        }

        var window = new PhotinoWindow()
            .SetTitle("AI Usage Tracker")
            .SetUseOsDefaultSize(false)
            .SetSize(new Size(1280, 860)) // restore size when un-maximized
            .Center()
            .SetResizable(true)
            .SetMaximized(true)
            .SetDevToolsEnabled(true);

        var iconPath = WebAssets.ExtractIcon();
        if (iconPath is not null) window.SetIconFile(iconPath);

        var router = new MessageRouter(window);
        Bridge.Handlers.SessionHandlers.Register(router);
        Bridge.Handlers.ManualHandlers.Register(router);
        Bridge.Handlers.JiraHandlers.Register(router);
        Bridge.Handlers.SettingsHandlers.Register(router);
        Bridge.Handlers.StatsHandlers.Register(router);
        Bridge.Handlers.ExportHandlers.Register(router);
        Bridge.Handlers.LiveCodeHandlers.Register(router, window);
        window.RegisterWebMessageReceivedHandler(router.OnMessage);

        var indexPath = Path.Combine(WebAssets.EnsureExtracted(), "index.html");
        window.Load(new Uri("file:///" + indexPath.Replace('\\', '/') + route));
        window.WaitForClose();
    }

    /// <summary>Headless debug commands: --scan runs the transcript scanner, --sql runs a read-only query.</summary>
    private static void RunCli(string[] args)
    {
        switch (args[0])
        {
            case "--scan":
                var r = new Scanner.TranscriptScanner().Run();
                Console.WriteLine($"sessions={r.Sessions} newFiles={r.NewFiles} updatedFiles={r.UpdatedFiles} skippedFiles={r.SkippedFiles}");
                break;

            case "--pty-test":
                RunPtyTest();
                break;

            case "--sql" when args.Length > 1:
                using (var conn = Db.Open())
                using (var cmd = conn.CreateCommand())
                {
                    using (var guard = conn.CreateCommand())
                    {
                        guard.CommandText = "PRAGMA query_only = ON";
                        guard.ExecuteNonQuery();
                    }
                    cmd.CommandText = args[1];
                    try
                    {
                        using var reader = cmd.ExecuteReader();
                        Console.WriteLine(string.Join("\t", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)));
                        while (reader.Read())
                            Console.WriteLine(string.Join("\t", Enumerable.Range(0, reader.FieldCount)
                                .Select(i => reader.IsDBNull(i) ? "NULL" : reader.GetValue(i)?.ToString())));
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException ex)
                    {
                        Console.WriteLine($"SQL error: {ex.Message} (--sql is read-only)");
                        Environment.ExitCode = 1;
                    }
                }
                break;

            case "--set" when args.Length > 2:
                if (args[1] == "jira_token")
                    Settings.SettingsStore.SetProtected("jira_token", args[2]);
                else
                    Settings.SettingsStore.Set(args[1], args[2]);
                if (args[1] == "project_key_allowlist")
                    Bridge.Handlers.SettingsHandlers.PurgeDisallowedAutoLinks();
                Console.WriteLine($"set {args[1]}");
                break;

            default:
                Console.WriteLine("Usage: AIUsage [--scan | --sql \"SELECT ...\" | --set <key> <value> | --pty-test]");
                break;
        }
    }

    /// <summary>Headless smoke test for the ConPTY interop (Terminal/ConPtySession): spawns
    /// cmd.exe in a pseudo-console, feeds it a command, and verifies the output comes back.</summary>
    private static void RunPtyTest()
    {
        var output = new System.Text.StringBuilder();
        using var exited = new ManualResetEventSlim(false);
        var code = -1;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stamps = new System.Collections.Concurrent.ConcurrentQueue<string>();

        // Exercises the real ConPtySession wrapper. ping streams ~1 line/sec for ~5s; multiple
        // timestamped chunks arriving during the run prove continuous streaming (not batched at close).
        using var session = new Terminal.ConPtySession();
        session.Output += bytes =>
        {
            stamps.Enqueue($"{sw.ElapsedMilliseconds}ms:{bytes.Length}B");
            output.Append(System.Text.Encoding.UTF8.GetString(bytes));
        };
        session.Exited += c => { code = c; exited.Set(); };

        session.Start("ping.exe", new[] { "-n", "6", "127.0.0.1" },
            Environment.CurrentDirectory, envOverrides: null, cols: 120, rows: 30);

        exited.Wait(TimeSpan.FromSeconds(20));
        var chunks = stamps.Count;
        Console.WriteLine();
        Console.WriteLine($"[pty-test] exitCode={code} chunks={chunks} streamed={chunks > 2} " +
                          $"timeline=[{string.Join(" ", stamps)}]");
        Environment.ExitCode = code == 0 && chunks > 2 ? 0 : 1;
    }
}
