using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Workstream.Api;

namespace Workstream.Tests;

/// <summary>
/// A person at the web app: signed in through SqlOS's headless flow exactly as the app does it (start; sign up
/// or sign in with a password, with an invitation's token when there is one; exchange the code), then calling
/// the API with the access token. Each new person gets a new address, so every journey starts clean.
/// </summary>
public sealed class Person : IDisposable
{
    public const string Password = "Correct-horse-9!";
    const string ClientId = WorkstreamApp.AppClientId;
    const string Scope = "openid profile email offline_access workstream.read workstream.write";
    static readonly string RedirectUri = Stack.WebOrigin + WorkstreamApp.WebRedirectPath;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly HttpClient http = new() { BaseAddress = Stack.Origin };

    public string Email { get; private init; } = "";
    public string Name { get; private init; } = "";
    public string UserId { get; private set; } = "";
    /// <summary>The access token this person's requests carry.</summary>
    public string Token => http.DefaultRequestHeaders.Authorization?.Parameter ?? "";

    public static string NewEmail() => $"person-{Guid.NewGuid():N}@workstream.test";

    /// <summary>Someone new: an account, and a team of their own unless they come by invitation.</summary>
    public static Task<Person> SignUpAsync(string name = "Ada Lovelace", string? email = null, string? invitationToken = null) =>
        SignAsync(email ?? NewEmail(), Password, name, invitationToken);

    public static Task<Person> SignInAsync(string email, string password = Password, string? invitationToken = null) =>
        SignAsync(email, password, null, invitationToken);

    /// <summary>A person and the one team they got by signing up.</summary>
    public static async Task<(Person Person, TeamDto Team)> WithTeamAsync(string name = "Ada Lovelace")
    {
        var person = await SignUpAsync(name);
        return (person, (await person.GetAsync<ListPage<TeamDto>>("/api/teams")).Data.Single());
    }

    /// <summary>The same person calling with another of their tokens: the command line's (CommandLine.cs).</summary>
    public static Person Holding(string accessToken, string email, string userId)
    {
        var person = new Person { Email = email, UserId = userId };
        person.http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return person;
    }

    static async Task<Person> SignAsync(string email, string password, string? name, string? invitationToken)
    {
        // The flow keeps its place in cookies, as a browser would.
        using var flow = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = Stack.Origin };
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var start = new JsonObject
        {
            ["responseType"] = "code", ["clientId"] = ClientId, ["redirectUri"] = RedirectUri, ["scope"] = Scope,
            ["state"] = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16)),
            ["codeChallenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), ["codeChallengeMethod"] = "S256",
        };
        // Given an invitation's token and no view, SqlOS answers with the invitation.
        if (invitationToken is null) start["view"] = name is null ? "login" : "signup";
        else start["invitationToken"] = invitationToken;
        var started = await StepAsync(flow, "start", start);
        var requestId = started["viewModel"]?["requestId"]?.GetValue<string>() ?? throw new InvalidOperationException($"SqlOS started no flow: {started.ToJsonString()}");

        var step = new JsonObject { ["requestId"] = requestId, ["email"] = email, ["password"] = password };
        if (name is not null) step["displayName"] = name;
        if (invitationToken is not null) step["invitationToken"] = invitationToken;
        var result = await StepAsync(flow, name is null ? "password/login" : "signup", step);
        if (result["redirectUrl"] is null)
        {
            if (result["viewModel"]?["error"]?.GetValue<string>() is { Length: > 0 } refused) throw new SignInRefused(refused);
            // On several teams: the session is for the first; the API asks for a team by its address.
            var team = result["viewModel"]?["organizationSelection"]?[0]?["id"]?.GetValue<string>() ?? throw new SignInRefused(result.ToJsonString());
            result = await StepAsync(flow, "organization/select", new JsonObject { ["requestId"] = requestId, ["organizationId"] = team });
        }
        var redirect = new Uri(result["redirectUrl"]?.GetValue<string>() ?? throw new SignInRefused(result.ToJsonString()));
        var code = QueryHelpers.ParseQuery(redirect.Query)["code"].ToString();
        using var tokens = await flow.PostAsync("/sqlos/auth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = ClientId, ["code"] = code, ["redirect_uri"] = RedirectUri, ["code_verifier"] = verifier,
        }));
        tokens.EnsureSuccessStatusCode();
        var granted = (await tokens.Content.ReadFromJsonAsync<JsonObject>())!;
        var person = new Person { Email = email, Name = name ?? "" };
        person.http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", granted["access_token"]!.GetValue<string>());
        person.UserId = (await person.GetAsync<MeDto>("/api/me")).UserId;
        return person;
    }

    static async Task<JsonObject> StepAsync(HttpClient flow, string step, JsonObject body)
    {
        using var response = await flow.PostAsJsonAsync($"/sqlos/auth/headless/{step}", body);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new SignInRefused($"{step} answered {(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    public Task<HttpResponseMessage> GetAsync(string path) => http.GetAsync(path);
    public Task<HttpResponseMessage> PostAsync(string path, object? body = null) => http.PostAsJsonAsync(path, body ?? new { }, Json);
    public Task<HttpResponseMessage> PutAsync(string path, object body) => http.PutAsJsonAsync(path, body, Json);
    public Task<HttpResponseMessage> DeleteAsync(string path) => http.DeleteAsync(path);
    /// <summary>A body sent as it is, such as a file.</summary>
    public Task<HttpResponseMessage> PutAsync(string path, HttpContent content) => http.PutAsync(path, content);

    public async Task<T> GetAsync<T>(string path) => await ReadAsync<T>(await GetAsync(path));
    public async Task<T> PostAsync<T>(string path, object? body = null) => await ReadAsync<T>(await PostAsync(path, body));
    public async Task<T> PutAsync<T>(string path, object body) => await ReadAsync<T>(await PutAsync(path, body));

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.IsTrue(response.IsSuccessStatusCode, $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery} answered {(int)response.StatusCode}: {text}");
            return JsonSerializer.Deserialize<T>(text, Json)!;
        }
    }

    public void Dispose() => http.Dispose();
}

/// <summary>SqlOS refused a sign-in or a sign-up, in its own words.</summary>
public sealed class SignInRefused(string message) : Exception(message);

/// <summary>Assertions about how the API answered.</summary>
public static class Answers
{
    public static async Task IsAsync(this Task<HttpResponseMessage> answer, HttpStatusCode expected, string? saying = null)
    {
        using var response = await answer;
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(expected, response.StatusCode, $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}: {text}");
        if (saying is not null) StringAssert.Contains(text, saying);
    }

    /// <summary>The token of an invitation, from the link the API hands the person who made it.</summary>
    public static string TokenOf(PendingInvitation invitation) =>
        QueryHelpers.ParseQuery(new Uri(invitation.Link ?? throw new InvalidOperationException("The invitation has no link.")).Query)["token"].ToString();
}
