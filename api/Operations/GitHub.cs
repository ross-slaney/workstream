using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using Workstream.Api.Domain;
using Workstream.Api.GitHub;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api;

/// <summary>
/// What the web app sends once GitHub has handed the administrator back: the state Workstream signed when they
/// set off, and the code and installation GitHub added (docs/core-model.md, "Connect GitHub").
/// </summary>
public sealed record ConnectGitHub(string? State, string? Code, long? InstallationId);

/// <summary>A token for the desktop's repository, good until <see cref="ExpiresAt"/> (an hour). Nothing keeps it.</summary>
public sealed record DesktopGitHubToken(string Token, DateTimeOffset ExpiresAt, string Repository);

public static partial class Operations
{
    const string GitHubStatePurpose = "Workstream.GitHub.Install";
    static readonly TimeSpan GitHubStateLifetime = TimeSpan.FromHours(1);
    const string GitHubNotSetUp = "GitHub is not set up for Workstream yet.";
    const string GitHubDidNotAnswer = "GitHub did not answer. Try again in a moment.";

    /// <summary>The team's GitHub connection, or none, for anyone who may see the team.</summary>
    public static async Task<Outcome<GitHubConnection?>> GitHubConnectionAsync(Caller caller, string teamId, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var seen = await ViewTeamAsync(caller, teamId, db, fga, ct);
        if (!seen.IsOk) return seen.As<GitHubConnection?>();
        return Outcome<GitHubConnection?>.Ok(await db.GitHubConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TeamId == teamId, ct));
    }

    /// <summary>The repositories the team's connection reaches, read from GitHub as the installation: what an environment can pick from.</summary>
    public static async Task<Outcome<IReadOnlyList<GitHubRepository>>> GitHubRepositoriesAsync(Caller caller, string teamId, WorkstreamDbContext db, ISqlOSFgaAuthService fga,
        GitHubOptions options, GitHubApp github, CancellationToken ct)
    {
        var connection = await GitHubConnectionAsync(caller, teamId, db, fga, ct);
        if (!connection.IsOk) return connection.As<IReadOnlyList<GitHubRepository>>();
        if (!options.IsConfigured) return Outcome<IReadOnlyList<GitHubRepository>>.Unavailable(GitHubNotSetUp);
        if (connection.Value is not { } connected) return Outcome<IReadOnlyList<GitHubRepository>>.NotFound();
        try
        {
            return Outcome<IReadOnlyList<GitHubRepository>>.Ok(await github.RepositoriesAsync(connected.InstallationId, ct));
        }
        catch (GitHubRefused refused) when (refused.Status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)
        {
            return Outcome<IReadOnlyList<GitHubRepository>>.Unavailable($"Workstream is no longer installed on {connected.Account} on GitHub. Connect GitHub again.");
        }
        catch (Exception error) when (error is GitHubRefused or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Outcome<IReadOnlyList<GitHubRepository>>.Unavailable(GitHubDidNotAnswer);
        }
    }

    /// <summary>
    /// An administrator sets off to install the app: the address on GitHub, carrying a state that says which
    /// team and who, signed by Workstream and good for an hour.
    /// </summary>
    public static async Task<Outcome<string>> StartGitHubInstallAsync(Caller caller, string teamId, WorkstreamDbContext db, ISqlOSFgaAuthService fga,
        GitHubOptions options, IDataProtectionProvider protection, CancellationToken ct)
    {
        var allowed = await TeamWithAsync(caller, teamId, Permissions.TeamManageSettings, "Only a team administrator can connect GitHub.", db, fga, ct);
        if (!allowed.IsOk) return allowed.As<string>();
        if (!options.IsConfigured) return Outcome<string>.Unavailable(GitHubNotSetUp);
        var state = StateProtector(protection).Protect($"{teamId}\n{caller.PrincipalId}", GitHubStateLifetime);
        return Outcome<string>.Ok(options.InstallUrl(state));
    }

    /// <summary>
    /// The administrator is back from GitHub. The state must be one Workstream signed for this same person, who
    /// must still administer the team; the installation must be one GitHub says their account can reach (the
    /// installation id in the address alone proves nothing: anyone can type another). Then the team is
    /// connected to it. The person's own GitHub token is used for that one question and kept nowhere.
    /// </summary>
    public static async Task<Outcome<GitHubConnection>> ConnectGitHubAsync(Caller caller, ConnectGitHub body, WorkstreamDbContext db, ISqlOSFgaAuthService fga,
        GitHubOptions options, IDataProtectionProvider protection, GitHubApp github, CancellationToken ct)
    {
        if (!options.IsConfigured) return Outcome<GitHubConnection>.Unavailable(GitHubNotSetUp);
        if (ReadState(protection, body.State) is not { } state) return Outcome<GitHubConnection>.Invalid("This link from GitHub has expired. Connect GitHub again from the team's page.");
        var (teamId, startedBy) = state;
        if (startedBy != caller.PrincipalId) return Outcome<GitHubConnection>.Forbidden("Someone else started connecting GitHub. Connect it yourself from the team's page.");
        var allowed = await TeamWithAsync(caller, teamId, Permissions.TeamManageSettings, "Only a team administrator can connect GitHub.", db, fga, ct);
        if (!allowed.IsOk) return allowed.As<GitHubConnection>();
        if (body.InstallationId is not > 0) return Outcome<GitHubConnection>.Invalid("GitHub did not say where Workstream was installed. Connect GitHub again.");
        if (string.IsNullOrWhiteSpace(body.Code)) return Outcome<GitHubConnection>.Invalid("GitHub did not say who installed Workstream. Connect GitHub again.");
        GitHubInstallation? installation;
        try
        {
            if (await github.UserTokenAsync(body.Code.Trim(), ct) is not { } personal) return Outcome<GitHubConnection>.Invalid("GitHub did not accept the sign-in. Connect GitHub again.");
            installation = await github.UserInstallationAsync(personal, body.InstallationId.Value, ct);
        }
        catch (Exception error) when (error is GitHubRefused or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Outcome<GitHubConnection>.Unavailable(GitHubDidNotAnswer);
        }
        if (installation is null) return Outcome<GitHubConnection>.Forbidden("Your GitHub account cannot reach that installation of Workstream.");

        var now = DateTimeOffset.UtcNow;
        var connection = await db.GitHubConnections.SingleOrDefaultAsync(x => x.TeamId == teamId, ct);
        if (connection is null)
        {
            connection = GitHubConnection.Connect(allowed.Value!, installation.Id, installation.Account, installation.AccountType, caller.PrincipalId, now);
            db.GitHubConnections.Add(connection);
        }
        else
        {
            connection.Reconnect(installation.Id, installation.Account, installation.AccountType, caller.PrincipalId, now);
        }
        await db.SaveChangesAsync(ct);
        return Outcome<GitHubConnection>.Ok(connection);
    }

    /// <summary>
    /// The team's desktops get no more GitHub tokens. The app stays installed on GitHub, where whoever owns the
    /// account removes it (another team may be connected to the same installation).
    /// </summary>
    public static async Task<Outcome<bool>> DisconnectGitHubAsync(Caller caller, string teamId, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var allowed = await TeamWithAsync(caller, teamId, Permissions.TeamManageSettings, "Only a team administrator can disconnect GitHub.", db, fga, ct);
        if (!allowed.IsOk) return allowed.As<bool>();
        var connection = await db.GitHubConnections.SingleOrDefaultAsync(x => x.TeamId == teamId, ct);
        if (connection is null) return Outcome<bool>.NotFound();
        db.GitHubConnections.Remove(connection);
        await db.SaveChangesAsync(ct);
        return Outcome<bool>.Ok(true);
    }

    /// <summary>GitHub says the app was removed from an account: every team connected to that installation is disconnected.</summary>
    public static async Task<int> ForgetGitHubInstallationAsync(long installationId, WorkstreamDbContext db, CancellationToken ct)
    {
        var connections = await db.GitHubConnections.Where(x => x.InstallationId == installationId).ToListAsync(ct);
        db.GitHubConnections.RemoveRange(connections);
        await db.SaveChangesAsync(ct);
        return connections.Count;
    }

    /// <summary>
    /// A desktop asks for a GitHub token: for its task's environment's repository and nothing else, from its
    /// team's connection, good for an hour, minted now and kept nowhere. The task is the one the desktop holds
    /// <c>TASK_RUN_AGENT</c> on. Not there (404) when the task has no repository on GitHub, the team is not
    /// connected, or the connection does not reach the repository: the desktop then does without.
    /// </summary>
    public static async Task<Outcome<DesktopGitHubToken>> DesktopGitHubTokenAsync(Caller desktop, WorkstreamDbContext db, GitHubOptions options, GitHubApp github, CancellationToken ct)
    {
        if (await DesktopTaskAsync(desktop, db, ct, tracked: false) is not { } task) return Outcome<DesktopGitHubToken>.NotFound();
        if (!options.IsConfigured) return Outcome<DesktopGitHubToken>.Unavailable(GitHubNotSetUp);
        var repositoryUrl = await db.Environments.AsNoTracking().Where(x => x.Id == task.EnvironmentId).Select(x => x.RepositoryUrl).SingleOrDefaultAsync(ct);
        if (GitHubRepositoryName.Of(repositoryUrl) is not { } repository) return Outcome<DesktopGitHubToken>.NotFound();
        var connection = await db.GitHubConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TeamId == task.TeamId, ct);
        if (connection is null || !connection.Reaches(repository)) return Outcome<DesktopGitHubToken>.NotFound();
        try
        {
            var token = await github.InstallationTokenAsync(connection.InstallationId, repository.Name, ct);
            return Outcome<DesktopGitHubToken>.Ok(new DesktopGitHubToken(token.Token, token.ExpiresAt, repository.FullName));
        }
        catch (GitHubRefused refused) when (refused.Status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.UnprocessableEntity or System.Net.HttpStatusCode.Forbidden)
        {
            // The installation was removed, or does not reach this repository.
            return Outcome<DesktopGitHubToken>.NotFound();
        }
        catch (Exception error) when (error is GitHubRefused or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Outcome<DesktopGitHubToken>.Unavailable(GitHubDidNotAnswer);
        }
    }

    static ITimeLimitedDataProtector StateProtector(IDataProtectionProvider protection) =>
        protection.CreateProtector(GitHubStatePurpose).ToTimeLimitedDataProtector();

    /// <summary>The team and the person a state was signed for, when it is one Workstream signed and it has not expired.</summary>
    internal static (string TeamId, string UserId)? ReadState(IDataProtectionProvider protection, string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return null;
        try
        {
            var parts = StateProtector(protection).Unprotect(state.Trim()).Split('\n');
            return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0 ? (parts[0], parts[1]) : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
