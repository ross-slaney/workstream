using Hangfire;
using Microsoft.EntityFrameworkCore;
using SqlOS.Fga.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using Workstream.Api.Builds;
using Workstream.Api.Domain;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api.Desktops;

/// <summary>
/// Everything that happens to a desktop outside a request: making and starting its machine and following it
/// until its screen can be watched, pausing it, archiving it, deleting it, keeping a team's spare, and the
/// sweep that pauses unused desktops, archives ones paused a long time, removes spares nobody took and deletes
/// strays. Each job is an operation run by a
/// principal of its own, <c>agent::desktop-operator</c>, which holds <c>desktop_operator</c> on root: it checks
/// <c>TASK_OPERATE_DESKTOP</c> on every task it touches and pages the tasks it sweeps with the same permission.
/// A job changes a task only from the state the person's request left it in, so a job that runs twice, or late,
/// does nothing the second time.
/// </summary>
public sealed class DesktopJobs(IServiceScopeFactory scopes, IDesktopHost host, DesktopOptions options, TimeProvider clock, ILogger<DesktopJobs> log)
{
    public const string SweepId = "desktop-sweep";
    public const string SweepCron = "*/5 * * * *";
    const string Operator = PlatformSeed.OperatorSubjectId;
    const int PageSize = 200;
    /// <summary>How long a start job follows a machine. One that has not shown a screen by then is left to the sweep.</summary>
    static readonly TimeSpan FollowFor = TimeSpan.FromMinutes(5);
    static readonly TimeSpan AskHostEvery = TimeSpan.FromSeconds(2);
    /// <summary>How old a machine no task owns must be before the sweep deletes it.</summary>
    static readonly TimeSpan StrayAfter = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Make the task's machine, or start the one it has, and follow it until there is a screen to watch,
    /// writing down each step as it happens. The desktop's first exchange is what makes it ready.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    public async Task StartAsync(string taskId, CancellationToken ct)
    {
        string? existing;
        DesktopSpec? spec = null;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            var task = await OperableAsync(scope, db, taskId, DesktopState.Starting, ct);
            if (task is null) return;
            existing = task.Host;
            if (existing is null)
            {
                // The key is made here and handed to the machine; nothing keeps it. A machine made for a task is
                // that task's desktop from the start: it has the task's id, and works on the task at once.
                var key = await DesktopKeys.IssueAsync(db, scope.ServiceProvider.GetRequiredService<SqlOSCryptoService>(), task.Id, task.TeamId, ct);
                await DesktopKeys.GrantAsync(db, task.Id, task, ct);
                await db.SaveChangesAsync(ct);
                // A paused desktop whose machine was given up comes back on its own disk, under the name the
                // disk was kept by. An archived desktop comes back from its own archive and nothing else: the new
                // machine's disk is a copy of the one that was archived. Any other new machine starts from the
                // environment's saved setup, or the image.
                var onDisk = task.Disk is not null;
                spec = new DesktopSpec(task.Disk ?? DesktopHosting.NameFor(task.Id), task.Id, task.TeamId, task.Size, options.ApiUrl, key,
                    Save: onDisk || task.Archive is not null ? null : await SaveForAsync(scope, db, task, ct), Archive: onDisk ? null : task.Archive,
                    Iphone: await HasIphoneAsync(db, task.EnvironmentId, ct), Kept: onDisk);
            }
        }
        var machine = spec?.Name ?? existing!;

        // A desktop that failed costs nothing: whatever part of a new machine was made is removed, and a
        // machine that would not start again is released. Starting the task again begins from there: a task
        // whose new machine was removed has none, so the next start makes one, from its archive when it has one.
        // A machine made from the desktop's kept disk is given up again and never deleted: deleting a machine
        // deletes its disk, and that disk is the desktop.
        async Task FailAsync(Exception error)
        {
            log.LogError(error, "The desktop of task {TaskId} did not start.", taskId);
            await ChangeAsync(taskId, async (db, task, now) =>
            {
                if (task.State != DesktopState.Starting) return;
                task.Fail(WordsFor(error), now);
                if (spec is { Kept: true }) task.MachineReleased(machine);
                else if (spec is not null) task.MachineRemoved();
                await EndRunAsync(db, task.Id, now, ct);
            }, ct);
            try
            {
                if (spec is { Kept: true }) await host.ReleaseAsync(machine, ct);
                else if (spec is not null) await host.DeleteAsync(machine, ct);
                else await host.StopAsync(machine, ct);
            }
            catch (Exception cleanup) when (cleanup is not OperationCanceledException)
            {
                log.LogWarning(cleanup, "The machine of task {TaskId} could not be cleared after its start failed.", taskId);
            }
        }

