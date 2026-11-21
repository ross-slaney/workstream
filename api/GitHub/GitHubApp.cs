using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workstream.Api.GitHub;

/// <summary>
/// Workstream's GitHub App (<c>Workstream:GitHub</c>): the app a team administrator installs on their GitHub
/// account or organization, and the OAuth client of the same app, which proves the person who installed it can
/// reach that installation. Empty settings leave the feature "not set up" (infra/README.md, "GitHub").
/// </summary>
public sealed class GitHubOptions
{
    /// <summary>The app's id, which its tokens are signed for (the JWT's <c>iss</c>).</summary>
    public string AppId { get; set; } = "";
    /// <summary>The app's name in its address, https://github.com/apps/{slug}.</summary>
    public string AppSlug { get; set; } = "";
    /// <summary>The app's OAuth client: GitHub hands back a code for the person who installed it.</summary>
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>The app's private key, as GitHub gives it (PEM), or that PEM encoded in base64.</summary>
    public string PrivateKey { get; set; } = "";
    /// <summary>Optional. Where set, GitHub's webhook deliveries are taken and checked with it.</summary>
    public string WebhookSecret { get; set; } = "";
    public string ApiUrl { get; set; } = "https://api.github.com";
    public string WebUrl { get; set; } = "https://github.com";

    public bool IsConfigured =>
        AppId.Length > 0 && AppSlug.Length > 0 && ClientId.Length > 0 && ClientSecret.Length > 0 && PrivateKeyPem() is not null;

    /// <summary>Where an administrator installs the app, carrying the state Workstream signed for them.</summary>
    public string InstallUrl(string state) => $"{Web}/apps/{Uri.EscapeDataString(AppSlug)}/installations/new?state={Uri.EscapeDataString(state)}";

    public string Web => WebUrl.TrimEnd('/');
    public string Api => ApiUrl.TrimEnd('/');

    /// <summary>
    /// The key as PEM. A secret store may keep its line breaks as the two characters <c>\n</c>, or the whole PEM
    /// in base64 (the deploy does that, infra/README.md); both are taken. Null when it is neither.
    /// </summary>
    public string? PrivateKeyPem()
    {
        var text = PrivateKey.Trim();
        if (text.Length == 0) return null;
        if (text.Contains("-----BEGIN", StringComparison.Ordinal)) return text.Replace("\\n", "\n");
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(text));
            return decoded.Contains("-----BEGIN", StringComparison.Ordinal) ? decoded : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The app's own token: a JWT signed RS256 with its private key, which is what GitHub takes to make an
    /// installation's token. Issued a minute in the past for clocks apart, and good for nine minutes (GitHub takes ten at most).
    /// </summary>
    public string AppToken(DateTimeOffset now)
    {
        var pem = PrivateKeyPem() ?? throw new InvalidOperationException("Workstream:GitHub:PrivateKey is not a PEM private key.");
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iat = now.AddSeconds(-60).ToUnixTimeSeconds(), exp = now.AddMinutes(9).ToUnixTimeSeconds(), iss = AppId }));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{Base64Url(signature)}";
    }

    /// <summary>Whether a webhook delivery's <c>X-Hub-Signature-256</c> is GitHub's for this body.</summary>
    public bool IsSignedDelivery(byte[] body, string? signature)
    {
        if (WebhookSecret.Length == 0 || signature is null || !signature.StartsWith("sha256=", StringComparison.Ordinal)) return false;
        byte[] given;
        try
        {
            given = Convert.FromHexString(signature["sha256=".Length..]);
        }
        catch (FormatException)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), body), given);
    }

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>An installation of the app: the GitHub account it is on (<c>Organization</c> or <c>User</c>).</summary>
public sealed record GitHubInstallation(long Id, string Account, string AccountType);

public sealed record GitHubRepository(string FullName, string Url, string DefaultBranch, bool Private);

