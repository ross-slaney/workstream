using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Workstream.Tests;

/// <summary>
/// GitHub as SqlOS meets it when a person signs in with GitHub, played under SqlOS's own client for providers:
/// the code GitHub hands back when someone approves, the token it becomes, and the profile and addresses that
/// token reads. It checks what real GitHub checks: the OAuth client's id and secret, that a code works once and
/// was issued for the callback it is redeemed with, and that a token reads only its own account.
/// </summary>
public sealed class FakeGitHubSignIn : HttpMessageHandler
{
    public const string ClientId = "Ov23journeys";
    public const string ClientSecret = "github-oauth-client-secret-for-journeys";
    public const string Web = "https://github.com";
    public const string Api = "https://api.github.com";

    /// <summary>Someone's GitHub account: its number, its name there, and its primary address, verified or not.</summary>
    public sealed record Account(long Id, string Login, string? Name, string Email, bool Verified);

    readonly ConcurrentDictionary<string, (Account Account, string Callback)> codes = new();
    readonly ConcurrentDictionary<string, Account> tokens = new();
    long lastId = 9_000_000;

    /// <summary>The scopes of every token handed out, in order: what SqlOS asked GitHub for.</summary>
    public ConcurrentQueue<string> Redeemed { get; } = new();

    public Account NewAccount(string email, string? name = "Grace Hopper", bool verified = true)
    {
        var id = Interlocked.Increment(ref lastId);
        return new Account(id, $"octo-{id}", name, email, verified);
    }

    /// <summary>The person approves on GitHub's page: the code GitHub sends them back to the callback with.</summary>
    public string Approve(Account account, Uri authorize)
    {
        var asked = QueryHelpers.ParseQuery(authorize.Query);
        Assert.AreEqual(ClientId, asked["client_id"].ToString());
        var code = "code-" + Guid.NewGuid().ToString("N");
        codes[code] = (account, asked["redirect_uri"].ToString());
        return code;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var site = uri.GetLeftPart(UriPartial.Authority);
        var bearer = request.Headers.Authorization?.Scheme == "Bearer" ? request.Headers.Authorization.Parameter : null;

        if (site == Web && request.Method == HttpMethod.Post && uri.AbsolutePath == "/login/oauth/access_token")
        {
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(ct));
            if (form["client_id"] != ClientId || form["client_secret"] != ClientSecret) return Json(new { error = "incorrect_client_credentials" });
            // A code works once, for the callback it was issued to.
            if (!codes.TryRemove(form["code"].ToString(), out var issued) || issued.Callback != form["redirect_uri"].ToString())
            {
                return Json(new { error = "bad_verification_code", error_description = "The code passed is incorrect or expired." });
            }
            var token = "gho_" + Guid.NewGuid().ToString("N");
            tokens[token] = issued.Account;
            Redeemed.Enqueue("read:user,user:email");
            return Json(new { access_token = token, token_type = "bearer", scope = "read:user,user:email" });
        }

        if (site == Api && request.Method == HttpMethod.Get && bearer is not null && tokens.TryGetValue(bearer, out var account))
        {
            if (uri.AbsolutePath == "/user") return Json(new { id = account.Id, login = account.Login, name = account.Name });
            if (uri.AbsolutePath == "/user/emails")
            {
                return Json(new object[]
                {
                    new { email = $"{account.Id}+{account.Login}@users.noreply.github.com", primary = false, verified = true, visibility = (string?)null },
                    new { email = account.Email, primary = true, verified = account.Verified, visibility = "private" },
                });
            }
        }

        return Json(new { message = site == Api ? "Bad credentials" : "Not Found" }, site == Api ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound);
    }

    static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
