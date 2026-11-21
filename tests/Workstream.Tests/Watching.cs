using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Workstream.Tests;

/// <summary>
/// Opening a desktop's display through the API's relay, the way the web app's remote-desktop client does: a
/// WebSocket whose requested subprotocols carry the person's token, from the web app's own origin.
/// </summary>
public static class Watching
{
    /// <summary>
    /// What the relay answered: the status it refused with, or 101 and the first bytes the display sent.
    /// </summary>
    public static async Task<(HttpStatusCode Status, string Greeting)> OpenAsync(string taskId, string? token, string origin = Stack.WebOrigin)
    {
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.AddSubProtocol("binary");
        if (token is not null) socket.Options.AddSubProtocol("bearer." + token);
        socket.Options.SetRequestHeader("Origin", origin);
        var url = new UriBuilder(Stack.Origin) { Scheme = "ws", Path = $"/api/tasks/{taskId}/desktop" }.Uri;
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await socket.ConnectAsync(url, patience.Token);
        }
        catch (WebSocketException)
        {
            return (socket.HttpStatusCode, "");
        }
        var buffer = new byte[64];
        var received = await socket.ReceiveAsync(buffer, patience.Token);
        return (HttpStatusCode.SwitchingProtocols, Encoding.ASCII.GetString(buffer, 0, received.Count));
    }

    /// <summary>
    /// Open the desktop as a viewer, send these bytes the way noVNC does, then a fence carrying a mark of this
    /// stream's own, and answer with what the display received on that connection up to the mark: what the
    /// relay let through, and nothing of any other journey's.
    /// </summary>
    public static async Task<byte[]> SendAsync(string taskId, string token, params byte[][] messages)
    {
        var mark = Guid.NewGuid().ToByteArray();
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("binary");
        socket.Options.AddSubProtocol("bearer." + token);
        socket.Options.SetRequestHeader("Origin", Stack.WebOrigin);
        var url = new UriBuilder(Stack.Origin) { Scheme = "ws", Path = $"/api/tasks/{taskId}/desktop" }.Uri;
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await socket.ConnectAsync(url, patience.Token);
        await socket.ReceiveAsync(new byte[64], patience.Token);
        foreach (var message in messages.Append(Rfb.Fence(mark)))
        {
            await socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, patience.Token);
        }
        var fence = Rfb.Fence(mark);
        while (!patience.IsCancellationRequested)
        {
            foreach (var viewer in Stack.Desktops.Display.Viewers)
            {
                var received = viewer.Received;
                var at = received.AsSpan().IndexOf(fence);
                if (at >= 0) return received[..at];
            }
            await Task.Delay(50, patience.Token);
        }
        throw new TimeoutException("The display never received the stream's mark.");
    }
}
