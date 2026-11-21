using System.Security.Cryptography;
using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SqlOS.Extensions;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using Workstream.Api.Desktops;
using Workstream.Api.Domain;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api;

/// <summary>
/// What the preparing desktop says about the setup it is doing: <c>running</c> with what it has written so
/// far, <c>done</c> when it ended well (with the repository's commit and how much is on the disk), or
/// <c>failed</c> with the setup script's exit code. A clone that failed has no exit code.
/// </summary>
public sealed record SetupReport(string State, int? Exit, string? Output, string? Commit, long? Bytes);

/// <summary>
/// An environment's prepared setup as its page shows it. <see cref="State"/> is <c>nothing</c> (nothing to
/// prepare), <c>not_prepared</c>, <c>preparing</c>, <c>prepared</c>, <c>out_of_date</c> or <c>failed</c>;
/// <see cref="Because"/> says why a save is out of date: <c>setup</c> (the settings changed) or
/// <c>desktop</c> (desktops are newer than it). <see cref="StartedAt"/> is null while a preparation waits for
/// a machine.
/// </summary>
public sealed record SetupDto(
    string State, string? Because, DateTimeOffset? PreparedAt, string? Commit, long Bytes, decimal MonthlyUsd,
    DateTimeOffset? StartedAt, string? Output, string? Failure);