        DesktopPlace place;
        try
        {
            place = spec is not null ? await host.CreateAsync(spec, ct) : await host.StartAsync(existing!, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await FailAsync(error);
            return;
        }

        var kept = false;
        await ChangeAsync(taskId, (_, task, _) =>
        {
            task.DesktopMade(place.Host, place.Address);
            kept = task.State == DesktopState.Starting || task.State == DesktopState.Ready;
            return Task.CompletedTask;
        }, ct);
        // The person stopped or deleted the task while its machine was being made: the machine goes the same way.
        if (!kept)
        {
            await SettleAsync(taskId, place.Host, ct);
            return;
        }

        await FollowAsync(place,
            on: () => StepAsync(taskId, (task, run, now) =>
            {
                task.MachineOn(now);
                run?.MachineOn(now);
            }, ct),
            display: async () =>
            {
                await StepAsync(taskId, (task, run, now) =>
                {
                    task.DisplayUp(now);
                    run?.DisplayUp(now);
                }, ct);
                await ForgetArchiveAsync(taskId, ct);
            },
            failed: why => FailAsync(new InvalidOperationException(why)),
            wanted: () => IsStartingAsync(taskId, ct),
            // The agent was there before the display was seen to answer. It is answering: its time is kept.
            unwanted: async () =>
            {
                if (!await host.DisplayAnswersAsync(place, ct)) return;
                await StepAsync(taskId, (_, run, now) => run?.DisplayUp(now), ct);
                await ForgetArchiveAsync(taskId, ct);
            },
            what: $"task {taskId}", ct);
    }

    /// <summary>
    /// The desktop is up on a machine of its own again, so the archive it came back from is no longer needed
    /// and is removed. Until then the archive is the only whole copy of the task's disk, and stays.
    /// </summary>
    async Task ForgetArchiveAsync(string taskId, CancellationToken ct)
    {
        string? archive = null;
        await ChangeAsync(taskId, (_, task, _) =>
        {
            archive = task.Restored();
            return Task.CompletedTask;
        }, ct);
        if (archive is not null) await host.DeleteArchiveAsync(archive, ct);
    }

    /// <summary>
    /// Follow a machine the host has only taken the request for, until its display answers. Two things are
    /// asked at once, each at its own pace: the display every quarter second, since its answer is the moment
    /// there is a screen to watch, and the host every two seconds, for when the machine is on and in case it
    /// could not be started. Ends early, through <paramref name="unwanted"/>, when whatever asked for the
    /// machine no longer waits for it.
    /// </summary>
    async Task FollowAsync(DesktopPlace place, Func<Task> on, Func<Task> display, Func<string, Task> failed, Func<Task<bool>> wanted, Func<Task> unwanted, string what, CancellationToken ct)
    {
        using var following = CancellationTokenSource.CreateLinkedTokenSource(ct);
        following.CancelAfter(FollowFor);
        var answered = Task.Run(async () =>
        {
            while (!await host.DisplayAnswersAsync(place, following.Token)) await Task.Delay(TimeSpan.FromMilliseconds(250), following.Token);
        }, following.Token);
        var hosted = Task.Run(async () =>
        {
            var isOn = false;
            while (true)
            {
                var state = new MachineState(isOn);
                try
                {
                    state = await host.StateAsync(place.Host, following.Token);
                }
                catch (Exception error) when (error is HttpRequestException or InvalidOperationException)
                {
                    log.LogWarning(error, "The host did not say how the machine of {What} is doing.", what);
                }
                if (state.Failure is not null) return state.Failure;
                if (state.On && !isOn)
                {
                    isOn = true;
                    await on();
                }
                if (!await wanted()) return null;
                await Task.Delay(AskHostEvery, following.Token);
            }
        }, following.Token);

        try
        {
            var first = await Task.WhenAny(answered, hosted);
            if (first == answered && answered.IsCompletedSuccessfully)
            {
                await display();
            }
            else if (first == hosted && await hosted is { } failure)
            {
                await failed(failure);
            }
            else
            {
                await first;
                await unwanted();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("The display of {What} did not answer in {Minutes} minutes.", what, FollowFor.TotalMinutes);
        }
        finally
        {
            following.Cancel();
            try
            {
                await Task.WhenAll(answered, hosted);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // The one that was not first, stopped where it was. What it was doing no longer matters.
                log.LogDebug(error, "The following of {What} ended.", what);
            }
        }
    }

    /// <summary>
    /// Make a spare's machine and follow it until its display answers. The spare is ready when its agent first
    /// asks the API what it is for (<see cref="Operations.EnsureSpareAsync"/> made the row). The operator
    /// checks <c>TEAM_OPERATE_SPARE</c> on the team it is for.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    public async Task StartSpareAsync(string spareId, CancellationToken ct)
    {
        DesktopSpec spec;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            var spare = await db.Spares.SingleOrDefaultAsync(x => x.Id == spareId, ct);
            if (spare is null || spare.Host is not null) return;
            if (!await scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>().Allows(Operator, Permissions.TeamOperateSpare, Team.ResourceIdFor(spare.TeamId)))
            {
                log.LogError("The desktop operator may not keep a spare for team {TeamId}.", spare.TeamId);
                return;
            }
            // Its key opens nothing until a task takes the spare: the account is granted nothing here.
            var key = await DesktopKeys.IssueAsync(db, scope.ServiceProvider.GetRequiredService<SqlOSCryptoService>(), spare.Id, spare.TeamId, ct);
            await db.SaveChangesAsync(ct);
            spec = new DesktopSpec(DesktopHosting.NameFor(spare.Id), spare.Id, spare.TeamId, spare.Size, options.ApiUrl, key, MachinePurpose.Spare,
                Iphone: await HasIphoneAsync(db, spare.EnvironmentId, ct));
        }

        DesktopPlace place;
        try
        {
            place = await host.CreateAsync(spec, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "The spare {SpareId} could not be made.", spareId);
            await RemoveSpareAsync(spareId, spec.Name, ct);
            return;
        }

        var kept = false;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            if (await db.Spares.SingleOrDefaultAsync(x => x.Id == spareId, ct) is { } spare)
            {
                spare.Made(place.Host, place.Address);
                await db.SaveChangesAsync(ct);
                kept = true;
            }
        }
        // Removed while its machine was being made: the machine goes too.
        if (!kept)
        {
            await host.DeleteAsync(place.Host, ct);
            return;
        }

