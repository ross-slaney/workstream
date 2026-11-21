using Hangfire;
using Microsoft.AspNetCore.DataProtection;
using SqlOS.Email.Interfaces;
using SqlOS.Fga.Interfaces;
using Workstream.Api.Desktops;
using Workstream.Api.Domain;
using Workstream.Api.Model;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api;

/// <summary>
/// A task as the list and the screen show it: what it is, who started it, and where its desktop is.
/// <see cref="Step"/> says how far a starting desktop has come: <c>machine</c> (being made or turned on),
/// <c>boot</c> (on and starting up) or <c>agent</c> (its screen can be watched; the agent has not joined yet).
/// </summary>
public sealed record TaskDto(
    string Id, string TeamId, string EnvironmentId, string Title, string CreatedBy, string? CreatedByName, DateTimeOffset CreatedAt,
    string State, string Step, string? Failure, string Size, bool AgentWorking, DateTimeOffset StateChangedAt);

public sealed record RenameTask(string Title);

/// <summary>A message a person sends. <see cref="Key"/> is a UUID the client makes, so a send tried again writes once.</summary>
public sealed record Say(string Text, string Key);

/// <summary>
/// One entry in the conversation. <see cref="Author"/> is <c>person</c> or <c>agent</c>; <see cref="Kind"/> is
/// <c>text</c> or <c>activity</c>; <see cref="Mine"/> says the caller wrote it. A person's message with no
/// name was typed at the desktop itself.
/// </summary>
public sealed record MessageDto(long Id, string Author, string? AuthorName, bool Mine, string Kind, string Text, DateTimeOffset CreatedAt);

/// <summary>
/// The conversation after a message, with the task as it is now and what the reader may do with it: one read
/// keeps a screen current, including a share that changed or ended.
/// </summary>
public sealed record ConversationDto(IReadOnlyList<MessageDto> Messages, TaskDto Task, TaskAccess Access);

/// <summary>Share a task: an email, a role (<c>watcher</c>, <c>prompter</c>, <c>driver</c>), and how long (<c>hour</c>, <c>day</c>, <c>week</c>, <c>never</c> to end).</summary>
public sealed record ShareTask(string Email, string Role, string? ExpiresIn);

/// <summary>The token from an invitation's link.</summary>
public sealed record AcceptTaskInvitation(string Token);

/// <summary>The task an invitation opened.</summary>
public sealed record AcceptedTaskInvitation(string TaskId);

