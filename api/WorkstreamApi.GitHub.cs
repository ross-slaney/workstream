using SqlOS.Fga.Interfaces;
using Workstream.Api.Domain;
using Workstream.Api.GitHub;
using Workstream.Api.Model;

namespace Workstream.Api;

public static partial class WorkstreamApi
{
    /// <summary>
    /// GitHub as the signed-in person reaches it, with the token GitHub gave them at sign-in: their
    /// repositories and branches for the environment form, a repository's workstream.yml, a suggested
    /// environment, and saving workstream.yml in the repository. A task's page asks whether its desktop
    /// reaches GitHub.
    /// </summary>
    static void MapGitHub(RouteGroupBuilder api)
    {
        api.MapGet("/github/repositories", ListMyRepositories).WithName("list_my_github_repositories").WithTags("GitHub").Produces<GitHubRepositories>().ProducesProblem(503);
        api.MapGet("/github/repositories/{owner}/{name}/branches", ListMyBranches).WithName("list_github_branches").WithTags("GitHub").Produces<GitHubBranches>().ProducesProblem(403).ProducesProblem(503);
        api.MapGet("/github/repositories/{owner}/{name}/workstream-file", GetRepositoryFile).WithName("get_workstream_file").WithTags("GitHub").Produces<RepositoryFile>().ProducesProblem(403).ProducesProblem(503);
        api.MapPost("/teams/{teamId}/environment-suggestions", PostEnvironmentSuggestion).WithName("suggest_environment").WithTags("Environments").Produces<EnvironmentSuggestion>()
            .ProducesProblem(400).ProducesProblem(402).ProducesProblem(403).ProducesProblem(503);
        api.MapPost("/environments/{id}/workstream-file", PostRepositoryFile).WithName("save_workstream_file").WithTags("Environments").Produces<SavedRepositoryFile>()
            .ProducesProblem(400).ProducesProblem(403).ProducesProblem(503);
        api.MapGet("/tasks/{id}/github", GetTaskGitHub).WithName("get_task_github").WithTags("Tasks").Produces<TaskGitHub>();
    }

    static async Task<IResult> ListMyRepositories(HttpContext http, WorkstreamApp app, IGitHubTokens tokens, GitHubClient github, CancellationToken ct) =>
        Answer(await Operations.MyRepositoriesAsync(CallerOf(http), app, tokens, github, ct));

    static async Task<IResult> ListMyBranches(string owner, string name, HttpContext http, IGitHubTokens tokens, GitHubClient github, CancellationToken ct) =>
        Answer(await Operations.MyBranchesAsync(CallerOf(http), owner, name, tokens, github, ct));

    static async Task<IResult> GetRepositoryFile(string owner, string name, string? branch, HttpContext http, IGitHubTokens tokens, GitHubClient github, CancellationToken ct) =>
        Answer(await Operations.RepositoryFileAsync(CallerOf(http), owner, name, branch, tokens, github, ct));

    static async Task<IResult> PostEnvironmentSuggestion(string teamId, SuggestEnvironment body, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga,
        IGitHubTokens tokens, GitHubClient github, TeamModel model, ModelOptions models, CancellationToken ct) =>
        Answer(await Operations.SuggestEnvironmentAsync(CallerOf(http), teamId, body, db, fga, tokens, github, model, models, ct));

    static async Task<IResult> PostRepositoryFile(string id, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IGitHubTokens tokens, GitHubClient github, CancellationToken ct) =>
        Answer(await Operations.SaveRepositoryFileAsync(CallerOf(http), id, db, fga, tokens, github, ct));

    static async Task<IResult> GetTaskGitHub(string id, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, WorkstreamApp app, IGitHubTokens tokens, CancellationToken ct) =>
        Answer(await Operations.TaskGitHubAsync(CallerOf(http), id, db, fga, app, tokens, ct));

    static async Task<IResult> GetDesktopGitHubToken(HttpContext http, WorkstreamDbContext db, WorkstreamApp app, IGitHubTokens tokens, CancellationToken ct) =>
        Answer(await Operations.DesktopGitHubTokenAsync(Caller.Desktop(http), db, app, tokens, ct));

    static IResult Answer<T>(Outcome<T> outcome) => outcome.IsOk ? Results.Ok(outcome.Value) : ToResult(outcome);
}
