using Hangfire;
using Microsoft.EntityFrameworkCore;
using SqlOS.Extensions;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using Workstream.Api.Desktops;
using Workstream.Api.Domain;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api;

/// <summary>Someone is in a team's workspace and may start a task. The environment is the one they have chosen, when they have chosen one.</summary>
public sealed record WantSpare(string? EnvironmentId);

public static partial class Operations
{
    /// <summary>
    /// Someone is in the team's workspace: have a desktop running for the task they are about to start, so it
    /// starts at once (<see cref="Spare"/>). The check is the one for starting a task, <c>TEAM_CREATE_TASK</c>
    /// on the team, because that is all a spare is for. Answers whether one was asked for just now; no is not a
    /// refusal, it means the team has one already or a limit says not now.
    /// </summary>
    public static async Task<Outcome<bool>> WantSpareAsync(Caller caller, string teamId, WantSpare? body, WorkstreamDbContext db, ISqlOSFgaAuthService fga,
        IBackgroundJobClient jobs, DesktopOptions options, TimeProvider clock, CancellationToken ct)
    {
        var allowed = await TeamWithAsync(caller, teamId, Permissions.TeamCreateTask, "You can't start tasks in this team.", db, fga, ct);
        if (!allowed.IsOk) return allowed.As<bool>();
        WorkEnvironment? environment = null;
        if (body?.EnvironmentId is { Length: > 0 } id)
        {
            environment = await db.Environments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.TeamId == teamId, ct);
            if (environment is not null && !await fga.Allows(caller.PrincipalId, Permissions.EnvironmentView, environment.ResourceId)) environment = null;
        }
        var spareId = await EnsureSpareAsync(allowed.Value!, environment, db, options, clock.GetUtcNow(), ct);
        if (spareId is not null) jobs.Enqueue<DesktopJobs>(job => job.StartSpareAsync(spareId, CancellationToken.None));
        return Outcome<bool>.Ok(spareId is not null);
    }

    /// <summary>
    /// The one place a spare comes from. Makes the row for a new spare and answers its id, for the caller to
    /// start its machine; answers null when the team has one (which is then wanted for a while longer) or when
    /// a limit says no: spares are off, the team already runs as many desktops as it may, as many spares are
    /// running as there may be at once, or the day's hours of spare time are used.
    /// </summary>
    public static async Task<string?> EnsureSpareAsync(Team team, WorkEnvironment? environment, WorkstreamDbContext db, DesktopOptions options, DateTimeOffset now, CancellationToken ct)
    {
        if (!options.Spares.Enabled) return null;
        var existing = await db.Spares.SingleOrDefaultAsync(x => x.TeamId == team.Id, ct);
        if (existing is not null)
        {
            existing.Wanted(now);
            await db.SaveChangesAsync(ct);
            return null;
        }
        // No environment named: the one the team used last, and before its first task the first it has.
        if (environment is null)
        {
            var last = await db.Tasks.AsNoTracking().Where(x => x.TeamId == team.Id).OrderByDescending(x => x.CreatedAt).Select(x => x.EnvironmentId).FirstOrDefaultAsync(ct);
            environment = last is not null
                ? await db.Environments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == last, ct)
                : await db.Environments.AsNoTracking().Where(x => x.TeamId == team.Id).OrderBy(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        }
        if (environment is null) return null;
        if (await RunningDesktopsAsync(db, team.Id, ct) >= team.MaxRunningDesktops) return null;
        if (await db.Spares.CountAsync(ct) >= options.Spares.Max) return null;
        if (await SpareHoursAsync(db, now.AddHours(-24), now, ct) >= options.Spares.HoursPerDay) return null;

        var spare = Spare.For(team, environment, now);
        db.Spares.Add(spare);
        db.DesktopRuns.Add(DesktopRun.Begin(spare, now));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Someone else on the team opened the workspace in the same moment, and theirs was first.
            return null;
        }
        return spare.Id;
    }

    /// <summary>Hours of spare time inside a window, over every team.</summary>
    public static async Task<double> SpareHoursAsync(WorkstreamDbContext db, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var runs = await db.DesktopRuns.AsNoTracking().Where(x => x.Purpose == MachinePurpose.Spare && (x.EndedAt == null || x.EndedAt >= from))
            .Select(x => new { x.StartedAt, x.EndedAt }).ToListAsync(ct);
        return HoursOf(runs.Select(x => (x.StartedAt, x.EndedAt)), from, to);
    }

    /// <summary>
    /// The team's spare, taken for a task from this environment when it is up and fits; null when there is none
    /// to take. Taking is deleting its row, which only one caller can do. What makes it the task's desktop, the
    /// grant and the task's own fields, is the caller's to save with the task, in the same transaction.
    /// </summary>
    static async Task<Spare?> TakeSpareAsync(WorkstreamDbContext db, string teamId, WorkEnvironment environment, DesktopOptions options, DateTimeOffset now, CancellationToken ct)
    {
        if (!options.Spares.Enabled) return null;
        var spare = await db.Spares.AsNoTracking().SingleOrDefaultAsync(x => x.TeamId == teamId, ct);
        if (spare is null || !spare.Serves(environment, now)) return null;
        return await db.Spares.Where(x => x.Id == spare.Id).ExecuteDeleteAsync(ct) == 1 ? spare : null;
    }
}
