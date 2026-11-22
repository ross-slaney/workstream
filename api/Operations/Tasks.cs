using Hangfire;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SqlOS.Extensions;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using Workstream.Api.Desktops;
using Workstream.Api.Domain;
using Workstream.Api.Email;
using Workstream.Api.Model;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api;

/// <summary>A task as a person starts it: the environment its desktop comes from, and what they want done.</summary>
public sealed record StartTask(string Id, string EnvironmentId, string? Title, string? Prompt, string? PromptKey);

/// <summary>What the agent said or did since the desktop's last exchange, and where the desktop is in the conversation.</summary>
public sealed record DesktopExchange(bool Working, long After, IReadOnlyList<DesktopMessage>? Messages);

/// <summary>A message from the desktop: the agent's (<c>agent</c>) or someone typing in the agent's window on the desktop (<c>person</c>).</summary>
public sealed record DesktopMessage(string Key, string Author, string Kind, string Text);

public sealed record DesktopPrompt(long Id, string Text);

/// <summary>What people wrote since <see cref="DesktopExchange.After"/>, and how many times they asked the agent to stop.</summary>
public sealed record DesktopReply(IReadOnlyList<DesktopPrompt> Prompts, int Interrupts, string Title);

public sealed record DesktopRepository(string Url, string? Branch);

/// <summary>Where the agent calls models, and which: every call goes through this API with the desktop's key.</summary>
public sealed record DesktopModel(string BaseUrl, string Main, string Sonnet, string Small);

/// <summary>
/// Everything a desktop needs to set itself up, read with its key each time it starts. A desktop that
/// <see cref="Prepare"/>s is doing an environment's setup to be saved: it has no task (<see cref="TaskId"/> is
/// empty), no instructions and no model, and <see cref="Title"/> is the environment's name.
/// <see cref="SetupMinutes"/> is how long the setup script may run before the desktop stops it.
/// </summary>
public sealed record DesktopSetup(
    string TaskId, string Title, IReadOnlyList<string> Apps, DesktopRepository? Repository, string? SetupScript,
    IReadOnlyDictionary<string, string> Variables, string? Instructions, DesktopModel? Model, bool Prepare = false, int SetupMinutes = 45);

public static partial class Operations
{
    const int MaxExchangeMessages = 100;

    /// <summary>A team's tasks, newest first: the query its list pages with <c>TASK_VIEW</c>.</summary>
    public static IQueryable<WorkTask> Tasks(WorkstreamDbContext db, string teamId) =>
        db.Tasks.AsNoTracking().Where(x => x.TeamId == teamId && x.State != DesktopState.Deleting)
            .OrderByDescending(x => x.CreatedAt);

    /// <summary>A task's conversation after a message, oldest first: the query pages with <c>TASK_VIEW</c>.</summary>
    public static IQueryable<TaskMessage> Messages(WorkstreamDbContext db, string taskId, long after) =>
        db.TaskMessages.AsNoTracking().Where(x => x.TaskId == taskId && x.Id > after).OrderBy(x => x.Id);

    public static async Task<Outcome<WorkTask>> ViewTaskAsync(Caller caller, string id, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var task = await db.Tasks.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (task is null || task.State == DesktopState.Deleting || !await fga.Allows(caller.PrincipalId, Permissions.TaskView, task.ResourceId)) return Outcome<WorkTask>.NotFound();
        return Outcome<WorkTask>.Ok(task);
    }

    /// <summary>The task, when the caller may see it and also holds <paramref name="permission"/> on it: 404, then 403.</summary>
    internal static async Task<Outcome<WorkTask>> TaskWithAsync(Caller caller, string id, string permission, string refusal, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var seen = await ViewTaskAsync(caller, id, db, fga, ct);
        if (!seen.IsOk) return seen;
        return await fga.Allows(caller.PrincipalId, permission, seen.Value!.ResourceId) ? seen : Outcome<WorkTask>.Forbidden(refusal);
    }

    static Task<int> RunningDesktopsAsync(WorkstreamDbContext db, string teamId, CancellationToken ct) =>
        db.Tasks.CountAsync(x => x.TeamId == teamId && (x.State == DesktopState.Starting || x.State == DesktopState.Ready || x.State == DesktopState.Stopping), ct);

    static string DesktopLimitWords(int limit) =>
        $"Your team can run {limit} {(limit == 1 ? "desktop" : "desktops")} at once. Stop one to start another.";