public static partial class WorkstreamApi
{
    static void MapTasks(RouteGroupBuilder api)
    {
        api.MapGet("/teams/{teamId}/tasks", ListTasks).WithName("list_tasks").WithTags("Tasks").Produces<ListPage<TaskDto>>();
        api.MapPost("/teams/{teamId}/tasks", PostTask).WithName("start_task").WithTags("Tasks").Produces<TaskDto>(201).ProducesProblem(402).ProducesProblem(409);
        api.MapPost("/teams/{teamId}/spare", PostSpare).WithName("want_spare").WithTags("Tasks").Produces(204);
        api.MapGet("/tasks/{id}", GetTask).WithName("get_task").WithTags("Tasks").Produces<TaskDto>();
        api.MapPut("/tasks/{id}", PutTask).WithName("rename_task").WithTags("Tasks").Produces<TaskDto>();
        api.MapDelete("/tasks/{id}", DeleteTask).WithName("delete_task").WithTags("Tasks");
        api.MapPost("/tasks/{id}/stop", StopTask).WithName("stop_task").WithTags("Tasks").Produces<TaskDto>();
        api.MapPost("/tasks/{id}/start", StartDesktop).WithName("start_desktop").WithTags("Tasks").Produces<TaskDto>().ProducesProblem(402);
        api.MapPost("/tasks/{id}/interrupt", Interrupt).WithName("interrupt_agent").WithTags("Tasks");
        api.MapGet("/tasks/{id}/messages", ListMessages).WithName("list_messages").WithTags("Tasks").Produces<ConversationDto>();
        api.MapPost("/tasks/{id}/messages", SayMessage).WithName("say").WithTags("Tasks").Produces<MessageDto>(201).ProducesProblem(409);

        api.MapGet("/shared-tasks", ListSharedTasks).WithName("list_shared_tasks").WithTags("Sharing").Produces<ListPage<TaskDto>>();
        api.MapGet("/tasks/{id}/access", GetTaskAccess).WithName("list_task_access").WithTags("Sharing").Produces<TaskAccessList>().ProducesProblem(403);
        api.MapPost("/tasks/{id}/access", PostTaskShare).WithName("share_task").WithTags("Sharing").Produces<TaskShared>(201).ProducesProblem(400).ProducesProblem(403).ProducesProblem(409);
        api.MapDelete("/tasks/{id}/access/people/{userId}", DeleteTaskShare).WithName("revoke_task_share").WithTags("Sharing").ProducesProblem(403);
        api.MapDelete("/tasks/{id}/access/invitations/{invitationId}", DeleteTaskInvitation).WithName("withdraw_task_invitation").WithTags("Sharing").ProducesProblem(403);
        api.MapPost("/task-invitations/accept", PostAcceptTaskInvitation).WithName("accept_task_invitation").WithTags("Sharing").Produces<AcceptedTaskInvitation>().ProducesProblem(403);
    }

    static void MapDesktop(RouteGroupBuilder desktop)
    {
        desktop.MapGet("/setup", GetDesktopSetup).ExcludeFromDescription();
        desktop.MapPost("/setup", ReportSetup).ExcludeFromDescription();
        desktop.MapPost("/exchange", Exchange).ExcludeFromDescription();
    }

    static TaskDto ToDto(WorkTask x, IReadOnlyDictionary<string, string> names) => new(
        x.Id, x.TeamId, x.EnvironmentId, x.Title, x.CreatedBy, names.GetValueOrDefault(x.CreatedBy), x.CreatedAt,
        x.State.ToString().ToLowerInvariant(), x.Step.ToString().ToLowerInvariant(), x.Failure, x.Size, x.AgentWorking, x.StateChangedAt);

    static async Task<IResult> TaskResultAsync(Outcome<WorkTask> outcome, WorkstreamDbContext db, CancellationToken ct, bool created = false)
    {
        if (!outcome.IsOk) return ToResult(outcome);
        var task = outcome.Value!;
        var dto = ToDto(task, await NamesAsync(db, [task.CreatedBy], ct));
        return created ? Results.Created($"/api/tasks/{task.Id}", dto) : Results.Ok(dto);
    }

    static async Task<IResult> ListTasks(string teamId, string? cursor, int? pageSize, HttpContext http, WorkstreamDbContext db, CancellationToken ct)
    {
        var (page, error) = await AuthorizedPageAsync(Operations.Tasks(db, teamId), CallerOf(http).PrincipalId, Permissions.TaskView, pageSize, cursor, ct);
        if (error is not null) return error;
        var names = await NamesAsync(db, page!.Data.Select(x => x.CreatedBy), ct);
        return Results.Ok(new ListPage<TaskDto>([.. page.Data.Select(x => ToDto(x, names))], page.NextCursor));
    }

    static async Task<IResult> PostTask(string teamId, StartTask body, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IBackgroundJobClient jobs, DesktopOptions desktops, CancellationToken ct) =>
        await TaskResultAsync(await Operations.StartTaskAsync(CallerOf(http), teamId, body, db, fga, jobs, desktops, ct), db, ct, created: true);

