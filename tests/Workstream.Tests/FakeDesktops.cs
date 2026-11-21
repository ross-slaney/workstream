using System.Collections.Concurrent;
using Workstream.Api.Desktops;

namespace Workstream.Tests;

/// <summary>
/// The desktops, played: it makes no machine, and remembers what it was asked. A journey reads the key a
/// desktop was made with here, the way the machine reads it from its first boot, and then speaks as that
/// desktop (<see cref="Desktop"/>).
/// </summary>
public sealed class FakeDesktops : IDesktopHost
{
    public ConcurrentDictionary<string, DesktopSpec> Made { get; } = new();
    public ConcurrentDictionary<string, bool> Running { get; } = new();
    public ConcurrentBag<string> Deleted { get; } = [];
    /// <summary>Task ids whose machine cannot be made: the host refuses, as Azure does out of quota.</summary>
    public ConcurrentDictionary<string, bool> Refuse { get; } = new();

    public Task<DesktopPlace> CreateAsync(DesktopSpec spec, CancellationToken ct)
    {
        if (Refuse.ContainsKey(spec.TaskId)) throw new InvalidOperationException("No capacity.");
        Made[spec.Name] = spec;
        Running[spec.Name] = true;
        return Task.FromResult(new DesktopPlace(spec.Name, "127.0.0.1:5900"));
    }

    public Task<DesktopPlace> StartAsync(string host, CancellationToken ct)
    {
        Running[host] = true;
        return Task.FromResult(new DesktopPlace(host, "127.0.0.1:5900"));
    }

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

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Running.Keys.ToList());

    /// <summary>What the desktop for this task was made with.</summary>
    public DesktopSpec For(string taskId) => Made.Values.Single(spec => spec.TaskId == taskId);
}
