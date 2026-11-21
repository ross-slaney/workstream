using Hangfire;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using Workstream.Api.Domain;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api.Desktops;

/// <summary>
/// Everything that happens to a desktop outside a request: making and starting its machine and following it
/// until its screen can be watched, stopping it, deleting it, and the sweep that stops idle desktops and
/// removes strays. Each job is an operation run by a
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
                // The key is made here and handed to the machine; nothing keeps it.
                var key = await DesktopKeys.IssueAsync(db, scope.ServiceProvider.GetRequiredService<SqlOSCryptoService>(), task, ct);
                await db.SaveChangesAsync(ct);
                spec = new DesktopSpec(DesktopHosting.NameFor(task.Id), task.Id, task.TeamId, task.Size, options.ApiUrl, key);
            }
        }
        var machine = spec?.Name ?? existing!;

        // A desktop that failed costs nothing: whatever part of a new machine was made is removed, and a
        // machine that would not start again is released. Starting the task again begins from there.
        async Task FailAsync(Exception error)
        {
            log.LogError(error, "The desktop of task {TaskId} did not start.", taskId);
            await ChangeAsync(taskId, async (db, task, now) =>
            {
                if (task.State != DesktopState.Starting) return;
                task.Fail(WordsFor(error), now);
                await EndRunAsync(db, task.Id, now, ct);
            }, ct);
            try
            {
                if (spec is not null) await host.DeleteAsync(machine, ct);
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

        // The host has only taken the request. Ask the display every quarter second, since its answer is the
        // moment there is a screen to watch, and the host every two seconds, for when the machine is on and
        // in case it could not be started.
        var began = clock.GetUtcNow();
        var asked = DateTimeOffset.MinValue;
        var on = false;
        while (clock.GetUtcNow() - began < FollowFor)
        {
            if (await host.DisplayAnswersAsync(place, ct))
            {
                await StepAsync(taskId, (task, run, now) =>
                {
                    task.DisplayUp(now);
                    run?.DisplayUp(now);
                }, ct);
                return;
            }
            if (clock.GetUtcNow() - asked >= AskHostEvery)
            {
                asked = clock.GetUtcNow();
                var state = new MachineState(on);
                try
                {
                    state = await host.StateAsync(place.Host, ct);
                }
                catch (Exception error) when (error is HttpRequestException or InvalidOperationException)
                {
                    log.LogWarning(error, "The host did not say how the machine of task {TaskId} is doing.", taskId);
                }
                if (state.Failure is not null)
                {
                    await FailAsync(new InvalidOperationException(state.Failure));
                    return;
                }
                if (state.On && !on)
                {
                    on = true;
                    await StepAsync(taskId, (task, run, now) =>
                    {
                        task.MachineOn(now);
                        run?.MachineOn(now);
                    }, ct);
                }
                if (!await IsStartingAsync(taskId, ct)) return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
        log.LogWarning("The display of task {TaskId} did not answer in {Minutes} minutes.", taskId, FollowFor.TotalMinutes);
    }

    /// <summary>Release the machine of a task a person, or the sweep, asked to stop.</summary>
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
        if (machine is not null) await host.StopAsync(machine, ct);
        await ChangeAsync(taskId, async (db, task, now) =>
        {
            if (task.State != DesktopState.Stopping) return;
            task.Stopped(now);
            await EndRunAsync(db, task.Id, now, ct);
        }, ct);
    }

    /// <summary>Remove a deleted task for good: its machine, its key, its conversation, then the row and its node.</summary>
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
            await host.DeleteAsync(task.Host ?? DesktopHosting.NameFor(taskId), ct);
        }
        // The conversation goes in pieces, tracked, so SqlOS's page entries go with the rows.
        while (true)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            var messages = await db.TaskMessages.Where(x => x.TaskId == taskId).OrderBy(x => x.Id).Take(500).ToListAsync(ct);
            if (messages.Count == 0) break;
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
            await DesktopKeys.RetireAsync(db, taskId, ct);
            db.Tasks.Remove(task);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Every five minutes: stop the desktops nobody is using, fail the ones that never came up, finish what a
    /// lost job left half done, and delete machines no task owns.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 240)]
    [AutomaticRetry(Attempts = 0)]
    public async Task SweepAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var idle = TimeSpan.FromMinutes(options.IdleMinutes);
        var startTimeout = TimeSpan.FromMinutes(options.StartTimeoutMinutes);
        var stuck = TimeSpan.FromMinutes(20);
        var owned = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            List<WorkTask> tasks;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
                var page = await db.Tasks.AsNoTracking().OrderBy(x => x.Id).ToPageAsync(Operator, Permissions.TaskOperateDesktop, PageSize, cursor, ct);
                tasks = [.. page.Data];
                cursor = page.NextCursor;
            }
            foreach (var task in tasks)
            {
                owned.Add(task.Host ?? DesktopHosting.NameFor(task.Id));
                if (task.State == DesktopState.Ready && !task.AgentWorking && now - task.ActiveAt > idle)
                {
                    log.LogInformation("Stopping the idle desktop of task {TaskId}.", task.Id);
                    await ChangeAsync(task.Id, (_, current, at) =>
                    {
                        if (current.State == DesktopState.Ready) current.Stop(at);
                        return Task.CompletedTask;
                    }, ct);
                    await StopAsync(task.Id, ct);
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

        // Deleted tasks are not in the page (their nodes are inactive); a delete job that was lost is run again here.
        List<string> deleting;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            var before = now - stuck;
            deleting = await db.Tasks.AsNoTracking().Where(x => x.State == DesktopState.Deleting && x.StateChangedAt < before).Select(x => x.Id).Take(PageSize).ToListAsync(ct);
            foreach (var pending in await db.Tasks.AsNoTracking().Where(x => x.State == DesktopState.Deleting).Select(x => new { x.Id, x.Host }).ToListAsync(ct))
            {
                owned.Add(pending.Host ?? DesktopHosting.NameFor(pending.Id));
            }
        }
        foreach (var id in deleting) await DeleteAsync(id, ct);

        // A machine no task owns is a stray once it has been there a while. A newer one may belong to a task
        // started after this sweep read the tasks, which can be minutes ago when it had desktops to stop.
        foreach (var stray in (await host.ListAsync(ct)).Where(machine =>
                     machine.Name.StartsWith("ws-", StringComparison.Ordinal) && !owned.Contains(machine.Name) && now - machine.CreatedAt > StrayAfter))
        {
            log.LogWarning("Deleting the machine {Machine}: no task owns it.", stray.Name);
            await host.DeleteAsync(stray.Name, ct);
        }
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
            || message.Contains("allocation", StringComparison.OrdinalIgnoreCase) => "There is no capacity for another desktop right now. Stop one, or try again later.",
        var message when message.Contains("no desktop host", StringComparison.OrdinalIgnoreCase) => error.Message,
        _ => "The desktop could not be started. Start it again; if it keeps failing, tell us.",
    };
}
