using Hangfire;

namespace Workstream.Api.Desktops;

/// <summary>
/// Every fifteen minutes: write down what every team keeps (desktops' disks, archived tasks, saved setups), so
/// storage can be added up in GB-months (<see cref="Operations.ReconcileStorageAsync"/>). It runs as
/// <c>agent::desktop-operator</c>, like the jobs that make and remove what it counts. Nothing else writes these
/// rows: a disk made or removed between two runs is counted from the next one.
/// </summary>
public sealed class StorageJobs(IServiceScopeFactory scopes, DesktopOptions options, TimeProvider clock, ILogger<StorageJobs> log)
{
    public const string ReconcileId = "storage-reconcile";
    public const string ReconcileCron = "*/15 * * * *";

    [DisableConcurrentExecution(timeoutInSeconds: 240)]
    [AutomaticRetry(Attempts = 0)]
    public async Task ReconcileAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var changed = await Operations.ReconcileStorageAsync(scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>(), options, clock.GetUtcNow(), ct);
        if (changed is { Began: 0, Ended: 0 }) return;
        log.LogInformation("Storage kept: {Began} began, {Ended} ended.", changed.Began, changed.Ended);
    }
}
