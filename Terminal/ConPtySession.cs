using Porta.Pty;

namespace AIUsage.Terminal;

/// <summary>
/// A single interactive process hosted in a pseudo-console. Raw output bytes are surfaced via
/// <see cref="Output"/> for a terminal emulator to render; keystrokes are fed back via
/// <see cref="Write"/>. This is the .NET half of the Live Code terminal.
///
/// Backed by the Porta.Pty library (a maintained ConPTY wrapper). A hand-rolled raw-ConPTY
/// implementation was tried first (the plan's approach B) but conhost would not stream output
/// continuously on this Windows build — it only flushed on resize/close — so we took the
/// plan's pre-authorized fallback to a library, which streams reliably and is more portable
/// for the published exe.
/// </summary>
public sealed class ConPtySession : IDisposable
{
    /// <summary>Raw stdout/stderr bytes from the child (UTF-8 with ANSI escapes).</summary>
    public event Action<byte[]>? Output;
    /// <summary>Raised once when the child process exits, with its exit code.</summary>
    public event Action<int>? Exited;

    private IPtyConnection? _pty;
    private Thread? _reader;
    private volatile bool _disposed;

    /// <param name="app">Executable to launch (shell or claude).</param>
    /// <param name="args">Arguments passed to <paramref name="app"/>.</param>
    /// <param name="envOverrides">Applied over the current environment; a null value removes a variable.</param>
    public void Start(string app, IReadOnlyList<string> args, string? workingDir,
                      IReadOnlyDictionary<string, string?>? envOverrides, short cols, short rows)
    {
        if (cols < 1) cols = 120;
        if (rows < 1) rows = 30;

        var options = new PtyOptions
        {
            Name = "AIUsage Live Code",
            Cols = cols,
            Rows = rows,
            Cwd = string.IsNullOrWhiteSpace(workingDir) ? Environment.CurrentDirectory : workingDir!,
            App = app,
            CommandLine = args as string[] ?? args.ToArray(),
            Environment = BuildEnvironment(envOverrides)
        };

        _pty = PtyProvider.SpawnAsync(options, CancellationToken.None).GetAwaiter().GetResult();
        _pty.ProcessExited += (_, e) => { if (!_disposed) Exited?.Invoke(e.ExitCode); };

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "pty-read" };
        _reader.Start();
    }

    public void Write(byte[] data)
    {
        if (_disposed || _pty is null) return;
        try
        {
            _pty.WriterStream.Write(data, 0, data.Length);
            _pty.WriterStream.Flush();
        }
        catch (IOException) { /* child gone */ }
        catch (ObjectDisposedException) { }
    }

    public void Resize(short cols, short rows)
    {
        if (_disposed || _pty is null || cols < 1 || rows < 1) return;
        try { _pty.Resize(cols, rows); } catch { /* race with exit */ }
    }

    private void ReadLoop()
    {
        var buffer = new byte[4096];
        try
        {
            int n;
            while (!_disposed && (n = _pty!.ReaderStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var chunk = new byte[n];
                Array.Copy(buffer, chunk, n);
                Output?.Invoke(chunk);
            }
        }
        catch (IOException) { /* pipe closed on exit */ }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _pty?.Kill(); } catch { /* already gone */ }
        _pty = null;
    }

    /// <summary>Current environment with overrides applied (null value removes a variable). The full
    /// set is passed so the child inherits PATH etc.; overriding to null strips a variable
    /// (e.g. ANTHROPIC_API_KEY, so Claude Code uses subscription auth).
    ///
    /// NOTE: Porta.Pty inherits the parent (this) process's environment and does not honor a
    /// removal via its options dict, so to actually strip a variable we ALSO unset it in this
    /// process. That's a process-global side effect, but this app never reads the stripped keys
    /// (only ANTHROPIC_API_KEY today), so it's safe.</summary>
    private static IDictionary<string, string> BuildEnvironment(IReadOnlyDictionary<string, string?>? overrides)
    {
        if (overrides is not null)
            foreach (var (key, value) in overrides)
                if (value is null)
                    Environment.SetEnvironmentVariable(key, null); // drop from this process so children don't inherit it

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var key = e.Key as string;
            if (string.IsNullOrEmpty(key)) continue;
            env[key] = e.Value as string ?? "";
        }
        if (overrides is not null)
            foreach (var (key, value) in overrides)
            {
                if (value is null) env.Remove(key);
                else env[key] = value;
            }
        return env;
    }
}
