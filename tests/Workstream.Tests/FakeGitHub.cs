using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Workstream.Api.GitHub;

namespace Workstream.Tests;

/// <summary>
/// GitHub's API as a person's token reaches it, played under the API's own GitHub client: accounts, the
/// repositories each can reach, their branches and files, and what was written to them (commits, branches,
/// pull requests). It checks what real GitHub checks: a token reads only its own account's repositories, a
/// protected branch takes no commit straight onto it, and someone who may only read cannot write.
/// </summary>
public sealed class FakeGitHub : HttpMessageHandler
{
    public const string Api = "http://api.github.test";

    /// <summary>A repository: its branches and, on each, its files by path. <see cref="Protected"/> branches take changes only through a pull request.</summary>
    public sealed class Repository(string owner, string name, bool canPush)
    {
        public string Owner { get; } = owner;
        public string Name { get; } = name;
        public string FullName => $"{Owner}/{Name}";
        public string Url => $"https://github.com/{FullName}";
        public string DefaultBranch { get; set; } = "main";
        public bool CanPush { get; } = canPush;
        public DateTimeOffset PushedAt { get; set; } = DateTimeOffset.UtcNow;
        public ConcurrentDictionary<string, ConcurrentDictionary<string, string>> Branches { get; } = new() { ["main"] = new() };
        public HashSet<string> Protected { get; } = [];
        public ConcurrentQueue<(string Branch, string Path, string Message)> Commits { get; } = new();
        public ConcurrentQueue<(string Head, string Base, string Title)> PullRequests { get; } = new();
        /// <summary>The workflow runs asked for: the workflow's file, the branch or tag, and the inputs as JSON.</summary>
        public ConcurrentQueue<(string Workflow, string Ref, string Inputs)> Runs { get; } = new();

        public Repository With(string path, string text, string branch = "main")
        {
            Branches.GetOrAdd(branch, _ => new())[path] = text;
            return this;
        }
    }

    readonly ConcurrentDictionary<string, List<Repository>> accounts = new();
    readonly ConcurrentDictionary<string, (long Id, string Login, string? Name)> users = new();
    readonly ConcurrentDictionary<string, Repository> repositories = new(StringComparer.OrdinalIgnoreCase);
    long last;

    /// <summary>Every request that reached GitHub, method and path: what the API asked.</summary>
    public ConcurrentQueue<string> Asked { get; } = new();

    /// <summary>A new GitHub account, and the token GitHub gave it.</summary>
    public string NewAccount(string? name = "Ada Lovelace")
    {
        var token = "gho_" + Guid.NewGuid().ToString("N");
        accounts[token] = [];
        var id = Interlocked.Increment(ref last) + 50_000_000;
        users[token] = (id, $"octo-{id}", name);
        return token;
    }

    /// <summary>Who a token's account is, as GitHub's <c>/user</c> says.</summary>
    public (long Id, string Login, string? Name) UserOf(string token) => users[token];

    /// <summary>A new repository of its own that the token's account reaches.</summary>
    public Repository Give(string token, string? name = null, bool canPush = true)
    {
        var id = Interlocked.Increment(ref last);
        var repository = new Repository($"acme-{id}", name ?? "app", canPush);
        repositories[repository.FullName] = repository;
        accounts.GetOrAdd(token, _ => []).Add(repository);
        return repository;
    }

