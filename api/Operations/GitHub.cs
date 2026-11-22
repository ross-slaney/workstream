using System.Net;
using Hangfire;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using Workstream.Api.Desktops;
using Workstream.Api.Domain;
using Workstream.Api.GitHub;
using Workstream.Api.Model;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api;

/// <summary>
/// The person's repositories on GitHub, most recently pushed first. <see cref="Available"/> says whether this
/// Workstream signs people in with GitHub at all; <see cref="SignedIn"/> whether Workstream has a GitHub token
/// of theirs. <see cref="More"/> says there are more than were read.
/// </summary>
public sealed record GitHubRepositories(bool Available, bool SignedIn, IReadOnlyList<GitHubRepository> Repositories, bool More);

/// <summary>A repository's branches, its default one first.</summary>
public sealed record GitHubBranches(string DefaultBranch, IReadOnlyList<string> Branches);

/// <summary>
/// A repository's <c>workstream.yml</c> on a branch: whether it is there, and its settings, or what is wrong
/// with it, a line each.
/// </summary>
public sealed record RepositoryFile(bool Found, WorkstreamFileSettings? Settings, IReadOnlyList<string> Errors);

/// <summary>Which repository and branch to suggest an environment for. A null branch is the repository's default.</summary>
public sealed record SuggestEnvironment(string? RepositoryUrl, string? Branch);

/// <summary>
/// <c>workstream.yml</c> saved in the repository: committed to the environment's branch (<c>commit</c>), or, where
/// the branch takes no commits straight onto it, on a new branch with a pull request (<c>pull_request</c>).
/// <see cref="Url"/> is the commit or the pull request on GitHub.
/// </summary>
public sealed record SavedRepositoryFile(string Kind, string Url, string Branch);

/// <summary>The token of the person who started the desktop's task. GitHub decides how long it lasts; nothing here keeps it.</summary>
public sealed record DesktopGitHubToken(string Token, string? Repository);

/// <summary>
/// Whether a task's desktop can reach GitHub as the person who started it. <see cref="SignInNeeded"/> is true
/// when its environment's repository is on GitHub, it has no token of its own in its variables, and the person
/// who started it has not signed in with GitHub.
/// </summary>
public sealed record TaskGitHub(bool SignInNeeded, bool StartedByYou, string? StartedByName);

public static partial class Operations
{
    const string GitHubDidNotAnswer = "GitHub did not answer. Try again in a moment.";
    const string SignInWithGitHub = "Sign in with GitHub first, so Workstream can reach your repositories.";
    const string GitHubSignInExpired = "GitHub no longer accepts your sign-in. Sign out and sign in with GitHub again.";
    /// <summary>How long a task's start waits for its repository's workstream.yml before going on with what the environment holds.</summary>
    static readonly TimeSpan FileAtStartFor = TimeSpan.FromSeconds(8);

    static bool GitHubFailed(Exception error, CancellationToken ct) =>
        error is GitHubRefused or HttpRequestException or System.Text.Json.JsonException || error is TaskCanceledException && !ct.IsCancellationRequested;

    /// <summary>The person's own repositories, read from GitHub with their token. Nothing is checked in Workstream: it is their GitHub.</summary>
    public static async Task<Outcome<GitHubRepositories>> MyRepositoriesAsync(Caller caller, WorkstreamApp app, IGitHubTokens tokens, GitHubClient github, CancellationToken ct)
    {
        if (await tokens.ForUserAsync(caller.PrincipalId, ct) is not { } token) return Outcome<GitHubRepositories>.Ok(new(app.GitHubSignIn, false, [], false));
        try
        {
            var (repositories, more) = await github.RepositoriesAsync(token, ct);
            return Outcome<GitHubRepositories>.Ok(new(true, true, repositories, more));
        }
        catch (GitHubRefused refused) when (refused.Status == HttpStatusCode.Unauthorized)
        {
            return Outcome<GitHubRepositories>.Ok(new(app.GitHubSignIn, false, [], false));
        }
        catch (Exception error) when (GitHubFailed(error, ct))
        {
            return Outcome<GitHubRepositories>.Unavailable(GitHubDidNotAnswer);
        }
    }

