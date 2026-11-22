using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using SqlOS.AuthServer.Configuration;
using SqlOS.AuthServer.Contracts;
using Workstream.Api;

namespace Workstream.Tests;

/// <summary>
/// Sign in with GitHub, beside the emailed code. SqlOS owns it: the sign-in page lists the provider, SqlOS sends
/// the browser to GitHub and takes it back at its own callback, and the person it finds or makes is signed in to
/// the web app like anyone else. GitHub is played (<see cref="FakeGitHubSignIn"/>); the browser is an HTTP client
/// with cookies that follows the redirects a real one would.
/// </summary>
[TestClass]
public sealed class GitHubSignInJourneys
{
    static readonly string Origin = Stack.Origin.GetLeftPart(UriPartial.Authority);

    /// <summary>
    /// A browser on the web app's sign-in page (client/app/sign-in.tsx): the flow it starts, the GitHub button,
    /// the round trip, and the page it lands on afterwards.
    /// </summary>
    sealed class SignInPage : IDisposable
    {
        const string Scope = "openid profile email offline_access workstream.read workstream.write";
        static readonly string RedirectUri = Stack.WebOrigin + WorkstreamApp.WebRedirectPath;

        readonly HttpClient http = new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = Stack.Origin };
        readonly string verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        readonly string state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));

        public string RequestId { get; private set; } = "";
        /// <summary>What the page draws: SqlOS's view model for the step on screen.</summary>
        public JsonObject View { get; private set; } = [];

        public static async Task<SignInPage> OpenAsync()
        {
            var page = new SignInPage();
            var started = await page.StepAsync("start", new JsonObject
            {
                ["responseType"] = "code", ["clientId"] = WorkstreamApp.AppClientId, ["redirectUri"] = RedirectUri, ["scope"] = Scope, ["state"] = page.state,
                ["codeChallenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(page.verifier))), ["codeChallengeMethod"] = "S256", ["view"] = "login",
            });
            page.View = started["viewModel"]!.AsObject();
            page.RequestId = page.View["requestId"]!.GetValue<string>();
            return page;
        }

        /// <summary>The providers the page shows a button for.</summary>
        public JsonArray Providers => View["providers"]?.AsArray() ?? [];

        /// <summary>The person presses "Continue with GitHub": where SqlOS sends the browser, on GitHub.</summary>
        public async Task<Uri> ContinueWithGitHubAsync()
        {
            var github = Providers.Single(x => x!["providerType"]!.GetValue<string>() == "GitHub")!;
            var result = await StepAsync("provider/start", new JsonObject { ["requestId"] = RequestId, ["connectionId"] = github["connectionId"]!.GetValue<string>() });
            Assert.AreEqual("redirect", result["type"]?.GetValue<string>(), result.ToJsonString());
            return new Uri(result["redirectUrl"]!.GetValue<string>());
        }

        /// <summary>
        /// GitHub sends the browser back to SqlOS's callback, with a code or with a refusal; the browser follows
        /// SqlOS's own redirects until it is handed to the web app. Answers the page it lands on there.
        /// </summary>
        public async Task<Uri> ReturnFromGitHubAsync(Uri authorize, string? code = null, string? error = null)
        {
            var asked = QueryHelpers.ParseQuery(authorize.Query);
            var back = QueryHelpers.AddQueryString(asked["redirect_uri"].ToString(), new Dictionary<string, string?>
            {
                ["state"] = asked["state"].ToString(), ["code"] = code, ["error"] = error,
            }.Where(x => x.Value is not null));
            var next = new Uri(back);
            for (var hop = 0; hop < 5 && next.GetLeftPart(UriPartial.Authority) == Origin; hop++)
            {
                using var response = await http.GetAsync(next);
                Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode, $"{next.AbsolutePath} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
                next = new Uri(Stack.Origin, response.Headers.Location!);
            }
            return next;
        }

        /// <summary>SqlOS handed the browser to the sign-in page with the request it began: no code, and none of SqlOS's words or tokens in the address.</summary>
        public void AssertCameBackSignedOut(Uri landing, string view)
        {
            Assert.AreEqual($"{Stack.WebOrigin}{WorkstreamApp.SignInPath}", landing.GetLeftPart(UriPartial.Path));
            var query = QueryHelpers.ParseQuery(landing.Query);
            CollectionAssert.AreEquivalent(new[] { "request", "view" }, query.Keys.ToArray());
            Assert.AreEqual(RequestId, query["request"].ToString());
            Assert.AreEqual(view, query["view"].ToString());
        }

        /// <summary>SqlOS's view model for the request, read again as a page reads it after the browser comes back.</summary>
        public async Task<JsonObject> ReadAgainAsync(string view)
        {
            using var response = await http.GetAsync($"/sqlos/auth/headless/requests/{Uri.EscapeDataString(RequestId)}?view={view}");
            var text = await response.Content.ReadAsStringAsync();
            Assert.IsTrue(response.IsSuccessStatusCode, text);
            return JsonNode.Parse(text)!.AsObject();
        }

        /// <summary>
        /// The web app's callback page (client/app/auth/callback.tsx): the code SqlOS came back with, checked
        /// against the state the sign-in began with, becomes the person's session.
        /// </summary>
        public async Task<Person> FinishAsync(Uri landing, string email)
        {
            Assert.AreEqual(RedirectUri, landing.GetLeftPart(UriPartial.Path), landing.ToString());
            var query = QueryHelpers.ParseQuery(landing.Query);
            Assert.AreEqual(state, query["state"].ToString());
            using var tokens = await http.PostAsync("/sqlos/auth/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["client_id"] = WorkstreamApp.AppClientId, ["code"] = query["code"].ToString(), ["redirect_uri"] = RedirectUri, ["code_verifier"] = verifier,
            }));
            var granted = JsonNode.Parse(await tokens.Content.ReadAsStringAsync())!.AsObject();
            Assert.IsTrue(tokens.IsSuccessStatusCode, granted.ToJsonString());
            var access = granted["access_token"]!.GetValue<string>();
            using var asking = Person.Holding(access, email, "");
            return Person.Holding(access, email, (await asking.GetAsync<MeDto>("/api/me")).UserId);
        }

        async Task<JsonObject> StepAsync(string step, JsonObject body)
        {
            using var response = await http.PostAsJsonAsync($"/sqlos/auth/headless/{step}", body);
            var text = await response.Content.ReadAsStringAsync();
            Assert.IsTrue(response.IsSuccessStatusCode, $"{step} answered {(int)response.StatusCode}: {text}");
            return JsonNode.Parse(text)!.AsObject();
        }

        public void Dispose() => http.Dispose();
    }

    /// <summary>The whole round trip for someone GitHub lets through who is on one team at most.</summary>
    static async Task<Person> SignInWithGitHubAsync(FakeGitHubSignIn.Account account)
    {
        using var page = await SignInPage.OpenAsync();
        var github = await page.ContinueWithGitHubAsync();
        var landing = await page.ReturnFromGitHubAsync(github, Stack.GitHubSignIn.Approve(account, github));
        return await page.FinishAsync(landing, account.Email);
    }

    [TestMethod]
    public async Task The_sign_in_page_offers_GitHub_and_sends_the_browser_there_for_the_profile_and_the_email_only()
    {
        using var page = await SignInPage.OpenAsync();

        var provider = page.Providers.Single()!;
        Assert.AreEqual("GitHub", provider["providerType"]!.GetValue<string>());
        Assert.AreEqual("GitHub", provider["displayName"]!.GetValue<string>());

        var github = await page.ContinueWithGitHubAsync();
        Assert.AreEqual("https://github.com/login/oauth/authorize", github.GetLeftPart(UriPartial.Path));
        var asked = QueryHelpers.ParseQuery(github.Query);
        Assert.AreEqual(FakeGitHubSignIn.ClientId, asked["client_id"].ToString());
        // The one address the GitHub OAuth client is registered with.
        Assert.AreEqual($"{Origin}/sqlos/auth/oidc/callback", asked["redirect_uri"].ToString());
        // Who they are and their verified address; nothing of their repositories.
        Assert.AreEqual("read:user user:email", asked["scope"].ToString());
        Assert.IsFalse(github.Query.Contains(FakeGitHubSignIn.ClientSecret));
    }

    [TestMethod]
    public async Task Someone_new_signs_in_with_GitHub_and_gets_an_account_and_a_team_of_their_own()
    {
        var account = Stack.GitHubSignIn.NewAccount(Person.NewEmail(), "Grace Hopper");

        using var grace = await SignInWithGitHubAsync(account);

        var me = await grace.GetAsync<MeDto>("/api/me");
        Assert.AreEqual(account.Email, me.Email);
        Assert.AreEqual("Grace Hopper", me.Name);
        Assert.IsFalse(me.SystemAdmin);
        // What a new person gets (IdentityProvisioning.cs): a team named after them, which they administer, with its standard environment.
        var team = (await grace.GetAsync<ListPage<TeamDto>>("/api/teams")).Data.Single();
        Assert.AreEqual("Grace Hopper's team", team.Name);
        Assert.IsTrue(team.Admin);
        Assert.AreEqual(1, (await grace.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{team.Id}/environments")).Data.Count);
        // And they change things like anyone signed in to the web app.
        await grace.PutAsync($"/api/teams/{team.Id}", new WriteTeam("Compilers")).IsAsync(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task Signing_in_with_GitHub_again_is_the_same_person_and_makes_no_second_team()
    {
        var account = Stack.GitHubSignIn.NewAccount(Person.NewEmail());
        using var first = await SignInWithGitHubAsync(account);
        var team = (await first.GetAsync<ListPage<TeamDto>>("/api/teams")).Data.Single();

        // Their address on GitHub changed since: the account is found by its GitHub number, not by the address.
        using var again = await SignInWithGitHubAsync(account with { Email = Person.NewEmail() });

        Assert.AreEqual(first.UserId, again.UserId);
        Assert.AreEqual(team.Id, (await again.GetAsync<ListPage<TeamDto>>("/api/teams")).Data.Single().Id);
        Assert.AreEqual(account.Email, (await again.GetAsync<MeDto>("/api/me")).Email);
    }

    [TestMethod]
    public async Task Someone_with_an_account_who_signs_in_with_GitHub_at_the_same_address_is_the_same_person()
    {
        var (ada, team) = await Person.WithTeamAsync("Ada Lovelace");
        using var _ = ada;

        using var viaGitHub = await SignInWithGitHubAsync(Stack.GitHubSignIn.NewAccount(ada.Email.ToUpperInvariant(), "ada-on-github"));

        Assert.AreEqual(ada.UserId, viaGitHub.UserId);
        var me = await viaGitHub.GetAsync<MeDto>("/api/me");
        // The account they had: its name and its one team, not a new one named after the GitHub profile.
        Assert.AreEqual("Ada Lovelace", me.Name);
        var teams = (await viaGitHub.GetAsync<ListPage<TeamDto>>("/api/teams")).Data;
        Assert.AreEqual(team.Id, teams.Single().Id);
        Assert.IsTrue(teams.Single().Admin);
    }

    [TestMethod]
    public async Task A_GitHub_account_without_a_verified_address_is_refused_and_no_account_is_made()
    {
        var address = Person.NewEmail();
        using var page = await SignInPage.OpenAsync();
        var github = await page.ContinueWithGitHubAsync();

        var landing = await page.ReturnFromGitHubAsync(github, Stack.GitHubSignIn.Approve(Stack.GitHubSignIn.NewAccount(address, verified: false), github));

        // Back on the sign-in page with the request it began and no code to exchange; the address has no account.
        page.AssertCameBackSignedOut(landing, "login");
        await Assert.ThrowsExactlyAsync<SignInRefused>(() => Person.SignInAsync(address));
    }

    [TestMethod]
    public async Task Someone_who_says_no_on_GitHub_comes_back_to_the_sign_in_page_signed_out()
    {
        using var page = await SignInPage.OpenAsync();
        var github = await page.ContinueWithGitHubAsync();

        var landing = await page.ReturnFromGitHubAsync(github, error: "access_denied");

        page.AssertCameBackSignedOut(landing, "login");
    }

    [TestMethod]
    public async Task A_code_from_GitHub_works_once_and_a_callback_nobody_began_signs_nobody_in()
    {
        var account = Stack.GitHubSignIn.NewAccount(Person.NewEmail());
        using var page = await SignInPage.OpenAsync();
        var github = await page.ContinueWithGitHubAsync();
        var code = Stack.GitHubSignIn.Approve(account, github);
        using var signedIn = await page.FinishAsync(await page.ReturnFromGitHubAsync(github, code), account.Email);

        // The same callback again: SqlOS's state was spent with the first.
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = Stack.Origin };
        var asked = QueryHelpers.ParseQuery(github.Query);
        using var replayed = await http.GetAsync($"/sqlos/auth/oidc/callback?code={code}&state={Uri.EscapeDataString(asked["state"].ToString())}");
        Assert.IsNull(replayed.Headers.Location?.ToString() is { } location && location.Contains("code=") ? location : null);
        // A state SqlOS never issued.
        using var forged = await http.GetAsync("/sqlos/auth/oidc/callback?code=anything&state=made-up");
        Assert.IsNull(forged.Headers.Location?.ToString() is { } elsewhere && elsewhere.Contains("code=") ? elsewhere : null);
    }

    /// <summary>Lin, with a team of her own, also on Ada's: SqlOS asks which team a session of hers is for.</summary>
    static async Task<(Person Lin, string[] TeamIds)> OnTwoTeamsAsync()
    {
        var (ada, team) = await Person.WithTeamAsync("Ada Lovelace");
        using var _ = ada;
        var (lin, own) = await Person.WithTeamAsync("Lin Clark");
        var invitation = await ada.PostAsync<PendingInvitation>($"/api/teams/{team.Id}/invitations", new Invite(lin.Email));
        (await Person.SignInAsync(lin.Email, invitationToken: Answers.TokenOf(invitation))).Dispose();
        return (lin, [team.Id, own.Id]);
    }

    [TestMethod]
    public async Task Someone_on_several_teams_comes_back_from_GitHub_to_the_sign_in_page_signed_out()
    {
        var (lin, _) = await OnTwoTeamsAsync();
        using var __ = lin;

        using var page = await SignInPage.OpenAsync();
        var github = await page.ContinueWithGitHubAsync();
        var landing = await page.ReturnFromGitHubAsync(github, Stack.GitHubSignIn.Approve(Stack.GitHubSignIn.NewAccount(lin.Email), github));

        // SqlOS asks which team the session is for and hands the browser to the sign-in page. The page cannot
        // answer: SqlOS 11.4.1 reads a request back without the teams to choose from
        // (SqlOSHeadlessAuthService.GetRequestAsync), so it says GitHub sign-in did not work and shows the form
        // again (client/app/sign-in.tsx). Nobody is signed in.
        page.AssertCameBackSignedOut(landing, "organization");
        Assert.AreEqual(0, (await page.ReadAgainAsync("organization"))["organizationSelection"]!.AsArray().Count);
    }

    /// <summary>On the command line's page, signed out: the person presses "Continue with GitHub". Where SqlOS sends the browser.</summary>
    static async Task<Uri> ContinueWithGitHubAsync(CommandLine.Browser browser)
    {
        var view = await browser.ViewAsync();
        Assert.AreEqual("device", view["view"]?.GetValue<string>());
        var connection = view["providers"]!.AsArray().Single()!["connectionId"]!.GetValue<string>();
        var started = await browser.StepAsync("provider/start", new JsonObject { ["requestId"] = browser.RequestId, ["connectionId"] = connection });
        return new Uri(started["redirectUrl"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task The_command_line_is_approved_by_someone_who_signs_in_with_GitHub_on_its_page()
    {
        var account = Stack.GitHubSignIn.NewAccount(Person.NewEmail(), "Katherine Johnson");
        using var katherine = await SignInWithGitHubAsync(account);
        var device = await CommandLine.StartAsync();

        using (var browser = await CommandLine.Browser.OpenAsync(device))
        {
            var github = await ContinueWithGitHubAsync(browser);

            // GitHub sends the browser back; SqlOS hands it to the device page again, signed in this time.
            var landing = await browser.FollowAsync(github, Stack.GitHubSignIn.Approve(account, github));
            Assert.AreEqual($"{Stack.WebOrigin}{WorkstreamApp.DevicePath}", landing.GetLeftPart(UriPartial.Path));
            Assert.AreEqual(browser.RequestId, QueryHelpers.ParseQuery(landing.Query)["request"].ToString());
            await browser.ApproveAsync();
        }

        var (tokens, error) = await CommandLine.PollAsync(device);
        Assert.IsNull(error);
        using var cli = Person.Holding(tokens!["access_token"]!.GetValue<string>(), account.Email, katherine.UserId);
        Assert.AreEqual(katherine.UserId, (await cli.GetAsync<MeDto>("/api/me")).UserId);
        // The command line's own grant: it starts tasks and changes nothing else, however the person signed in.
        var team = (await cli.GetAsync<ListPage<TeamDto>>("/api/teams")).Data.Single();
        await cli.PutAsync($"/api/teams/{team.Id}", new WriteTeam("Renamed")).IsAsync(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task Someone_GitHub_does_not_sign_in_on_the_command_lines_page_leaves_it_unapproved()
    {
        var (lin, _) = await OnTwoTeamsAsync();
        using var __ = lin;
        var device = await CommandLine.StartAsync();

        using (var browser = await CommandLine.Browser.OpenAsync(device))
        {
            // On several teams: SqlOS has a question left, as on the sign-in page, and hands the browser to the
            // web app's sign-in with the command line's request. The page sends it back to the device page, which
            // says GitHub sign-in did not work (client/app/sign-in.tsx).
            var github = await ContinueWithGitHubAsync(browser);
            var landing = await browser.FollowAsync(github, Stack.GitHubSignIn.Approve(Stack.GitHubSignIn.NewAccount(lin.Email), github));
            Assert.AreEqual($"{Stack.WebOrigin}{WorkstreamApp.SignInPath}", landing.GetLeftPart(UriPartial.Path));
            Assert.AreEqual(browser.RequestId, QueryHelpers.ParseQuery(landing.Query)["request"].ToString());
            // Nobody is signed in on the page, so there is nothing to approve: it still shows sign-in for the request.
            Assert.AreEqual("device", (await browser.ViewAsync())["view"]?.GetValue<string>());
        }

        var (tokens, error) = await CommandLine.PollAsync(device);
        Assert.IsNull(tokens);
        Assert.AreEqual("authorization_pending", error);
    }

    [TestMethod]
    public void Without_the_two_settings_no_provider_is_seeded_so_the_page_shows_no_button()
    {
        var app = WorkstreamApp.From(new ConfigurationBuilder().Build());
        var auth = new SqlOSAuthServerOptions();

        Assert.IsFalse(app.SeedGitHubSignIn(auth, new ConfigurationBuilder().Build()));
        Assert.AreEqual(0, auth.OidcConnectionSeeds.Count);
        // Empty values, as a deployment without the variable and the secret passes them, are the same as none.
        Assert.IsFalse(app.SeedGitHubSignIn(auth, Settings(("SqlOS:Oidc:GitHub:ClientId", ""), ("SqlOS:Oidc:GitHub:ClientSecret", " "))));
        Assert.AreEqual(0, auth.OidcConnectionSeeds.Count);
    }

    [TestMethod]
    public void With_both_settings_GitHub_is_seeded_with_SqlOS_callback_under_the_API_and_its_default_scopes()
    {
        var configuration = Settings(("PUBLIC_ORIGIN", "https://api.workstream.example/"), ("SqlOS:Oidc:GitHub:ClientId", "client"), ("SqlOS:Oidc:GitHub:ClientSecret", "secret"));
        var app = WorkstreamApp.From(configuration);
        var auth = new SqlOSAuthServerOptions();

        Assert.IsTrue(app.SeedGitHubSignIn(auth, configuration));

        var seed = auth.OidcConnectionSeeds.Single();
        Assert.AreEqual(SqlOSOidcProviderType.GitHub, seed.ProviderType);
        Assert.AreEqual("client", seed.ClientId);
        Assert.AreEqual("secret", seed.ClientSecret);
        Assert.IsTrue(seed.IsEnabled);
        CollectionAssert.AreEqual(new[] { "https://api.workstream.example/sqlos/auth/oidc/callback" }, seed.AllowedCallbackUris);
        // No scopes of its own: SqlOS asks for read:user and user:email.
        Assert.IsNull(seed.Scopes);
    }

    [TestMethod]
    public void One_setting_without_the_other_stops_the_API_from_starting()
    {
        var app = WorkstreamApp.From(new ConfigurationBuilder().Build());
        Assert.ThrowsExactly<InvalidOperationException>(() => app.SeedGitHubSignIn(new SqlOSAuthServerOptions(), Settings(("SqlOS:Oidc:GitHub:ClientId", "client"))));
        Assert.ThrowsExactly<InvalidOperationException>(() => app.SeedGitHubSignIn(new SqlOSAuthServerOptions(), Settings(("SqlOS:Oidc:GitHub:ClientSecret", "secret"))));
    }

    static IConfiguration Settings(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(x => x.Key, x => (string?)x.Value)).Build();
}
