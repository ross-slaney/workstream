using Microsoft.EntityFrameworkCore;
using SqlOS.Fga.Extensions;
using Workstream.Api.Desktops;
using Workstream.Api.Domain;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api;

/// <summary>What a reconcile of the storage rows changed: how many it began and how many it ended.</summary>
public sealed record StorageReconciled(int Began, int Ended);

public static partial class Operations
{
    const int StoragePageSize = 500;

    /// <summary>
    /// Make the storage rows agree with what every team keeps now (<see cref="StorageRun.Reconcile"/>): a disk
    /// for each task that has a machine, at its size's disk; an archive for each archived task and a saved
    /// setup for each environment that has one, at what is on them. The desktop operator does it, as it does
    /// everything else to the desktops: the tasks are its page with <c>TASK_OPERATE_DESKTOP</c>, so a deleted
    /// task, whose node is gone, keeps nothing. Saved setups are the operator's own stock, read as its sweep
    /// reads them.
    /// </summary>
    public static async Task<StorageReconciled> ReconcileStorageAsync(WorkstreamDbContext db, DesktopOptions options, DateTimeOffset now, CancellationToken ct)
    {
        var kept = new List<KeptStorage>();
        string? cursor = null;
        do
        {
            var page = await db.Tasks.AsNoTracking().Where(x => x.Host != null || x.Archive != null).OrderBy(x => x.Id)
                .ToPageAsync(PlatformSeed.OperatorSubjectId, Permissions.TaskOperateDesktop, StoragePageSize, cursor, ct);
            kept.AddRange(page.Data.SelectMany(task => KeptStorage.Of(task, options.DiskGbOf(task.Size))));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        kept.AddRange((await db.Setups.AsNoTracking().Where(x => x.Save != null).ToListAsync(ct)).SelectMany(KeptStorage.Of));

        var open = await db.StorageRuns.Where(x => x.EndedAt == null).ToListAsync(ct);
        var began = StorageRun.Reconcile(open, kept, now);
        db.StorageRuns.AddRange(began);
        var ended = open.Count(x => x.EndedAt is not null);
        if (began.Count > 0 || ended > 0) await db.SaveChangesAsync(ct);
        return new StorageReconciled(began.Count, ended);
    }
}
