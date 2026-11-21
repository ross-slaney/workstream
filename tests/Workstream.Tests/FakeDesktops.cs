using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Workstream.Api.Desktops;

namespace Workstream.Tests;

/// <summary>
/// The desktops, played: it makes no machine, and remembers what it was asked. A journey reads the key a
/// desktop was made with here, the way the machine reads it from its user data, and then speaks as that
/// desktop (<see cref="Desktop"/>). A machine is on and its display answers as soon as it is asked for, unless
/// a journey holds one of them back to see a start step by step.
/// </summary>
public sealed class FakeDesktops : IDesktopHost
{
    public ConcurrentDictionary<string, DesktopSpec> Made { get; } = new();
    public ConcurrentDictionary<string, bool> Running { get; } = new();
    public ConcurrentBag<string> Deleted { get; } = [];
    /// <summary>Desktops (by the task's id, or the spare's) whose machine cannot be made: the host refuses, as Azure does out of quota.</summary>
    public ConcurrentDictionary<string, bool> Refuse { get; } = new();
    /// <summary>Machines the host has taken the request for and has not turned on yet.</summary>
    public ConcurrentDictionary<string, bool> NotOn { get; } = new();
    /// <summary>Machines whose display does not answer yet.</summary>
    public ConcurrentDictionary<string, bool> NoDisplay { get; } = new();
    /// <summary>Machines the host took the request for and then could not start, with its words for why.</summary>
    public ConcurrentDictionary<string, string> Broken { get; } = new();
    /// <summary>Where every played machine's display is: one listener that greets as a display does.</summary>
    public PlayedDisplay Display { get; } = new();

    public Task<DesktopPlace> CreateAsync(DesktopSpec spec, CancellationToken ct)
    {
        if (Refuse.ContainsKey(spec.DesktopId)) throw new InvalidOperationException("No capacity.");
        Made[spec.Name] = spec;
        Running[spec.Name] = true;
        return Task.FromResult(new DesktopPlace(spec.Name, Display.Address));
    }

    public Task<DesktopPlace> StartAsync(string host, CancellationToken ct)
    {
        Running[host] = true;
        return Task.FromResult(new DesktopPlace(host, Display.Address));
    }

    public Task<MachineState> StateAsync(string host, CancellationToken ct) =>
        Task.FromResult(Broken.TryGetValue(host, out var why) ? new MachineState(false, why) : new MachineState(!NotOn.ContainsKey(host)));

    public Task<bool> DisplayAnswersAsync(DesktopPlace place, CancellationToken ct) =>
        Task.FromResult(!NoDisplay.ContainsKey(place.Host) && !NotOn.ContainsKey(place.Host) && !Broken.ContainsKey(place.Host));

    public Task StopAsync(string host, CancellationToken ct)
    {
        Running[host] = false;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string host, CancellationToken ct)
    {
        Running.TryRemove(host, out _);
        Made.TryRemove(host, out _);
        Deleted.Add(host);
        return Task.CompletedTask;
    }

    /// <summary>When each machine was made. A journey makes one old to play a machine left behind long ago.</summary>
    public ConcurrentDictionary<string, DateTimeOffset> MadeAt { get; } = new();

    public Task<IReadOnlyList<HostedMachine>> ListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<HostedMachine>>(Running.Keys.Select(name => new HostedMachine(name, MadeAt.GetValueOrDefault(name, DateTimeOffset.UtcNow))).ToList());

    /// <summary>The saved setups the host holds, by name, with the machine each was saved from and when.</summary>
    public ConcurrentDictionary<string, (string Machine, DateTimeOffset At)> Saves { get; } = new();

    public Task SaveAsync(string host, string name, CancellationToken ct)
    {
        if (Running.GetValueOrDefault(host)) throw new InvalidOperationException("Only a stopped machine's disk can be saved.");
        Saves[name] = (host, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    public Task DeleteSaveAsync(string name, CancellationToken ct)
    {
        Saves.TryRemove(name, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<HostedMachine>> ListSavesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<HostedMachine>>(Saves.Select(save => new HostedMachine(save.Key, save.Value.At)).ToList());

    /// <summary>What the machine for this desktop (a task's id, or a spare's) was made with.</summary>
    public DesktopSpec For(string desktopId) => Made.Values.Single(spec => spec.DesktopId == desktopId);
}

/// <summary>
/// A display, played: it takes whoever connects and greets them the way a remote-desktop server does, which
/// is all a journey needs to know that bytes came through the relay from a desktop.
/// </summary>
public sealed class PlayedDisplay : IDisposable
{
    public const string Greeting = "RFB 003.008\n";
    readonly TcpListener listener = new(IPAddress.Loopback, 0);

    public string Address { get; }

    public PlayedDisplay()
    {
        listener.Start();
        Address = $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        _ = AcceptAsync();
    }

    async Task AcceptAsync()
    {
        try
        {
            while (true) _ = GreetAsync(await listener.AcceptTcpClientAsync());
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException)
        {
            // Stopped.
        }
    }

    static async Task GreetAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(Greeting));
                // Held open until the other side goes, as a display holds its viewers.
                while (await stream.ReadAsync(new byte[256]) > 0) { }
            }
            catch (Exception error) when (error is IOException or SocketException)
            {
                // The viewer left.
            }
        }
    }

    public void Dispose() => listener.Stop();
}
