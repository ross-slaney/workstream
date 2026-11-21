using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Workstream.Api;

namespace Workstream.Tests;

/// <summary>
/// The command line (cli/workstream.mjs): signed in through the browser with SqlOS's device flow, it is the
/// person, with the person's grants, and its token reads and starts tasks and changes nothing else.
/// </summary>
[TestClass]
public sealed class CommandLineJourneys
{
    static async Task<(Person Person, TeamDto Team, string EnvironmentId)> TeamAsync(string name = "Ada Lovelace")
    {
        var (person, team) = await Person.WithTeamAsync(name);
        var environment = (await person.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{team.Id}/environments")).Data.Single();
        return (person, team, environment.Id);
    }

    static StartTask NewTask(string environmentId, string prompt = "Fix issue #12: the sign-in button is grey. Work on a branch and open a pull request.") =>
        new(Guid.NewGuid().ToString("D"), environmentId, null, prompt, Guid.NewGuid().ToString("D"));

    [TestMethod]
    public async Task Anyone_can_read_how_to_sign_the_command_line_in()
    {
        var settings = await CommandLine.SettingsAsync();
        var origin = Stack.Origin.GetLeftPart(UriPartial.Authority);
        Assert.AreEqual($"{origin}/sqlos/auth", settings.Issuer);
        Assert.AreEqual(WorkstreamApp.CliClientId, settings.ClientId);
        Assert.AreEqual($"{origin}/api", settings.Resource);
        Assert.AreEqual(Stack.WebOrigin, settings.Web);
        CollectionAssert.AreEquivalent(new[] { "openid", "profile", "email", "offline_access", "workstream.read", "workstream.start" }, settings.Scope.Split(' '));
    }

    [TestMethod]
    public async Task The_owner_signs_the_command_line_in_in_the_browser_and_starts_and_reads_a_task_as_themselves()
    {
        var (ada, team, environment) = await TeamAsync();
        // The link the command line prints opens the web app's device page, through SqlOS.
        var device = await CommandLine.StartAsync();
        StringAssert.StartsWith(device.Link, $"{Stack.Origin.GetLeftPart(UriPartial.Authority)}/sqlos/auth/device?user_code=");
        using (var browser = await CommandLine.Browser.OpenAsync(device))
        {
            Assert.AreEqual($"{Stack.WebOrigin}{WorkstreamApp.DevicePath}", browser.Page.GetLeftPart(UriPartial.Path));
            Assert.AreEqual("device", QueryHelpers.ParseQuery(browser.Page.Query)["view"].ToString());
            // Before the person says yes, the command line is told to keep waiting.
            Assert.AreEqual("authorization_pending", (await CommandLine.PollAsync(device)).Error);
            await browser.SignInAsync(ada.Email, Person.Password);
            var asked = await browser.ViewAsync();
            Assert.AreEqual(device.UserCode, asked["deviceAuthorization"]?["userCode"]?.GetValue<string>());
            await browser.ApproveAsync();
        }
        var (tokens, error) = await CommandLine.PollAsync(device);
        Assert.IsNull(error);
        CollectionAssert.AreEquivalent(new[] { "openid", "profile", "email", "offline_access", "workstream.read", "workstream.start" }, tokens!["scope"]!.GetValue<string>().Split(' '));
        using var cli = Person.Holding(tokens["access_token"]!.GetValue<string>(), ada.Email, ada.UserId);

        // It is Ada: the same person, the same teams.
        Assert.AreEqual(ada.UserId, (await cli.GetAsync<MeDto>("/api/me")).UserId);
        Assert.AreEqual(team.Id, (await cli.GetAsync<ListPage<TeamDto>>("/api/teams")).Data.Single().Id);
        Assert.AreEqual(environment, (await cli.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{team.Id}/environments")).Data.Single().Id);

        var task = await cli.PostAsync<TaskDto>($"/api/teams/{team.Id}/tasks", NewTask(environment));
        Assert.AreEqual(ada.UserId, task.CreatedBy);
        Assert.AreEqual("starting", task.State);
        Assert.AreEqual(task.Id, (await cli.GetAsync<TaskDto>($"/api/tasks/{task.Id}")).Id);
        Assert.AreEqual(task.Id, (await cli.GetAsync<ListPage<TaskDto>>($"/api/teams/{team.Id}/tasks")).Data.Single().Id);
        Assert.AreEqual("person", (await cli.GetAsync<ConversationDto>($"/api/tasks/{task.Id}/messages?after=0")).Messages.Single().Author);
        // The web app sees the task the command line started.
        Assert.AreEqual(task.Id, (await ada.GetAsync<TaskDto>($"/api/tasks/{task.Id}")).Id);
        ada.Dispose();
    }

    [TestMethod]
    public async Task The_command_line_changes_nothing_but_starting_a_task()
    {
        var (ada, team, environment) = await TeamAsync();
        var task = await ada.PostAsync<TaskDto>($"/api/teams/{team.Id}/tasks", NewTask(environment));
        using var session = await CommandLine.SignInAsync(ada.Email);
        var cli = session.Person;

        const string refusal = "only read and start tasks";
        await cli.PutAsync($"/api/tasks/{task.Id}", new RenameTask("Mine")).IsAsync(HttpStatusCode.Forbidden, refusal);
        await cli.PostAsync($"/api/tasks/{task.Id}/messages", new Say("And another thing", Guid.NewGuid().ToString("D"))).IsAsync(HttpStatusCode.Forbidden, refusal);
        await cli.PostAsync($"/api/tasks/{task.Id}/interrupt").IsAsync(HttpStatusCode.Forbidden, refusal);
        await cli.PostAsync($"/api/tasks/{task.Id}/stop").IsAsync(HttpStatusCode.Forbidden, refusal);
        await cli.DeleteAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.Forbidden, refusal);
        await cli.PostAsync($"/api/teams/{team.Id}/spare", new WantSpare(null)).IsAsync(HttpStatusCode.Forbidden, refusal);
        await cli.PostAsync($"/api/teams/{team.Id}/invitations", new Invite(Person.NewEmail())).IsAsync(HttpStatusCode.Forbidden, refusal);
        await cli.PutAsync($"/api/teams/{team.Id}", new WriteTeam("Renamed")).IsAsync(HttpStatusCode.Forbidden, refusal);
        await cli.PostAsync("/api/teams", new WriteTeam("Another")).IsAsync(HttpStatusCode.Forbidden, refusal);
        await cli.DeleteAsync($"/api/environments/{environment}").IsAsync(HttpStatusCode.Forbidden, refusal);
        Assert.AreNotEqual("Mine", (await ada.GetAsync<TaskDto>($"/api/tasks/{task.Id}")).Title);

        // Watching a desktop is using it, so the relay takes the web app's token and not the command line's.
        using var desktop = await Desktop.StartAsync(task.Id);
        await desktop.ExchangeAsync();
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Watching.OpenAsync(task.Id, session.AccessToken)).Status);
        Assert.AreEqual(HttpStatusCode.SwitchingProtocols, (await Watching.OpenAsync(task.Id, ada.Token)).Status);
        ada.Dispose();
    }

