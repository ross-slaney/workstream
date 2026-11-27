namespace Workstream.Api.Domain;

/// <summary>What kind of storage a team keeps. Each is a line of its usage, and of what storage costs.</summary>
public static class StorageKind
{
    /// <summary>The disk of a task's desktop, running or not, counted at the disk's full size.</summary>
    public const string Disk = "disk";
    /// <summary>What is kept of an archived task, counted at what is on it.</summary>
    public const string Archive = "archive";
    /// <summary>An environment's saved setup (<see cref="EnvironmentSetup"/>), counted at what is on it.</summary>
    public const string Setup = "setup";

    /// <summary>The order they are shown in.</summary>
    public static readonly IReadOnlyList<string> All = [Disk, Archive, Setup];
}

/// <summary>
/// One thing a team keeps now, as the tasks and the environments say it: its kind, the task or environment it
/// belongs to, and how many gigabytes it is counted at.
/// </summary>
public sealed record KeptStorage(string TeamId, string Kind, string SourceId, decimal Gb)
{
    /// <summary>
    /// What a task keeps. Its desktop's disk while it has a machine (<see cref="WorkTask.Host"/>), running or
    /// not, or is paused on its disk alone (<see cref="WorkTask.Disk"/>), at the size the disk was made with;
    /// and its archive, at what is on it.
    /// </summary>
    public static IEnumerable<KeptStorage> Of(WorkTask task, int diskGb)
    {
        if ((task.Host ?? task.Disk) is not null) yield return new KeptStorage(task.TeamId, StorageKind.Disk, task.Id, diskGb);
        if (task.Archive is not null) yield return new KeptStorage(task.TeamId, StorageKind.Archive, task.Id, StorageRun.GbOf(task.ArchiveBytes ?? 0));
    }

    /// <summary>What an environment's setup keeps: its save, at what is on it.</summary>
    public static IEnumerable<KeptStorage> Of(EnvironmentSetup setup)
    {
        if (setup.Save is not null) yield return new KeptStorage(setup.TeamId, StorageKind.Setup, setup.EnvironmentId, StorageRun.GbOf(setup.SaveBytes));
    }
}

/// <summary>
/// One stretch of a team keeping something at one size: a desktop's disk, an archived task, an environment's
/// saved setup. Like <see cref="DesktopRun"/> for machines' hours, these are what storage is added up from, in
/// the unit a bill would use: gigabytes times the hours kept, over <see cref="HoursInMonth"/>, is GB-months.
///
/// Nothing writes one when something happens. A job compares what every team keeps now with the rows still
/// open and makes them agree (<see cref="Reconcile"/>), so a row is as exact as the job is frequent, and a
/// missed run is put right by the next.
/// </summary>
public sealed class StorageRun
{
    /// <summary>The hours in a month, for GB-months: 365 days over 12, as cloud storage is usually billed.</summary>
    public const double HoursInMonth = 730;
    const decimal BytesInGb = 1_073_741_824m;

    /// <summary>For EF Core.</summary>
    StorageRun()
    {
    }

    public string Id { get; private set; } = "";
    public string TeamId { get; private set; } = "";
    /// <summary>One of <see cref="StorageKind"/>.</summary>
    public string Kind { get; private set; } = "";
    /// <summary>The task whose disk or archive it is, or the environment whose saved setup it is.</summary>
    public string SourceId { get; private set; } = "";
    public decimal Gb { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }

    public static StorageRun Begin(KeptStorage kept, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid().ToString("D"), TeamId = kept.TeamId, Kind = kept.Kind, SourceId = kept.SourceId, Gb = kept.Gb, StartedAt = now,
    };

    public void End(DateTimeOffset now) => EndedAt ??= now;

    /// <summary>Bytes as gigabytes, to a thousandth, so a size that has not changed reads the same each time.</summary>
    public static decimal GbOf(long bytes) => Math.Round(Math.Max(0, bytes) / BytesInGb, 3);

    /// <summary>
    /// GB-months inside a window: each stretch's gigabytes times its hours inside the window, over the hours
    /// in a month. A stretch still open counts up to the window's end.
    /// </summary>
    public static double GbMonths(IEnumerable<(decimal Gb, DateTimeOffset StartedAt, DateTimeOffset? EndedAt)> runs, DateTimeOffset from, DateTimeOffset to) =>
        runs.Sum(run =>
        {
            var start = run.StartedAt > from ? run.StartedAt : from;
            var end = run.EndedAt is { } ended && ended < to ? ended : to;
            return end > start ? (double)run.Gb * (end - start).TotalHours : 0;
        }) / HoursInMonth;

    /// <summary>
    /// Make the open rows agree with what is kept now. Something kept with no open row gets one; an open row
    /// whose thing is gone is ended; one whose size changed is ended and a new one begins at the same moment,
    /// so no time is counted twice or lost. Answers the rows to add; the ended ones are among
    /// <paramref name="open"/>. Run again with nothing changed, it does nothing.
    /// </summary>
    public static IReadOnlyList<StorageRun> Reconcile(IEnumerable<StorageRun> open, IEnumerable<KeptStorage> kept, DateTimeOffset now)
    {
        var keptNow = new Dictionary<(string Kind, string SourceId), KeptStorage>();
        foreach (var thing in kept) keptNow[(thing.Kind, thing.SourceId)] = thing;

        var counted = new HashSet<(string Kind, string SourceId)>();
        foreach (var run in open.Where(x => x.EndedAt is null).OrderBy(x => x.StartedAt))
        {
            var key = (run.Kind, run.SourceId);
            // One open row for each thing: the same size as now, and the first of them if there were ever two.
            if (keptNow.TryGetValue(key, out var thing) && thing.Gb == run.Gb && thing.TeamId == run.TeamId && counted.Add(key)) continue;
            run.End(now);
        }
        return [.. keptNow.Where(x => !counted.Contains(x.Key)).Select(x => Begin(x.Value, now))];
    }
}