    /// <summary>
    /// Someone is in the team's workspace: have a desktop running for their next task. 204 whether or not one
    /// was asked for, since there is nothing for the caller to do with the answer.
    /// </summary>
    static async Task<IResult> PostSpare(string teamId, WantSpare? body, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IBackgroundJobClient jobs, DesktopOptions desktops, TimeProvider clock, CancellationToken ct)
    {
        var outcome = await Operations.WantSpareAsync(CallerOf(http), teamId, body, db, fga, jobs, desktops, clock, ct);
        return outcome.IsOk ? Results.NoContent() : ToResult(outcome);
    }

    static async Task<IResult> GetTask(string id, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct) =>
        await TaskResultAsync(await Operations.ViewTaskAsync(CallerOf(http), id, db, fga, ct), db, ct);

    static async Task<IResult> PutTask(string id, RenameTask body, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct) =>
        await TaskResultAsync(await Operations.RenameTaskAsync(CallerOf(http), id, body.Title, db, fga, ct), db, ct);

    static async Task<IResult> StopTask(string id, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IBackgroundJobClient jobs, CancellationToken ct) =>
        await TaskResultAsync(await Operations.StopTaskAsync(CallerOf(http), id, db, fga, jobs, ct), db, ct);

    static async Task<IResult> StartDesktop(string id, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IBackgroundJobClient jobs, CancellationToken ct) =>
        await TaskResultAsync(await Operations.StartDesktopAsync(CallerOf(http), id, db, fga, jobs, ct), db, ct);

    static async Task<IResult> DeleteTask(string id, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IBackgroundJobClient jobs, CancellationToken ct)
    {
        var outcome = await Operations.DeleteTaskAsync(CallerOf(http), id, db, fga, jobs, ct);
        return outcome.IsOk ? Results.NoContent() : ToResult(outcome);
    }

    static async Task<IResult> Interrupt(string id, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var outcome = await Operations.InterruptAsync(CallerOf(http), id, db, fga, ct);
        return outcome.IsOk ? Results.NoContent() : ToResult(outcome);
    }

    static MessageDto ToDto(TaskMessage x, IReadOnlyDictionary<string, string> names, string callerId) => new(
        x.Id, x.Author == MessageAuthor.Agent ? "agent" : "person", names.GetValueOrDefault(x.AuthorId), x.AuthorId == callerId,
        x.Kind == MessageKind.Activity ? "activity" : "text", x.Text, x.CreatedAt);

    /// <summary>
    /// The conversation after <paramref name="after"/>, oldest first, with the task as it is now. A screen
    /// reads this every second or two, passing the id of the last message it has.
    /// </summary>
    static async Task<IResult> ListMessages(string id, long? after, int? pageSize, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var caller = CallerOf(http);
        var seen = await Operations.ViewTaskAsync(caller, id, db, fga, ct);
        if (!seen.IsOk) return ToResult(seen);
        var (page, error) = await AuthorizedPageAsync(Operations.Messages(db, id, Math.Max(0, after ?? 0)), caller.PrincipalId, Permissions.TaskView, pageSize ?? 200, null, ct);
        if (error is not null) return error;
        var names = await NamesAsync(db, page!.Data.Select(x => x.AuthorId).Append(seen.Value!.CreatedBy), ct);
        return Results.Ok(new ConversationDto([.. page.Data.Select(x => ToDto(x, names, caller.PrincipalId))], ToDto(seen.Value!, names),
            await Operations.AccessOfAsync(caller, seen.Value!, fga)));
    }

    /// <summary>
    /// The tasks shared with the caller: an authorized page over every team's tasks but their own teams', so
    /// what it finds is what a task grant reaches.
    /// </summary>
    static async Task<IResult> ListSharedTasks(string? cursor, int? pageSize, HttpContext http, WorkstreamDbContext db, CancellationToken ct)
    {
        var caller = CallerOf(http);
        var teams = await Operations.TeamIdsOfAsync(db, caller.PrincipalId, ct);
        var (page, error) = await AuthorizedPageAsync(Operations.SharedTasks(db, teams), caller.PrincipalId, Permissions.TaskView, pageSize, cursor, ct);
        if (error is not null) return error;
        var names = await NamesAsync(db, page!.Data.Select(x => x.CreatedBy), ct);
        return Results.Ok(new ListPage<TaskDto>([.. page.Data.Select(x => ToDto(x, names))], page.NextCursor));
    }