/// <summary>An installation's token. GitHub makes them good for one hour.</summary>
public sealed record GitHubToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>GitHub answered, and not with success. <see cref="Status"/> is what it answered.</summary>
public sealed class GitHubRefused(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// The calls Workstream makes to GitHub, under one named client (<see cref="ClientName"/>), which the tests
/// play (tests/Workstream.Tests/FakeGitHub.cs). Nothing GitHub hands out is stored: an installation's token is
/// asked for when it is needed and given straight on.
/// </summary>
public sealed class GitHubApp(IHttpClientFactory clients, GitHubOptions options, TimeProvider clock)
{
    public const string ClientName = "github";
    /// <summary>At most this many pages of a hundred are read from a list GitHub pages.</summary>
    const int MaxPages = 10;

    /// <summary>What a desktop's token may do in its repository: push a branch and open a pull request.</summary>
    public static readonly IReadOnlyDictionary<string, string> DesktopPermissions = new Dictionary<string, string>
    {
        ["contents"] = "write", ["pull_requests"] = "write", ["metadata"] = "read",
    };

    /// <summary>What listing an installation's repositories needs: to see them.</summary>
    static readonly IReadOnlyDictionary<string, string> ListPermissions = new Dictionary<string, string> { ["metadata"] = "read" };

    /// <summary>The person's token for the code GitHub handed back after they installed the app. Null when GitHub refused the code.</summary>
    public async Task<string?> UserTokenAsync(string code, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.Web}/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId, ["client_secret"] = options.ClientSecret, ["code"] = code,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await Client().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        var answer = await response.Content.ReadFromJsonAsync<OAuthAnswer>(ct);
        return answer?.AccessToken is { Length: > 0 } token ? token : null;
    }

    /// <summary>The installation, when the person whose token this is can reach it on GitHub; null when they cannot.</summary>
    public async Task<GitHubInstallation?> UserInstallationAsync(string userToken, long installationId, CancellationToken ct)
    {
        for (var page = 1; page <= MaxPages; page++)
        {
            using var request = Request(HttpMethod.Get, $"/user/installations?per_page=100&page={page}", userToken);
            var answer = await SendAsync<InstallationsPage>(request, ct);
            if (answer.Installations.FirstOrDefault(x => x.Id == installationId) is { } found) return new(found.Id, found.Account.Login, found.Account.Type);
            if (answer.Installations.Count < 100) return null;
        }
        return null;
    }

    /// <summary>
    /// A new token for the installation, good for an hour. With <paramref name="repository"/>, for that one
    /// repository of the installation's account and the desktop's permissions; without, to list what it reaches.
    /// </summary>
    public async Task<GitHubToken> InstallationTokenAsync(long installationId, string? repository, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, $"/app/installations/{installationId}/access_tokens", options.AppToken(clock.GetUtcNow()));
        request.Content = JsonContent.Create(repository is null
            ? new TokenRequest(null, ListPermissions)
            : new TokenRequest([repository], DesktopPermissions));
        var answer = await SendAsync<TokenAnswer>(request, ct);
        return new GitHubToken(answer.Token, answer.ExpiresAt);
    }

    /// <summary>The repositories the installation reaches, A to Z.</summary>
    public async Task<IReadOnlyList<GitHubRepository>> RepositoriesAsync(long installationId, CancellationToken ct)
    {
        var token = await InstallationTokenAsync(installationId, null, ct);
        var repositories = new List<GitHubRepository>();
        for (var page = 1; page <= MaxPages; page++)
        {
            using var request = Request(HttpMethod.Get, $"/installation/repositories?per_page=100&page={page}", token.Token);
            var answer = await SendAsync<RepositoriesPage>(request, ct);
            repositories.AddRange(answer.Repositories.Select(x => new GitHubRepository(x.FullName, x.HtmlUrl, x.DefaultBranch ?? "main", x.Private)));
            if (answer.Repositories.Count < 100) break;
        }
        return [.. repositories.OrderBy(x => x.FullName, StringComparer.OrdinalIgnoreCase)];
    }

    HttpClient Client() => clients.CreateClient(ClientName);

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
        using var response = await Client().SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            throw new GitHubRefused(response.StatusCode, $"GitHub answered {(int)response.StatusCode}: {text[..Math.Min(text.Length, 300)]}");
        }
        return await response.Content.ReadFromJsonAsync<T>(ct) ?? throw new GitHubRefused(response.StatusCode, "GitHub answered with nothing.");
    }

    sealed record OAuthAnswer([property: JsonPropertyName("access_token")] string? AccessToken);
    sealed record Account([property: JsonPropertyName("login")] string Login, [property: JsonPropertyName("type")] string Type);
    sealed record Installation([property: JsonPropertyName("id")] long Id, [property: JsonPropertyName("account")] Account Account);
    sealed record InstallationsPage([property: JsonPropertyName("installations")] List<Installation> Installations);
    sealed record TokenRequest(
        [property: JsonPropertyName("repositories"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Repositories,
        [property: JsonPropertyName("permissions")] IReadOnlyDictionary<string, string> Permissions);
    sealed record TokenAnswer([property: JsonPropertyName("token")] string Token, [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
    sealed record Repository(
        [property: JsonPropertyName("full_name")] string FullName, [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("default_branch")] string? DefaultBranch, [property: JsonPropertyName("private")] bool Private);
    sealed record RepositoriesPage([property: JsonPropertyName("repositories")] List<Repository> Repositories);
}

public static class GitHubHosting
{
    public static WebApplicationBuilder AddGitHub(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(builder.Configuration.GetSection("Workstream:GitHub").Get<GitHubOptions>() ?? new GitHubOptions());
        builder.Services.AddHttpClient(GitHubApp.ClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Workstream", "1"));
        });
        builder.Services.AddTransient<GitHubApp>();
        return builder;
    }
}
