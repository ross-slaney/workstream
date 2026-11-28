using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workstream.Api;
using Workstream.Api.Desktops;
using Workstream.Api.Domain;

namespace Workstream.Tests;

/// <summary>Tasks: a desktop of its own for each, the conversation with its agent, and who may reach either.</summary>
[TestClass]
public sealed class TaskJourneys
{
    static async Task<(Person Person, TeamDto Team, string EnvironmentId)> TeamAsync(string name = "Ada Lovelace")
    {
        var (person, team) = await Person.WithTeamAsync(name);
        var environment = (await person.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{team.Id}/environments")).Data.Single();
        return (person, team, environment.Id);
    }

    static Task<TaskDto> StartAsync(Person person, string teamId, string environmentId, string prompt = "Open example.com and tell me the heading.") =>
        person.PostAsync<TaskDto>($"/api/teams/{teamId}/tasks", new StartTask(Guid.NewGuid().ToString("D"), environmentId, null, prompt, Guid.NewGuid().ToString("D")));

    [TestMethod]
    public async Task Starting_a_task_makes_a_desktop_that_sets_itself_up_and_talks_with_the_person()
    {
        var (ada, team, environment) = await TeamAsync();
        var task = await StartAsync(ada, team.Id, environment);
        Assert.AreEqual("starting", task.State);
        // The title is the start of what was asked.
        StringAssert.StartsWith(task.Title, "Open example.com");
        Assert.AreEqual(ada.UserId, task.CreatedBy);

        using var desktop = await Desktop.StartAsync(task.Id);
        // The machine is told two things: where the API is, and its key.
        var spec = Stack.Desktops.For(task.Id);
        Assert.AreEqual(Stack.Origin.GetLeftPart(UriPartial.Authority), spec.ApiUrl);
        Assert.AreEqual(team.Id, spec.TeamId);

        var setup = await desktop.SetupAsync();
        Assert.AreEqual(task.Id, setup.TaskId);
        CollectionAssert.Contains(setup.Apps.ToList(), "chrome");
        // Every model call goes back through this API.
        StringAssert.StartsWith(setup.Model.BaseUrl, Stack.Origin.GetLeftPart(UriPartial.Authority));
        Assert.AreEqual("claude-opus-5-5", setup.Model.Main);

        // The agent's first turn: it reads what the person asked, and says what it is doing.
        var first = await desktop.ExchangeAsync(working: true, after: 0, Desktop.Agent("Opening the browser.", "activity"), Desktop.Agent("The heading is Example Domain."));
        Assert.AreEqual("Open example.com and tell me the heading.", first.Prompts.Single().Text);
        var conversation = await ada.GetAsync<ConversationDto>($"/api/tasks/{task.Id}/messages?after=0");
        Assert.AreEqual("ready", conversation.Task.State);
        Assert.IsTrue(conversation.Task.AgentWorking);
        CollectionAssert.AreEqual(new[] { "person", "agent", "agent" }, conversation.Messages.Select(x => x.Author).ToArray());
        Assert.IsTrue(conversation.Messages[0].Mine);
        Assert.AreEqual("activity", conversation.Messages[1].Kind);

        // The person answers; a send tried again writes once; the agent reads only what is new.
        var key = Guid.NewGuid().ToString("D");
        await ada.PostAsync($"/api/tasks/{task.Id}/messages", new Say("Now open the About page.", key)).IsAsync(HttpStatusCode.Created);
        await ada.PostAsync($"/api/tasks/{task.Id}/messages", new Say("Now open the About page.", key)).IsAsync(HttpStatusCode.Conflict);
        var second = await desktop.ExchangeAsync(working: false, after: first.Prompts.Single().Id);
        Assert.AreEqual("Now open the About page.", second.Prompts.Single().Text);
        var later = await ada.GetAsync<ConversationDto>($"/api/tasks/{task.Id}/messages?after={conversation.Messages[^1].Id}");
        Assert.AreEqual(1, later.Messages.Count);
        Assert.IsFalse(later.Task.AgentWorking);

        // Stopping the agent mid-turn reaches the desktop as a count that went up.
        await ada.PostAsync($"/api/tasks/{task.Id}/interrupt").IsAsync(HttpStatusCode.NoContent);
        Assert.AreEqual(second.Interrupts + 1, (await desktop.ExchangeAsync(after: second.Prompts.Single().Id)).Interrupts);

        await ada.PostAsync($"/api/tasks/{task.Id}/messages", new Say("  ", Guid.NewGuid().ToString("D"))).IsAsync(HttpStatusCode.BadRequest);
        Assert.AreEqual("Heading check", (await ada.PutAsync<TaskDto>($"/api/tasks/{task.Id}", new RenameTask("Heading check"))).Title);
    }

    [TestMethod]
    public async Task The_task_says_which_command_the_agent_is_running_and_since_when()
    {
        var (ada, team, environment) = await TeamAsync();
        var task = await StartAsync(ada, team.Id, environment);
        using var desktop = await Desktop.StartAsync(task.Id);
        await desktop.ExchangeAsync(working: true);
        Assert.IsNull((await ada.GetAsync<TaskDto>($"/api/tasks/{task.Id}")).Running);

        // A build that has run for ten minutes: the start is counted back from the API's clock.
        var before = DateTimeOffset.UtcNow;
        await desktop.ExchangeRunningAsync("./gradlew assembleDebug", 600);
        var running = (await ada.GetAsync<TaskDto>($"/api/tasks/{task.Id}")).Running!;
        Assert.AreEqual("./gradlew assembleDebug", running.Text);
        Assert.IsTrue((before - running.Since - TimeSpan.FromSeconds(600)).Duration() < TimeSpan.FromSeconds(5), $"Started {running.Since}, asked at {before}.");

        // The same command a moment later keeps its start; a count that has moved away replaces it.
        await desktop.ExchangeRunningAsync("./gradlew assembleDebug", 602);
        Assert.AreEqual(running.Since, (await ada.GetAsync<TaskDto>($"/api/tasks/{task.Id}")).Running!.Since);
        await desktop.ExchangeRunningAsync("./gradlew assembleDebug", 30);
        Assert.IsTrue((await ada.GetAsync<TaskDto>($"/api/tasks/{task.Id}")).Running!.Since > running.Since.AddMinutes(5));

        // A long command is cut to what the row holds, and a teammate reads the same.
        await desktop.ExchangeRunningAsync(new string('x', 500), 1);
        Assert.AreEqual(WorkTask.MaxRunningLength, (await ada.GetAsync<TaskDto>($"/api/tasks/{task.Id}")).Running!.Text.Length);

        // Between commands, and when the agent has finished, nothing is said to be running.
        await desktop.ExchangeAsync(working: true);
        Assert.IsNull((await ada.GetAsync<TaskDto>($"/api/tasks/{task.Id}")).Running);
        await desktop.ExchangeRunningAsync("npm test", 3);
        await desktop.ExchangeRunningAsync("npm test", 4, working: false);
        Assert.IsNull((await ada.GetAsync<TaskDto>($"/api/tasks/{task.Id}")).Running);

        // A stranger reads none of it.
        var (grace, _) = await Person.WithTeamAsync("Grace Hopper");
        await grace.GetAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task A_desktop_is_stopped_started_again_and_deleted_with_its_task()
    {
        var (ada, team, environment) = await TeamAsync();
        var task = await StartAsync(ada, team.Id, environment);
        using var desktop = await Desktop.StartAsync(task.Id);
        await desktop.ExchangeAsync();

        Assert.AreEqual("stopping", (await ada.PostAsync<TaskDto>($"/api/tasks/{task.Id}/stop")).State);
        await Stack.Jobs.StopAsync(task.Id, CancellationToken.None);
        Assert.AreEqual("stopped", (await ada.GetAsync<TaskDto>($"/api/tasks/{task.Id}")).State);
        Assert.IsFalse(Stack.Desktops.Running[desktop.Host]);
        // A stopped desktop's time is counted up to the stop.
        Assert.AreEqual(0, (await ada.GetAsync<TeamUsage>($"/api/teams/{team.Id}/usage")).RunningDesktops);

        Assert.AreEqual("starting", (await ada.PostAsync<TaskDto>($"/api/tasks/{task.Id}/start")).State);
        await Stack.Jobs.StartAsync(task.Id, CancellationToken.None);
        Assert.IsTrue(Stack.Desktops.Running[desktop.Host]);
        await desktop.ExchangeAsync();
        Assert.AreEqual("ready", (await ada.GetAsync<TaskDto>($"/api/tasks/{task.Id}")).State);
        Assert.AreEqual(1, (await ada.GetAsync<TeamUsage>($"/api/teams/{team.Id}/usage")).RunningDesktops);

        await ada.DeleteAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NoContent);
        // Gone from the list at once; the machine goes when the job has run.
        Assert.AreEqual(0, (await ada.GetAsync<ListPage<TaskDto>>($"/api/teams/{team.Id}/tasks")).Data.Count);
        await Stack.Jobs.DeleteAsync(task.Id, CancellationToken.None);
        CollectionAssert.Contains(Stack.Desktops.Deleted.ToList(), desktop.Host);
        await ada.GetAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NotFound);
        // Its key died with it.
        await desktop.GetAsync("/api/desktop/setup").IsAsync(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task Everyone_on_the_team_shares_a_task_and_a_stranger_reaches_none_of_it()
    {
        var (ada, team, environment) = await TeamAsync();
        var task = await StartAsync(ada, team.Id, environment);
        var invitation = await ada.PostAsync<PendingInvitation>($"/api/teams/{team.Id}/invitations", new Invite(Person.NewEmail()));
        var lin = await Person.SignUpAsync("Lin Clark", invitation.Email, Answers.TokenOf(invitation));
        var stranger = await Person.SignUpAsync("Grace Hopper");

        // A teammate sees it, with who started it, writes to it and may stop it.
        var seen = (await lin.GetAsync<ListPage<TaskDto>>($"/api/teams/{team.Id}/tasks")).Data.Single();
        Assert.AreEqual("Ada Lovelace", seen.CreatedByName);
        await lin.PostAsync($"/api/tasks/{task.Id}/messages", new Say("I'll take a look too.", Guid.NewGuid().ToString("D"))).IsAsync(HttpStatusCode.Created);
        var conversation = await ada.GetAsync<ConversationDto>($"/api/tasks/{task.Id}/messages?after=0");
        Assert.AreEqual("Lin Clark", conversation.Messages[^1].AuthorName);
        Assert.IsFalse(conversation.Messages[^1].Mine);

        foreach (var path in new[] { $"/api/tasks/{task.Id}", $"/api/tasks/{task.Id}/messages?after=0" })
        {
            await stranger.GetAsync(path).IsAsync(HttpStatusCode.NotFound);
        }
        // A list is an authorized page: someone who may see none of it gets an empty one.
        Assert.AreEqual(0, (await stranger.GetAsync<ListPage<TaskDto>>($"/api/teams/{team.Id}/tasks")).Data.Count);
        await stranger.PostAsync($"/api/tasks/{task.Id}/messages", new Say("Hello?", Guid.NewGuid().ToString("D"))).IsAsync(HttpStatusCode.NotFound);
        await stranger.PostAsync($"/api/tasks/{task.Id}/stop").IsAsync(HttpStatusCode.NotFound);
        await stranger.PostAsync($"/api/tasks/{task.Id}/interrupt").IsAsync(HttpStatusCode.NotFound);
        await stranger.PutAsync($"/api/tasks/{task.Id}", new RenameTask("Mine")).IsAsync(HttpStatusCode.NotFound);
        await stranger.DeleteAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NotFound);
        await stranger.PostAsync($"/api/teams/{team.Id}/tasks", new StartTask(Guid.NewGuid().ToString("D"), environment, null, "Hi", Guid.NewGuid().ToString("D"))).IsAsync(HttpStatusCode.NotFound);
        // Starting in their own team from someone else's environment is refused too.
        var theirs = (await stranger.GetAsync<ListPage<TeamDto>>("/api/teams")).Data.Single();
        await stranger.PostAsync($"/api/teams/{theirs.Id}/tasks", new StartTask(Guid.NewGuid().ToString("D"), environment, null, "Hi", Guid.NewGuid().ToString("D"))).IsAsync(HttpStatusCode.BadRequest);

        // Once removed from the team, the teammate reaches none of it either: SqlOS ends the session that was
        // for this team, and signing in again finds nothing of it.
        await ada.DeleteAsync($"/api/teams/{team.Id}/people/{lin.UserId}").IsAsync(HttpStatusCode.NoContent);
        await lin.GetAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.Unauthorized);
        var back = await Person.SignInAsync(lin.Email);
        await back.GetAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task A_desktops_key_reaches_its_own_task_and_nothing_a_person_does()
    {
        var (ada, team, environment) = await TeamAsync();
        var mine = await StartAsync(ada, team.Id, environment, "First task.");
        var other = await StartAsync(ada, team.Id, environment, "Second task.");
        using var desktop = await Desktop.StartAsync(mine.Id);
        using var second = await Desktop.StartAsync(other.Id);

        Assert.AreEqual(mine.Id, (await desktop.SetupAsync()).TaskId);
        Assert.AreEqual("First task.", (await desktop.ExchangeAsync()).Prompts.Single().Text);
        Assert.AreEqual("Second task.", (await second.ExchangeAsync()).Prompts.Single().Text);

        // The key is not a person's token: none of the people's routes take it.
        foreach (var path in new[] { "/api/me", "/api/teams", $"/api/tasks/{mine.Id}", $"/api/tasks/{other.Id}/messages?after=0" })
        {
            await desktop.GetAsync(path).IsAsync(HttpStatusCode.Unauthorized);
        }
        // And a person's token is not a key.
        await ada.GetAsync("/api/desktop/setup").IsAsync(HttpStatusCode.Unauthorized);
        await ada.PostAsync("/api/desktop/exchange", new DesktopExchange(false, 0, null)).IsAsync(HttpStatusCode.Unauthorized);
        using var anonymous = new HttpClient { BaseAddress = Stack.Origin };
        await anonymous.GetAsync("/api/desktop/setup").IsAsync(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task A_team_runs_as_many_desktops_as_its_limit_and_no_more()
    {
        var (ada, team, environment) = await TeamAsync();
        Assert.AreEqual(2, team.MaxRunningDesktops);
        var first = await StartAsync(ada, team.Id, environment);
        await StartAsync(ada, team.Id, environment);
        await ada.PostAsync($"/api/teams/{team.Id}/tasks", new StartTask(Guid.NewGuid().ToString("D"), environment, null, "A third.", Guid.NewGuid().ToString("D")))
            .IsAsync(HttpStatusCode.PaymentRequired, "limit_reached");

        // Pausing one makes room, and the paused one cannot start while the room is taken again.
        await ada.PostAsync($"/api/tasks/{first.Id}/stop").IsAsync(HttpStatusCode.OK);
        await Stack.Jobs.StopAsync(first.Id, CancellationToken.None);
        await StartAsync(ada, team.Id, environment, "A third.");
        await ada.PostAsync($"/api/tasks/{first.Id}/start").IsAsync(HttpStatusCode.PaymentRequired);
    }

    [TestMethod]
    public async Task A_desktop_that_cannot_be_made_fails_in_words_and_can_be_started_again()
    {
        var (ada, team, environment) = await TeamAsync();
        var task = await StartAsync(ada, team.Id, environment);
        Stack.Desktops.Refuse[task.Id] = true;
        await Stack.Jobs.StartAsync(task.Id, CancellationToken.None);
        var failed = await ada.GetAsync<TaskDto>($"/api/tasks/{task.Id}");
        Assert.AreEqual("failed", failed.State);
        Assert.IsFalse(string.IsNullOrWhiteSpace(failed.Failure));
        // A failed desktop holds no place against the limit, and whatever was made of its machine is removed.
        Assert.AreEqual(0, (await ada.GetAsync<TeamUsage>($"/api/teams/{team.Id}/usage")).RunningDesktops);
        CollectionAssert.Contains(Stack.Desktops.Deleted.ToList(), DesktopHosting.NameFor(task.Id));

        Stack.Desktops.Refuse.TryRemove(task.Id, out _);
        Assert.AreEqual("starting", (await ada.PostAsync<TaskDto>($"/api/tasks/{task.Id}/start")).State);
        using var desktop = await Desktop.StartAsync(task.Id);
        await desktop.ExchangeAsync();
        Assert.AreEqual("ready", (await ada.GetAsync<TaskDto>($"/api/tasks/{task.Id}")).State);
    }

    /// <summary>The task, read until its start has come as far as <paramref name="step"/>.</summary>
    static async Task<TaskDto> UntilStepAsync(Person person, string taskId, string step)
    {
        for (var asked = 0; asked < 100; asked++)
        {
            var task = await person.GetAsync<TaskDto>($"/api/tasks/{taskId}");
            if (task.Step == step) return task;
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
        Assert.Fail($"The start never reached '{step}'.");
        return null!;
    }

    [TestMethod]
    public async Task A_start_is_written_down_step_by_step_and_its_screen_is_watched_before_the_agent_joins()
    {
        var (ada, team, environment) = await TeamAsync();
        var task = await StartAsync(ada, team.Id, environment);
        Assert.AreEqual("machine", task.Step);
        var machine = DesktopHosting.NameFor(task.Id);
        // The host has only taken the request: the machine is not on, and nothing answers.
        Stack.Desktops.NotOn[machine] = true;
        Stack.Desktops.NoDisplay[machine] = true;
        var starting = Stack.Jobs.StartAsync(task.Id, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        Assert.AreEqual("machine", (await ada.GetAsync<TaskDto>($"/api/tasks/{task.Id}")).Step);
        Assert.AreEqual(HttpStatusCode.Conflict, (await Watching.OpenAsync(task.Id, ada.Token)).Status);

        // The host says it is on.
        Stack.Desktops.NotOn.TryRemove(machine, out _);
        await UntilStepAsync(ada, task.Id, "boot");
        Assert.AreEqual(HttpStatusCode.Conflict, (await Watching.OpenAsync(task.Id, ada.Token)).Status);

        // Its display answers: the job's work is done, and there is a screen, though no agent has spoken yet.
        Stack.Desktops.NoDisplay.TryRemove(machine, out _);
        await starting;
        var watchable = await ada.GetAsync<TaskDto>($"/api/tasks/{task.Id}");
        Assert.AreEqual("starting", watchable.State);
        Assert.AreEqual("agent", watchable.Step);
        var (status, greeting) = await Watching.OpenAsync(task.Id, ada.Token);
        Assert.AreEqual(HttpStatusCode.SwitchingProtocols, status);
        Assert.AreEqual(PlayedDisplay.Greeting, greeting);

        // The agent's first exchange makes it ready, and the screen stays.
        using var desktop = Desktop.Of(task.Id);
        await desktop.ExchangeAsync();
        Assert.AreEqual("ready", (await ada.GetAsync<TaskDto>($"/api/tasks/{task.Id}")).State);
        Assert.AreEqual(HttpStatusCode.SwitchingProtocols, (await Watching.OpenAsync(task.Id, ada.Token)).Status);

        // A desktop asked to stop has no screen, and the next start begins from the first step.
        await ada.PostAsync($"/api/tasks/{task.Id}/stop").IsAsync(HttpStatusCode.OK);
        Assert.AreEqual(HttpStatusCode.Conflict, (await Watching.OpenAsync(task.Id, ada.Token)).Status);
        await Stack.Jobs.StopAsync(task.Id, CancellationToken.None);
        Assert.AreEqual("machine", (await ada.PostAsync<TaskDto>($"/api/tasks/{task.Id}/start")).Step);
    }

    [TestMethod]
    public async Task Only_the_team_watches_a_desktop_and_only_from_the_web_app()
    {
        var (ada, team, environment) = await TeamAsync();
        var invitation = await ada.PostAsync<PendingInvitation>($"/api/teams/{team.Id}/invitations", new Invite(Person.NewEmail()));
        using var ben = await Person.SignUpAsync("Ben Shneiderman", invitation.Email, Answers.TokenOf(invitation));
        using var stranger = await Person.SignUpAsync("A Stranger");
        using var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
        var task = await StartAsync(ada, team.Id, environment);
        using var desktop = await Desktop.StartAsync(task.Id);

        // The team's administrator and a member.
        Assert.AreEqual(HttpStatusCode.SwitchingProtocols, (await Watching.OpenAsync(task.Id, ada.Token)).Status);
        Assert.AreEqual(HttpStatusCode.SwitchingProtocols, (await Watching.OpenAsync(task.Id, ben.Token)).Status);
        // A stranger and the system administrator are told there is no such task.
        Assert.AreEqual(HttpStatusCode.NotFound, (await Watching.OpenAsync(task.Id, stranger.Token)).Status);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Watching.OpenAsync(task.Id, administrator.Token)).Status);
        // A desktop's key is not a person's token, and nobody is nobody.
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Watching.OpenAsync(task.Id, desktop.Key)).Status);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Watching.OpenAsync(task.Id, null)).Status);
        // Another site's page cannot open the stream with a person's token.
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Watching.OpenAsync(task.Id, ada.Token, "https://elsewhere.example")).Status);
    }

    [TestMethod]
    public async Task A_machine_the_host_could_not_start_fails_the_task_in_words_and_is_removed()
    {
        var (ada, team, environment) = await TeamAsync();
        var task = await StartAsync(ada, team.Id, environment);
        var machine = DesktopHosting.NameFor(task.Id);
        // The host took the request and then could not find room for the machine.
        Stack.Desktops.Broken[machine] = "Allocation failed. We do not have sufficient capacity for the requested VM size in this region.";
        await Stack.Jobs.StartAsync(task.Id, CancellationToken.None);
        var failed = await ada.GetAsync<TaskDto>($"/api/tasks/{task.Id}");
        Assert.AreEqual("failed", failed.State);
        StringAssert.Contains(failed.Failure, "no capacity");
        CollectionAssert.Contains(Stack.Desktops.Deleted.ToList(), machine);
        Assert.AreEqual(HttpStatusCode.Conflict, (await Watching.OpenAsync(task.Id, ada.Token)).Status);
    }

    [TestMethod]
    public async Task The_sweep_pauses_an_unused_desktop_fails_one_that_never_arrived_and_removes_strays()
    {
        var (ada, team, environment) = await TeamAsync();
        var idle = await StartAsync(ada, team.Id, environment, "Left alone.");
        var silent = await StartAsync(ada, team.Id, environment, "Never arrives.");
        using var desktop = await Desktop.StartAsync(idle.Id);
        await desktop.ExchangeAsync();
        await Stack.Jobs.StartAsync(silent.Id, CancellationToken.None);
        // Two machines no task knows. One was left behind long ago by something that went wrong; the other is
        // a minute old, and could belong to a task started while the sweep was busy.
        await Stack.Desktops.CreateAsync(new DesktopSpec("ws-stray-0000000000", Guid.NewGuid().ToString("D"), team.Id, "standard", "", ""), CancellationToken.None);
        Stack.Desktops.MadeAt["ws-stray-0000000000"] = DateTimeOffset.UtcNow.AddHours(-2);
        await Stack.Desktops.CreateAsync(new DesktopSpec("ws-new-00000000000", Guid.NewGuid().ToString("D"), team.Id, "standard", "", ""), CancellationToken.None);
        Stack.Desktops.MadeAt["ws-new-00000000000"] = DateTimeOffset.UtcNow.AddMinutes(-1);

        // Nobody has used the first for longer than its team's half hour (PauseJourneys has what counts as use).
        using (var scope = Stack.Services.CreateScope())
        {
            var longAgo = DateTimeOffset.UtcNow.AddMinutes(-31);
            await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Tasks.Where(x => x.Id == idle.Id).ExecuteUpdateAsync(x => x.SetProperty(task => task.ActiveAt, longAgo));
        }
        // The same sweep with no patience for a start: late at once.
        var impatient = new DesktopJobs(
            Stack.Services.GetRequiredService<IServiceScopeFactory>(), Stack.Desktops, new DesktopOptions { Host = "none", StartTimeoutMinutes = 0 },
            TimeProvider.System, Stack.Services.GetRequiredService<ILogger<DesktopJobs>>());
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await impatient.SweepAsync(CancellationToken.None);

        var swept = await ada.GetAsync<TaskDto>($"/api/tasks/{idle.Id}");
        Assert.IsTrue(swept.State is "stopping" or "stopped", swept.State);
        Assert.AreEqual("failed", (await ada.GetAsync<TaskDto>($"/api/tasks/{silent.Id}")).State);
        Assert.IsFalse(Stack.Desktops.Made.ContainsKey("ws-stray-0000000000"));
        Assert.IsTrue(Stack.Desktops.Made.ContainsKey("ws-new-00000000000"));
        await Stack.Desktops.DeleteAsync("ws-new-00000000000", CancellationToken.None);
    }
}
