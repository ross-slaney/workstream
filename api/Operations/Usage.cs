using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Models;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using Workstream.Api.Desktops;
using Workstream.Api.Domain;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api;

/// <summary>The hours a team's desktops of one size ran this month. Each size is a line of its own, because each has its own rate.</summary>
public sealed record SizeHours(string Size, double Hours);

/// <summary>
/// The storage of one kind (<see cref="StorageKind"/>) a team kept this month: the gigabytes kept now, and the
/// month so far in GB-months, which is gigabytes times the share of a month they were kept.
/// </summary>
public sealed record StorageUse(string Kind, decimal Gb, double GbMonths);

/// <summary>
/// What a team used this calendar month (UTC), beside what it may use, in the units a bill would use: dollars
/// of models against the allowance, desktop hours by size, and storage in GB-months by kind. A size or a kind
/// the team did not use this month is not listed.
/// </summary>
public sealed record TeamUsage(
    DateTimeOffset Since, decimal ModelSpentUsd, decimal ModelAllowanceUsd,
    IReadOnlyList<SizeHours> DesktopHours, IReadOnlyList<StorageUse> Storage, int RunningDesktops, int MaxRunningDesktops);

/// <summary>How many desktops were running at a moment.</summary>
public sealed record RunningPoint(DateTimeOffset At, int Running);

public sealed record RunningDesktop(string TaskId, string Title, string TeamId, string TeamName, string Size, string State, bool AgentWorking, DateTimeOffset Since);

public sealed record TeamNumbers(string Id, string Name, int People, int Tasks, int RunningDesktops, int MaxRunningDesktops, decimal ModelSpentUsd, decimal ModelAllowanceUsd, double DesktopHours);

/// <summary>
/// What machines of one size cost this month for one purpose (a task's desktop, a spare, a machine preparing an
/// environment): the hours they ran, and those hours at the size's price.
/// </summary>
public sealed record MachineCost(string Purpose, string Size, double Hours, decimal Usd);

/// <summary>
/// What storage of one kind (<see cref="StorageKind"/>) cost this month over every team, from the recorded
/// rows: how many are kept now and their gigabytes, the month so far in GB-months, and those at the kind's price.
/// </summary>
public sealed record StorageCost(string Kind, int Kept, decimal Gb, double GbMonths, decimal Usd);

/// <summary>A spare that is running now.</summary>
public sealed record SpareNow(string TeamId, string TeamName, string Size, string State, DateTimeOffset Since);

/// <summary>
/// How long desktops took to start over the last seven days, from one source (a new machine from the image, a
/// spare, the task's own stopped machine): how many started, and the middle one's seconds until there was a
/// screen to watch and until the agent had joined.
/// </summary>
public sealed record StartTimes(string Source, int Starts, double? ScreenSeconds, double? AgentSeconds);

/// <summary>
/// What the desktops cost Workstream this month: the machines' time by purpose and size, the storage kept by
/// kind, and the spares running now.
/// </summary>
public sealed record MachineCosts(IReadOnlyList<MachineCost> ThisMonth, IReadOnlyList<StorageCost> Storage, IReadOnlyList<SpareNow> Spares);

/// <summary>The system administrator's page: what is running now, the last day's curve, each team's month, what the desktops cost and how long they take to start.</summary>
public sealed record AdminOverview(
    int RunningDesktops, int Teams, int People, int Tasks, decimal ModelSpentUsd, double DesktopHours,
    IReadOnlyList<RunningPoint> Running, IReadOnlyList<RunningDesktop> Desktops, IReadOnlyList<TeamNumbers> TeamNumbers,
    MachineCosts Machines, IReadOnlyList<StartTimes> Starts);