        await FollowAsync(place,
            on: () => Task.CompletedTask,
            display: () => Task.CompletedTask,
            failed: async why =>
            {
                log.LogWarning("The spare {SpareId} could not be started: {Why}", spareId, why);
                await RemoveSpareAsync(spareId, place.Host, ct);
            },
            wanted: async () =>
            {
                await using var scope = scopes.CreateAsyncScope();
                return await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Spares.AsNoTracking().AnyAsync(x => x.Id == spareId && x.State == SpareState.Starting, ct);
            },
            unwanted: () => Task.CompletedTask,
            what: $"spare {spareId}", ct);
    }

    /// <summary>A task took the team's spare: make the next one, when the limits allow.</summary>
    [AutomaticRetry(Attempts = 0)]
    public async Task RefillSpareAsync(string teamId, string environmentId, CancellationToken ct)
    {
        string? spareId;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            var team = await db.Teams.SingleOrDefaultAsync(x => x.Id == teamId, ct);
            if (team is null || !await scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>().Allows(Operator, Permissions.TeamOperateSpare, team.ResourceId)) return;
            var environment = await db.Environments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == environmentId && x.TeamId == teamId, ct);
            spareId = await Operations.EnsureSpareAsync(team, environment, db, options, clock.GetUtcNow(), ct);
        }
        if (spareId is not null) await StartSpareAsync(spareId, ct);
    }

    /// <summary>
    /// Remove a spare nothing took: its row, its key, its time and its machine. Deleting the row is what
    /// decides it. A task that took the spare in the same moment deleted the row first, and then the machine is
    /// that task's and is left alone.
    /// </summary>
    async Task RemoveSpareAsync(string spareId, string? machine, CancellationToken ct)
    {
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            if (await db.Spares.Where(x => x.Id == spareId).ExecuteDeleteAsync(ct) != 1) return;
            await EndRunAsync(db, spareId, clock.GetUtcNow(), ct);
            await DesktopKeys.RetireAsync(db, spareId, ct);
            await db.SaveChangesAsync(ct);
        }
        if (machine is not null) await host.DeleteAsync(machine, ct);
    }

    /// <summary>Whether the environment's desktops come with the iPhone, and so start from the image that has its simulator.</summary>
    static async Task<bool> HasIphoneAsync(WorkstreamDbContext db, string environmentId, CancellationToken ct) =>
        await db.Environments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == environmentId, ct) is { } environment && environment.Apps.Contains(AppCatalog.Iphone);

    /// <summary>
    /// What a task's new machine starts from: the environment's saved setup when it is good for the
    /// environment's settings now and for these desktops, otherwise the image (null). Starting from a save
    /// writes that down on the run, and a save older than a week is made again behind the task, which uses the
    /// old one. Finding no save to start from, when there is a setup, asks for one.
    /// </summary>
    async Task<string?> SaveForAsync(AsyncServiceScope scope, WorkstreamDbContext db, WorkTask task, CancellationToken ct)
    {
        if (!options.Saves.Enabled) return null;
        var environment = await db.Environments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == task.EnvironmentId, ct);
        if (environment is not { HasSetup: true }) return null;
        var now = clock.GetUtcNow();
        string? save = null;
        var prepare = false;
        await Operations.ChangeSetupAsync(db, environment, setup =>
        {
            save = null;
            if (setup.Serves(environment, options.ImageVersion))
            {
                save = setup.Save;
                setup.Used(now);
                prepare = setup.IsStale(now, options.Saves.RefreshDays) && setup.WorthPreparing(environment) && setup.WantedAt is null;
            }
            else
            {
                prepare = setup.WorthPreparing(environment);
            }
            if (prepare) setup.Want(now);
        }, ct);
        if (save is not null)
        {
            var run = await db.DesktopRuns.Where(x => x.TaskId == task.Id && x.EndedAt == null).OrderByDescending(x => x.StartedAt).FirstOrDefaultAsync(ct);
            run?.FromSave();
            await db.SaveChangesAsync(ct);
        }
        var environmentId = environment.Id;
        if (prepare) scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>().Enqueue<DesktopJobs>(job => job.PrepareAsync(environmentId, CancellationToken.None));
        return save;
    }

    /// <summary>
    /// Prepare an environment that is wanted (docs/core-model.md, "Prepare an environment"): a machine of its
    /// own, as the environment's preparing account, which holds <c>environment_setup</c> on the environment
    /// and nothing else, with a key made for this attempt. The machine's agent does the setup and says how it
    /// went (<see cref="Operations.ReportSetupAsync"/>); this job only makes the machine. Nothing begins while
    /// as many are preparing as may at once, or the day's hours of preparing are used: what is wanted waits for
    /// the sweep. The operator checks <c>ENVIRONMENT_OPERATE_SETUP</c> on the environment.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    public async Task PrepareAsync(string environmentId, CancellationToken ct)
    {
        if (!options.Saves.Enabled) return;
        DesktopSpec spec;
        string runId;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            var environment = await db.Environments.SingleOrDefaultAsync(x => x.Id == environmentId, ct);
            if (environment is not { HasSetup: true }) return;
            if (!await scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>().Allows(Operator, Permissions.EnvironmentOperateSetup, environment.ResourceId))
            {
                log.LogError("The desktop operator may not prepare environment {EnvironmentId}.", environmentId);
                return;
            }
            var setup = await db.Setups.SingleOrDefaultAsync(x => x.EnvironmentId == environmentId, ct);
            if (setup is null || setup.WantedAt is null || setup.IsPreparing) return;
            var now = clock.GetUtcNow();
            if (!await Operations.RoomToPrepareAsync(db, options, now, ct)) return;

            runId = Guid.NewGuid().ToString("D");
            var desktopId = EnvironmentSetup.DesktopIdFor(environmentId);
            setup.Begin(runId, environment, options.ImageVersion, now);
            db.DesktopRuns.Add(DesktopRun.BeginPrepare(runId, environment, now));
            // One account for the environment's preparing, granted once; each attempt is given a key of its own,
            // so a machine from an earlier attempt can no longer speak for this one.
            var key = await DesktopKeys.IssueAsync(db, scope.ServiceProvider.GetRequiredService<SqlOSCryptoService>(), desktopId, environment.TeamId, ct);
            await db.GrantRoleAsync(DesktopKeys.SubjectIdFor(desktopId), environment, Roles.EnvironmentSetup, ct);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Another job began it in the same moment.
                return;
            }
            spec = new DesktopSpec(DesktopHosting.NameFor(runId), desktopId, environment.TeamId, environment.Size, options.ApiUrl, key, MachinePurpose.Prepare,
                Iphone: environment.Apps.Contains(AppCatalog.Iphone));
        }

        DesktopPlace place;
        try
        {
            place = await host.CreateAsync(spec, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "The machine to prepare environment {EnvironmentId} on could not be made.", environmentId);
            await EndPreparingAsync(environmentId, runId, "The machine to prepare on could not be made. Prepare it again.", spec.Name, ct);
            return;
        }

        var kept = false;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            if (await db.Setups.SingleOrDefaultAsync(x => x.EnvironmentId == environmentId && x.RunId == runId, ct) is { } setup)
            {
                setup.RunMade(place.Host);
                try
                {
                    await db.SaveChangesAsync(ct);
                    kept = true;
                }
                catch (DbUpdateConcurrencyException)
                {
                    // The attempt ended while its machine was being made.
                }
            }
        }
        if (!kept) await host.DeleteAsync(place.Host, ct);
    }

    /// <summary>
    /// The setup ended well on the preparing machine: stop it, keep its disk as the environment's save, and
    /// remove the machine. The save it replaces is removed: an environment keeps one. The machine is stopped
    /// first, so the disk is saved whole.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    public async Task SavePreparedAsync(string environmentId, string runId, CancellationToken ct)
    {
        string machine;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            var environment = await db.Environments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == environmentId, ct);
            var setup = await db.Setups.AsNoTracking().SingleOrDefaultAsync(x => x.EnvironmentId == environmentId && x.RunId == runId && x.RunSaving, ct);
            if (environment is null || setup is null) return;
            if (!await scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>().Allows(Operator, Permissions.EnvironmentOperateSetup, environment.ResourceId)) return;
            machine = setup.RunHost ?? DesktopHosting.NameFor(runId);
        }
        var name = EnvironmentSetup.SaveNameFor(runId);
        try
        {
            await host.StopAsync(machine, ct);
            await host.SaveAsync(machine, name, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "The prepared disk of environment {EnvironmentId} could not be saved.", environmentId);
            await EndPreparingAsync(environmentId, runId, "The setup ran, but its desktop could not be saved. Prepare it again.", machine, ct);
            return;
        }

        var removed = new List<string>();
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            var environment = await db.Environments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == environmentId, ct);
            var setup = await db.Setups.SingleOrDefaultAsync(x => x.EnvironmentId == environmentId && x.RunId == runId, ct);
            if (environment is null || setup is null)
            {
                removed.Add(name);
            }
            else
            {
                var now = clock.GetUtcNow();
                if (setup.Saved(name, now) is { } replaced) removed.Add(replaced);
                // Its settings moved on to nothing to prepare while it ran: the save would be nobody's.
                if (!environment.HasSetup && setup.Forget() is { } unused) removed.Add(unused);
                await EndRunAsync(db, runId, now, ct);
                await DesktopKeys.RetireAsync(db, EnvironmentSetup.DesktopIdFor(environmentId), ct);
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // The attempt was ended while the disk was being saved (it took too long): the save is nobody's.
                    removed = [name];
                }
            }
        }
        foreach (var save in removed.Distinct()) await host.DeleteSaveAsync(save, ct);
        await host.DeleteAsync(machine, ct);
        await PrepareWantedAsync(ct);
    }

    /// <summary>An attempt ends without a save: why, the machine removed, its key and its time ended. The save there was, if any, stays.</summary>
    async Task EndPreparingAsync(string environmentId, string runId, string why, string machine, CancellationToken ct)
    {
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            if (await db.Setups.SingleOrDefaultAsync(x => x.EnvironmentId == environmentId && x.RunId == runId, ct) is { } setup)
            {
                setup.Fail(why, null);
                await EndRunAsync(db, runId, clock.GetUtcNow(), ct);
                await DesktopKeys.RetireAsync(db, EnvironmentSetup.DesktopIdFor(environmentId), ct);
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // It ended another way in the same moment.
                }
            }
        }
        await host.DeleteAsync(machine, ct);
        await PrepareWantedAsync(ct);
    }

    /// <summary>A preparing machine whose attempt is over: its setup failed, or its environment was deleted.</summary>
    [AutomaticRetry(Attempts = 2)]
    public async Task RemovePreparingMachineAsync(string machine, CancellationToken ct)
    {
        await host.DeleteAsync(machine, ct);
        await PrepareWantedAsync(ct);
    }

    /// <summary>A save nothing will start from again: its environment was deleted, or has nothing to prepare now.</summary>
    [AutomaticRetry(Attempts = 2)]
    public Task DeleteSaveAsync(string save, CancellationToken ct) => host.DeleteSaveAsync(save, ct);

    /// <summary>Begin preparing what is wanted, oldest first, while the limits leave room.</summary>
    async Task PrepareWantedAsync(CancellationToken ct)
    {
        if (!options.Saves.Enabled) return;
        List<string> wanted;
        await using (var scope = scopes.CreateAsyncScope())
        {
            wanted = await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Setups.AsNoTracking()
                .Where(x => x.WantedAt != null && x.RunId == null).OrderBy(x => x.WantedAt).Select(x => x.EnvironmentId).Take(20).ToListAsync(ct);
        }
        foreach (var environmentId in wanted)
        {
            await using (var scope = scopes.CreateAsyncScope())
            {
                if (!await Operations.RoomToPrepareAsync(scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>(), options, clock.GetUtcNow(), ct)) return;
            }
            await PrepareAsync(environmentId, ct);
        }
    }

    /// <summary>
    /// Pause the desktop of a task a person, or the sweep, asked to pause: the machine is released
    /// (<see cref="IDesktopHost.StopAsync"/>) and its disk is kept. Where the host can make a machine from that
    /// disk again, the machine is given up altogether (<see cref="IDesktopHost.ReleaseAsync"/>), so a paused
    /// desktop does not count against the host's limit on machines. No machine holds the desktop's key then,
    /// so it is ended, and the machine the desktop comes back on is given a new one, as after an archive.
    /// </summary>
    [AutomaticRetry(Attempts = 2)]
    public async Task StopAsync(string taskId, CancellationToken ct)
    {
        string? machine;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            var task = await OperableAsync(scope, db, taskId, DesktopState.Stopping, ct);
            if (task is null) return;
            machine = task.Host;
        }
        var released = false;
        if (machine is not null)
        {
            await host.StopAsync(machine, ct);
            released = await host.ReleaseAsync(machine, ct);
        }
        await ChangeAsync(taskId, async (db, task, now) =>
        {
            if (task.State != DesktopState.Stopping) return;
            task.Stopped(now);
            if (released)
            {
                task.MachineReleased(machine!);
                await DesktopKeys.RetireForTaskAsync(db, task, ct);
            }
            await EndRunAsync(db, task.Id, now, ct);
        }, ct);
    }

    /// <summary>
    /// Archive a desktop that has been paused for as long as its team allows (docs/core-model.md, "Pause,
    /// archive, delete"): copy its disk to an archive, write the archive down on the task, and only then delete
    /// the machine and its disk. A copy that cannot be made leaves the desktop paused as it was, to be tried at
    /// the next sweep. A task left <see cref="DesktopState.Archiving"/> by an attempt that was cut short is
    /// taken up again here; the copy that attempt made, if any, is nobody's and the sweep removes it.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    public async Task ArchiveAsync(string taskId, CancellationToken ct)
    {
        string machine;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            var task = await db.Tasks.SingleOrDefaultAsync(x => x.Id == taskId, ct);
            if (task is not { State: DesktopState.Stopped or DesktopState.Archiving } || (task.Host ?? task.Disk) is null) return;
            if (!await scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>().Allows(Operator, Permissions.TaskOperateDesktop, task.ResourceId))
            {
                log.LogError("The desktop operator may not act on task {TaskId}.", taskId);
                return;
            }
            // The paused desktop's machine, or the disk it is kept on when the machine was given up.
            machine = (task.Host ?? task.Disk)!;
            if (task.State == DesktopState.Stopped)
            {
                task.Archiving();
                await db.SaveChangesAsync(ct);
            }
        }

        var archive = DesktopHosting.NewArchiveNameFor(taskId);
        long? bytes;
        try
        {
            bytes = await host.ArchiveAsync(machine, archive, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogWarning(error, "The disk of task {TaskId} could not be archived.", taskId);
            await ChangeAsync(taskId, (_, task, _) =>
            {
                task.NotArchived();
                return Task.CompletedTask;
            }, ct);
            // The machine and its disk are untouched, so whatever was made of the copy can go.
            await host.DeleteArchiveAsync(archive, ct);
            return;
        }

        var kept = false;
        await ChangeAsync(taskId, async (db, task, now) =>
        {
            if (task.State != DesktopState.Archiving) return;
            task.Archived(archive, bytes, now);
            // No machine holds the desktop's key any more, so it is ended. The machine the desktop comes back on is given a new one.
            await DesktopKeys.RetireForTaskAsync(db, task, ct);
            kept = true;
        }, ct);
        // The task was deleted while its disk was being copied: the copy is nobody's, and the delete job removes the machine.
        if (!kept)
        {
            await host.DeleteArchiveAsync(archive, ct);
            return;
        }
        await host.DeleteAsync(machine, ct);
    }

    /// <summary>Remove a deleted task for good: its machine, its archive, its key, its conversation, who watched it, then the row and its node.</summary>
    [AutomaticRetry(Attempts = 2)]
    public async Task DeleteAsync(string taskId, CancellationToken ct)
    {
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            // A deleted task's node is inactive, so nothing may be checked on it: the row's state is the instruction.
            var task = await db.Tasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == taskId, ct);
            if (task is not { State: DesktopState.Deleting }) return;
            // The name is the same every time, so a machine still being made by a start job is found too.
            // A paused desktop kept on its disk alone goes by the name the disk was kept by.
            await host.DeleteAsync(task.Host ?? task.Disk ?? DesktopHosting.NameFor(taskId), ct);
            if (task.Archive is not null) await host.DeleteArchiveAsync(task.Archive, ct);
        }
        // The conversation goes in pieces, tracked, so SqlOS's page entries go with the rows.
        while (true)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            var messages = await db.TaskMessages.Where(x => x.TaskId == taskId).OrderBy(x => x.Id).Take(500).ToListAsync(ct);
            if (messages.Count == 0) break;
            // A picture's file goes with its message.
            var files = scope.ServiceProvider.GetRequiredService<IBuildFiles>();
            foreach (var picture in messages.Where(x => x.Kind == MessageKind.Picture)) await files.DeleteAsync(TaskMessage.PictureFile(taskId, picture.ClientKey), ct);
            db.TaskMessages.RemoveRange(messages);
            await db.SaveChangesAsync(ct);
        }
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            var task = await db.Tasks.SingleOrDefaultAsync(x => x.Id == taskId, ct);
            if (task is not { State: DesktopState.Deleting }) return;
            var now = clock.GetUtcNow();
            await EndRunAsync(db, taskId, now, ct);
            await DesktopKeys.RetireForTaskAsync(db, task, ct);
            // Invitations nobody took, and the shares of those who did.
            db.TaskInvitations.RemoveRange(await db.TaskInvitations.Where(x => x.TaskId == taskId).ToListAsync(ct));
            db.RemoveRange(await db.Set<SqlOSFgaGrant>().Where(g => g.ResourceId == task.ResourceId && TaskShareRoles.All.Contains(g.RoleId)).ToListAsync(ct));
            await db.TaskWatchers.Where(x => x.TaskId == taskId).ExecuteDeleteAsync(ct);
            db.Tasks.Remove(task);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Every five minutes: pause the desktops nobody is using, archive the ones paused for as long as their
    /// team allows, fail the ones that never came up, finish what a lost job left half done, and delete
    /// machines and archives no task owns. Nothing here deletes a task: only a person does.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 240)]
    [AutomaticRetry(Attempts = 0)]
    public async Task SweepAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var startTimeout = TimeSpan.FromMinutes(options.StartTimeoutMinutes);
        var stuck = TimeSpan.FromMinutes(20);
        var owned = new HashSet<string>(StringComparer.Ordinal);
        var archives = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            List<WorkTask> tasks;
            Dictionary<string, (TimeSpan Pause, TimeSpan Archive)> times;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
                var page = await db.Tasks.AsNoTracking().OrderBy(x => x.Id).ToPageAsync(Operator, Permissions.TaskOperateDesktop, PageSize, cursor, ct);
                tasks = [.. page.Data];
                cursor = page.NextCursor;
                // When each of these tasks' teams pauses and archives: the team's own settings.
                var teamIds = tasks.Select(x => x.TeamId).Distinct().ToList();
                times = (await db.Teams.AsNoTracking().Where(x => teamIds.Contains(x.Id)).Select(x => new { x.Id, x.PauseAfterMinutes, x.ArchiveAfterDays }).ToListAsync(ct))
                    .ToDictionary(x => x.Id, x => (TimeSpan.FromMinutes(x.PauseAfterMinutes), TimeSpan.FromDays(x.ArchiveAfterDays)));
            }
            foreach (var task in tasks)
            {
                // An archived desktop has no machine: one still there under its name was left by a delete that
                // failed, and goes as a stray.
                if (task.State != DesktopState.Archived) owned.Add(task.Host ?? task.Disk ?? DesktopHosting.NameFor(task.Id));
                if (task.Archive is not null) archives.Add(task.Archive);
                var (pauseAfter, archiveAfter) = times.GetValueOrDefault(task.TeamId,
                    (TimeSpan.FromMinutes(Team.DefaultPauseAfterMinutes), TimeSpan.FromDays(Team.DefaultArchiveAfterDays)));
                if (task.IsIdle(now, pauseAfter))
                {
                    log.LogInformation("Pausing the desktop of task {TaskId}: nobody has used it for {Minutes} minutes.", task.Id, pauseAfter.TotalMinutes);
                    await ChangeAsync(task.Id, (_, current, at) =>
                    {
                        // Read again: someone may have used it since the page was read.
                        if (current.IsIdle(now, pauseAfter)) current.Stop(at);
                        return Task.CompletedTask;
                    }, ct);
                    await StopAsync(task.Id, ct);
                }
                else if (task.IsDueForArchive(now, archiveAfter) || task.State == DesktopState.Archiving)
                {
                    log.LogInformation("Archiving the desktop of task {TaskId}: it has been paused for {Days} days.", task.Id, archiveAfter.TotalDays);
                    await ArchiveAsync(task.Id, ct);
                    if (await ArchiveOfAsync(task.Id, ct) is { } made) archives.Add(made);
                }
                else if (task.State == DesktopState.Ready && task.Archive is not null)
                {
                    // Back from its archive, on a start whose job did not see the screen come up.
                    await ForgetArchiveAsync(task.Id, ct);
                }
                else if (task.State == DesktopState.Starting && now - task.StateChangedAt > startTimeout)
                {
                    log.LogWarning("The desktop of task {TaskId} never came up.", task.Id);
                    await ChangeAsync(task.Id, async (db, current, at) =>
                    {
                        if (current.State != DesktopState.Starting) return;
                        current.Fail("The desktop did not come up in time. Start it again.", at);
                        await EndRunAsync(db, current.Id, at, ct);
                    }, ct);
                    // A machine that came up without its agent is not left running.
                    if (task.Host is not null) await host.StopAsync(task.Host, ct);
                }
                else if (task.State == DesktopState.Stopping && now - task.StateChangedAt > stuck)
                {
                    await StopAsync(task.Id, ct);
                }
            }
        }
        while (cursor is not null);

        // Spares: one nobody on the team has wanted for a while goes, and so does one that never came up. Their
        // machines are owned while they are here.
        List<Spare> spares;
        await using (var scope = scopes.CreateAsyncScope())
        {
            spares = await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Spares.AsNoTracking().ToListAsync(ct);
        }
        foreach (var spare in spares)
        {
            var machine = spare.Host ?? DesktopHosting.NameFor(spare.Id);
            owned.Add(machine);
            var unwanted = now - spare.WantedAt > TimeSpan.FromMinutes(options.Spares.IdleMinutes);
            var neverCame = spare.State == SpareState.Starting && now - spare.CreatedAt > startTimeout;
            if (!unwanted && !neverCame) continue;
            log.LogInformation("Removing the spare {SpareId} of team {TeamId}: {Why}.", spare.Id, spare.TeamId, unwanted ? "nobody has wanted it for a while" : "it never came up");
            await RemoveSpareAsync(spare.Id, machine, ct);
        }
        // A spare's time left open by a spare that is gone: a task claimed it and then could not be saved.
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            var open = await db.DesktopRuns.Where(x => x.Purpose == MachinePurpose.Spare && x.EndedAt == null && !db.Spares.Any(spare => spare.Id == x.TaskId)).ToListAsync(ct);
            foreach (var run in open) run.End(now);
            if (open.Count > 0) await db.SaveChangesAsync(ct);
        }

        // Deleted tasks are not in the page (their nodes are inactive); a delete job that was lost is run again here.
        List<string> deleting;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            var before = now - stuck;
            deleting = await db.Tasks.AsNoTracking().Where(x => x.State == DesktopState.Deleting && x.StateChangedAt < before).Select(x => x.Id).Take(PageSize).ToListAsync(ct);
            foreach (var pending in await db.Tasks.AsNoTracking().Where(x => x.State == DesktopState.Deleting).Select(x => new { x.Id, x.Host, x.Archive }).ToListAsync(ct))
            {
                owned.Add(pending.Host ?? DesktopHosting.NameFor(pending.Id));
                if (pending.Archive is not null) archives.Add(pending.Archive);
            }
        }
        foreach (var id in deleting) await DeleteAsync(id, ct);

        // Saved setups: an attempt that took too long fails, a save nobody has started from for a month goes,
        // and so does any save no environment knows. A preparing machine is owned while its attempt lasts.
        await SweepSetupsAsync(owned, now, ct);

        // A machine no task owns is a stray once it has been there a while. A newer one may belong to a task
        // started after this sweep read the tasks, which can be minutes ago when it had desktops to stop.
        foreach (var stray in (await host.ListAsync(ct)).Where(machine =>
                     machine.Name.StartsWith("ws-", StringComparison.Ordinal) && !owned.Contains(machine.Name) && now - machine.CreatedAt > StrayAfter))
        {
            log.LogWarning("Deleting the machine {Machine}: no task owns it.", stray.Name);
            await host.DeleteAsync(stray.Name, ct);
        }

        // The same for archives: one no task names is a stray once it has been there a while. A newer one may
        // have been made for a task since this sweep read the tasks, or be one that is being made now.
        foreach (var stray in (await host.ListArchivesAsync(ct)).Where(archive =>
                     archive.Name.StartsWith(DesktopHosting.ArchivePrefix, StringComparison.Ordinal) && !archives.Contains(archive.Name) && now - archive.CreatedAt > StrayAfter))
        {
            log.LogWarning("Deleting the archive {Archive}: no task owns it.", stray.Name);
            await host.DeleteArchiveAsync(stray.Name, ct);
        }
    }

    async Task<string?> ArchiveOfAsync(string taskId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Tasks.AsNoTracking().Where(x => x.Id == taskId).Select(x => x.Archive).SingleOrDefaultAsync(ct);
    }

    async Task SweepSetupsAsync(HashSet<string> owned, DateTimeOffset now, CancellationToken ct)
    {
        List<EnvironmentSetup> setups;
        await using (var scope = scopes.CreateAsyncScope())
        {
            setups = await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Setups.AsNoTracking().ToListAsync(ct);
        }
        foreach (var setup in setups.Where(x => x.IsPreparing))
        {
            var machine = setup.RunHost ?? DesktopHosting.NameFor(setup.RunId!);
            owned.Add(machine);
            if (now - setup.RunStartedAt > TimeSpan.FromMinutes(options.PrepareMinutes))
            {
                log.LogWarning("Environment {EnvironmentId} took too long to prepare.", setup.EnvironmentId);
                await EndPreparingAsync(setup.EnvironmentId, setup.RunId!, $"Preparing did not finish in {options.PrepareMinutes} minutes.", machine, ct);
            }
        }
        foreach (var setup in setups.Where(x => x.Save is not null && now - (x.UsedAt ?? x.SavedAt ?? now) > TimeSpan.FromDays(options.Saves.UnusedDays)))
        {
            log.LogInformation("Removing the saved setup of environment {EnvironmentId}: nothing has started from it for {Days} days.", setup.EnvironmentId, options.Saves.UnusedDays);
            string? removed = null;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
                if (await db.Setups.SingleOrDefaultAsync(x => x.EnvironmentId == setup.EnvironmentId && x.Save == setup.Save, ct) is { } current)
                {
                    removed = current.Forget();
                    try
                    {
                        await db.SaveChangesAsync(ct);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        removed = null;
                    }
                }
            }
            if (removed is not null) await host.DeleteSaveAsync(removed, ct);
        }
        // A preparing machine's time left open by an attempt that is gone: its environment was deleted.
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            var open = await db.DesktopRuns.Where(x => x.Purpose == MachinePurpose.Prepare && x.EndedAt == null && !db.Setups.Any(setup => setup.RunId == x.TaskId)).ToListAsync(ct);
            foreach (var run in open) run.End(now);
            if (open.Count > 0) await db.SaveChangesAsync(ct);
        }

        // Read again just before the host is asked, so a save an attempt has just made is known by its name.
        HashSet<string> known;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var rows = await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Setups.AsNoTracking()
                .Where(x => x.Save != null || x.RunId != null).Select(x => new { x.Save, x.RunId }).ToListAsync(ct);
            known = [.. rows.SelectMany(x => new[] { x.Save, x.RunId is null ? null : EnvironmentSetup.SaveNameFor(x.RunId) }).OfType<string>()];
        }
        foreach (var stray in (await host.ListSavesAsync(ct)).Where(save => save.Name.StartsWith("wss-", StringComparison.Ordinal) && !known.Contains(save.Name)))
        {
            log.LogWarning("Deleting the saved setup {Save}: no environment knows it.", stray.Name);
            await host.DeleteSaveAsync(stray.Name, ct);
        }

        await PrepareWantedAsync(ct);
    }

    /// <summary>The task, when it is in the state the job was queued for and the operator may act on it.</summary>
    async Task<WorkTask?> OperableAsync(AsyncServiceScope scope, WorkstreamDbContext db, string taskId, DesktopState expected, CancellationToken ct)
    {
        var task = await db.Tasks.SingleOrDefaultAsync(x => x.Id == taskId, ct);
        if (task is null || task.State != expected) return null;
        if (await scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>().Allows(Operator, Permissions.TaskOperateDesktop, task.ResourceId)) return task;
        log.LogError("The desktop operator may not act on task {TaskId}.", taskId);
        return null;
    }

    /// <summary>Change the task in a scope of its own, when it is still there.</summary>
    async Task ChangeAsync(string taskId, Func<WorkstreamDbContext, WorkTask, DateTimeOffset, Task> change, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
        var task = await db.Tasks.SingleOrDefaultAsync(x => x.Id == taskId, ct);
        if (task is null) return;
        await change(db, task, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Write down a step of the task's start, on the task and on the run that is open.</summary>
    async Task StepAsync(string taskId, Action<WorkTask, DesktopRun?, DateTimeOffset> step, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
        var task = await db.Tasks.SingleOrDefaultAsync(x => x.Id == taskId, ct);
        if (task is null) return;
        var run = await db.DesktopRuns.Where(x => x.TaskId == taskId && x.EndedAt == null).OrderByDescending(x => x.StartedAt).FirstOrDefaultAsync(ct);
        step(task, run, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
    }

    async Task<bool> IsStartingAsync(string taskId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Tasks.AsNoTracking().AnyAsync(x => x.Id == taskId && x.State == DesktopState.Starting, ct);
    }

    /// <summary>A machine that finished being made after its task moved on: stop it, or delete it, as the task now says.</summary>
    async Task SettleAsync(string taskId, string machine, CancellationToken ct)
    {
        DesktopState? state;
        await using (var scope = scopes.CreateAsyncScope())
        {
            state = await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Tasks.AsNoTracking()
                .Where(x => x.Id == taskId).Select(x => (DesktopState?)x.State).SingleOrDefaultAsync(ct);
        }
        if (state is null or DesktopState.Deleting) await host.DeleteAsync(machine, ct);
        else await host.StopAsync(machine, ct);
    }

    static async Task EndRunAsync(WorkstreamDbContext db, string taskId, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var run in await db.DesktopRuns.Where(x => x.TaskId == taskId && x.EndedAt == null).ToListAsync(ct)) run.End(now);
    }

    /// <summary>Why a desktop did not start, in words a person can act on. Azure's own words are kept for the usual causes.</summary>
    static string WordsFor(Exception error) => error.Message switch
    {
        var message when message.Contains("quota", StringComparison.OrdinalIgnoreCase) || message.Contains("capacity", StringComparison.OrdinalIgnoreCase)
            || message.Contains("allocation", StringComparison.OrdinalIgnoreCase) => "There is no capacity for another desktop right now. Pause one, or try again later.",
        var message when message.Contains("no desktop host", StringComparison.OrdinalIgnoreCase) => error.Message,
        _ => "The desktop could not be started. Start it again; if it keeps failing, tell us.",
    };
}