    /// <summary>A repository's branches, as the person sees them on GitHub.</summary>
    public static async Task<Outcome<GitHubBranches>> MyBranchesAsync(Caller caller, string owner, string name, IGitHubTokens tokens, GitHubClient github, CancellationToken ct)
    {
        if (GitHubRepositoryName.Parse(owner, name) is not { } repository) return Outcome<GitHubBranches>.NotFound();
        if (await tokens.ForUserAsync(caller.PrincipalId, ct) is not { } token) return Outcome<GitHubBranches>.Forbidden(SignInWithGitHub);
        try
        {
            if (await github.RepositoryAsync(token, repository, ct) is not { } found) return Outcome<GitHubBranches>.NotFound();
            var branches = await github.BranchesAsync(token, repository, ct);
            return Outcome<GitHubBranches>.Ok(new(found.DefaultBranch, [found.DefaultBranch, .. branches.Where(x => x != found.DefaultBranch).Order(StringComparer.OrdinalIgnoreCase)]));
        }
        catch (GitHubRefused refused) when (refused.Status == HttpStatusCode.Unauthorized)
        {
            return Outcome<GitHubBranches>.Forbidden(GitHubSignInExpired);
        }
        catch (Exception error) when (GitHubFailed(error, ct))
        {
            return Outcome<GitHubBranches>.Unavailable(GitHubDidNotAnswer);
        }
    }

    /// <summary>The repository's <c>workstream.yml</c> on a branch, read strictly. Not there is an answer, not an error.</summary>
    public static async Task<Outcome<RepositoryFile>> RepositoryFileAsync(Caller caller, string owner, string name, string? branch, IGitHubTokens tokens, GitHubClient github, CancellationToken ct)
    {
        if (GitHubRepositoryName.Parse(owner, name) is not { } repository) return Outcome<RepositoryFile>.NotFound();
        if (await tokens.ForUserAsync(caller.PrincipalId, ct) is not { } token) return Outcome<RepositoryFile>.Forbidden(SignInWithGitHub);
        try
        {
            var file = await github.FileAsync(token, repository, Branch(branch), WorkstreamFile.Path, ct);
            if (file is null) return Outcome<RepositoryFile>.Ok(new(false, null, []));
            var read = WorkstreamFile.Read(file.Text);
            return Outcome<RepositoryFile>.Ok(new(true, read.Settings, read.Errors));
        }
        catch (GitHubRefused refused) when (refused.Status == HttpStatusCode.Unauthorized)
        {
            return Outcome<RepositoryFile>.Forbidden(GitHubSignInExpired);
        }
        catch (Exception error) when (GitHubFailed(error, ct))
        {
            return Outcome<RepositoryFile>.Unavailable(GitHubDidNotAnswer);
        }
    }

