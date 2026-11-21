using Hangfire;
using Microsoft.AspNetCore.DataProtection;
using SqlOS.Fga.Interfaces;
using Workstream.Api.Desktops;
using Workstream.Api.Domain;
using Workstream.Api.Model;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api;

/// <summary>A task as the list and the screen show it: what it is, who started it, and where its desktop is.</summary>
public sealed record TaskDto(
    string Id, string TeamId, string EnvironmentId, string Title, string CreatedBy, string? CreatedByName, DateTimeOffset CreatedAt,
    string State, string? Failure, string Size, bool AgentWorking, DateTimeOffset StateChangedAt);

public sealed record RenameTask(string Title);

/// <summary>A message a person sends. <see cref="Key"/> is a UUID the client makes, so a send tried again writes once.</summary>
public sealed record Say(string Text, string Key);

/// <summary>
/// One entry in the conversation. <see cref="Author"/> is <c>person</c> or <c>agent</c>; <see cref="Kind"/> is
/// <c>text</c> or <c>activity</c>; <see cref="Mine"/> says the caller wrote it. A person's message with no
/// name was typed at the desktop itself.
/// </summary>
public sealed record MessageDto(long Id, string Author, string? AuthorName, bool Mine, string Kind, string Text, DateTimeOffset CreatedAt);

/// <summary>The conversation after a message, with the task as it is now: one read keeps a screen current.</summary>
public sealed record ConversationDto(IReadOnlyList<MessageDto> Messages, TaskDto Task);

public static partial class WorkstreamApi
{
    static void MapTasks(RouteGroupBuilder api)
    {
        api.MapGet("/teams/{teamId}/tasks", ListTasks).WithName("list_tasks").WithTags("Tasks").Produces<ListPage<TaskDto>>();
        api.MapPost("/teams/{teamId}/tasks", PostTask).WithName("start_task").WithTags("Tasks").Produces<TaskDto>(201).ProducesProblem(402).ProducesProblem(409);
        api.MapGet("/tasks/{id}", GetTask).WithName("get_task").WithTags("Tasks").Produces<TaskDto>();
        api.MapPut("/tasks/{id}", PutTask).WithName("rename_task").WithTags("Tasks").Produces<TaskDto>();
        api.MapDelete("/tasks/{id}", DeleteTask).WithName("delete_task").WithTags("Tasks");
        api.MapPost("/tasks/{id}/stop", StopTask).WithName("stop_task").WithTags("Tasks").Produces<TaskDto>();
        api.MapPost("/tasks/{id}/start", StartDesktop).WithName("start_desktop").WithTags("Tasks").Produces<TaskDto>().ProducesProblem(402);
        api.MapPost("/tasks/{id}/interrupt", Interrupt).WithName("interrupt_agent").WithTags("Tasks");
        api.MapGet("/tasks/{id}/messages", ListMessages).WithName("list_messages").WithTags("Tasks").Produces<ConversationDto>();
        api.MapPost("/tasks/{id}/messages", SayMessage).WithName("say").WithTags("Tasks").Produces<MessageDto>(201).ProducesProblem(409);
    }

    static void MapDesktop(RouteGroupBuilder desktop)
    {
        desktop.MapGet("/setup", GetDesktopSetup).ExcludeFromDescription();
        desktop.MapPost("/exchange", Exchange).ExcludeFromDescription();
    }

    static TaskDto ToDto(WorkTask x, IReadOnlyDictionary<string, string> names) => new(
        x.Id, x.TeamId, x.EnvironmentId, x.Title, x.CreatedBy, names.GetValueOrDefault(x.CreatedBy), x.CreatedAt,
        x.State.ToString().ToLowerInvariant(), x.Failure, x.Size, x.AgentWorking, x.StateChangedAt);

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

    static async Task<IResult> PostTask(string teamId, StartTask body, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IBackgroundJobClient jobs, CancellationToken ct) =>
        await TaskResultAsync(await Operations.StartTaskAsync(CallerOf(http), teamId, body, db, fga, jobs, ct), db, ct, created: true);

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
        return Results.Ok(new ConversationDto([.. page.Data.Select(x => ToDto(x, names, caller.PrincipalId))], ToDto(seen.Value!, names)));
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

    static async Task<IResult> Exchange(DesktopExchange body, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var outcome = await Operations.ExchangeAsync(Caller.Desktop(http), body, db, fga, ct);
        return outcome.IsOk ? Results.Ok(outcome.Value) : ToResult(outcome);
    }
}
