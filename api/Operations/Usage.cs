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
    IReadOnlyList<SizeHours> DesktopHours, IReadOnlyList<StorageUse> Storage, int RunningDesktops, int MaxRunningDesktops, double MacBuildMinutes = 0);

/// <summary>What one person used this month: what their desktops' model calls cost, how many calls and tasks, and their desktops' hours. A name is empty when nobody is known for it.</summary>
public sealed record PersonUse(string UserId, string Name, decimal CostUsd, int Calls, int Tasks, double DesktopHours);

/// <summary>What one tool's model calls cost this month. <c>workstream</c> is Workstream's own agent; any other is a tool on a desktop, such as <c>opencode</c>.</summary>
public sealed record ToolUse(string Tool, decimal CostUsd, int Calls);

/// <summary>What one model was used for this month. <see cref="InputTokens"/> is everything sent, cached or not.</summary>
public sealed record ModelUse(string Model, decimal CostUsd, int Calls, long InputTokens, long OutputTokens);

/// <summary>One day (UTC) of the month.</summary>
public sealed record DayUse(DateOnly Day, decimal CostUsd, int Calls);

/// <summary>One task's model calls this month. A deleted task has no title.</summary>
public sealed record TaskUse(string TaskId, string? Title, string UserName, decimal CostUsd, int Calls);

/// <summary>
/// Who and what used the team's models this calendar month (UTC): by person, by tool, by model, by day, and
/// the ten tasks that cost most. <see cref="Everyone"/> says whose use it is: the whole team's for someone who
/// may see each person's (<c>TEAM_VIEW_PEOPLE_USAGE</c>), otherwise the caller's own.
/// </summary>
public sealed record ModelReport(
    DateTimeOffset Since, bool Everyone, decimal CostUsd, int Calls,
    IReadOnlyList<PersonUse> People, IReadOnlyList<ToolUse> Tools, IReadOnlyList<ModelUse> Models, IReadOnlyList<DayUse> Days, IReadOnlyList<TaskUse> Tasks);

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
public sealed record MachineCosts(IReadOnlyList<MachineCost> ThisMonth, IReadOnlyList<StorageCost> Storage, IReadOnlyList<SpareNow> Spares, MacCost? Mac = null);

/// <summary>What the Mac that builds iPhone apps cost this month over every team: how many builds ended, their minutes, and those at a Mac minute's price.</summary>
public sealed record MacCost(int Builds, double Minutes, decimal Usd);

/// <summary>One person's model calls this month, over every team they made them in, with the team.</summary>
public sealed record AdminPersonUse(string Name, string TeamName, decimal CostUsd, int Calls);

/// <summary>
/// Every team's model calls this month, as whoever runs Workstream watches them: how many, by which tool and
/// which model, and the twenty people whose calls cost most. Sizes and costs only: no call's words are kept.
/// </summary>
public sealed record AdminModels(int Calls, IReadOnlyList<ToolUse> Tools, IReadOnlyList<ModelUse> Models, IReadOnlyList<AdminPersonUse> People);

