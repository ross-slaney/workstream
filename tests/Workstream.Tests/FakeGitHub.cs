using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;

namespace Workstream.Tests;

/// <summary>
/// GitHub, played under the API's own GitHub client: the app's installations and their repositories, the codes
/// GitHub hands back after an installation and the people's tokens they become, and the installation tokens
/// the app mints. It checks what real GitHub checks: the app's JWT is RS256 with the app's key, a code is used
/// once, and a token reaches only the repositories of its installation.
/// </summary>
public sealed class FakeGitHub : HttpMessageHandler
{
    public const string AppId = "424242";
    public const string AppSlug = "workstream-journeys";
    public const string ClientId = "Iv1.journeys";
    public const string ClientSecret = "github-client-secret-for-journeys";
    public const string WebhookSecret = "github-webhook-secret-for-journeys";
    public const string Api = "http://api.github.test";
    public const string Web = "http://github.test";

    public sealed record Repository(string Name, bool Private = true, string DefaultBranch = "main");

    public sealed record Installation(long Id, string Account, string Type, IReadOnlyList<Repository> Repositories);

    /// <summary>A token the app minted: for which installation, which repositories (all when null), with what permissions.</summary>
    public sealed record Minted(string Token, long InstallationId, IReadOnlyList<string>? Repositories, IReadOnlyDictionary<string, string> Permissions, DateTimeOffset ExpiresAt);

    readonly RSA key = RSA.Create(2048);
    readonly ConcurrentDictionary<long, Installation> installations = new();
    readonly ConcurrentDictionary<string, long[]> codes = new();
    readonly ConcurrentDictionary<string, long[]> people = new();
    long lastId = 7_000_000;

    public ConcurrentDictionary<string, Minted> Tokens { get; } = new();

    /// <summary>The app's private key, as GitHub hands it to whoever made the app.</summary>
    public string PrivateKeyPem => key.ExportRSAPrivateKeyPem();

    /// <summary>The app installed on a new account of its own, reaching these repositories.</summary>
    public Installation Install(string type = "Organization", params string[] repositories)
    {
        var id = Interlocked.Increment(ref lastId);
        var installation = new Installation(id, $"acme-{id}", type, [.. repositories.Select(name => new Repository(name, Private: name != "site"))]);
        installations[id] = installation;
        return installation;
    }

    /// <summary>The app removed from its account.</summary>
    public void Uninstall(long id) => installations.TryRemove(id, out _);

    /// <summary>The code GitHub hands back to someone whose account reaches these installations.</summary>
    public string CodeFor(params long[] reachable)
    {
        var code = "code-" + Guid.NewGuid().ToString("N");
        codes[code] = reachable;
        return code;
    }

    /// <summary>The tokens minted for an installation, newest last.</summary>
    public IReadOnlyList<Minted> MintedFor(long installationId) => [.. Tokens.Values.Where(x => x.InstallationId == installationId).OrderBy(x => x.ExpiresAt)];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath;
        var query = QueryHelpers.ParseQuery(uri.Query);
        var bearer = request.Headers.Authorization?.Scheme == "Bearer" ? request.Headers.Authorization.Parameter : null;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var site = uri.GetLeftPart(UriPartial.Authority);

        if (site == Web && request.Method == HttpMethod.Post && path == "/login/oauth/access_token")
        {
            var form = QueryHelpers.ParseQuery(body);
            if (form["client_id"] != ClientId || form["client_secret"] != ClientSecret) return Json(new { error = "incorrect_client_credentials" });
            // A code works once.
            if (!codes.TryRemove(form["code"].ToString(), out var reachable)) return Json(new { error = "bad_verification_code" });
            var token = "ghu_" + Guid.NewGuid().ToString("N");
            people[token] = reachable;
            return Json(new { access_token = token, token_type = "bearer", scope = "" });
        }

        if (site != Api) return new HttpResponseMessage(HttpStatusCode.NotFound);

        if (request.Method == HttpMethod.Get && path == "/user/installations")
        {
            if (bearer is null || !people.TryGetValue(bearer, out var reachable)) return Status(HttpStatusCode.Unauthorized);
            var page = int.Parse(query["page"].FirstOrDefault() ?? "1");
            var list = page == 1 ? reachable.Where(installations.ContainsKey).Select(id => installations[id]).ToList() : [];
            return Json(new { total_count = list.Count, installations = list.Select(x => new { id = x.Id, account = new { login = x.Account, type = x.Type } }) });
        }

        if (request.Method == HttpMethod.Post && path.StartsWith("/app/installations/", StringComparison.Ordinal) && path.EndsWith("/access_tokens", StringComparison.Ordinal))
        {
            if (!IsAppToken(bearer)) return Status(HttpStatusCode.Unauthorized);
            var id = long.Parse(path["/app/installations/".Length..^"/access_tokens".Length]);
            if (!installations.TryGetValue(id, out var installation)) return Status(HttpStatusCode.NotFound);
            var asked = JsonNode.Parse(body)!.AsObject();
            var repositories = asked["repositories"]?.AsArray().Select(x => x!.GetValue<string>()).ToList();
            if (repositories is not null && repositories.Any(name => installation.Repositories.All(x => x.Name != name)))
            {
                return Status(HttpStatusCode.UnprocessableEntity, "There is at least one repository that does not exist or is not accessible to the parent installation.");
            }
            var permissions = asked["permissions"]!.AsObject().ToDictionary(x => x.Key, x => x.Value!.GetValue<string>());
            var minted = new Minted("ghs_" + Guid.NewGuid().ToString("N"), id, repositories, permissions, DateTimeOffset.UtcNow.AddHours(1));
            Tokens[minted.Token] = minted;
            return Json(new { token = minted.Token, expires_at = minted.ExpiresAt, permissions }, HttpStatusCode.Created);
        }

        if (request.Method == HttpMethod.Get && path == "/installation/repositories")
        {
            if (bearer is null || !Tokens.TryGetValue(bearer, out var minted) || !installations.TryGetValue(minted.InstallationId, out var installation)) return Status(HttpStatusCode.Unauthorized);
            var page = int.Parse(query["page"].FirstOrDefault() ?? "1");
            var list = page == 1 ? installation.Repositories : [];
            return Json(new
            {
                total_count = list.Count,
                repositories = list.Select(x => new { full_name = $"{installation.Account}/{x.Name}", html_url = $"https://github.com/{installation.Account}/{x.Name}", default_branch = x.DefaultBranch, @private = x.Private }),
            });
        }

        return Status(HttpStatusCode.NotFound);
    }

    /// <summary>The app's JWT as GitHub reads it: RS256, signed with the app's key, issued for the app, good for ten minutes at most.</summary>
    bool IsAppToken(string? jwt)
    {
        var parts = jwt?.Split('.');
        if (parts is not { Length: 3 }) return false;
        var header = JsonNode.Parse(WebEncoders.Base64UrlDecode(parts[0]))!;
        if (header["alg"]?.GetValue<string>() != "RS256") return false;
        if (!key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), WebEncoders.Base64UrlDecode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return false;
        var payload = JsonNode.Parse(WebEncoders.Base64UrlDecode(parts[1]))!;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var issued = payload["iat"]!.GetValue<long>();
        var expires = payload["exp"]!.GetValue<long>();
        return payload["iss"]?.GetValue<string>() == AppId && issued <= now && expires > now && expires - issued <= 600;
    }

    /// <summary>A webhook delivery, signed as GitHub signs it.</summary>
    public static string Signature(string body, string secret = WebhookSecret) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    static HttpResponseMessage Status(HttpStatusCode status, string message = "") => Json(new { message }, status);
}
