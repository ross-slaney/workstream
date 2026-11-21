using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Workstream.Api;
using Workstream.Api.Domain;
using Workstream.Api.GitHub;

namespace Workstream.Tests;

/// <summary>
/// A team's GitHub connection: an administrator installs Workstream's GitHub App and the team is connected to
/// that installation; everyone on the team sees what it reaches; a task's desktop gets a token for its own
/// repository, for an hour, and nothing else.
/// </summary>
[TestClass]
public sealed class GitHubJourneys
{
    /// <summary>What GitHub hands back: the state Workstream signed, from the address it sent the administrator to.</summary>
    static async Task<string> SetOffAsync(Person person, string teamId)
    {
        var install = await person.PostAsync<GitHubInstall>($"/api/teams/{teamId}/github/install");
        StringAssert.StartsWith(install.Url, $"{FakeGitHub.Web}/apps/{FakeGitHub.AppSlug}/installations/new?state=");
        return QueryHelpers.ParseQuery(new Uri(install.Url).Query)["state"].ToString();
    }

    /// <summary>The whole round: set off, install on GitHub, come back through the API's callback, and finish in the web app.</summary>
    static async Task<GitHubConnected> ConnectAsync(Person person, string teamId, FakeGitHub.Installation installation)
    {
        var state = await SetOffAsync(person, teamId);
        var code = Stack.GitHub.CodeFor(installation.Id);
        // GitHub sends the browser to the callback, which hands it, and what GitHub added, to the web app.
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = Stack.Origin };
        using var back = await browser.GetAsync($"/api/github/callback?code={code}&installation_id={installation.Id}&setup_action=install&state={Uri.EscapeDataString(state)}");
        Assert.AreEqual(HttpStatusCode.Redirect, back.StatusCode);
        var landing = back.Headers.Location!;
        Assert.AreEqual($"{Stack.WebOrigin}/github", landing.GetLeftPart(UriPartial.Path));
        var passed = QueryHelpers.ParseQuery(landing.Query);
        Assert.AreEqual(state, passed["state"].ToString());
        Assert.AreEqual(code, passed["code"].ToString());
        return await person.PostAsync<GitHubConnected>("/api/github/connect", new ConnectGitHub(passed["state"], passed["code"], long.Parse(passed["installation_id"]!)));
    }

    static async Task<Person> MemberAsync(Person admin, string teamId, bool asAdmin = false)
    {
        var invitation = await admin.PostAsync<PendingInvitation>($"/api/teams/{teamId}/invitations", new Invite(Person.NewEmail(), asAdmin));
        return await Person.SignUpAsync("Lin Clark", invitation.Email, Answers.TokenOf(invitation));
    }

    static async Task<TaskDto> TaskFromAsync(Person person, string teamId, string? repositoryUrl)
    {
        var environment = Guid.NewGuid().ToString("D");
        await person.PostAsync($"/api/teams/{teamId}/environments",
            new AddEnvironment(environment, "Project " + environment[..6], ["chrome", "vscode", "terminal"], "small", repositoryUrl, null, null, null, null)).IsAsync(HttpStatusCode.Created);
        return await person.PostAsync<TaskDto>($"/api/teams/{teamId}/tasks", new StartTask(Guid.NewGuid().ToString("D"), environment, null, "Fix the build.", Guid.NewGuid().ToString("D")));
    }

    [TestMethod]
    public async Task An_administrator_connects_the_team_and_everyone_on_it_sees_what_it_reaches()
    {
        var (ada, team) = await Person.WithTeamAsync("Ada Lovelace");
        var before = await ada.GetAsync<GitHubDto>($"/api/teams/{team.Id}/github");
        Assert.IsTrue(before.Available);
        Assert.IsFalse(before.Connected);

        var installation = Stack.GitHub.Install("Organization", "site", "app");
        var connected = await ConnectAsync(ada, team.Id, installation);
        Assert.AreEqual(team.Id, connected.TeamId);
        Assert.IsTrue(connected.GitHub.Connected);
        Assert.AreEqual(installation.Account, connected.GitHub.Account);
        Assert.AreEqual("Organization", connected.GitHub.AccountType);
        Assert.AreEqual("Ada Lovelace", connected.GitHub.ConnectedByName);
        Assert.AreEqual($"{FakeGitHub.Web}/organizations/{installation.Account}/settings/installations/{installation.Id}", connected.GitHub.ManageUrl);

        // The repositories are read from GitHub as the installation, A to Z.
        var repositories = await ada.GetAsync<List<GitHubRepository>>($"/api/teams/{team.Id}/github/repositories");
        CollectionAssert.AreEqual(new[] { $"{installation.Account}/app", $"{installation.Account}/site" }, repositories.Select(x => x.FullName).ToArray());
        Assert.AreEqual($"https://github.com/{installation.Account}/app", repositories[0].Url);
        Assert.IsTrue(repositories[0].Private);
        Assert.AreEqual("main", repositories[0].DefaultBranch);
        // Listing asked for nothing but to see them.
        CollectionAssert.AreEqual(new[] { "metadata" }, Stack.GitHub.MintedFor(installation.Id).Single().Permissions.Keys.ToArray());

        // A member sees the connection and its repositories, for the environments they write, and changes neither.
        var lin = await MemberAsync(ada, team.Id);
        Assert.AreEqual(installation.Account, (await lin.GetAsync<GitHubDto>($"/api/teams/{team.Id}/github")).Account);
        Assert.AreEqual(2, (await lin.GetAsync<List<GitHubRepository>>($"/api/teams/{team.Id}/github/repositories")).Count);
        await lin.PostAsync($"/api/teams/{team.Id}/github/install").IsAsync(HttpStatusCode.Forbidden, "administrator");
        await lin.DeleteAsync($"/api/teams/{team.Id}/github").IsAsync(HttpStatusCode.Forbidden, "administrator");

        // A person's installation of their own account is changed under their own settings.
        var mine = Stack.GitHub.Install("User", "dotfiles");
        var again = await ConnectAsync(ada, team.Id, mine);
        Assert.AreEqual($"{FakeGitHub.Web}/settings/installations/{mine.Id}", again.GitHub.ManageUrl);

        await ada.DeleteAsync($"/api/teams/{team.Id}/github").IsAsync(HttpStatusCode.NoContent);
        Assert.IsFalse((await lin.GetAsync<GitHubDto>($"/api/teams/{team.Id}/github")).Connected);
        await ada.GetAsync($"/api/teams/{team.Id}/github/repositories").IsAsync(HttpStatusCode.NotFound);
        await ada.DeleteAsync($"/api/teams/{team.Id}/github").IsAsync(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task Only_the_administrator_who_set_off_connects_and_only_to_an_installation_their_GitHub_account_reaches()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var acme = Stack.GitHub.Install("Organization", "app");
        var theirs = Stack.GitHub.Install("Organization", "secrets");
        var state = await SetOffAsync(ada, team.Id);

        // Another administrator cannot finish what Ada started, with her state.
        var lin = await MemberAsync(ada, team.Id, asAdmin: true);
        await lin.PostAsync("/api/github/connect", new ConnectGitHub(state, Stack.GitHub.CodeFor(acme.Id), acme.Id)).IsAsync(HttpStatusCode.Forbidden, "Someone else");

        // The installation id in the address proves nothing: Ada's GitHub account reaches acme, not theirs.
        await ada.PostAsync("/api/github/connect", new ConnectGitHub(state, Stack.GitHub.CodeFor(acme.Id), theirs.Id)).IsAsync(HttpStatusCode.Forbidden, "cannot reach");

        // A code works once; a state Workstream did not sign is no state; GitHub must say which installation and who.
        var code = Stack.GitHub.CodeFor(acme.Id);
        await ada.PostAsync("/api/github/connect", new ConnectGitHub(state, code, acme.Id)).IsAsync(HttpStatusCode.OK);
        await ada.PostAsync("/api/github/connect", new ConnectGitHub(state, code, acme.Id)).IsAsync(HttpStatusCode.BadRequest, "did not accept");
        await ada.PostAsync("/api/github/connect", new ConnectGitHub("made-up", Stack.GitHub.CodeFor(acme.Id), acme.Id)).IsAsync(HttpStatusCode.BadRequest, "expired");
        await ada.PostAsync("/api/github/connect", new ConnectGitHub(state, Stack.GitHub.CodeFor(acme.Id), null)).IsAsync(HttpStatusCode.BadRequest, "where");
        await ada.PostAsync("/api/github/connect", new ConnectGitHub(state, null, acme.Id)).IsAsync(HttpStatusCode.BadRequest, "who");

        // Someone who stopped administering the team since they set off connects nothing.
        var linState = await SetOffAsync(lin, team.Id);
        await ada.PutAsync($"/api/teams/{team.Id}/people/{lin.UserId}/admin", new SetAdmin(false)).IsAsync(HttpStatusCode.NoContent);
        await lin.PostAsync("/api/github/connect", new ConnectGitHub(linState, Stack.GitHub.CodeFor(theirs.Id), theirs.Id)).IsAsync(HttpStatusCode.Forbidden, "administrator");
        Assert.AreEqual(acme.Account, (await ada.GetAsync<GitHubDto>($"/api/teams/{team.Id}/github")).Account);
    }

    [TestMethod]
    public async Task A_stranger_the_system_administrator_a_desktop_and_nobody_reach_no_teams_connection()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var installation = Stack.GitHub.Install("Organization", "app");
        await ConnectAsync(ada, team.Id, installation);
        var state = await SetOffAsync(ada, team.Id);

        var stranger = await Person.SignUpAsync("Grace Hopper");
        var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
        foreach (var outsider in new[] { stranger, administrator })
        {
            await outsider.GetAsync($"/api/teams/{team.Id}/github").IsAsync(HttpStatusCode.NotFound);
            await outsider.GetAsync($"/api/teams/{team.Id}/github/repositories").IsAsync(HttpStatusCode.NotFound);
            await outsider.PostAsync($"/api/teams/{team.Id}/github/install").IsAsync(HttpStatusCode.NotFound);
            await outsider.DeleteAsync($"/api/teams/{team.Id}/github").IsAsync(HttpStatusCode.NotFound);
            // A state taken from the administrator's address is still theirs.
            await outsider.PostAsync("/api/github/connect", new ConnectGitHub(state, Stack.GitHub.CodeFor(installation.Id), installation.Id)).IsAsync(HttpStatusCode.Forbidden);
        }

        // A desktop's key opens nothing a person's token does, and nobody signed out reaches anything.
        var task = await TaskFromAsync(ada, team.Id, $"https://github.com/{installation.Account}/app");
        using var desktop = await Desktop.StartAsync(task.Id);
        using var withKey = new HttpClient { BaseAddress = Stack.Origin };
        withKey.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", desktop.Key);
        await withKey.GetAsync($"/api/teams/{team.Id}/github").IsAsync(HttpStatusCode.Unauthorized);
        await withKey.PostAsync("/api/github/connect", JsonBody(new ConnectGitHub(state, "code", installation.Id))).IsAsync(HttpStatusCode.Unauthorized);
        using var nobody = new HttpClient { BaseAddress = Stack.Origin };
        await nobody.GetAsync($"/api/teams/{team.Id}/github").IsAsync(HttpStatusCode.Unauthorized);
        await nobody.GetAsync($"/api/teams/{team.Id}/github/repositories").IsAsync(HttpStatusCode.Unauthorized);
        await nobody.PostAsync($"/api/teams/{team.Id}/github/install", null).IsAsync(HttpStatusCode.Unauthorized);
        await nobody.PostAsync("/api/github/connect", JsonBody(new ConnectGitHub(state, "code", installation.Id))).IsAsync(HttpStatusCode.Unauthorized);
        await nobody.DeleteAsync($"/api/teams/{team.Id}/github").IsAsync(HttpStatusCode.Unauthorized);
        Assert.IsTrue((await ada.GetAsync<GitHubDto>($"/api/teams/{team.Id}/github")).Connected);
    }

    [TestMethod]
    public async Task A_desktop_gets_a_token_for_its_own_repository_for_an_hour_and_nothing_else()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var installation = Stack.GitHub.Install("Organization", "app", "site");
        await ConnectAsync(ada, team.Id, installation);

        var task = await TaskFromAsync(ada, team.Id, $"https://github.com/{installation.Account}/app.git");
        using var desktop = await Desktop.StartAsync(task.Id);
        var token = await Person.ReadAsync<DesktopGitHubToken>(await desktop.GetAsync("/api/desktop/github/token"));
        Assert.AreEqual($"{installation.Account}/app", token.Repository);
        StringAssert.StartsWith(token.Token, "ghs_");
        Assert.IsTrue(token.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(55) && token.ExpiresAt <= DateTimeOffset.UtcNow.AddHours(1).AddMinutes(1));
        // Minted for that one repository, to push a branch and open a pull request.
        var minted = Stack.GitHub.Tokens[token.Token];
        CollectionAssert.AreEqual(new[] { "app" }, minted.Repositories!.ToArray());
        CollectionAssert.AreEquivalent(new Dictionary<string, string> { ["contents"] = "write", ["pull_requests"] = "write", ["metadata"] = "read" }, minted.Permissions.ToDictionary());
        // Asked again, a new one: nothing keeps them.
        var second = await Person.ReadAsync<DesktopGitHubToken>(await desktop.GetAsync("/api/desktop/github/token"));
        Assert.AreNotEqual(token.Token, second.Token);

        // A person's token is not a desktop's key, and nobody signed out gets anything.
        await ada.GetAsync("/api/desktop/github/token").IsAsync(HttpStatusCode.Unauthorized);
        using var nobody = new HttpClient { BaseAddress = Stack.Origin };
        await nobody.GetAsync("/api/desktop/github/token").IsAsync(HttpStatusCode.Unauthorized);

        // No repository, a repository on another account, or one the installation does not reach: no token.
        foreach (var url in new string?[] { null, "https://gitlab.com/acme/app", "https://github.com/someone-else/app", $"https://github.com/{installation.Account}/missing" })
        {
            var other = await TaskFromAsync(ada, team.Id, url);
            using var otherDesktop = await Desktop.StartAsync(other.Id);
            await otherDesktop.GetAsync("/api/desktop/github/token").IsAsync(HttpStatusCode.NotFound);
            // Within the team's limit of running desktops.
            await ada.DeleteAsync($"/api/tasks/{other.Id}").IsAsync(HttpStatusCode.NoContent);
            await Stack.Jobs.DeleteAsync(other.Id, CancellationToken.None);
        }
        Assert.IsFalse(Stack.GitHub.Tokens.Values.Any(x => x.Repositories?.Contains("someone-else") == true));

        // Another team's desktop, on the same repository's address, gets nothing from this team's connection.
        var (grace, theirTeam) = await Person.WithTeamAsync("Grace Hopper");
        var theirTask = await TaskFromAsync(grace, theirTeam.Id, $"https://github.com/{installation.Account}/app");
        using var theirDesktop = await Desktop.StartAsync(theirTask.Id);
        await theirDesktop.GetAsync("/api/desktop/github/token").IsAsync(HttpStatusCode.NotFound);

        // Disconnected, the team's desktops get nothing; a deleted task's desktop is nobody.
        await ada.DeleteAsync($"/api/teams/{team.Id}/github").IsAsync(HttpStatusCode.NoContent);
        await desktop.GetAsync("/api/desktop/github/token").IsAsync(HttpStatusCode.NotFound);
        await ConnectAsync(ada, team.Id, installation);
        await ada.DeleteAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NoContent);
        await Stack.Jobs.DeleteAsync(task.Id, CancellationToken.None);
        await desktop.GetAsync("/api/desktop/github/token").IsAsync(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task A_machine_preparing_an_environment_gets_a_token_for_its_repository_while_it_prepares()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var installation = Stack.GitHub.Install("Organization", "app");
        await ConnectAsync(ada, team.Id, installation);
        var environment = Guid.NewGuid().ToString("D");
        await ada.PostAsync($"/api/teams/{team.Id}/environments",
            new AddEnvironment(environment, "Prepared app", ["terminal"], "small", $"https://github.com/{installation.Account}/app", null, "npm ci", null, null)).IsAsync(HttpStatusCode.Created);
        await Stack.Jobs.PrepareAsync(environment, CancellationToken.None);
        using var preparing = Desktop.Preparing(environment);

        // Its clone of a private repository works: a token for that one repository, from the team's connection.
        var token = await Person.ReadAsync<DesktopGitHubToken>(await preparing.GetAsync("/api/desktop/github/token"));
        Assert.AreEqual($"{installation.Account}/app", token.Repository);
        CollectionAssert.AreEqual(new[] { "app" }, Stack.GitHub.Tokens[token.Token].Repositories!.ToArray());

        // Once it has said how the setup went, it gets nothing more.
        await preparing.ReportAsync("failed", exit: 1).IsAsync(HttpStatusCode.NoContent);
        await preparing.GetAsync("/api/desktop/github/token").IsAsync(HttpStatusCode.Unauthorized);
        await Stack.Jobs.RemovePreparingMachineAsync(preparing.Host, CancellationToken.None);
        await ada.DeleteAsync($"/api/environments/{environment}").IsAsync(HttpStatusCode.NoContent);
    }

    [TestMethod]
    public async Task Removing_the_app_on_GitHub_disconnects_every_team_on_that_installation()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var installation = Stack.GitHub.Install("Organization", "app");
        await ConnectAsync(ada, team.Id, installation);
        using var github = new HttpClient { BaseAddress = Stack.Origin };

        Task<HttpResponseMessage> DeliverAsync(string body, string? signature, string kind = "installation")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/github/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("X-GitHub-Event", kind);
            if (signature is not null) request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", signature);
            return github.SendAsync(request);
        }

        var removed = new JsonObject { ["action"] = "deleted", ["installation"] = new JsonObject { ["id"] = installation.Id } }.ToJsonString();
        // A delivery GitHub did not sign changes nothing.
        await DeliverAsync(removed, null).IsAsync(HttpStatusCode.Unauthorized);
        await DeliverAsync(removed, FakeGitHub.Signature(removed, "another-secret")).IsAsync(HttpStatusCode.Unauthorized);
        // Another event, or another action, is taken and ignored.
        await DeliverAsync(removed, FakeGitHub.Signature(removed), "push").IsAsync(HttpStatusCode.NoContent);
        var added = new JsonObject { ["action"] = "created", ["installation"] = new JsonObject { ["id"] = installation.Id } }.ToJsonString();
        await DeliverAsync(added, FakeGitHub.Signature(added)).IsAsync(HttpStatusCode.NoContent);
        Assert.IsTrue((await ada.GetAsync<GitHubDto>($"/api/teams/{team.Id}/github")).Connected);

        await DeliverAsync(removed, FakeGitHub.Signature(removed)).IsAsync(HttpStatusCode.NoContent);
        Assert.IsFalse((await ada.GetAsync<GitHubDto>($"/api/teams/{team.Id}/github")).Connected);
    }

    [TestMethod]
    public async Task An_installation_removed_on_GitHub_is_said_plainly_when_its_repositories_are_read()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var installation = Stack.GitHub.Install("Organization", "app");
        await ConnectAsync(ada, team.Id, installation);
        Stack.GitHub.Uninstall(installation.Id);
        await ada.GetAsync($"/api/teams/{team.Id}/github/repositories").IsAsync(HttpStatusCode.ServiceUnavailable, "no longer installed");
    }

    static StringContent JsonBody(object body) => new(System.Text.Json.JsonSerializer.Serialize(body, Person.Json), Encoding.UTF8, "application/json");
}

