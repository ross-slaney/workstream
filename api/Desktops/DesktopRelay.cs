using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using Workstream.Api.Domain;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api.Desktops;

/// <summary>
/// The only way into a desktop: <c>GET /api/tasks/{id}/desktop</c> as a WebSocket. The browser's remote-desktop
/// client speaks to the desktop's display through it, and this relay copies bytes both ways without reading
/// them. What it does read is who is asking: a browser cannot put a bearer token on a WebSocket, so the token
/// rides in the requested subprotocols as <c>bearer.{token}</c> beside <c>binary</c>, and is validated the way
/// SqlOS validates it on any request. Then <c>TASK_USE</c> on the task, checked again every minute for as long
/// as the stream stays open.
/// </summary>
public static class DesktopRelay
{
    public const string Route = "/api/tasks/{id}/desktop";
    const string TokenProtocol = "bearer.";
    const string StreamProtocol = "binary";
    static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(60);

    public static async Task HandleAsync(HttpContext http, string id)
    {
        var services = http.RequestServices;
        var app = services.GetRequiredService<WorkstreamApp>();
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        // Only the web app's pages open this stream.
        if (!services.GetRequiredService<IHostEnvironment>().IsDevelopment()
            && !string.Equals(http.Request.Headers.Origin.ToString(), app.WebOrigin, StringComparison.OrdinalIgnoreCase))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var raw = http.WebSockets.WebSocketRequestedProtocols.FirstOrDefault(p => p.StartsWith(TokenProtocol, StringComparison.Ordinal))?[TokenProtocol.Length..];
        var token = raw is null ? null : await services.GetRequiredService<SqlOSAuthService>().ValidateAccessTokenAsync(raw, app.Resource, http.RequestAborted);
        if (token is not { UserId.Length: > 0, SessionId.Length: > 0 }
            || !(token.Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(WorkstreamApp.ReadScope))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var (status, address) = await AdmitAsync(scopes, token.UserId, token.SessionId, id, touch: true, http.RequestAborted);
        if (address is null)
        {
            http.Response.StatusCode = status;
            return;
        }

        using var desktop = new TcpClient { NoDelay = true };
        try
        {
            var separator = address.LastIndexOf(':');
            using var connecting = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            connecting.CancelAfter(TimeSpan.FromSeconds(8));
            await desktop.ConnectAsync(address[..separator], int.Parse(address[(separator + 1)..]), connecting.Token);
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException)
        {
            http.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using var browser = await http.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = StreamProtocol,
            // A still screen sends nothing; pings keep the connection from being dropped as idle on the way.
            KeepAliveInterval = TimeSpan.FromSeconds(20),
        });
        using var open = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        var display = desktop.GetStream();
        var first = await Task.WhenAny(
            ToDesktopAsync(browser, display, open.Token),
            ToBrowserAsync(display, browser, open.Token),
            WatchAsync(scopes, token.UserId, token.SessionId, id, open.Token));
        open.Cancel();
        if (browser.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, first.IsFaulted ? "The desktop closed the stream." : "Closed.", closing.Token);
            }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException)
            {
                // The browser is already gone.
            }
        }
    }

    /// <summary>
    /// Whether this person may have this desktop on screen now, and where it is. Their session must be live,
    /// they must hold <c>TASK_USE</c> on the task, and its display must be answering, which it is a few
    /// seconds before its agent first exchanges. Each check that passes also records that the desktop is in
    /// use, which is what keeps the sweep from stopping it, and that this person has it on screen.
    /// </summary>
    static async Task<(int Status, string? Address)> AdmitAsync(IServiceScopeFactory scopes, string userId, string sessionId, string taskId, bool touch, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
        var fga = scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>();
        var now = DateTime.UtcNow;
        if (!await db.Set<SqlOSSession>().AsNoTracking().AnyAsync(x => x.Id == sessionId && x.UserId == userId && x.RevokedAt == null && x.AbsoluteExpiresAt > now, ct))
        {
            return (StatusCodes.Status401Unauthorized, null);
        }
        var task = await db.Tasks.SingleOrDefaultAsync(x => x.Id == taskId, ct);
        if (task is null || task.State == DesktopState.Deleting || !await fga.Allows(userId, Permissions.TaskView, task.ResourceId)) return (StatusCodes.Status404NotFound, null);
        if (!await fga.Allows(userId, Permissions.TaskUse, task.ResourceId)) return (StatusCodes.Status403Forbidden, null);
        if (!task.CanBeWatched) return (StatusCodes.Status409Conflict, null);
        if (touch)
        {
            var at = DateTimeOffset.UtcNow;
            task.Active(at);
            await db.SaveChangesAsync(ct);
            // And that this person has it on screen, so they are not emailed about what they are watching.
            var watcher = await db.TaskWatchers.SingleOrDefaultAsync(x => x.TaskId == task.Id && x.UserId == userId, ct);
            if (watcher is null) db.TaskWatchers.Add(TaskWatcher.Of(task, userId, at));
            else watcher.Seen(at);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Their other stream, opened at the same moment, wrote it first: it says the same.
            }
        }
        return (StatusCodes.Status200OK, task.Address);
    }

    /// <summary>Ends, and so closes the stream, when the person may no longer watch.</summary>
    static async Task WatchAsync(IServiceScopeFactory scopes, string userId, string sessionId, string taskId, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(CheckEvery, ct);
            var (_, address) = await AdmitAsync(scopes, userId, sessionId, taskId, touch: true, ct);
            if (address is null) return;
        }
    }

    static async Task ToDesktopAsync(WebSocket browser, NetworkStream display, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var received = await browser.ReceiveAsync(buffer.AsMemory(), ct);
            if (received.MessageType == WebSocketMessageType.Close) return;
            await display.WriteAsync(buffer.AsMemory(0, received.Count), ct);
        }
    }

    static async Task ToBrowserAsync(NetworkStream display, WebSocket browser, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await display.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) return;
            await browser.SendAsync(buffer.AsMemory(0, read), WebSocketMessageType.Binary, endOfMessage: true, ct);
        }
    }
}
