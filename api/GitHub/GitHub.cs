using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Workstream.Api.Domain;

namespace Workstream.Api.GitHub;

/// <summary>Where GitHub's API is (<c>Workstream:GitHub</c>). Only the tests change it.</summary>
public sealed class GitHubOptions
{
    public string ApiUrl { get; set; } = "https://api.github.com";

    public string Api => ApiUrl.TrimEnd('/');
}

/// <summary>
/// A person's GitHub token: the one GitHub gave SqlOS when they signed in to Workstream with GitHub. Every read
/// of it goes through here, so nothing else knows where it is kept. Null when the person never signed in with
/// GitHub, or GitHub no longer accepts what was kept.
/// </summary>
public interface IGitHubTokens
{
    Task<string?> ForUserAsync(string userId, CancellationToken ct);
}

/// <summary>
/// No tokens: SqlOS 11.4.1 does not keep the provider's token, so nobody has one yet. Replaced by the SqlOS
/// implementation once SqlOS keeps them (docs/core-model.md, "Sign up and sign in").
/// </summary>
public sealed class NoGitHubTokens : IGitHubTokens
{
    public Task<string?> ForUserAsync(string userId, CancellationToken ct) => Task.FromResult<string?>(null);
}

/// <summary>A repository the person can reach on GitHub. <see cref="Url"/> is what an environment clones.</summary>
public sealed record GitHubRepository(string FullName, string Url, string DefaultBranch, bool Private, DateTimeOffset? PushedAt);

/// <summary>A GitHub account: its number, its login and the name it shows, when it has one.</summary>
public sealed record GitHubUser(long Id, string Login, string? Name)
{
    /// <summary>The address GitHub gives every account for commits, which links them to it without showing a real address.</summary>
    public string NoReplyEmail => $"{Id}+{Login}@users.noreply.github.com";
}

/// <summary>A file in a repository: its text and the blob's id, which GitHub needs to replace it.</summary>
public sealed record GitHubFile(string Path, string Text, string Sha);

/// <summary>A file in a repository's tree, with its size in bytes.</summary>
public sealed record GitHubTreeEntry(string Path, long Size);

/// <summary>
/// A commit's files. <see cref="Truncated"/> says GitHub stopped listing them (a very large repository): what is
/// listed is still right, just not all.
/// </summary>
public sealed record GitHubTree(string Commit, IReadOnlyList<GitHubTreeEntry> Files, bool Truncated);