    static async Task<IResult> GetTaskAccess(string id, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var outcome = await Operations.AccessListAsync(CallerOf(http), id, db, fga, ct);
        return outcome.IsOk ? Results.Ok(outcome.Value) : ToResult(outcome);
    }

    static async Task<IResult> PostTaskShare(string id, ShareTask body, HttpContext http, WorkstreamApp app, WorkstreamDbContext db, ISqlOSFgaAuthService fga, ISqlOSEmailSender sender, ISqlOSTransactionalEmailService mail, ILoggerFactory logs, CancellationToken ct)
    {
        var outcome = await Operations.ShareTaskAsync(CallerOf(http), id, body.Email, body.Role, body.ExpiresIn, app, db, fga, sender, mail, logs.CreateLogger("Workstream.Sharing"), ct);
        return outcome.IsOk ? Results.Created($"/api/tasks/{id}/access", outcome.Value) : ToResult(outcome);
    }

    static async Task<IResult> DeleteTaskShare(string id, string userId, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var outcome = await Operations.RevokeTaskShareAsync(CallerOf(http), id, userId, db, fga, ct);
        return outcome.IsOk ? Results.NoContent() : ToResult(outcome);
    }

    static async Task<IResult> DeleteTaskInvitation(string id, string invitationId, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var outcome = await Operations.WithdrawTaskInvitationAsync(CallerOf(http), id, invitationId, db, fga, ct);
        return outcome.IsOk ? Results.NoContent() : ToResult(outcome);
    }

    static async Task<IResult> PostAcceptTaskInvitation(AcceptTaskInvitation body, HttpContext http, WorkstreamDbContext db, CancellationToken ct)
    {
        var outcome = await Operations.AcceptTaskInvitationAsync(CallerOf(http), body.Token, db, ct);
        return outcome.IsOk ? Results.Ok(new AcceptedTaskInvitation(outcome.Value!)) : ToResult(outcome);
    }

    static async Task<IResult> SayMessage(string id, Say body, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var caller = CallerOf(http);
        var outcome = await Operations.SayAsync(caller, id, body.Text, body.Key, db, fga, ct);
        if (!outcome.IsOk) return ToResult(outcome);
        return Results.Created($"/api/tasks/{id}/messages", ToDto(outcome.Value!, await NamesAsync(db, [caller.PrincipalId], ct), caller.PrincipalId));
    }

    static async Task<IResult> GetDesktopSetup(HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IDataProtectionProvider protection, DesktopOptions desktops, ModelOptions model, CancellationToken ct)
    {
        var outcome = await Operations.DesktopSetupAsync(Caller.Desktop(http), db, fga, protection, desktops, model, ct);
        return outcome.IsOk ? Results.Ok(outcome.Value) : ToResult(outcome);
    }

    /// <summary>The preparing desktop says how the environment's setup is going.</summary>
    static async Task<IResult> ReportSetup(SetupReport body, HttpContext http, WorkstreamDbContext db, IBackgroundJobClient jobs, CancellationToken ct)
    {
        var outcome = await Operations.ReportSetupAsync(Caller.Desktop(http), body, db, jobs, ct);
        return outcome.IsOk ? Results.NoContent() : ToResult(outcome);
    }

    static async Task<IResult> Exchange(DesktopExchange body, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IBackgroundJobClient jobs, CancellationToken ct)
    {
        var outcome = await Operations.ExchangeAsync(Caller.Desktop(http), body, db, fga, jobs, ct);
        return outcome.IsOk ? Results.Ok(outcome.Value) : ToResult(outcome);
    }
}