    /// <summary>
    /// Start a task: the row, its first message, and a desktop on its way. The check is on the team (a create
    /// checks the parent) and on the environment the desktop comes from.
    /// </summary>
    public static async Task<Outcome<WorkTask>> StartTaskAsync(Caller caller, string teamId, StartTask body,
        WorkstreamDbContext db, ISqlOSFgaAuthService fga, IBackgroundJobClient jobs, DesktopOptions options, CancellationToken ct)
    {
        var allowed = await TeamWithAsync(caller, teamId, Permissions.TeamCreateTask, "You can't start tasks in this team.", db, fga, ct);
        if (!allowed.IsOk) return allowed.As<WorkTask>();
        var team = allowed.Value!;
        if (await db.Tasks.AnyAsync(x => x.Id == body.Id, ct)) return Outcome<WorkTask>.Exists("That task already exists.");
        var environment = await db.Environments.SingleOrDefaultAsync(x => x.Id == body.EnvironmentId && x.TeamId == teamId, ct);
        if (environment is null || !await fga.Allows(caller.PrincipalId, Permissions.EnvironmentView, environment.ResourceId))
        {
            return Outcome<WorkTask>.Invalid("Choose one of the team's environments.");
        }
        if (await RunningDesktopsAsync(db, teamId, ct) >= team.MaxRunningDesktops) return Outcome<WorkTask>.LimitReached(DesktopLimitWords(team.MaxRunningDesktops));
        try
        {
            var now = DateTimeOffset.UtcNow;
            var task = WorkTask.Start(body.Id, team, environment, string.IsNullOrWhiteSpace(body.Title) ? WorkTask.TitleFrom(body.Prompt) : body.Title, caller.PrincipalId, now);
            var prompt = string.IsNullOrWhiteSpace(body.Prompt)
                ? null
                : TaskMessage.Write(task, caller.PrincipalId, MessageAuthor.Person, MessageKind.Text, body.Prompt, body.PromptKey ?? Guid.NewGuid().ToString("D"), now);

            // The team's spare, when it has one that is up and fits: the task starts on a desktop that is
            // already running. Taking it and making the task are one transaction, so a machine is never
            // between owners where the sweep could see it as nobody's.
            // What a new machine for the task would start from: the environment's saved setup when it is good
            // for these settings and these desktops, otherwise the image. The start job decides it again.
            var setup = await db.Setups.AsNoTracking().SingleOrDefaultAsync(x => x.EnvironmentId == environment.Id, ct);
            var save = options.Saves.Enabled && setup?.Serves(environment, options.ImageVersion) == true ? setup.Save : null;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var spare = await TakeSpareAsync(db, teamId, environment, save, options, now, ct);
            db.Tasks.Add(task);
            var run = DesktopRun.Begin(task, now, spare is null ? StartSource.Image : StartSource.Spare);
            db.DesktopRuns.Add(run);
            if (prompt is not null) db.TaskMessages.Add(prompt);
            if (spare is not null)
            {
                task.TakeSpare(spare, now);
                run.DisplayUp(now);
                // What makes it this task's desktop: its account works on the task from now on. Its key is the one it has.
                await DesktopKeys.GrantAsync(db, spare.Id, task, ct);
                foreach (var spareRun in await db.DesktopRuns.Where(x => x.TaskId == spare.Id && x.EndedAt == null).ToListAsync(ct)) spareRun.End(now);
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            if (spare is null) jobs.Enqueue<DesktopJobs>(job => job.StartAsync(task.Id, CancellationToken.None));
            // The next task gets the same: another spare for the team, when the limits allow.
            else jobs.Enqueue<DesktopJobs>(job => job.RefillSpareAsync(teamId, environment.Id, CancellationToken.None));
            return Outcome<WorkTask>.Ok(task);
        }
        catch (DomainRuleException rule)
        {
            return Outcome<WorkTask>.Invalid(rule);
        }
    }

    public static async Task<Outcome<WorkTask>> RenameTaskAsync(Caller caller, string id, string? title, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var allowed = await TaskWithAsync(caller, id, Permissions.TaskManage, "You can't rename this task.", db, fga, ct);
        if (!allowed.IsOk) return allowed;
        try
        {
            allowed.Value!.Rename(title);
            await db.SaveChangesAsync(ct);
            return allowed;
        }
        catch (DomainRuleException rule)
        {
            return Outcome<WorkTask>.Invalid(rule);
        }
    }

    /// <summary>Stop the desktop: the machine is released, its disk kept.</summary>
    public static async Task<Outcome<WorkTask>> StopTaskAsync(Caller caller, string id, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IBackgroundJobClient jobs, CancellationToken ct)
    {
        var allowed = await TaskWithAsync(caller, id, Permissions.TaskManage, "You can't stop this task's desktop.", db, fga, ct);
        if (!allowed.IsOk) return allowed;
        try
        {
            allowed.Value!.Stop(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct);
            jobs.Enqueue<DesktopJobs>(job => job.StopAsync(id, CancellationToken.None));
            return allowed;
        }
        catch (DomainRuleException rule)
        {
            return Outcome<WorkTask>.Invalid(rule);
        }
    }

    /// <summary>Start a stopped desktop again: the same machine and disk, within the team's limit.</summary>
    public static async Task<Outcome<WorkTask>> StartDesktopAsync(Caller caller, string id, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IBackgroundJobClient jobs, CancellationToken ct)
    {
        var allowed = await TaskWithAsync(caller, id, Permissions.TaskManage, "You can't start this task's desktop.", db, fga, ct);
        if (!allowed.IsOk) return allowed;
        var task = allowed.Value!;
        var team = await db.Teams.AsNoTracking().SingleAsync(x => x.Id == task.TeamId, ct);
        if (await RunningDesktopsAsync(db, task.TeamId, ct) >= team.MaxRunningDesktops) return Outcome<WorkTask>.LimitReached(DesktopLimitWords(team.MaxRunningDesktops));
        try
        {
            var now = DateTimeOffset.UtcNow;
            task.StartAgain(now);
            db.DesktopRuns.Add(DesktopRun.Begin(task, now));
            await db.SaveChangesAsync(ct);
            jobs.Enqueue<DesktopJobs>(job => job.StartAsync(id, CancellationToken.None));
            return allowed;
        }
        catch (DomainRuleException rule)
        {
            return Outcome<WorkTask>.Invalid(rule);
        }
    }

    /// <summary>Delete the task: it leaves every list now; the machine, the conversation and the node follow.</summary>
    public static async Task<Outcome<bool>> DeleteTaskAsync(Caller caller, string id, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IBackgroundJobClient jobs, CancellationToken ct)
    {
        var allowed = await TaskWithAsync(caller, id, Permissions.TaskManage, "You can't delete this task.", db, fga, ct);
        if (!allowed.IsOk) return allowed.As<bool>();
        allowed.Value!.Delete(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        jobs.Enqueue<DesktopJobs>(job => job.DeleteAsync(id, CancellationToken.None));
        return Outcome<bool>.Ok(true);
    }

    /// <summary>Ask the agent to stop what it is doing. The desktop sees the count change at its next exchange.</summary>
    public static async Task<Outcome<bool>> InterruptAsync(Caller caller, string id, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var allowed = await TaskWithAsync(caller, id, Permissions.TaskPrompt, "You can watch this task but not stop its agent.", db, fga, ct);
        if (!allowed.IsOk) return allowed.As<bool>();
        allowed.Value!.Interrupt();
        await db.SaveChangesAsync(ct);
        return Outcome<bool>.Ok(true);
    }

    /// <summary>A person writes in the conversation. A send tried again answers that it already exists.</summary>
    public static async Task<Outcome<TaskMessage>> SayAsync(Caller caller, string taskId, string? text, string? clientKey, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var allowed = await TaskWithAsync(caller, taskId, Permissions.TaskPrompt, "You can watch this task but not write to its agent.", db, fga, ct);
        if (!allowed.IsOk) return allowed.As<TaskMessage>();
        var task = allowed.Value!;
        if (clientKey is not null && await db.TaskMessages.AnyAsync(x => x.ResourceId == task.ResourceId && x.ClientKey == clientKey, ct))
        {
            return Outcome<TaskMessage>.Exists("That message was already sent.");
        }
        try
        {
            var now = DateTimeOffset.UtcNow;
            var message = TaskMessage.Write(task, caller.PrincipalId, MessageAuthor.Person, MessageKind.Text, text, clientKey ?? "", now);
            db.TaskMessages.Add(message);
            task.Active(now);
            await db.SaveChangesAsync(ct);
            return Outcome<TaskMessage>.Ok(message);
        }
        catch (DomainRuleException rule)
        {
            return Outcome<TaskMessage>.Invalid(rule);
        }
    }

    /// <summary>
    /// The task this desktop works on: the one task its service account holds <c>task_desktop</c> on, read as
    /// its own page of the tasks. A spare holds it on none, and neither does the desktop of a deleted task,
    /// whose node is inactive.
    /// </summary>
    public static async Task<WorkTask?> DesktopTaskAsync(Caller desktop, WorkstreamDbContext db, CancellationToken ct, bool tracked = true)
    {
        var tasks = tracked ? db.Tasks : db.Tasks.AsNoTracking();
        var page = await tasks.OrderBy(x => x.Id).ToPageAsync(desktop.PrincipalId, Permissions.TaskRunAgent, 1, null, ct);
        return page.Data.FirstOrDefault(x => x.State != DesktopState.Deleting);
    }

    /// <summary>
    /// A desktop with no task asked the API something: it is a spare waiting to be given one, and now known to
    /// be up. Written at most every ten seconds, since a waiting spare asks twice a second.
    /// </summary>
    static async Task SpareAskedAsync(Caller desktop, WorkstreamDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var spare = await db.Spares.SingleOrDefaultAsync(x => x.Id == desktop.DesktopId, ct);
        if (spare is null || spare.State == SpareState.Ready && spare.SeenAt is { } seen && now - seen < TimeSpan.FromSeconds(10)) return;
        spare.Seen(now);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>What a desktop sets itself up from: its environment, with the variables readable, and where its agent calls models.</summary>
    public static async Task<Outcome<DesktopSetup>> DesktopSetupAsync(Caller desktop, WorkstreamDbContext db, ISqlOSFgaAuthService fga,
        IDataProtectionProvider protection, DesktopOptions desktops, ModelOptions model, CancellationToken ct)
    {
        if (await DesktopTaskAsync(desktop, db, ct, tracked: false) is not { } task)
        {
            if (await PreparingAsync(desktop, db, ct) is { } preparing) return Outcome<DesktopSetup>.Ok(PrepareSetupOf(preparing.Environment, protection, desktops));
            await SpareAskedAsync(desktop, db, ct);
            return Outcome<DesktopSetup>.NotFound();
        }
        var environment = await db.Environments.AsNoTracking().SingleAsync(x => x.Id == task.EnvironmentId, ct);
        return Outcome<DesktopSetup>.Ok(new DesktopSetup(
            task.Id, task.Title, environment.Apps,
            environment.RepositoryUrl is { } url ? new DesktopRepository(url, environment.RepositoryBranch) : null,
            environment.SetupScript, VariablesOf(environment, protection), environment.Instructions,
            new DesktopModel($"{desktops.ApiUrl.TrimEnd('/')}{ModelGateway.Path}", model.Main, model.Sonnet, model.Small), SetupMinutes: desktops.SetupMinutes));
    }

    /// <summary>
    /// The desktop's exchange, about once a second: it hands over what the agent said and did, says whether the
    /// agent is working, and takes what people wrote since the message it last read. The first exchange is what
    /// makes a starting desktop ready. An exchange that ends the agent's turn queues the email to the task's
    /// people (<see cref="TaskEmails"/>), which decides whether anyone hears about it.
    /// </summary>
    public static async Task<Outcome<DesktopReply>> ExchangeAsync(Caller desktop, DesktopExchange body, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IBackgroundJobClient jobs, CancellationToken ct)
    {
        if (await DesktopTaskAsync(desktop, db, ct) is not { } task)
        {
            await SpareAskedAsync(desktop, db, ct);
            return Outcome<DesktopReply>.NotFound();
        }
        var now = DateTimeOffset.UtcNow;
        var incoming = (body.Messages ?? []).Take(MaxExchangeMessages).ToList();
        if (incoming.Count > 0)
        {
            var keys = incoming.Select(x => x.Key).ToList();
            var known = await db.TaskMessages.AsNoTracking().Where(x => x.ResourceId == task.ResourceId && keys.Contains(x.ClientKey)).Select(x => x.ClientKey).ToListAsync(ct);
            foreach (var message in incoming.Where(x => !known.Contains(x.Key)).DistinctBy(x => x.Key))
            {
                try
                {
                    db.TaskMessages.Add(TaskMessage.Write(task, desktop.PrincipalId,
                        message.Author == "person" ? MessageAuthor.Person : MessageAuthor.Agent,
                        message.Kind == "activity" ? MessageKind.Activity : MessageKind.Text, message.Text, message.Key, now));
                }
                catch (DomainRuleException)
                {
                    // An empty or keyless message from the desktop is dropped: the exchange goes on.
                }
            }
            task.Active(now);
        }
        var turnEnded = task.EndsTurn(body.Working);
        if (task.DesktopSeen(body.Working, now))
        {
            // The first exchange of this start: how long the agent took is kept with the run.
            foreach (var run in await db.DesktopRuns.Where(x => x.TaskId == task.Id && x.EndedAt == null).ToListAsync(ct)) run.Ready(now);
        }
        await db.SaveChangesAsync(ct);
        if (turnEnded)
        {
            var taskId = task.Id;
            jobs.Schedule<TaskEmails>(job => job.SendAsync(taskId, CancellationToken.None), TaskEmails.After);
        }

        // What people wrote since: the desktop's own page of the conversation, read as its service account.
        var after = Math.Max(0, body.After);
        var prompts = await db.TaskMessages.AsNoTracking()
            .Where(x => x.TaskId == task.Id && x.Id > after && x.Author == MessageAuthor.Person && x.AuthorId != desktop.PrincipalId)
            .OrderBy(x => x.Id)
            .ToPageAsync(desktop.PrincipalId, Permissions.TaskRunAgent, 20, null, ct);
        return Outcome<DesktopReply>.Ok(new DesktopReply([.. prompts.Data.Select(x => new DesktopPrompt(x.Id, x.Text))], task.Interrupts, task.Title));
    }
}