public static partial class Operations
{
    /// <summary>The start of the calendar month a team's allowance is counted in.</summary>
    public static DateTimeOffset MonthStart(DateTimeOffset now) => new(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>What the team's agents have spent on models since <paramref name="since"/>.</summary>
    public static async Task<decimal> ModelSpentAsync(WorkstreamDbContext db, string teamId, DateTimeOffset since, CancellationToken ct) =>
        await db.ModelUsage.Where(x => x.TeamId == teamId && x.At >= since).SumAsync(x => (decimal?)x.CostUsd, ct) ?? 0m;

    /// <summary>Hours of desktop time inside a window, from the runs that touch it.</summary>
    static double HoursOf(IEnumerable<(DateTimeOffset StartedAt, DateTimeOffset? EndedAt)> runs, DateTimeOffset from, DateTimeOffset to) =>
        runs.Sum(run =>
        {
            var start = run.StartedAt > from ? run.StartedAt : from;
            var end = run.EndedAt is { } ended && ended < to ? ended : to;
            return end > start ? (end - start).TotalHours : 0;
        });

    public static async Task<Outcome<TeamUsage>> UsageAsync(Caller caller, string teamId, WorkstreamDbContext db, ISqlOSFgaAuthService fga, TimeProvider clock, CancellationToken ct)
    {
        var allowed = await TeamWithAsync(caller, teamId, Permissions.TeamViewUsage, "You can't see this team's usage.", db, fga, ct);
        if (!allowed.IsOk) return allowed.As<TeamUsage>();
        var team = allowed.Value!;
        var now = clock.GetUtcNow();
        var since = MonthStart(now);
        // The team's own use: its tasks' desktops. A spare kept for the team is Workstream's cost, not the team's.
        var runs = await db.DesktopRuns.AsNoTracking().Where(x => x.TeamId == teamId && x.Purpose == MachinePurpose.Task && (x.EndedAt == null || x.EndedAt >= since))
            .Select(x => new { x.Size, x.StartedAt, x.EndedAt }).ToListAsync(ct);
        var hours = runs.GroupBy(x => x.Size).OrderBy(x => SizeOrder(x.Key)).ThenBy(x => x.Key)
            .Select(size => new SizeHours(size.Key, Math.Round(HoursOf(size.Select(x => (x.StartedAt, x.EndedAt)), since, now), 2))).ToList();
        var kept = await db.StorageRuns.AsNoTracking().Where(x => x.TeamId == teamId && (x.EndedAt == null || x.EndedAt >= since))
            .Select(x => new { x.Kind, x.Gb, x.StartedAt, x.EndedAt }).ToListAsync(ct);
        var storage = kept.GroupBy(x => x.Kind).OrderBy(x => KindOrder(x.Key)).ThenBy(x => x.Key)
            .Select(kind => new StorageUse(
                kind.Key, kind.Where(x => x.EndedAt == null).Sum(x => x.Gb),
                Math.Round(StorageRun.GbMonths(kind.Select(x => (x.Gb, x.StartedAt, x.EndedAt)), since, now), 3))).ToList();
        return Outcome<TeamUsage>.Ok(new TeamUsage(
            since, await ModelSpentAsync(db, teamId, since, ct), team.ModelAllowanceUsd, hours, storage,
            await RunningDesktopsAsync(db, teamId, ct), team.MaxRunningDesktops));
    }

    /// <summary>Sizes from the smallest up, and kinds in the order the product lists them; anything unknown goes last.</summary>
    static int SizeOrder(string size) => PlaceIn(DesktopSizes.Keys, size);

    static int KindOrder(string kind) => PlaceIn(StorageKind.All, kind);

    static int PlaceIn(IReadOnlyList<string> order, string key)
    {
        for (var at = 0; at < order.Count; at++)
        {
            if (order[at] == key) return at;
        }
        return int.MaxValue;
    }

    /// <summary>
    /// Everything the system administrator's page shows. One check, on root; the numbers are sums over every
    /// team, which SqlOS has no authorized form of.
    /// </summary>
    public static async Task<Outcome<AdminOverview>> AdminOverviewAsync(Caller caller, WorkstreamDbContext db, ISqlOSFgaAuthService fga, DesktopOptions desktops, TimeProvider clock, CancellationToken ct)
    {
        if (!await fga.HasCapabilityAsync(caller.PrincipalId, Permissions.AdminView)) return Outcome<AdminOverview>.Forbidden("This page is for system administrators.");
        var now = clock.GetUtcNow();
        var since = MonthStart(now);
        var dayAgo = now.AddHours(-24);
        var windowStart = dayAgo < since ? dayAgo : since;

        var everyRun = await db.DesktopRuns.AsNoTracking().Where(x => x.EndedAt == null || x.EndedAt >= windowStart)
            .Select(x => new { x.TeamId, x.Purpose, x.Size, x.StartedAt, x.EndedAt }).ToListAsync(ct);
        // Desktops are tasks' desktops: the curve, the hours and each team's time count those.
        var runs = everyRun.Where(x => x.Purpose == MachinePurpose.Task).ToList();
        // The last day in half-hour steps: how many runs were open at each.
        var points = Enumerable.Range(0, 49).Select(i => dayAgo.AddMinutes(30 * i))
            .Select(at => new RunningPoint(at, runs.Count(run => run.StartedAt <= at && (run.EndedAt == null || run.EndedAt > at)))).ToList();

        var running = await db.Tasks.AsNoTracking()
            .Where(x => x.State == DesktopState.Starting || x.State == DesktopState.Ready || x.State == DesktopState.Stopping)
            .OrderBy(x => x.StateChangedAt).Take(500).ToListAsync(ct);
        var teams = await db.Teams.AsNoTracking().OrderBy(x => x.CreatedAt).Take(500).ToListAsync(ct);
        var names = await TeamNamesAsync(db, teams.Select(x => x.Id), ct);
        var people = await db.Set<SqlOSMembership>().AsNoTracking().Where(x => x.IsActive).GroupBy(x => x.OrganizationId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.TeamId, x => x.Count, ct);
        var tasks = await db.Tasks.AsNoTracking().Where(x => x.State != DesktopState.Deleting).GroupBy(x => x.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.TeamId, x => x.Count, ct);
        var spent = await db.ModelUsage.AsNoTracking().Where(x => x.At >= since).GroupBy(x => x.TeamId)
            .Select(g => new { TeamId = g.Key, Cost = g.Sum(x => x.CostUsd) }).ToDictionaryAsync(x => x.TeamId, x => x.Cost, ct);

        var numbers = teams.Select(team => new TeamNumbers(
            team.Id, names.GetValueOrDefault(team.Id, "Team"), people.GetValueOrDefault(team.Id), tasks.GetValueOrDefault(team.Id),
            running.Count(x => x.TeamId == team.Id), team.MaxRunningDesktops, spent.GetValueOrDefault(team.Id), team.ModelAllowanceUsd,
            Math.Round(HoursOf(runs.Where(x => x.TeamId == team.Id).Select(x => (x.StartedAt, x.EndedAt)), since, now), 2))).ToList();

        // What the machines cost: every run's hours this month, by what it ran for and at which size, at the size's hourly price.
        var costs = everyRun.GroupBy(x => (x.Purpose, x.Size))
            .Select(line => (line.Key.Purpose, line.Key.Size, Hours: HoursOf(line.Select(x => (x.StartedAt, x.EndedAt)), since, now)))
            .Select(line => new MachineCost(line.Purpose, line.Size, Math.Round(line.Hours, 2), Math.Round((decimal)line.Hours * desktops.Prices.HourOf(line.Size), 2)))
            .OrderBy(x => x.Purpose == MachinePurpose.Task ? 0 : 1).ThenBy(x => x.Purpose).ThenBy(x => SizeOrder(x.Size)).ThenBy(x => x.Size).ToList();
        // What storage cost: the rows the job keeps of what every team keeps, in GB-months, at each kind's price.
        var kept = await db.StorageRuns.AsNoTracking().Where(x => x.EndedAt == null || x.EndedAt >= since)
            .Select(x => new { x.Kind, x.Gb, x.StartedAt, x.EndedAt }).ToListAsync(ct);
        var storage = kept.GroupBy(x => x.Kind).OrderBy(x => KindOrder(x.Key)).ThenBy(x => x.Key).Select(kind =>
        {
            var gbMonths = StorageRun.GbMonths(kind.Select(x => (x.Gb, x.StartedAt, x.EndedAt)), since, now);
            var open = kind.Where(x => x.EndedAt == null).ToList();
            return new StorageCost(kind.Key, open.Count, open.Sum(x => x.Gb), Math.Round(gbMonths, 3), Math.Round((decimal)gbMonths * desktops.Prices.GbMonthOf(kind.Key), 2));
        }).ToList();
        var spares = await db.Spares.AsNoTracking().OrderBy(x => x.CreatedAt).ToListAsync(ct);

        // How long a start takes, from what the runs wrote down as it happened.
        var weekAgo = now.AddDays(-7);
        var started = await db.DesktopRuns.AsNoTracking().Where(x => x.Purpose == MachinePurpose.Task && x.StartedAt >= weekAgo)
            .Select(x => new { x.Source, x.StartedAt, x.DisplayAt, x.ReadyAt }).ToListAsync(ct);
        static double? Middle(IEnumerable<double> seconds)
        {
            var sorted = seconds.OrderBy(x => x).ToList();
            return sorted.Count == 0 ? null : Math.Round(sorted[(sorted.Count - 1) / 2], 1);
        }
        var starts = started.GroupBy(x => x.Source).Select(source => new StartTimes(
                source.Key, source.Count(),
                Middle(source.Where(x => x.DisplayAt is not null).Select(x => (x.DisplayAt!.Value - x.StartedAt).TotalSeconds)),
                Middle(source.Where(x => x.ReadyAt is not null).Select(x => (x.ReadyAt!.Value - x.StartedAt).TotalSeconds))))
            .OrderBy(x => x.Source).ToList();

        return Outcome<AdminOverview>.Ok(new AdminOverview(
            running.Count, teams.Count, await db.Set<SqlOSUser>().CountAsync(x => x.IsActive, ct), tasks.Values.Sum(),
            spent.Values.Sum(), Math.Round(HoursOf(runs.Select(x => (x.StartedAt, x.EndedAt)), since, now), 2),
            points,
            [.. running.Select(x => new RunningDesktop(x.Id, x.Title, x.TeamId, names.GetValueOrDefault(x.TeamId, "Team"), x.Size, x.State.ToString().ToLowerInvariant(), x.AgentWorking, x.StateChangedAt))],
            numbers,
            new MachineCosts(costs, storage,
                [.. spares.Select(x => new SpareNow(x.TeamId, names.GetValueOrDefault(x.TeamId, "Team"), x.Size, x.State.ToString().ToLowerInvariant(), x.CreatedAt))]),
            starts));
    }

    /// <summary>A system administrator changes what a team may use: its model allowance and its desktops at once.</summary>
    public static async Task<Outcome<Team>> SetTeamLimitsAsync(Caller caller, string teamId, decimal modelAllowanceUsd, int maxRunningDesktops, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        if (!await fga.HasCapabilityAsync(caller.PrincipalId, Permissions.AdminView)) return Outcome<Team>.Forbidden("Only a system administrator can change a team's limits.");
        var team = await db.Teams.SingleOrDefaultAsync(x => x.Id == teamId, ct);
        if (team is null) return Outcome<Team>.NotFound();
        try
        {
            team.SetLimits(modelAllowanceUsd, maxRunningDesktops);
            await db.SaveChangesAsync(ct);
            return Outcome<Team>.Ok(team);
        }
        catch (DomainRuleException rule)
        {
            return Outcome<Team>.Invalid(rule);
        }
    }
}