/// <summary>The system administrator's page: what is running now, the last day's curve, each team's month, what the desktops cost and how long they take to start.</summary>
public sealed record AdminOverview(
    int RunningDesktops, int Teams, int People, int Tasks, decimal ModelSpentUsd, double DesktopHours,
    IReadOnlyList<RunningPoint> Running, IReadOnlyList<RunningDesktop> Desktops, IReadOnlyList<TeamNumbers> TeamNumbers,
    MachineCosts Machines, IReadOnlyList<StartTimes> Starts, AdminModels? Models = null);

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
            await RunningDesktopsAsync(db, teamId, ct), team.MaxRunningDesktops, await MacMinutesAsync(db, teamId, since, ct)));
    }

    /// <summary>
    /// Who and what used the team's models this month. One check on the team, then sums over its rows, which
    /// SqlOS has no authorized form of. Someone who may see each person's use gets the whole team's; anyone
    /// else who may see the team's usage gets their own. A call is its person's by what the call wrote down,
    /// or, for a call from before that was kept, by who started its task.
    /// </summary>
    public static async Task<Outcome<ModelReport>> ModelReportAsync(Caller caller, string teamId, WorkstreamDbContext db, ISqlOSFgaAuthService fga, TimeProvider clock, CancellationToken ct)
    {
        var allowed = await TeamWithAsync(caller, teamId, Permissions.TeamViewUsage, "You can't see this team's usage.", db, fga, ct);
        if (!allowed.IsOk) return allowed.As<ModelReport>();
        var everyone = await fga.Allows(caller.PrincipalId, Permissions.TeamViewPeopleUsage, allowed.Value!.ResourceId);
        var now = clock.GetUtcNow();
        var since = MonthStart(now);

        var calls = await (
            from use in db.ModelUsage.AsNoTracking().Where(x => x.TeamId == teamId && x.At >= since)
            join task in db.Tasks.AsNoTracking() on use.TaskId equals task.Id into found
            from task in found.DefaultIfEmpty()
            group use by new { use.TaskId, Title = (string?)task.Title, UserId = use.UserId ?? task.CreatedBy, use.Tool, use.Model, use.At.Year, use.At.Month, use.At.Day } into line
            select new
            {
                line.Key.TaskId, line.Key.Title, line.Key.UserId, line.Key.Tool, line.Key.Model, line.Key.Year, line.Key.Month, line.Key.Day,
                Cost = line.Sum(x => x.CostUsd), Calls = line.Count(),
                Input = line.Sum(x => x.InputTokens + x.CacheReadTokens + x.CacheWriteTokens), Output = line.Sum(x => x.OutputTokens),
            }).ToListAsync(ct);
        var runs = await (
            from run in db.DesktopRuns.AsNoTracking().Where(x => x.TeamId == teamId && x.Purpose == MachinePurpose.Task && (x.EndedAt == null || x.EndedAt >= since))
            join task in db.Tasks.AsNoTracking() on run.TaskId equals task.Id into found
            from task in found.DefaultIfEmpty()
            select new { run.TaskId, UserId = run.UserId ?? task.CreatedBy, run.StartedAt, run.EndedAt }).ToListAsync(ct);
        if (!everyone)
        {
            calls = [.. calls.Where(x => x.UserId == caller.PrincipalId)];
            runs = [.. runs.Where(x => x.UserId == caller.PrincipalId)];
        }

        var names = await NamesAsync(db, calls.Select(x => x.UserId).Concat(runs.Select(x => x.UserId)).Where(x => x is not null).Select(x => x!), ct);
        string NameOf(string? userId) => userId is not null && names.TryGetValue(userId, out var name) ? name : "";

        var people = calls.Select(x => x.UserId ?? "").Concat(runs.Select(x => x.UserId ?? "")).Distinct().Select(userId =>
        {
            var theirs = calls.Where(x => (x.UserId ?? "") == userId).ToList();
            var ran = runs.Where(x => (x.UserId ?? "") == userId).ToList();
            return new PersonUse(
                userId, NameOf(userId), theirs.Sum(x => x.Cost), theirs.Sum(x => x.Calls), theirs.Select(x => x.TaskId).Concat(ran.Select(x => x.TaskId)).Distinct().Count(),
                Math.Round(HoursOf(ran.Select(x => (x.StartedAt, x.EndedAt)), since, now), 2));
        }).OrderByDescending(x => x.CostUsd).ThenByDescending(x => x.DesktopHours).ThenBy(x => x.Name).ToList();
        var tools = calls.GroupBy(x => x.Tool).Select(x => new ToolUse(x.Key, x.Sum(y => y.Cost), x.Sum(y => y.Calls)))
            .OrderByDescending(x => x.CostUsd).ThenBy(x => x.Tool).ToList();
        var models = calls.GroupBy(x => x.Model).Select(x => new ModelUse(x.Key, x.Sum(y => y.Cost), x.Sum(y => y.Calls), x.Sum(y => y.Input), x.Sum(y => y.Output)))
            .OrderByDescending(x => x.CostUsd).ThenBy(x => x.Model).ToList();
        // Every day of the month so far, so a day nothing was used is a day of nothing and not a gap.
        var byDay = calls.GroupBy(x => new DateOnly(x.Year, x.Month, x.Day)).ToDictionary(x => x.Key, x => (Cost: x.Sum(y => y.Cost), Calls: x.Sum(y => y.Calls)));
        var first = DateOnly.FromDateTime(since.UtcDateTime);
        var days = Enumerable.Range(0, DateOnly.FromDateTime(now.UtcDateTime).DayNumber - first.DayNumber + 1).Select(first.AddDays)
            .Select(day => byDay.TryGetValue(day, out var used) ? new DayUse(day, used.Cost, used.Calls) : new DayUse(day, 0m, 0)).ToList();
        var tasks = calls.GroupBy(x => x.TaskId).Select(x => new TaskUse(x.Key, x.First().Title, NameOf(x.First().UserId), x.Sum(y => y.Cost), x.Sum(y => y.Calls)))
            .OrderByDescending(x => x.CostUsd).ThenBy(x => x.TaskId).Take(10).ToList();

        return Outcome<ModelReport>.Ok(new ModelReport(since, everyone, calls.Sum(x => x.Cost), calls.Sum(x => x.Calls), people, tools, models, days, tasks));
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
    public static async Task<Outcome<AdminOverview>> AdminOverviewAsync(Caller caller, WorkstreamDbContext db, ISqlOSFgaAuthService fga, DesktopOptions desktops, Builds.BuildOptions builds, TimeProvider clock, CancellationToken ct)
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

        // The models, by tool, by model and by person: Workstream pays for every call, so it watches all of them.
        var calls = await (
            from use in db.ModelUsage.AsNoTracking().Where(x => x.At >= since)
            join task in db.Tasks.AsNoTracking() on use.TaskId equals task.Id into found
            from task in found.DefaultIfEmpty()
            group use by new { use.TeamId, UserId = use.UserId ?? task.CreatedBy, use.Tool, use.Model } into line
            select new
            {
                line.Key.TeamId, line.Key.UserId, line.Key.Tool, line.Key.Model, Cost = line.Sum(x => x.CostUsd), Calls = line.Count(),
                Input = line.Sum(x => x.InputTokens + x.CacheReadTokens + x.CacheWriteTokens), Output = line.Sum(x => x.OutputTokens),
            }).ToListAsync(ct);
        var callers = await NamesAsync(db, calls.Where(x => x.UserId is not null).Select(x => x.UserId!), ct);
        var models = new AdminModels(
            calls.Sum(x => x.Calls),
            [.. calls.GroupBy(x => x.Tool).Select(x => new ToolUse(x.Key, x.Sum(y => y.Cost), x.Sum(y => y.Calls))).OrderByDescending(x => x.CostUsd).ThenBy(x => x.Tool)],
            [.. calls.GroupBy(x => x.Model).Select(x => new ModelUse(x.Key, x.Sum(y => y.Cost), x.Sum(y => y.Calls), x.Sum(y => y.Input), x.Sum(y => y.Output))).OrderByDescending(x => x.CostUsd).ThenBy(x => x.Model)],
            [.. calls.GroupBy(x => (x.TeamId, x.UserId)).Select(x => new AdminPersonUse(
                    x.Key.UserId is { } userId && callers.TryGetValue(userId, out var caller) ? caller : "", names.GetValueOrDefault(x.Key.TeamId, "Team"), x.Sum(y => y.Cost), x.Sum(y => y.Calls)))
                .OrderByDescending(x => x.CostUsd).ThenBy(x => x.Name).Take(20)]);

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

        // The Mac's time: every build that ended this month, at a Mac minute's price.
        var built = await db.Builds.AsNoTracking().Where(x => x.EndedAt >= since).Select(x => x.MacSeconds).ToListAsync(ct);
        var macMinutes = Math.Round(built.Sum() / 60.0, 1);
        var mac = new MacCost(built.Count, macMinutes, Math.Round((decimal)macMinutes * builds.MacMinuteUsd, 2));

        return Outcome<AdminOverview>.Ok(new AdminOverview(
            running.Count, teams.Count, await db.Set<SqlOSUser>().CountAsync(x => x.IsActive, ct), tasks.Values.Sum(),
            spent.Values.Sum(), Math.Round(HoursOf(runs.Select(x => (x.StartedAt, x.EndedAt)), since, now), 2),
            points,
            [.. running.Select(x => new RunningDesktop(x.Id, x.Title, x.TeamId, names.GetValueOrDefault(x.TeamId, "Team"), x.Size, x.State.ToString().ToLowerInvariant(), x.AgentWorking, x.StateChangedAt))],
            numbers,
            new MachineCosts(costs, storage,
                [.. spares.Select(x => new SpareNow(x.TeamId, names.GetValueOrDefault(x.TeamId, "Team"), x.Size, x.State.ToString().ToLowerInvariant(), x.CreatedAt))], mac),
            starts, models));
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
