using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using SqlOS.Fga.Interfaces;
using Workstream.Api.Domain;
using Workstream.Api.GitHub;

namespace Workstream.Api;

/// <summary>
/// A team's GitHub connection as the team's page shows it. <see cref="Available"/> says whether Workstream's
/// GitHub App is set up at all; <see cref="ManageUrl"/> is where on GitHub the installation is changed.
/// </summary>
public sealed record GitHubDto(bool Available, bool Connected, string? Account, string? AccountType, DateTimeOffset? ConnectedAt, string? ConnectedByName, string? ManageUrl);

/// <summary>Where on GitHub the administrator installs the app.</summary>
public sealed record GitHubInstall(string Url);

/// <summary>The team that was just connected, and its connection.</summary>
public sealed record GitHubConnected(string TeamId, GitHubDto GitHub);

public static partial class WorkstreamApi
{
    /// <summary>GitHub's callback and setup URL for the app (infra/README.md, "GitHub"). It hands the browser on to the web app.</summary>
    public const string GitHubCallbackPath = "/api/github/callback";
    public const string GitHubWebhookPath = "/api/github/webhook";
    const int MaxWebhookBytes = 5 * 1024 * 1024;

    static void MapGitHub(RouteGroupBuilder api)
    {
        api.MapGet("/teams/{teamId}/github", GetGitHub).WithName("get_github").WithTags("GitHub").Produces<GitHubDto>();
        api.MapGet("/teams/{teamId}/github/repositories", ListGitHubRepositories).WithName("list_github_repositories").WithTags("GitHub").Produces<IReadOnlyList<GitHubRepository>>().ProducesProblem(503);
        api.MapPost("/teams/{teamId}/github/install", StartGitHubInstall).WithName("start_github_install").WithTags("GitHub").Produces<GitHubInstall>().ProducesProblem(403).ProducesProblem(503);
        api.MapDelete("/teams/{teamId}/github", DisconnectGitHub).WithName("disconnect_github").WithTags("GitHub").ProducesProblem(403);
        api.MapPost("/github/connect", ConnectGitHubTeam).WithName("connect_github").WithTags("GitHub").Produces<GitHubConnected>().ProducesProblem(400).ProducesProblem(403);
    }

    /// <summary>GitHub's own calls: the browser coming back from installing the app, and the app's webhook. Neither carries a person's token.</summary>
    static void MapGitHubCallbacks(WebApplication app)
    {
        app.MapGet(GitHubCallbackPath, GitHubCallback).AllowAnonymous().ExcludeFromDescription();
        app.MapPost(GitHubWebhookPath, GitHubWebhook).AllowAnonymous().ExcludeFromDescription();
    }

    static async Task<GitHubDto> ToDtoAsync(GitHubConnection? connection, GitHubOptions options, WorkstreamDbContext db, CancellationToken ct)
    {
        if (connection is null) return new GitHubDto(options.IsConfigured, false, null, null, null, null, null);
        var names = await NamesAsync(db, [connection.ConnectedBy], ct);
        return new GitHubDto(options.IsConfigured, true, connection.Account, connection.AccountType, connection.ConnectedAt,
            names.GetValueOrDefault(connection.ConnectedBy), connection.ManageUrl(options.Web));
    }

    static async Task<IResult> GetGitHub(string teamId, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, GitHubOptions options, CancellationToken ct)
    {
        var outcome = await Operations.GitHubConnectionAsync(CallerOf(http), teamId, db, fga, ct);
        return outcome.IsOk ? Results.Ok(await ToDtoAsync(outcome.Value, options, db, ct)) : ToResult(outcome);
    }

    static async Task<IResult> ListGitHubRepositories(string teamId, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, GitHubOptions options, GitHubApp github, CancellationToken ct)
    {
        var outcome = await Operations.GitHubRepositoriesAsync(CallerOf(http), teamId, db, fga, options, github, ct);
        return outcome.IsOk ? Results.Ok(outcome.Value) : ToResult(outcome);
    }

