using System.Text.RegularExpressions;

namespace Workstream.Api.Domain;

/// <summary>A repository on GitHub, by its owner and its name.</summary>
public sealed partial record GitHubRepositoryName(string Owner, string Name)
{
    public string FullName => $"{Owner}/{Name}";

    /// <summary>The address an environment clones.</summary>
    public string Url => $"https://github.com/{Owner}/{Name}";

    /// <summary>The repository an environment's address names, when it is one on github.com; null for any other address.</summary>
    public static GitHubRepositoryName? Of(string? url)
    {
        if (url is null || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)) return null;
        var match = PathPattern().Match(uri.AbsolutePath);
        return match.Success ? new GitHubRepositoryName(match.Groups["owner"].Value, match.Groups["name"].Value) : null;
    }

    /// <summary>A repository as GitHub writes it, <c>owner/name</c>; null for anything else.</summary>
    public static GitHubRepositoryName? Parse(string? owner, string? name)
    {
        if (owner is null || name is null) return null;
        return OwnerPattern().IsMatch(owner) && NamePattern().IsMatch(name) && name is not ("." or "..") ? new GitHubRepositoryName(owner, name) : null;
    }

    [GeneratedRegex(@"^/(?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/(?<name>[A-Za-z0-9._-]+?)(?:\.git)?/?$")]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")]
    private static partial Regex OwnerPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex NamePattern();
}
