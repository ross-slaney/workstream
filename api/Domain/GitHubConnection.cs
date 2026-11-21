using System.Text.RegularExpressions;

namespace Workstream.Api.Domain;

/// <summary>
/// A team's GitHub connection: the one installation of Workstream's GitHub App the team's desktops get tokens
/// from (docs/core-model.md, "Connect GitHub"). It is configuration of the team, like its limits: it relates a
/// team to an account on GitHub, not a principal to a node, so it is a row and not a grant. Who may connect
/// and disconnect it is <c>TEAM_MANAGE_SETTINGS</c> on the team; who may use it is whoever holds
/// <c>TASK_RUN_AGENT</c> on a task in the team, for that task's repository.
/// </summary>
public sealed class GitHubConnection
{
    /// <summary>For EF Core.</summary>
    GitHubConnection()
    {
    }

    /// <summary>The team, which has one connection at most.</summary>
    public string TeamId { get; private set; } = "";
    /// <summary>GitHub's id of the installation.</summary>
    public long InstallationId { get; private set; }
    /// <summary>The GitHub account the app is installed on: an organization's or a person's login.</summary>
    public string Account { get; private set; } = "";
    /// <summary><c>Organization</c> or <c>User</c>, as GitHub says.</summary>
    public string AccountType { get; private set; } = "";
    /// <summary>Who connected it: a principal id, a fact. It grants nothing.</summary>
    public string ConnectedBy { get; private set; } = "";
    public DateTimeOffset ConnectedAt { get; private set; }

    public static GitHubConnection Connect(Team team, long installationId, string account, string accountType, string connectedBy, DateTimeOffset now)
    {
        var connection = new GitHubConnection { TeamId = team.Id };
        connection.Reconnect(installationId, account, accountType, connectedBy, now);
        return connection;
    }

    /// <summary>An administrator connects the team again, to the same installation or another.</summary>
    public void Reconnect(long installationId, string account, string accountType, string connectedBy, DateTimeOffset now)
    {
        if (installationId <= 0) throw new DomainRuleException("GitHub did not say which installation it was.");
        InstallationId = installationId;
        Account = Rules.Text(account, "The GitHub account", 100);
        AccountType = Rules.Text(accountType, "The kind of GitHub account", 20);
        ConnectedBy = Rules.Principal(connectedBy, "Whoever connected it");
        ConnectedAt = now;
    }

    /// <summary>Where on GitHub the installation is changed: which repositories it reaches, or removing it.</summary>
    public string ManageUrl(string web) => AccountType == "Organization"
        ? $"{web}/organizations/{Uri.EscapeDataString(Account)}/settings/installations/{InstallationId}"
        : $"{web}/settings/installations/{InstallationId}";

    /// <summary>Whether a repository is on this connection's account: a token is only ever asked for one that is.</summary>
    public bool Reaches(GitHubRepositoryName repository) => string.Equals(repository.Owner, Account, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A repository on GitHub, by its owner and its name.</summary>
public sealed partial record GitHubRepositoryName(string Owner, string Name)
{
    public string FullName => $"{Owner}/{Name}";

    /// <summary>The repository an environment's address names, when it is one on github.com; null for any other address.</summary>
    public static GitHubRepositoryName? Of(string? url)
    {
        if (url is null || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)) return null;
        var match = PathPattern().Match(uri.AbsolutePath);
        return match.Success ? new GitHubRepositoryName(match.Groups["owner"].Value, match.Groups["name"].Value) : null;
    }

    [GeneratedRegex(@"^/(?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/(?<name>[A-Za-z0-9._-]+?)(?:\.git)?/?$")]
    private static partial Regex PathPattern();
}