    static async Task<IResult> StartGitHubInstall(string teamId, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, GitHubOptions options, IDataProtectionProvider protection, CancellationToken ct)
    {
        var outcome = await Operations.StartGitHubInstallAsync(CallerOf(http), teamId, db, fga, options, protection, ct);
        return outcome.IsOk ? Results.Ok(new GitHubInstall(outcome.Value!)) : ToResult(outcome);
    }

    static async Task<IResult> ConnectGitHubTeam(ConnectGitHub body, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, GitHubOptions options,
        IDataProtectionProvider protection, GitHubApp github, CancellationToken ct)
    {
        var outcome = await Operations.ConnectGitHubAsync(CallerOf(http), body, db, fga, options, protection, github, ct);
        return outcome.IsOk ? Results.Ok(new GitHubConnected(outcome.Value!.TeamId, await ToDtoAsync(outcome.Value, options, db, ct))) : ToResult(outcome);
    }

    static async Task<IResult> DisconnectGitHub(string teamId, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var outcome = await Operations.DisconnectGitHubAsync(CallerOf(http), teamId, db, fga, ct);
        return outcome.IsOk ? Results.NoContent() : ToResult(outcome);
    }

    /// <summary>
    /// The browser, back from installing the app on GitHub. Nothing is decided here: the browser carries no
    /// person's token, so it is handed on to the web app (<see cref="WorkstreamApp.GitHubReturnPath"/>) with what
    /// GitHub added, and the web app finishes the connection as the person who is signed in there.
    /// </summary>
    static IResult GitHubCallback(HttpContext http, WorkstreamApp app)
    {
        var passed = new Dictionary<string, string?>();
        foreach (var name in new[] { "code", "installation_id", "setup_action", "state" })
        {
            if (http.Request.Query[name] is { Count: > 0 } value && value.ToString().Length <= 2000) passed[name] = value.ToString();
        }
        return Results.Redirect(QueryHelpers.AddQueryString($"{app.WebOrigin}{WorkstreamApp.GitHubReturnPath}", passed));
    }

    /// <summary>
    /// GitHub's webhook, where a secret is set: a delivery GitHub signed saying the app was removed from an
    /// account disconnects every team connected to that installation. Every other delivery is taken and ignored.
    /// </summary>
    static async Task<IResult> GitHubWebhook(HttpContext http, GitHubOptions options, WorkstreamDbContext db, CancellationToken ct)
    {
        if (options.WebhookSecret.Length == 0) return Results.NotFound();
        if (http.Request.ContentLength > MaxWebhookBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, ct);
        var body = buffer.ToArray();
        if (body.Length > MaxWebhookBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        if (!options.IsSignedDelivery(body, http.Request.Headers["X-Hub-Signature-256"].ToString())) return Results.Unauthorized();
        if (http.Request.Headers["X-GitHub-Event"].ToString() != "installation") return Results.NoContent();
        try
        {
            using var delivery = JsonDocument.Parse(body);
            var root = delivery.RootElement;
            if (root.TryGetProperty("action", out var action) && action.GetString() == "deleted"
                && root.TryGetProperty("installation", out var installation) && installation.TryGetProperty("id", out var id) && id.TryGetInt64(out var installationId))
            {
                await Operations.ForgetGitHubInstallationAsync(installationId, db, ct);
            }
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }
        return Results.NoContent();
    }

    static async Task<IResult> GetDesktopGitHubToken(HttpContext http, WorkstreamDbContext db, GitHubOptions options, GitHubApp github, CancellationToken ct)
    {
        var outcome = await Operations.DesktopGitHubTokenAsync(Caller.Desktop(http), db, options, github, ct);
        return outcome.IsOk ? Results.Ok(outcome.Value) : ToResult(outcome);
    }
}