/// <summary>The GitHub App's own pieces, with no stack: its settings, the token it signs, and the repositories it names.</summary>
[TestClass]
public sealed class GitHubAppTests
{
    static GitHubOptions Options(string key) => new() { AppId = "1", AppSlug = "workstream", ClientId = "Iv1.x", ClientSecret = "s", PrivateKey = key };

    [TestMethod]
    public void Without_every_setting_GitHub_is_not_set_up()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        Assert.IsFalse(new GitHubOptions().IsConfigured);
        Assert.IsFalse(new GitHubOptions { AppId = "1", AppSlug = "workstream", ClientId = "Iv1.x", PrivateKey = pem }.IsConfigured);
        Assert.IsFalse(Options("not a key").IsConfigured);
        // The key as GitHub gives it, with its line breaks escaped by a secret store, or in base64.
        Assert.IsTrue(Options(pem).IsConfigured);
        Assert.IsTrue(Options(pem.Replace("\n", "\\n")).IsConfigured);
        Assert.IsTrue(Options(Convert.ToBase64String(Encoding.UTF8.GetBytes(pem))).IsConfigured);
    }

    [TestMethod]
    public void The_apps_token_is_RS256_for_the_app_and_good_for_less_than_ten_minutes()
    {
        using var rsa = RSA.Create(2048);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var jwt = Options(Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ExportPkcs8PrivateKeyPem()))).AppToken(now);
        var parts = jwt.Split('.');
        Assert.AreEqual("RS256", JsonNode.Parse(WebEncoders.Base64UrlDecode(parts[0]))!["alg"]!.GetValue<string>());
        Assert.IsTrue(rsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), WebEncoders.Base64UrlDecode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var payload = JsonNode.Parse(WebEncoders.Base64UrlDecode(parts[1]))!;
        Assert.AreEqual("1", payload["iss"]!.GetValue<string>());
        Assert.AreEqual(now.ToUnixTimeSeconds() - 60, payload["iat"]!.GetValue<long>());
        Assert.AreEqual(now.ToUnixTimeSeconds() + 540, payload["exp"]!.GetValue<long>());
    }

    [TestMethod]
    public void A_webhook_delivery_is_taken_only_with_GitHubs_signature()
    {
        var options = new GitHubOptions { WebhookSecret = FakeGitHub.WebhookSecret };
        var body = Encoding.UTF8.GetBytes("{\"action\":\"deleted\"}");
        Assert.IsTrue(options.IsSignedDelivery(body, FakeGitHub.Signature("{\"action\":\"deleted\"}")));
        Assert.IsFalse(options.IsSignedDelivery(body, FakeGitHub.Signature("{\"action\":\"created\"}")));
        Assert.IsFalse(options.IsSignedDelivery(body, "sha256=zz"));
        Assert.IsFalse(options.IsSignedDelivery(body, null));
        Assert.IsFalse(new GitHubOptions().IsSignedDelivery(body, FakeGitHub.Signature("{\"action\":\"deleted\"}", "")));
    }

    [TestMethod]
    public void An_environments_address_names_a_GitHub_repository_only_when_it_is_one()
    {
        Assert.AreEqual(new GitHubRepositoryName("acme", "app"), GitHubRepositoryName.Of("https://github.com/acme/app"));
        Assert.AreEqual(new GitHubRepositoryName("acme", "app"), GitHubRepositoryName.Of("https://github.com/acme/app.git"));
        Assert.AreEqual(new GitHubRepositoryName("acme", "my.app"), GitHubRepositoryName.Of("https://www.github.com/acme/my.app/"));
        Assert.IsNull(GitHubRepositoryName.Of(null));
        Assert.IsNull(GitHubRepositoryName.Of("http://github.com/acme/app"));
        Assert.IsNull(GitHubRepositoryName.Of("https://gitlab.com/acme/app"));
        Assert.IsNull(GitHubRepositoryName.Of("https://github.com/acme"));
        Assert.IsNull(GitHubRepositoryName.Of("https://github.com/acme/app/tree/main"));
    }
}