    [TestMethod]
    public async Task A_member_starts_a_task_in_the_team_and_a_stranger_reaches_none_of_it()
    {
        var (ada, team, environment) = await TeamAsync();
        var address = Person.NewEmail();
        var invitation = await ada.PostAsync<PendingInvitation>($"/api/teams/{team.Id}/invitations", new Invite(address));
        using var ben = await Person.SignUpAsync("Ben Bitdiddle", address, Answers.TokenOf(invitation));
        using var member = await CommandLine.SignInAsync(ben.Email);

        var task = await member.Person.PostAsync<TaskDto>($"/api/teams/{team.Id}/tasks", NewTask(environment));
        Assert.AreEqual(ben.UserId, task.CreatedBy);
        Assert.AreEqual(task.Id, (await ada.GetAsync<TaskDto>($"/api/tasks/{task.Id}")).Id);

        // A stranger's command line is the stranger: no such team, no such task, nothing listed.
        using var grace = await Person.SignUpAsync("Grace Hopper");
        using var stranger = await CommandLine.SignInAsync(grace.Email);
        await stranger.Person.PostAsync($"/api/teams/{team.Id}/tasks", NewTask(environment)).IsAsync(HttpStatusCode.NotFound);
        await stranger.Person.GetAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NotFound);
        await stranger.Person.GetAsync($"/api/tasks/{task.Id}/messages?after=0").IsAsync(HttpStatusCode.NotFound);
        Assert.AreEqual(0, (await stranger.Person.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{team.Id}/environments")).Data.Count);
        Assert.AreEqual(0, (await stranger.Person.GetAsync<ListPage<TaskDto>>($"/api/teams/{team.Id}/tasks")).Data.Count);
        CollectionAssert.DoesNotContain((await stranger.Person.GetAsync<ListPage<TeamDto>>("/api/teams")).Data.Select(x => x.Id).ToList(), team.Id);
        ada.Dispose();
    }

    [TestMethod]
    public async Task Nobody_starts_or_reads_a_task_without_a_credential()
    {
        var (ada, team, environment) = await TeamAsync();
        var task = await ada.PostAsync<TaskDto>($"/api/teams/{team.Id}/tasks", NewTask(environment));
        using var anonymous = new HttpClient { BaseAddress = Stack.Origin };
        await anonymous.PostAsJsonAsync($"/api/teams/{team.Id}/tasks", NewTask(environment), Person.Json).IsAsync(HttpStatusCode.Unauthorized);
        await anonymous.GetAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.Unauthorized);
        await anonymous.GetAsync("/api/teams").IsAsync(HttpStatusCode.Unauthorized);

        // A desktop's key is not a person's: it starts nothing.
        using var desktop = await Desktop.StartAsync(task.Id);
        await desktop.PostAsync($"/api/teams/{team.Id}/tasks", NewTask(environment)).IsAsync(HttpStatusCode.Unauthorized);
        ada.Dispose();
    }