/// <summary>GitHub answered, and not with success. <see cref="Status"/> is what it answered.</summary>
public sealed class GitHubRefused(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// The calls Workstream makes to GitHub's API as a person, with their own token, under one named client
/// (<see cref="ClientName"/>) that the tests play (tests/Workstream.Tests/FakeGitHub.cs). Nothing here keeps a
/// token: it is handed in for each call.
/// </summary>
public sealed class GitHubClient(IHttpClientFactory clients, GitHubOptions options)
{
    public const string ClientName = "github";
    /// <summary>At most this many pages of a hundred are read of a list GitHub pages.</summary>
    const int MaxPages = 3;
    const int PerPage = 100;

    /// <summary>The account the token belongs to.</summary>
    public async Task<GitHubUser> UserAsync(string token, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, "/user", token);
        var answer = await SendAsync<Account>(request, ct);
        return new GitHubUser(answer.Id, answer.Login, string.IsNullOrWhiteSpace(answer.Name) ? null : answer.Name.Trim());
    }

    /// <summary>The repositories the person can reach, most recently pushed first, and whether there are more than were read.</summary>
    public async Task<(IReadOnlyList<GitHubRepository> Repositories, bool More)> RepositoriesAsync(string token, CancellationToken ct)
    {
        var repositories = new List<GitHubRepository>();
        for (var page = 1; page <= MaxPages; page++)
        {
            using var request = Request(HttpMethod.Get, $"/user/repos?sort=pushed&direction=desc&per_page={PerPage}&page={page}", token);
            var answer = await SendAsync<List<Repository>>(request, ct);
            repositories.AddRange(answer.Select(x => x.ToRepository()));
            if (answer.Count < PerPage) return (repositories, false);
        }
        return (repositories, true);
    }

    /// <summary>One repository, or null when the person cannot reach it.</summary>
    public async Task<GitHubRepository?> RepositoryAsync(string token, GitHubRepositoryName repository, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, $"/repos/{Path(repository)}", token);
        try
        {
            return (await SendAsync<Repository>(request, ct)).ToRepository();
        }
        catch (GitHubRefused refused) when (refused.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>The repository's branches, by name.</summary>
    public async Task<IReadOnlyList<string>> BranchesAsync(string token, GitHubRepositoryName repository, CancellationToken ct)
    {
        var branches = new List<string>();
        for (var page = 1; page <= MaxPages; page++)
        {
            using var request = Request(HttpMethod.Get, $"/repos/{Path(repository)}/branches?per_page={PerPage}&page={page}", token);
            var answer = await SendAsync<List<Branch>>(request, ct);
            branches.AddRange(answer.Select(x => x.Name));
            if (answer.Count < PerPage) break;
        }
        return branches;
    }

    /// <summary>The commit a branch is at, or null when there is no such branch.</summary>
    public async Task<string?> BranchCommitAsync(string token, GitHubRepositoryName repository, string branch, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, $"/repos/{Path(repository)}/git/ref/heads/{Segments(branch)}", token);
        try
        {
            return (await SendAsync<Reference>(request, ct)).Object.Sha;
        }
        catch (GitHubRefused refused) when (refused.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Every file at the branch's commit, or null when there is no such branch.</summary>
    public async Task<GitHubTree?> TreeAsync(string token, GitHubRepositoryName repository, string branch, CancellationToken ct)
    {
        if (await BranchCommitAsync(token, repository, branch, ct) is not { } commit) return null;
        using var request = Request(HttpMethod.Get, $"/repos/{Path(repository)}/git/trees/{commit}?recursive=1", token);
        var answer = await SendAsync<Tree>(request, ct);
        return new GitHubTree(commit, [.. answer.Entries.Where(x => x.Type == "blob").Select(x => new GitHubTreeEntry(x.Path, x.Size ?? 0))], answer.Truncated);
    }

    /// <summary>A file on a branch (the repository's default one when null), read as UTF-8 text; null when it is not there or is not a file.</summary>
    public async Task<GitHubFile?> FileAsync(string token, GitHubRepositoryName repository, string? branch, string path, CancellationToken ct)
    {
        var at = branch is null ? "" : $"?ref={Uri.EscapeDataString(branch)}";
        using var request = Request(HttpMethod.Get, $"/repos/{Path(repository)}/contents/{Segments(path)}{at}", token);
        Contents answer;
        try
        {
            answer = await SendAsync<Contents>(request, ct);
        }
        catch (GitHubRefused refused) when (refused.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            // A folder answers with a list.
            return null;
        }
        if (answer.Type != "file" || answer.Content is null) return null;
        try
        {
            return new GitHubFile(path, Encoding.UTF8.GetString(Convert.FromBase64String(answer.Content.Replace("\n", ""))), answer.Sha);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Write a file on a branch as one commit, as the person. <paramref name="sha"/> is the file's blob when it
    /// replaces one. Answers the commit's address on GitHub.
    /// </summary>
    public async Task<string> PutFileAsync(string token, GitHubRepositoryName repository, string branch, string path, string text, string message, string? sha, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Put, $"/repos/{Path(repository)}/contents/{Segments(path)}", token);
        request.Content = JsonContent.Create(new PutContents(message, Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), branch, sha));
        var answer = await SendAsync<PutAnswer>(request, ct);
        return answer.Commit.HtmlUrl;
    }

    /// <summary>A new branch at a commit.</summary>
    public async Task CreateBranchAsync(string token, GitHubRepositoryName repository, string branch, string commit, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, $"/repos/{Path(repository)}/git/refs", token);
        request.Content = JsonContent.Create(new NewReference($"refs/heads/{branch}", commit));
        await SendAsync<Reference>(request, ct);
    }

    /// <summary>A pull request from one branch into another, as the person. Answers its address on GitHub.</summary>
    public async Task<string> OpenPullRequestAsync(string token, GitHubRepositoryName repository, string head, string into, string title, string body, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, $"/repos/{Path(repository)}/pulls", token);
        request.Content = JsonContent.Create(new NewPullRequest(title, head, into, body));
        return (await SendAsync<PullRequest>(request, ct)).HtmlUrl;
    }

    static string Path(GitHubRepositoryName repository) => $"{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}";

    /// <summary>A branch or a file's path in an address: each part escaped, the slashes kept.</summary>
    static string Segments(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    HttpRequestMessage Request(HttpMethod method, string path, string bearer)
    {
        var request = new HttpRequestMessage(method, options.Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await clients.CreateClient(ClientName).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            throw new GitHubRefused(response.StatusCode, $"GitHub answered {(int)response.StatusCode}: {text[..Math.Min(text.Length, 300)]}");
        }
        return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new GitHubRefused(response.StatusCode, "GitHub answered with nothing.");
    }

    sealed record Repository(
        [property: JsonPropertyName("full_name")] string FullName, [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("default_branch")] string? DefaultBranch, [property: JsonPropertyName("private")] bool Private,
        [property: JsonPropertyName("pushed_at")] DateTimeOffset? PushedAt)
    {
        public GitHubRepository ToRepository() => new(FullName, HtmlUrl, DefaultBranch ?? "main", Private, PushedAt);
    }

    sealed record Account([property: JsonPropertyName("id")] long Id, [property: JsonPropertyName("login")] string Login, [property: JsonPropertyName("name")] string? Name);
    sealed record Branch([property: JsonPropertyName("name")] string Name);
    sealed record ReferenceObject([property: JsonPropertyName("sha")] string Sha);
    sealed record Reference([property: JsonPropertyName("ref")] string Ref, [property: JsonPropertyName("object")] ReferenceObject Object);
    sealed record NewReference([property: JsonPropertyName("ref")] string Ref, [property: JsonPropertyName("sha")] string Sha);
    sealed record TreeEntry([property: JsonPropertyName("path")] string Path, [property: JsonPropertyName("type")] string Type, [property: JsonPropertyName("size")] long? Size);
    sealed record Tree([property: JsonPropertyName("tree")] List<TreeEntry> Entries, [property: JsonPropertyName("truncated")] bool Truncated);
    sealed record Contents(
        [property: JsonPropertyName("type")] string Type, [property: JsonPropertyName("content")] string? Content, [property: JsonPropertyName("sha")] string Sha);
    sealed record PutContents(
        [property: JsonPropertyName("message")] string Message, [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("branch")] string Branch,
        [property: JsonPropertyName("sha"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Sha);
    sealed record Commit([property: JsonPropertyName("html_url")] string HtmlUrl);
    sealed record PutAnswer([property: JsonPropertyName("commit")] Commit Commit);
    sealed record NewPullRequest(
        [property: JsonPropertyName("title")] string Title, [property: JsonPropertyName("head")] string Head,
        [property: JsonPropertyName("base")] string Base, [property: JsonPropertyName("body")] string Body);
    sealed record PullRequest([property: JsonPropertyName("html_url")] string HtmlUrl);
}

public static class GitHubHosting
{
    public static WebApplicationBuilder AddGitHub(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(builder.Configuration.GetSection("Workstream:GitHub").Get<GitHubOptions>() ?? new GitHubOptions());
        builder.Services.AddHttpClient(GitHubClient.ClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Workstream", "1"));
        });
        builder.Services.AddTransient<GitHubClient>();
        // Until SqlOS keeps the provider's token, nobody has one.
        builder.Services.AddSingleton<IGitHubTokens, NoGitHubTokens>();
        return builder;
    }
}
