using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace UiaProbe;

public static class Cdp
{
    public static async Task<string> EvalAsync(string webSocketUrl, string js, int timeoutSeconds = 20)
    {
        using var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        await ws.ConnectAsync(new Uri(webSocketUrl), cts.Token);

        // Enable Runtime then evaluate.
        await SendAsync(ws, "{\"id\":1,\"method\":\"Runtime.enable\"}", cts.Token);
        var msg = JsonSerializer.Serialize(new { id = 2, method = "Runtime.evaluate", @params = new { expression = js, returnByValue = true, awaitPromise = true } });
        await SendAsync(ws, msg, cts.Token);

        var buffer = new byte[4 * 1024 * 1024];
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, cts.Token);
            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var id) && id.GetInt32() == 2)
            {
                if (root.TryGetProperty("result", out var res) && res.TryGetProperty("result", out var val) && val.TryGetProperty("value", out var v))
                    return v.GetRawText();
                if (root.TryGetProperty("result", out var res2) && res2.TryGetProperty("exceptionDetails", out _))
                    return "EXCEPTION: " + text;
                return "NO-VALUE: " + text[..Math.Min(400, text.Length)];
            }
            // keep reading (ignore events)
        }
    }

    private static async Task SendAsync(ClientWebSocket ws, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }
}