public static partial class Operations
{
    /// <summary>
    /// A fingerprint of everything a saved setup depends on (<see cref="WorkEnvironment.SetupInputs"/>). It is
    /// made where the environment is written, because that is the one place its variables are readable.
    /// </summary>
    public static string SetupInputsOf(WorkEnvironment environment, IReadOnlyDictionary<string, string> variables)
    {
        var inputs = new
        {
            repository = environment.RepositoryUrl,
            branch = environment.RepositoryBranch,
            script = environment.SetupScript,
            variables = variables.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new[] { x.Key, x.Value }),
            apps = environment.Apps.Order(StringComparer.Ordinal),
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(inputs))).ToLowerInvariant();
    }

    public static SetupDto SetupOf(WorkEnvironment environment, EnvironmentSetup? setup, DesktopOptions options)
    {
        var image = options.ImageVersion;
        var standing = setup?.StandingFor(environment, image) ?? (environment.HasSetup ? SetupStanding.NotPrepared : SetupStanding.NothingToPrepare);
        var state = standing switch
        {
            SetupStanding.NothingToPrepare => "nothing",
            SetupStanding.NotPrepared => "not_prepared",
            SetupStanding.Preparing => "preparing",
            SetupStanding.Prepared => "prepared",
            SetupStanding.OutOfDate => "out_of_date",
            _ => "failed",
        };
        return new SetupDto(
            state, standing == SetupStanding.OutOfDate ? setup!.OutOfDateBecause(environment, image) : null,
            setup?.SavedAt, setup?.SaveCommit, setup?.SaveBytes ?? 0, options.Prices.SaveMonthlyOf(setup?.SaveBytes ?? 0),
            setup?.RunStartedAt, setup?.Output, setup?.Failure);
    }

    /// <summary>
    /// An environment was added or changed: its setup inputs are written, and what that means for its save.
    /// New settings to prepare are prepared at once; with nothing left to prepare, the save is removed. The
    /// caller saves the environment first; this saves the setup, and answers what the jobs are to do.
    /// </summary>
    static async Task KeepSetupAsync(WorkEnvironment environment, string? before, IDataProtectionProvider protection, WorkstreamDbContext db,
        IBackgroundJobClient jobs, DesktopOptions options, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (!environment.HasSetup)
        {
            string? removed = null;
            await ChangeSetupAsync(db, environment, setup => removed = setup.Forget(), ct, create: false);
            if (removed is not null) jobs.Enqueue<DesktopJobs>(job => job.DeleteSaveAsync(removed, CancellationToken.None));
            return;
        }
        if (environment.SetupInputs == before || !options.Saves.Enabled) return;
        await ChangeSetupAsync(db, environment, setup => setup.Want(now), ct);
        var id = environment.Id;
        jobs.Enqueue<DesktopJobs>(job => job.PrepareAsync(id, CancellationToken.None));
    }

    /// <summary>
    /// Change an environment's setup row, making it when it is not there and <paramref name="create"/> says
    /// to. An attempt beginning or ending in the same moment wins: the row is read again and the change made
    /// on what it is now.
    /// </summary>
    public static async Task ChangeSetupAsync(WorkstreamDbContext db, WorkEnvironment environment, Action<EnvironmentSetup> change, CancellationToken ct, bool create = true)
    {
        for (var attempt = 0; ; attempt++)
        {
            var setup = await db.Setups.SingleOrDefaultAsync(x => x.EnvironmentId == environment.Id, ct);
            if (setup is null)
            {
                if (!create) return;
                setup = EnvironmentSetup.For(environment);
                db.Setups.Add(setup);
            }
            change(setup);
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                db.Entry(setup).State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// "Prepare again": someone who may change the environment asks for its setup to be done again now. The
    /// check is <c>ENVIRONMENT_EDIT</c>, since it spends a machine's time for the environment.
    /// </summary>
    public static async Task<Outcome<WorkEnvironment>> PrepareAgainAsync(Caller caller, string id, WorkstreamDbContext db, ISqlOSFgaAuthService fga,
        IDataProtectionProvider protection, IBackgroundJobClient jobs, DesktopOptions options, CancellationToken ct)
    {
        var seen = await ViewEnvironmentAsync(caller, id, db, fga, ct);
        if (!seen.IsOk) return seen;
        var environment = seen.Value!;
        if (!await fga.Allows(caller.PrincipalId, Permissions.EnvironmentEdit, environment.ResourceId)) return Outcome<WorkEnvironment>.Forbidden("You can't prepare this environment.");
        if (!options.Saves.Enabled) return Outcome<WorkEnvironment>.Unavailable("Saved setups are turned off here.");
        if (!environment.HasSetup) return Outcome<WorkEnvironment>.Invalid("There is nothing to prepare. Add a repository or a setup script first.");
        var current = await db.Setups.AsNoTracking().SingleOrDefaultAsync(x => x.EnvironmentId == id, ct);
        if (current is { IsPreparing: true } || current?.WantedAt is not null) return Outcome<WorkEnvironment>.Invalid("It is being prepared now.");
        if (environment.SetupInputs is null)
        {
            environment.KeepSetupInputs(SetupInputsOf(environment, VariablesOf(environment, protection)));
            await db.SaveChangesAsync(ct);
        }
        var now = DateTimeOffset.UtcNow;
        await ChangeSetupAsync(db, environment, setup => setup.Want(now), ct);
        jobs.Enqueue<DesktopJobs>(job => job.PrepareAsync(id, CancellationToken.None));
        return seen;
    }

    /// <summary>
    /// The environment this desktop prepares: the one its service account holds <c>environment_setup</c> on,
    /// read as its own page of the environments, with the attempt it is for still going. A task's desktop and
    /// a spare hold it on none.
    /// </summary>
    static async Task<(WorkEnvironment Environment, EnvironmentSetup Setup)?> PreparingAsync(Caller desktop, WorkstreamDbContext db, CancellationToken ct)
    {
        var page = await db.Environments.OrderBy(x => x.Id).ToPageAsync(desktop.PrincipalId, Permissions.EnvironmentRunSetup, 1, null, ct);
        if (page.Data.FirstOrDefault() is not { } environment) return null;
        var setup = await db.Setups.SingleOrDefaultAsync(x => x.EnvironmentId == environment.Id, ct);
        // Its machine has said how the setup ended, or the attempt is over: there is nothing more to do there.
        return setup is { IsPreparing: true, RunSaving: false } ? (environment, setup) : null;
    }

    /// <summary>What the preparing desktop does: the environment's setup, with its variables, and no task and no model.</summary>
    static DesktopSetup PrepareSetupOf(WorkEnvironment environment, IDataProtectionProvider protection) => new(
        "", environment.Name, environment.Apps,
        environment.RepositoryUrl is { } url ? new DesktopRepository(url, environment.RepositoryBranch) : null,
        environment.SetupScript, VariablesOf(environment, protection), null, null, Prepare: true);

    /// <summary>
    /// The preparing desktop says how the setup is going. Only the desktop doing the attempt that is under way
    /// can: its account holds <c>environment_setup</c> on the environment, and its key is this attempt's.
    /// When it ended well, the operator stops the machine and saves its disk; when it failed, the machine goes
    /// and tasks go on doing the setup themselves.
    /// </summary>
    public static async Task<Outcome<bool>> ReportSetupAsync(Caller desktop, SetupReport body, WorkstreamDbContext db, IBackgroundJobClient jobs, CancellationToken ct)
    {
        if (await PreparingAsync(desktop, db, ct) is not { } preparing) return Outcome<bool>.NotFound();
        var (environment, setup) = preparing;
        var runId = setup.RunId!;
        var machine = setup.RunHost ?? DesktopHosting.NameFor(runId);
        var now = DateTimeOffset.UtcNow;
        try
        {
            switch (body.State)
            {
                case "running":
                    setup.Wrote(body.Output);
                    await db.SaveChangesAsync(ct);
                    break;
                case "done":
                    setup.SetupDone(body.Output, body.Commit, body.Bytes ?? 0);
                    await db.SaveChangesAsync(ct);
                    var environmentId = environment.Id;
                    jobs.Enqueue<DesktopJobs>(job => job.SavePreparedAsync(environmentId, runId, CancellationToken.None));
                    break;
                case "failed":
                    setup.Fail(body.Exit switch
                    {
                        null => "The repository could not be cloned.",
                        124 => "The setup script did not finish within an hour.",
                        var code => $"The setup script stopped with an error (exit {code}).",
                    }, body.Output);
                    foreach (var run in await db.DesktopRuns.Where(x => x.TaskId == runId && x.EndedAt == null).ToListAsync(ct)) run.End(now);
                    await DesktopKeys.RetireAsync(db, EnvironmentSetup.DesktopIdFor(environment.Id), ct);
                    await db.SaveChangesAsync(ct);
                    jobs.Enqueue<DesktopJobs>(job => job.RemovePreparingMachineAsync(machine, CancellationToken.None));
                    break;
                default:
                    return Outcome<bool>.Invalid("The state is running, done or failed.");
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            // The attempt ended in the same moment (the sweep gave up on it): this desktop's word no longer counts.
            return Outcome<bool>.NotFound();
        }
        catch (DomainRuleException rule)
        {
            return Outcome<bool>.Invalid(rule);
        }
        return Outcome<bool>.Ok(true);
    }

    /// <summary>
    /// Whether another machine may begin preparing now: fewer are preparing than may at once, and the day's
    /// hours of preparing over every team are not used up.
    /// </summary>
    public static async Task<bool> RoomToPrepareAsync(WorkstreamDbContext db, DesktopOptions options, DateTimeOffset now, CancellationToken ct)
    {
        if (await db.Setups.CountAsync(x => x.RunId != null, ct) >= options.Saves.AtOnce) return false;
        var from = now.AddHours(-24);
        var runs = await db.DesktopRuns.AsNoTracking().Where(x => x.Purpose == MachinePurpose.Prepare && (x.EndedAt == null || x.EndedAt >= from))
            .Select(x => new { x.StartedAt, x.EndedAt }).ToListAsync(ct);
        return HoursOf(runs.Select(x => (x.StartedAt, x.EndedAt)), from, now) < options.Saves.HoursPerDay;
    }
}
