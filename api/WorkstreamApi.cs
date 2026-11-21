using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Models;
using SqlOS.Fga.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Pagination;
using Workstream.Api.Desktops;
using Workstream.Api.Domain;
using Workstream.Api.Model;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api;

/// <summary>A page of a list. Pass <see cref="NextCursor"/> back as <c>?cursor=</c> for the next page; it is null on the last one.</summary>
public sealed record ListPage<T>(IReadOnlyList<T> Data, string? NextCursor);

/// <summary>Who the caller is, whether they hold the system administrator's dashboard, and how this environment signs in.</summary>
public sealed record MeDto(string UserId, string Name, string? Email, bool SystemAdmin);

/// <summary>
/// The plain REST API. Every route resolves the caller from the validated credential, loads the row and its
/// real parent, checks SqlOS on the target node (creates check the parent), calls the domain and saves. Lists
/// are authorized pages. The verbs themselves live in <see cref="Operations"/>; these handlers adapt them.
/// </summary>
public static partial class WorkstreamApi
{
    /// <summary>The one CORS policy: the web app's origin (Workstream.cs).</summary>
    public const string WebCorsPolicy = "web";

    const int DefaultPageSize = 100;
    const int MaxPageSize = 500;

    public static WebApplication MapWorkstreamApi(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/ready", async (WorkstreamDbContext db, IConfiguration configuration, CancellationToken ct) =>
            await db.Database.CanConnectAsync(ct)
                ? Results.Ok(new { status = "ready", revision = configuration["SOURCE_COMMIT_SHA"] ?? "local" })
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        app.MapOpenApi();
        // CORS first, then who is calling. Left to itself the host puts authentication and authorization
        // before everything written here, and a refusal (401, 403) then leaves without the CORS headers: the
        // browser reports it to the web app as a failed connection, so the app could neither refresh its
        // token nor send the person to sign in.
        app.UseCors(WebCorsPolicy);
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseWebSockets();

        // People, with SqlOS's token.
        var api = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter(WriteScopeForChanges);
        api.MapGet("/me", GetMe).WithName("get_me").WithTags("Me").Produces<MeDto>();
        api.MapGet("/apps", () => Results.Ok(AppCatalog.All)).WithName("list_apps").WithTags("Environments").Produces<IReadOnlyList<DesktopApp>>();
        MapTeams(api);
        MapEnvironments(api);
        MapTasks(api);
        MapAdmin(api);

        // A person watching a desktop: a WebSocket, authorized inside (Desktops/DesktopRelay.cs).
        app.Map(DesktopRelay.Route, DesktopRelay.HandleAsync).ExcludeFromDescription();

        // A task's desktop, with its key: its own exchanges, and its agent's model calls.
        MapDesktop(app.MapGroup("/api/desktop").RequireAuthorization(DesktopKeys.Policy));
        app.Map($"{ModelGateway.Path}/{{**path}}", ModelGateway.HandleAsync).RequireAuthorization(DesktopKeys.Policy).ExcludeFromDescription();
        return app;
    }

    static async Task<IResult> GetMe(HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var caller = CallerOf(http);
        var user = await db.Set<SqlOSUser>().AsNoTracking().Where(x => x.Id == caller.PrincipalId).Select(x => new { x.DisplayName, x.DefaultEmail }).SingleOrDefaultAsync(ct);
        return Results.Ok(new MeDto(caller.PrincipalId, user?.DisplayName ?? "", user?.DefaultEmail, await fga.HasCapabilityAsync(caller.PrincipalId, Permissions.AdminView)));
    }

    /// <summary>The person calling; never what the request says.</summary>
    static Caller CallerOf(HttpContext http) => Caller.Of(http);

    /// <summary>A token without the write scope can look and not touch: every route that is not a GET needs it.</summary>
    static async ValueTask<object?> WriteScopeForChanges(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!HttpMethods.IsGet(http.Request.Method) && !CallerOf(http).HasScope(WorkstreamApp.WriteScope))
        {
            return Forbidden("This connection can only read.");
        }
        return await next(context);
    }

    /// <summary>An operation's failure as the status code and words the app expects.</summary>
    static IResult ToResult<T>(Outcome<T> outcome) => outcome.Kind switch
    {
        OutcomeKind.Invalid => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: outcome.Message),
        OutcomeKind.Forbidden => Forbidden(outcome.Message!),
        OutcomeKind.NotFound => Results.NotFound(),
        // 409: the thing already exists, so a create that was tried again is done.
        OutcomeKind.Exists => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: outcome.Message),
        // The words say which limit; the code lets the app show them beside the limit.
        OutcomeKind.LimitReached => Results.Problem(statusCode: StatusCodes.Status402PaymentRequired, title: outcome.Message,
            extensions: new Dictionary<string, object?> { ["code"] = "limit_reached" }),
        OutcomeKind.Unavailable => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: outcome.Message),
        _ => throw new InvalidOperationException("An ok outcome has no error result."),
    };

    static IResult Forbidden(string message) => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: message);

    static int PageSize(int? requested) => Math.Clamp(requested ?? DefaultPageSize, 1, MaxPageSize);

    /// <summary>
    /// One authorized page: SqlOS returns only the rows the subject may see with the permission, in the query's
    /// order. A cursor the list never issued is a 400.
    /// </summary>
    static async Task<(SqlOSCursorPage<T>? Page, IResult? Error)> AuthorizedPageAsync<T>(
        IQueryable<T> ordered, string subjectId, string permission, int? pageSize, string? cursor, CancellationToken ct)
        where T : class, IHasResourceId
    {
        try
        {
            return (await ordered.ToPageAsync(subjectId, permission, PageSize(pageSize), string.IsNullOrWhiteSpace(cursor) ? null : cursor, ct), null);
        }
        catch (Exception error) when (error is SqlOSCursorException or ArgumentException { ParamName: "cursor" })
        {
            return (null, Results.ValidationProblem(new Dictionary<string, string[]> { ["cursor"] = ["The cursor is not one this list issued."] }));
        }
    }

    /// <summary>The names of the people among these principals, for a list that says who.</summary>
    static async Task<Dictionary<string, string>> NamesAsync(WorkstreamDbContext db, IEnumerable<string> principalIds, CancellationToken ct)
    {
        var ids = principalIds.Distinct().ToList();
        return ids.Count == 0 ? [] : await db.Set<SqlOSUser>().AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
    }
}