    [TestMethod]
    public async Task Signing_the_command_line_out_ends_its_token_and_its_refresh()
    {
        var (ada, team, environment) = await TeamAsync();
        var session = await CommandLine.SignInAsync(ada.Email);
        await session.Person.GetAsync("/api/teams").IsAsync(HttpStatusCode.OK);

        // `workstream logout`: SqlOS ends the command line's session. The web app's session is another one.
        using var http = new HttpClient { BaseAddress = Stack.Origin };
        await http.PostAsJsonAsync("/sqlos/auth/logout", new { refreshToken = session.RefreshToken }).IsAsync(HttpStatusCode.NoContent);
        await session.Person.GetAsync("/api/teams").IsAsync(HttpStatusCode.Unauthorized);
        await session.Person.PostAsync($"/api/teams/{team.Id}/tasks", NewTask(environment)).IsAsync(HttpStatusCode.Unauthorized);
        var settings = await CommandLine.SettingsAsync();
        using var refreshed = await http.PostAsync("/sqlos/auth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token", ["client_id"] = settings.ClientId, ["refresh_token"] = session.RefreshToken, ["resource"] = settings.Resource,
        }));
        Assert.IsFalse(refreshed.IsSuccessStatusCode, "A signed-out command line cannot refresh.");
        await ada.GetAsync("/api/teams").IsAsync(HttpStatusCode.OK);
        session.Dispose();
        ada.Dispose();
    }

    [TestMethod]
    public async Task A_command_line_the_person_turns_down_gets_nothing()
    {
        var (ada, _, _) = await TeamAsync();
        var device = await CommandLine.StartAsync();
        using (var browser = await CommandLine.Browser.OpenAsync(device))
        {
            await browser.SignInAsync(ada.Email, Person.Password);
            await browser.DenyAsync();
        }
        Assert.AreEqual("access_denied", (await CommandLine.PollAsync(device)).Error);
        ada.Dispose();
    }

    [TestMethod]
    public async Task The_web_app_token_still_reads_and_changes_everything_the_person_may()
    {
        var (ada, team, environment) = await TeamAsync();
        var task = await ada.PostAsync<TaskDto>($"/api/teams/{team.Id}/tasks", NewTask(environment));
        Assert.AreEqual("Renamed", (await ada.PutAsync<TaskDto>($"/api/tasks/{task.Id}", new RenameTask("Renamed"))).Title);
        await ada.PostAsync($"/api/tasks/{task.Id}/messages", new Say("More", Guid.NewGuid().ToString("D"))).IsAsync(HttpStatusCode.Created);
        ada.Dispose();
    }
}