    /// <summary>
    /// Suggest an environment for a repository: its files are read from GitHub as the person, the ones that say
    /// how it is built are shown to the model, and its answer comes back as settings for the form. The model's
    /// call is charged to the team's allowance, like an agent's. Someone who may add an environment to the team
    /// may ask (<c>TEAM_CREATE_ENVIRONMENT</c>).
    /// </summary>
    public static async Task<Outcome<EnvironmentSuggestion>> SuggestEnvironmentAsync(Caller caller, string teamId, SuggestEnvironment body, WorkstreamDbContext db,
        ISqlOSFgaAuthService fga, IGitHubTokens tokens, GitHubClient github, TeamModel model, ModelOptions models, CancellationToken ct)
    {
        var allowed = await TeamWithAsync(caller, teamId, Permissions.TeamCreateEnvironment, "You can't add environments to this team.", db, fga, ct);
        if (!allowed.IsOk) return allowed.As<EnvironmentSuggestion>();
        if (GitHubRepositoryName.Of(body.RepositoryUrl) is not { } repository) return Outcome<EnvironmentSuggestion>.Invalid("Pick a repository on GitHub to suggest a setup for.");
        if (await tokens.ForUserAsync(caller.PrincipalId, ct) is not { } token) return Outcome<EnvironmentSuggestion>.Forbidden(SignInWithGitHub);
        GitHubTree tree;
        List<AnalysisFile> files;
        string branch;
        try
        {
            if (await github.RepositoryAsync(token, repository, ct) is not { } found) return Outcome<EnvironmentSuggestion>.NotFound();
            branch = Branch(body.Branch) ?? found.DefaultBranch;
            if (await github.TreeAsync(token, repository, branch, ct) is not { } read) return Outcome<EnvironmentSuggestion>.Invalid($"{repository.FullName} has no branch {branch}.");
            tree = read;
            var picked = RepositoryAnalysis.Pick(tree.Files.Select(x => (x.Path, x.Size)));
            // A few at a time: GitHub limits how many requests a person makes at once.
            using var gate = new SemaphoreSlim(6);
            var reads = picked.Select(async path =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    return await github.FileAsync(token, repository, branch, path, ct);
                }
                finally
                {
                    gate.Release();
                }
            });
            files = [.. (await Task.WhenAll(reads)).OfType<GitHubFile>().Where(x => x.Text.Length > 0).Select(x => RepositoryAnalysis.Cut(x.Path, x.Text))];
        }
        catch (GitHubRefused refused) when (refused.Status == HttpStatusCode.Unauthorized)
        {
            return Outcome<EnvironmentSuggestion>.Forbidden(GitHubSignInExpired);
        }
        catch (Exception error) when (GitHubFailed(error, ct))
        {
            return Outcome<EnvironmentSuggestion>.Unavailable(GitHubDidNotAnswer);
        }

        var request = RepositoryAnalysis.Request(models.Sonnet, repository.FullName, branch, [.. tree.Files.Select(x => x.Path)], tree.Truncated, files);
        var answer = await model.MessagesAsync(teamId, request, ct);
        if (!answer.IsOk) return answer.As<EnvironmentSuggestion>();
        return RepositoryAnalysis.Read(answer.Value!) is { } suggestion
            ? Outcome<EnvironmentSuggestion>.Ok(suggestion)
            : Outcome<EnvironmentSuggestion>.Unavailable("The suggestion could not be read. Try again.");
    }

    /// <summary>
    /// Save the environment as <c>workstream.yml</c> at its repository's root, as the person, with their GitHub
    /// token: one commit to the environment's branch. Where the branch takes no commits straight onto it (a
    /// protected branch, a rule), the file goes on a new branch and a pull request into the environment's
    /// branch is opened instead. Either way the environment is then read from the file when a task starts.
    /// </summary>
    public static async Task<Outcome<SavedRepositoryFile>> SaveRepositoryFileAsync(Caller caller, string environmentId, WorkstreamDbContext db, ISqlOSFgaAuthService fga,
        IGitHubTokens tokens, GitHubClient github, CancellationToken ct)
    {
        var seen = await ViewEnvironmentAsync(caller, environmentId, db, fga, ct);
        if (!seen.IsOk) return seen.As<SavedRepositoryFile>();
        var environment = seen.Value!;
        if (!await fga.Allows(caller.PrincipalId, Permissions.EnvironmentEdit, environment.ResourceId)) return Outcome<SavedRepositoryFile>.Forbidden("You can't change this environment.");
        if (GitHubRepositoryName.Of(environment.RepositoryUrl) is not { } repository) return Outcome<SavedRepositoryFile>.Invalid("The environment's repository is not on GitHub.");
        if (await tokens.ForUserAsync(caller.PrincipalId, ct) is not { } token) return Outcome<SavedRepositoryFile>.Forbidden(SignInWithGitHub);

        var text = WorkstreamFile.Write(new WorkstreamFileSettings(environment.Size, environment.Apps, environment.SetupScript, environment.VariableNames, environment.Instructions));
        SavedRepositoryFile saved;
        try
        {
            if (await github.RepositoryAsync(token, repository, ct) is not { } found) return Outcome<SavedRepositoryFile>.NotFound();
            var branch = environment.RepositoryBranch ?? found.DefaultBranch;
            var existing = await github.FileAsync(token, repository, branch, WorkstreamFile.Path, ct);
            var message = existing is null ? $"Add {WorkstreamFile.Path}" : $"Update {WorkstreamFile.Path}";
            try
            {
                saved = new("commit", await github.PutFileAsync(token, repository, branch, WorkstreamFile.Path, text, message, existing?.Sha, ct), branch);
            }
            catch (GitHubRefused refused) when (refused.Status is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity or HttpStatusCode.Forbidden)
            {
                // The branch takes changes only through a pull request: one of its own, from a branch of the file's.
                if (await github.BranchCommitAsync(token, repository, branch, ct) is not { } commit) return Outcome<SavedRepositoryFile>.Invalid($"{repository.FullName} has no branch {branch}.");
                var head = $"workstream-yml-{Guid.NewGuid().ToString("N")[..8]}";
                await github.CreateBranchAsync(token, repository, head, commit, ct);
                await github.PutFileAsync(token, repository, head, WorkstreamFile.Path, text, message, existing?.Sha, ct);
                var pull = await github.OpenPullRequestAsync(token, repository, head, branch, message,
                    $"What a Workstream desktop for this repository comes with. Once this is merged, each new task reads its desktop's setup from {WorkstreamFile.Path} on {branch}.", ct);
                saved = new("pull_request", pull, head);
            }
        }
        catch (GitHubRefused refused) when (refused.Status == HttpStatusCode.Unauthorized)
        {
            return Outcome<SavedRepositoryFile>.Forbidden(GitHubSignInExpired);
        }
        catch (GitHubRefused refused) when (refused.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity or HttpStatusCode.Conflict)
        {
            return Outcome<SavedRepositoryFile>.Forbidden($"GitHub did not let you write to {repository.FullName}. Ask someone who can push to it to save {WorkstreamFile.Path}, or copy it in yourself.");
        }
        catch (Exception error) when (GitHubFailed(error, ct))
        {
            return Outcome<SavedRepositoryFile>.Unavailable(GitHubDidNotAnswer);
        }
        environment.KeepFromFile(true, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        return Outcome<SavedRepositoryFile>.Ok(saved);
    }

    /// <summary>
    /// A task is starting on an environment that came from its repository's <c>workstream.yml</c>: the file is
    /// read again from GitHub, as the person starting it, so the repository stays where the environment is
    /// defined. What it says now becomes the environment's size, apps, setup script and instructions (its
    /// variables stay as they are, since their values live here). When GitHub cannot be reached in a few
    /// seconds, the person has no GitHub token, or the file is gone or not valid, the task starts with what the
    /// environment already holds.
    /// </summary>
    static async Task ReadFileAtStartAsync(WorkEnvironment environment, Caller caller, WorkstreamDbContext db, IDataProtectionProvider protection,
        IBackgroundJobClient jobs, DesktopOptions options, IGitHubTokens tokens, GitHubClient github, ILogger log, CancellationToken ct)
    {
        if (!environment.FromFile || GitHubRepositoryName.Of(environment.RepositoryUrl) is not { } repository) return;
        WorkstreamFileSettings? settings;
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(FileAtStartFor);
            if (await tokens.ForUserAsync(caller.PrincipalId, wait.Token) is not { } token) return;
            var file = await github.FileAsync(token, repository, environment.RepositoryBranch, WorkstreamFile.Path, wait.Token);
            settings = file is null ? null : WorkstreamFile.Read(file.Text).Settings;
        }
        catch (Exception error) when (GitHubFailed(error, CancellationToken.None) || error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogInformation("workstream.yml of environment {EnvironmentId} could not be read at a task's start; the task uses what the environment holds.", environment.Id);
            return;
        }
        if (settings is null) return;
        if (settings.Size == environment.Size && settings.Apps.SequenceEqual(environment.Apps) && settings.Setup == environment.SetupScript && settings.Instructions == environment.Instructions) return;
        var before = environment.SetupInputs;
        environment.Change(new EnvironmentSettings(environment.Name, settings.Apps, settings.Size, environment.RepositoryUrl, environment.RepositoryBranch,
            settings.Setup, settings.Instructions, FromFile: true), environment.ChangedBy, DateTimeOffset.UtcNow);
        environment.KeepSetupInputs(SetupInputsOf(environment, VariablesOf(environment, protection)));
        await db.SaveChangesAsync(ct);
        await KeepSetupAsync(environment, before, protection, db, jobs, options, ct);
    }

    /// <summary>
    /// A desktop asks for a GitHub token: the token of the person who started its task, which reaches what
    /// their GitHub account reaches, so the agent pushes and opens pull requests in their name. The task is the
    /// one the desktop holds <c>TASK_RUN_AGENT</c> on. A machine preparing an environment gets the token of the
    /// person who last saved the environment, while its attempt lasts. Nothing keeps it. Not there (404, with
    /// words the agent passes on) when that person has not signed in with GitHub.
    /// </summary>
    public static async Task<Outcome<DesktopGitHubToken>> DesktopGitHubTokenAsync(Caller desktop, WorkstreamDbContext db, WorkstreamApp app, IGitHubTokens tokens, CancellationToken ct)
    {
        string person;
        string? repositoryUrl;
        if (await DesktopTaskAsync(desktop, db, ct, tracked: false) is { } task)
        {
            person = task.CreatedBy;
            repositoryUrl = await db.Environments.AsNoTracking().Where(x => x.Id == task.EnvironmentId).Select(x => x.RepositoryUrl).SingleOrDefaultAsync(ct);
        }
        else if (await PreparingAsync(desktop, db, ct) is { } preparing)
        {
            person = preparing.Environment.ChangedBy;
            repositoryUrl = preparing.Environment.RepositoryUrl;
        }
        else
        {
            return Outcome<DesktopGitHubToken>.NotFound();
        }
        if (!app.GitHubSignIn) return Outcome<DesktopGitHubToken>.NotFound("Sign in with GitHub is not set up on this Workstream, so this desktop has no GitHub token. Put one in the environment's variables as GITHUB_TOKEN.");
        if (await tokens.ForUserAsync(person, ct) is not { } token)
        {
            var name = (await NamesAsync(db, [person], ct)).GetValueOrDefault(person) ?? "The person who started this task";
            return Outcome<DesktopGitHubToken>.NotFound($"{name} has not signed in to Workstream with GitHub, so this desktop cannot reach GitHub as them. Once they sign out and sign in with GitHub, ask again.");
        }
        return Outcome<DesktopGitHubToken>.Ok(new DesktopGitHubToken(token, GitHubRepositoryName.Of(repositoryUrl)?.FullName));
    }

    /// <summary>Whether the task's desktop reaches GitHub as the person who started it, for the task's page.</summary>
    public static async Task<Outcome<TaskGitHub>> TaskGitHubAsync(Caller caller, string taskId, WorkstreamDbContext db, ISqlOSFgaAuthService fga, WorkstreamApp app, IGitHubTokens tokens, CancellationToken ct)
    {
        var seen = await ViewTaskAsync(caller, taskId, db, fga, ct);
        if (!seen.IsOk) return seen.As<TaskGitHub>();
        var task = seen.Value!;
        var environment = await db.Environments.AsNoTracking().Where(x => x.Id == task.EnvironmentId).Select(x => new { x.RepositoryUrl, x.VariableNames }).SingleAsync(ct);
        var name = (await NamesAsync(db, [task.CreatedBy], ct)).GetValueOrDefault(task.CreatedBy);
        var needed = app.GitHubSignIn
            && GitHubRepositoryName.Of(environment.RepositoryUrl) is not null
            && !environment.VariableNames.Any(x => x is "GITHUB_TOKEN" or "GH_TOKEN")
            && await tokens.ForUserAsync(task.CreatedBy, ct) is null;
        return Outcome<TaskGitHub>.Ok(new TaskGitHub(needed, task.CreatedBy == caller.PrincipalId, name));
    }

    static string? Branch(string? branch) => string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();

    /// <summary>The names of the people among these principals.</summary>
    static async Task<Dictionary<string, string>> NamesAsync(WorkstreamDbContext db, IEnumerable<string> principalIds, CancellationToken ct)
    {
        var ids = principalIds.Distinct().ToList();
        return ids.Count == 0 ? [] : await db.Set<SqlOS.AuthServer.Models.SqlOSUser>().AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
    }
}