    /// <summary>The account's token no longer works: the person revoked it on GitHub.</summary>
    public void Revoke(string token) => accounts.TryRemove(token, out _);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        if (uri.GetLeftPart(UriPartial.Authority) != Api) return Status(HttpStatusCode.NotFound);
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        Asked.Enqueue($"{request.Method} {path}");
        var parsed = QueryHelpers.ParseQuery(uri.Query);
        string? Query(string name) => parsed.TryGetValue(name, out var value) ? value.ToString() : null;
        var bearer = request.Headers.Authorization?.Scheme == "Bearer" ? request.Headers.Authorization.Parameter : null;
        if (bearer is null || !accounts.TryGetValue(bearer, out var reachable)) return Status(HttpStatusCode.Unauthorized, "Bad credentials");
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);

        if (request.Method == HttpMethod.Get && path == "/user" && users.TryGetValue(bearer, out var user))
        {
            return Json(new { id = user.Id, login = user.Login, name = user.Name });
        }

        if (request.Method == HttpMethod.Get && path == "/user/repos")
        {
            var page = int.Parse(Query("page") ?? "1");
            var perPage = int.Parse(Query("per_page") ?? "30");
            var list = reachable.OrderByDescending(x => x.PushedAt).Skip((page - 1) * perPage).Take(perPage);
            return Json(list.Select(Describe));
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || parts[0] != "repos") return Status(HttpStatusCode.NotFound);
        var repository = reachable.FirstOrDefault(x => string.Equals(x.FullName, $"{parts[1]}/{parts[2]}", StringComparison.OrdinalIgnoreCase));
        if (repository is null) return Status(HttpStatusCode.NotFound);
        var rest = string.Join('/', parts.Skip(3));

        if (request.Method == HttpMethod.Get && rest == "") return Json(Describe(repository));
        if (request.Method == HttpMethod.Get && rest == "branches")
        {
            var page = int.Parse(Query("page") ?? "1");
            var size = int.Parse(Query("per_page") ?? "30");
            return Json(repository.Branches.Keys.Order(StringComparer.Ordinal).Skip((page - 1) * size).Take(size).Select(name => new { name }));
        }
        if (request.Method == HttpMethod.Get && rest.StartsWith("git/ref/heads/", StringComparison.Ordinal))
        {
            var branch = rest["git/ref/heads/".Length..];
            return repository.Branches.ContainsKey(branch)
                ? Json(new { @ref = $"refs/heads/{branch}", @object = new { sha = CommitOf(branch) } })
                : Status(HttpStatusCode.NotFound);
        }
        if (rest.StartsWith("contents/", StringComparison.Ordinal))
        {
            var file = rest["contents/".Length..];
            if (request.Method == HttpMethod.Get)
            {
                var branch = Query("ref") ?? repository.DefaultBranch;
                if (!repository.Branches.TryGetValue(branch, out var files) || !files.TryGetValue(file, out var text)) return Status(HttpStatusCode.NotFound);
                return Json(new { type = "file", encoding = "base64", content = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), sha = Sha(text), path = file });
            }
            if (request.Method == HttpMethod.Put)
            {
                var put = JsonNode.Parse(body)!;
                var branch = put["branch"]?.GetValue<string>() ?? repository.DefaultBranch;
                if (!repository.CanPush) return Status(HttpStatusCode.Forbidden, "Resource not accessible by integration");
                if (repository.Protected.Contains(branch)) return Status(HttpStatusCode.Conflict, "Repository rule violations found. Changes must be made through a pull request.");
                if (!repository.Branches.TryGetValue(branch, out var files)) return Status(HttpStatusCode.NotFound, "Branch not found");
                // Replacing a file needs its blob, as GitHub asks.
                if (files.TryGetValue(file, out var existing) && put["sha"]?.GetValue<string>() != Sha(existing)) return Status(HttpStatusCode.UnprocessableEntity, "\"sha\" wasn't supplied.");
                var text = Encoding.UTF8.GetString(Convert.FromBase64String(put["content"]!.GetValue<string>()));
                files[file] = text;
                var message = put["message"]!.GetValue<string>();
                repository.Commits.Enqueue((branch, file, message));
                return Json(new { content = new { path = file, sha = Sha(text) }, commit = new { html_url = $"https://github.com/{repository.FullName}/commit/{Sha(text + branch)}" } }, HttpStatusCode.Created);
            }
        }
        if (request.Method == HttpMethod.Post && rest == "git/refs")
        {
            if (!repository.CanPush) return Status(HttpStatusCode.Forbidden, "Resource not accessible by integration");
            var asked = JsonNode.Parse(body)!;
            var name = asked["ref"]!.GetValue<string>()["refs/heads/".Length..];
            var from = BranchOfCommit(asked["sha"]!.GetValue<string>());
            if (from is null || !repository.Branches.TryGetValue(from, out var files)) return Status(HttpStatusCode.UnprocessableEntity, "Object does not exist");
            repository.Branches[name] = new ConcurrentDictionary<string, string>(files);
            return Json(new { @ref = $"refs/heads/{name}", @object = new { sha = CommitOf(name) } }, HttpStatusCode.Created);
        }
        if (request.Method == HttpMethod.Post && rest.StartsWith("actions/workflows/", StringComparison.Ordinal) && rest.EndsWith("/dispatches", StringComparison.Ordinal))
        {
            if (!repository.CanPush) return Status(HttpStatusCode.Forbidden, "Must have admin rights to Repository.");
            var run = System.Text.Json.Nodes.JsonNode.Parse(body)!;
            repository.Runs.Enqueue((rest["actions/workflows/".Length..^"/dispatches".Length], run["ref"]!.GetValue<string>(), run["inputs"]!.ToJsonString()));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (request.Method == HttpMethod.Post && rest == "pulls")
        {
            var asked = JsonNode.Parse(body)!;
            var head = asked["head"]!.GetValue<string>();
            repository.PullRequests.Enqueue((head, asked["base"]!.GetValue<string>(), asked["title"]!.GetValue<string>()));
            return Json(new { html_url = $"https://github.com/{repository.FullName}/pull/{repository.PullRequests.Count}" }, HttpStatusCode.Created);
        }
        return Status(HttpStatusCode.NotFound);
    }

    static object Describe(Repository x) => new
    {
        full_name = x.FullName, html_url = x.Url, default_branch = x.DefaultBranch, @private = true, pushed_at = x.PushedAt,
    };

    /// <summary>A branch's commit is its name, so a tree is found by it again.</summary>
    static string CommitOf(string branch) => "c0" + Convert.ToHexString(Encoding.UTF8.GetBytes(branch)).ToLowerInvariant();

    static string? BranchOfCommit(string commit) =>
        commit.StartsWith("c0", StringComparison.Ordinal) ? Encoding.UTF8.GetString(Convert.FromHexString(commit[2..])) : null;

    static string Sha(string text) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    static HttpResponseMessage Status(HttpStatusCode status, string message = "") => Json(new { message }, status);
}

/// <summary>
/// The people's GitHub tokens, as SqlOS would keep them from their sign-in with GitHub: a journey gives a
/// person one (<see cref="Give"/>) or takes it away. Everyone else has none.
/// </summary>
public sealed class FakeGitHubTokens : IGitHubTokens
{
    readonly ConcurrentDictionary<string, string> tokens = new();

    public void Give(string userId, string token) => tokens[userId] = token;

    public void Forget(string userId) => tokens.TryRemove(userId, out _);

    public Task<string?> ForUserAsync(string userId, CancellationToken ct) => Task.FromResult(tokens.TryGetValue(userId, out var token) ? token : null);
}
