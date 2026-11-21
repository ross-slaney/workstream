using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Workstream.Api;

namespace Workstream.Tests;

/// <summary>
/// A person's command line (cli/workstream.mjs), signed in the way it does it: it reads <c>/cli</c>, asks SqlOS
/// for a device code, and the person opens the printed link in a browser, which lands on the web app's device
/// page, signs in there through SqlOS's headless flow and approves. The command line's next poll gets the
/// tokens. The browser here is an HTTP client with cookies, as the web app's page is (it sends credentials).
/// </summary>
public static class CommandLine
{
    /// <summary>A signed-in command line: the person calling with its token, and what it holds to refresh and sign out.</summary>
    public sealed record Session(Person Person, string AccessToken, string RefreshToken, string Scope) : IDisposable
    {
        public void Dispose() => Person.Dispose();
    }

    /// <summary>The device code the command line is given, and the link it prints.</summary>
    public sealed record Device(string DeviceCode, string UserCode, string Link);

    public static async Task<CliSettings> SettingsAsync()
    {
        using var http = new HttpClient { BaseAddress = Stack.Origin };
        return await Person.ReadAsync<CliSettings>(await http.GetAsync("/cli"));
    }

    /// <summary>What <c>workstream login</c> does first: ask SqlOS for a device code for this API.</summary>
    public static async Task<Device> StartAsync()
    {
        var settings = await SettingsAsync();
        using var http = new HttpClient { BaseAddress = Stack.Origin };
        using var response = await http.PostAsync("/sqlos/auth/device_authorization", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId, ["scope"] = settings.Scope, ["resource"] = settings.Resource,
        }));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.IsTrue(response.IsSuccessStatusCode, body.ToJsonString());
        return new Device(body["device_code"]!.GetValue<string>(), body["user_code"]!.GetValue<string>(), body["verification_uri_complete"]!.GetValue<string>());
    }

    /// <summary>One poll of the token endpoint, as the command line makes it: the tokens, or SqlOS's error code.</summary>
    public static async Task<(JsonObject? Tokens, string? Error)> PollAsync(Device device)
    {
        var settings = await SettingsAsync();
        using var http = new HttpClient { BaseAddress = Stack.Origin };
        using var response = await http.PostAsync("/sqlos/auth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["client_id"] = settings.ClientId,
            ["device_code"] = device.DeviceCode, ["resource"] = settings.Resource,
        }));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        return response.IsSuccessStatusCode ? (body, null) : (null, body["error"]?.GetValue<string>());
    }

    /// <summary>The person signs the command line in and approves it; then its poll gets a session of theirs.</summary>
    public static async Task<Session> SignInAsync(string email, string password = Person.Password)
    {
        var device = await StartAsync();
        using (var browser = await Browser.OpenAsync(device))
        {
            await browser.SignInAsync(email, password);
            await browser.ApproveAsync();
        }
        var (tokens, error) = await PollAsync(device);
        Assert.IsNull(error, $"The command line's poll was refused: {error}");
        var access = tokens!["access_token"]!.GetValue<string>();
        var person = Person.Holding(access, email, "");
        var me = await person.GetAsync<MeDto>("/api/me");
        person.Dispose();
        return new Session(Person.Holding(access, email, me.UserId), access, tokens["refresh_token"]!.GetValue<string>(), tokens["scope"]?.GetValue<string>() ?? "");
    }

    /// <summary>
    /// The person's browser on the device page (client/app/device.tsx): the link the command line printed, SqlOS's
    /// hand-off to the web app, and the page's headless steps, with SqlOS's session cookie kept between them.
    /// </summary>
    public sealed class Browser : IDisposable
    {
        readonly HttpClient http = new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = Stack.Origin };

        public string RequestId { get; private set; } = "";
        /// <summary>Where SqlOS handed the browser: the web app's device page.</summary>
        public Uri Page { get; private set; } = null!;

        public static async Task<Browser> OpenAsync(Device device)
        {
            var browser = new Browser();
            using var opened = await browser.http.GetAsync(device.Link);
            Assert.AreEqual(HttpStatusCode.Redirect, opened.StatusCode, await opened.Content.ReadAsStringAsync());
            browser.Page = opened.Headers.Location!;
            browser.RequestId = QueryHelpers.ParseQuery(browser.Page.Query)["request"].ToString();
            return browser;
        }

        /// <summary>What the page shows for the request: SqlOS's view model, as the page's <c>resume</c> reads it.</summary>
        public async Task<JsonObject> ViewAsync(string view = "device")
        {
            using var response = await http.GetAsync($"/sqlos/auth/headless/requests/{Uri.EscapeDataString(RequestId)}?view={view}");
            var text = await response.Content.ReadAsStringAsync();
            Assert.IsTrue(response.IsSuccessStatusCode, text);
            return JsonNode.Parse(text)!.AsObject();
        }

        public async Task<JsonObject> StepAsync(string step, JsonObject body)
        {
            using var response = await http.PostAsJsonAsync($"/sqlos/auth/headless/{step}", body);
            var text = await response.Content.ReadAsStringAsync();
            Assert.IsTrue(response.IsSuccessStatusCode, $"{step} answered {(int)response.StatusCode}: {text}");
            return JsonNode.Parse(text)!.AsObject();
        }

        /// <summary>Signed out, the page shows sign-in for the request; signing in ends at SqlOS's approval step.</summary>
        public async Task SignInAsync(string email, string password)
        {
            Assert.AreEqual("device", (await ViewAsync())["view"]?.GetValue<string>());
            var result = await StepAsync("password/login", new JsonObject { ["requestId"] = RequestId, ["email"] = email, ["password"] = password });
            if (result["viewModel"]?["view"]?.GetValue<string>() == "organization")
            {
                // On several teams the page picks the first, as sign-in does: the API asks for a team by its address.
                var team = result["viewModel"]!["organizationSelection"]![0]!["id"]!.GetValue<string>();
                result = await StepAsync("organization/select", new JsonObject { ["requestId"] = RequestId, ["organizationId"] = team });
            }
            Assert.AreEqual("redirect", result["type"]?.GetValue<string>(), result.ToJsonString());
        }

        /// <summary>The page reads the request again, now signed in, shows what is asked, and the person allows it.</summary>
        public async Task ApproveAsync()
        {
            var view = await ViewAsync();
            Assert.AreEqual("device-approve", view["view"]?.GetValue<string>(), view.ToJsonString());
            var approved = await StepAsync("device/approve", new JsonObject { ["requestId"] = RequestId });
            Assert.AreEqual("device-approved", approved["viewModel"]?["view"]?.GetValue<string>(), approved.ToJsonString());
        }

        public async Task DenyAsync()
        {
            var denied = await StepAsync("device/deny", new JsonObject { ["requestId"] = RequestId });
            Assert.AreEqual("device-denied", denied["viewModel"]?["view"]?.GetValue<string>(), denied.ToJsonString());
        }

        public void Dispose() => http.Dispose();
    }
}
