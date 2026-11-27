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

    /// <summary>
    /// Desktops (by the task's id) whose paused machine this host gives up, keeping its disk, as Azure does.
    /// Any other is kept as a stopped machine, as Docker keeps one.
    /// </summary>
    public ConcurrentDictionary<string, bool> GivesUp { get; } = new();
    /// <summary>The disks kept for machines that were given up, by the machine's name.</summary>
    public ConcurrentDictionary<string, DesktopSpec> Disks { get; } = new();

    public Task<bool> ReleaseAsync(string host, CancellationToken ct)
    {
        // Gone already: the disk is the desktop if it is still there.
        if (!Made.TryGetValue(host, out var spec)) return Task.FromResult(Disks.ContainsKey(host));
        if (!GivesUp.ContainsKey(spec.DesktopId)) return Task.FromResult(false);
        Running.TryRemove(host, out _);
        Made.TryRemove(host, out _);
        Disks[host] = spec;
        return Task.FromResult(true);
    }

    public Task<DesktopPlace> CreateAsync(DesktopSpec spec, CancellationToken ct)
    {
        if (Refuse.ContainsKey(spec.DesktopId)) throw new InvalidOperationException("No capacity.");
        // A machine takes a kept disk only while the disk is there, and a kept disk is taken as it is.
        if (spec.Kept && !Disks.TryRemove(spec.Name, out _)) throw new InvalidOperationException($"The disk of {spec.Name} is not there.");
        // As Azure: a disk is made from a saved setup only while the setup is there.
        if (spec.Save is not null && !Saves.ContainsKey(spec.Save)) throw new InvalidOperationException($"The saved setup {spec.Save} is not there.");
        // The same for an archive: a machine comes back only from one the host still holds.
        if (spec.Archive is not null && !Archives.ContainsKey(spec.Archive)) throw new InvalidOperationException($"The archive {spec.Archive} is not there.");
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
        // A machine's disk goes with it, and so does the disk of one that was given up.
        Disks.TryRemove(host, out _);
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

    /// <summary>The archives the host holds, by name, with the machine each was copied from and when.</summary>
    public ConcurrentDictionary<string, (string Machine, DateTimeOffset At)> Archives { get; } = new();
    /// <summary>Machines whose disk cannot be copied to an archive: the host refuses, as Azure does on a bad hour.</summary>
    public ConcurrentDictionary<string, bool> RefuseArchive { get; } = new();
    /// <summary>What the played host says every archive holds, in bytes.</summary>
    public const long ArchiveBytes = 64L * 1024 * 1024 * 1024;

    public Task<long?> ArchiveAsync(string host, string name, CancellationToken ct)
    {
        if (RefuseArchive.ContainsKey(host)) throw new InvalidOperationException("The disk could not be copied.");
        if (!Made.ContainsKey(host) && !Disks.ContainsKey(host)) throw new InvalidOperationException($"The machine {host} is gone.");
        if (Running.GetValueOrDefault(host)) throw new InvalidOperationException("Only a stopped machine's disk can be archived.");
        Archives[name] = (host, DateTimeOffset.UtcNow);
        return Task.FromResult<long?>(ArchiveBytes);
    }

    public Task DeleteArchiveAsync(string name, CancellationToken ct)
    {
        Archives.TryRemove(name, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<HostedMachine>> ListArchivesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<HostedMachine>>(Archives.Select(archive => new HostedMachine(archive.Key, archive.Value.At)).ToList());

    /// <summary>What the machine for this desktop (a task's id, or a spare's) was made with.</summary>
    public DesktopSpec For(string desktopId) => Made.Values.Single(spec => spec.DesktopId == desktopId);

    /// <summary>The machine preparing an environment now: made for its preparing account, and not deleted yet.</summary>
    public DesktopSpec? Preparing(string environmentId) =>
        Made.Values.SingleOrDefault(spec => spec.DesktopId == Workstream.Api.Domain.EnvironmentSetup.DesktopIdFor(environmentId));
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

    /// <summary>What each viewer sent, connection by connection, so a journey can see what the relay let through.</summary>
    public ConcurrentBag<Viewer> Viewers { get; } = [];

    async Task GreetAsync(TcpClient client)
    {
        var viewer = new Viewer();
        Viewers.Add(viewer);
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(Greeting));
                // Held open until the other side goes, as a display holds its viewers.
                var buffer = new byte[4096];
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0) viewer.Add(buffer.AsSpan(0, read));
            }
            catch (Exception error) when (error is IOException or SocketException)
            {
                // The viewer left.
            }
        }
    }

    public void Dispose() => listener.Stop();
}

/// <summary>The bytes one viewer's connection brought the played display.</summary>
public sealed class Viewer
{
    readonly List<byte> received = [];

    public void Add(ReadOnlySpan<byte> bytes)
    {
        lock (received) received.AddRange(bytes.ToArray());
    }

    public byte[] Received
    {
        get
        {
            lock (received) return [.. received];
        }
    }
}
