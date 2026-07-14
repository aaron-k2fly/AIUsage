using System.Text.Json;
using Photino.NET;

namespace AIUsage.Bridge;

/// <summary>
/// JSON request/response bus between the WebView and .NET.
/// Request:  { id, action, payload }
/// Response: { id, ok, data | error }
/// </summary>
public sealed class MessageRouter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly PhotinoWindow _window;
    private readonly Dictionary<string, Func<JsonElement, Task<object?>>> _handlers = new();

    public MessageRouter(PhotinoWindow window)
    {
        _window = window;
        Register("ping", _ => Task.FromResult<object?>(new { pong = true, dbPath = Data.Db.DbPath }));
    }

    public void Register(string action, Func<JsonElement, Task<object?>> handler) =>
        _handlers[action] = handler;

    public void OnMessage(object? sender, string message)
    {
        _ = Task.Run(async () =>
        {
            string? id = null;
            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;
                id = root.GetProperty("id").GetString();
                var action = root.GetProperty("action").GetString() ?? "";
                var payload = root.TryGetProperty("payload", out var p) ? p.Clone() : default;

                if (!_handlers.TryGetValue(action, out var handler))
                {
                    Send(new { id, ok = false, error = $"Unknown action '{action}'" });
                    return;
                }

                var data = await handler(payload);
                Send(new { id, ok = true, data });
            }
            catch (Exception ex)
            {
                Send(new { id, ok = false, error = ex.Message });
            }
        });
    }

    /// <summary>
    /// Push an unsolicited event to the WebView (no request id) — used for streaming channels
    /// like live terminal output. The frontend routes `{ type:"event", event, data }` through
    /// Bridge.on(event, handler). Safe to call from any thread (SendWebMessage marshals).
    /// </summary>
    public void PushEvent(string @event, object? data) =>
        Send(new { type = "event", @event, data });

    private void Send(object reply)
    {
        var json = JsonSerializer.Serialize(reply, JsonOpts);
        _window.SendWebMessage(json); // SendWebMessage marshals to the UI thread internally
    }
}
