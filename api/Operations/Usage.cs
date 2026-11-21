using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Models;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using Workstream.Api.Domain;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api;

/// <summary>What a team used this calendar month (UTC), beside what it may use.</summary>
public sealed record TeamUsage(DateTimeOffset Since, decimal ModelSpentUsd, decimal ModelAllowanceUsd, double DesktopHours, int RunningDesktops, int MaxRunningDesktops);

/// <summary>How many desktops were running at a moment.</summary>
public sealed record RunningPoint(DateTimeOffset At, int Running);

public sealed record RunningDesktop(string TaskId, string Title, string TeamId, string TeamName, string Size, string State, bool AgentWorking, DateTimeOffset Since);

public sealed record TeamNumbers(string Id, string Name, int People, int Tasks, int RunningDesktops, int MaxRunningDesktops, decimal ModelSpentUsd, decimal ModelAllowanceUsd, double DesktopHours);

/// <summary>The system administrator's page: what is running now, the last day's curve, and each team's month.</summary>
public sealed record AdminOverview(
    int RunningDesktops, int Teams, int People, int Tasks, decimal ModelSpentUsd, double DesktopHours,
    IReadOnlyList<RunningPoint> Running, IReadOnlyList<RunningDesktop> Desktops, IReadOnlyList<TeamNumbers> TeamNumbers);

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
        var runs = await db.DesktopRuns.AsNoTracking().Where(x => x.TeamId == teamId && (x.EndedAt == null || x.EndedAt >= since))
            .Select(x => new { x.StartedAt, x.EndedAt }).ToListAsync(ct);
        return Outcome<TeamUsage>.Ok(new TeamUsage(
            since, await ModelSpentAsync(db, teamId, since, ct), team.ModelAllowanceUsd,
            Math.Round(HoursOf(runs.Select(x => (x.StartedAt, x.EndedAt)), since, now), 2),
            await RunningDesktopsAsync(db, teamId, ct), team.MaxRunningDesktops));
    }

    /// <summary>
    /// Everything the system administrator's page shows. One check, on root; the numbers are sums over every
    /// team, which SqlOS has no authorized form of.
    /// </summary>
    public static async Task<Outcome<AdminOverview>> AdminOverviewAsync(Caller caller, WorkstreamDbContext db, ISqlOSFgaAuthService fga, TimeProvider clock, CancellationToken ct)
    {
        if (!await fga.HasCapabilityAsync(caller.PrincipalId, Permissions.AdminView)) return Outcome<AdminOverview>.Forbidden("This page is for system administrators.");
        var now = clock.GetUtcNow();
        var since = MonthStart(now);
        var dayAgo = now.AddHours(-24);
        var windowStart = dayAgo < since ? dayAgo : since;

        var runs = await db.DesktopRuns.AsNoTracking().Where(x => x.EndedAt == null || x.EndedAt >= windowStart)
            .Select(x => new { x.TeamId, x.StartedAt, x.EndedAt }).ToListAsync(ct);
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

        return Outcome<AdminOverview>.Ok(new AdminOverview(
            running.Count, teams.Count, await db.Set<SqlOSUser>().CountAsync(x => x.IsActive, ct), tasks.Values.Sum(),
            spent.Values.Sum(), Math.Round(HoursOf(runs.Select(x => (x.StartedAt, x.EndedAt)), since, now), 2),
            points,
            [.. running.Select(x => new RunningDesktop(x.Id, x.Title, x.TeamId, names.GetValueOrDefault(x.TeamId, "Team"), x.Size, x.State.ToString().ToLowerInvariant(), x.AgentWorking, x.StateChangedAt))],
            numbers));
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
