using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workstream.Api;
using Workstream.Api.Desktops;
using Workstream.Api.Domain;

namespace Workstream.Tests;

/// <summary>
/// Spares: a desktop kept running for a team while someone is in its workspace, the task that starts on it at
/// once, and the limits on what that may cost. These change the limits everyone shares, so they run alone.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SpareJourneys
{
    static DesktopOptions Options => Stack.Services.GetRequiredService<DesktopOptions>();

    static async Task<(Person Person, TeamDto Team, EnvironmentDto Environment)> TeamAsync(string name = "Ada Lovelace")
    {
        var (person, team) = await Person.WithTeamAsync(name);
        var environment = (await person.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{team.Id}/environments")).Data.Single();
        return (person, team, environment);
    }

    static Task<TaskDto> StartAsync(Person person, string teamId, string environmentId, string prompt = "Open example.com and tell me the heading.") =>
        person.PostAsync<TaskDto>($"/api/teams/{teamId}/tasks", new StartTask(Guid.NewGuid().ToString("D"), environmentId, null, prompt, Guid.NewGuid().ToString("D")));

    static async Task<Spare?> SpareOfAsync(string teamId)
    {
        using var scope = Stack.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Spares.AsNoTracking().SingleOrDefaultAsync(x => x.TeamId == teamId);
    }

    /// <summary>Someone opens the workspace, the spare's machine is made, and its agent asks what it is for: the spare is ready.</summary>
    static async Task<(Spare Spare, Desktop Desktop)> ReadySpareAsync(Person person, string teamId)
    {
        await person.PostAsync($"/api/teams/{teamId}/spare", new WantSpare(null)).IsAsync(HttpStatusCode.NoContent);
        var spare = await SpareOfAsync(teamId) ?? throw new AssertFailedException("No spare was asked for.");
        await Stack.Jobs.StartSpareAsync(spare.Id, CancellationToken.None);
        var desktop = Desktop.Of(spare.Id);
        await desktop.GetAsync("/api/desktop/setup").IsAsync(HttpStatusCode.NotFound);
        return ((await SpareOfAsync(teamId))!, desktop);
    }

    /// <summary>A sweep with no patience for a spare nobody has wanted.</summary>
    static DesktopJobs ImpatientSweep() => new(
        Stack.Services.GetRequiredService<IServiceScopeFactory>(), Stack.Desktops,
        new DesktopOptions { Host = "none", Spares = { IdleMinutes = 0 } },
        TimeProvider.System, Stack.Services.GetRequiredService<ILogger<DesktopJobs>>());

    [TestMethod]
    public async Task Someone_in_the_workspace_gets_a_spare_and_their_next_task_starts_on_it_at_once()
    {
        var (ada, team, environment) = await TeamAsync();
        var (spare, desktop) = await ReadySpareAsync(ada, team.Id);
        using var _ = desktop;
        Assert.AreEqual(SpareState.Ready, spare.State);
        Assert.AreEqual(environment.Id, spare.EnvironmentId);
        // Its machine is told the same two things as any desktop, and is marked as a spare.
        var made = Stack.Desktops.For(spare.Id);
        Assert.AreEqual(MachinePurpose.Spare, made.Purpose);
        Assert.AreEqual(team.Id, made.TeamId);
        // Its key opens nothing: there is no task for it to work on yet.
        await desktop.PostAsync("/api/desktop/exchange", new DesktopExchange(false, 0, null)).IsAsync(HttpStatusCode.NotFound);
        await desktop.AskModelAsync().IsAsync(HttpStatusCode.Unauthorized);
        // Opening the workspace again makes no second one.
        await ada.PostAsync($"/api/teams/{team.Id}/spare", new WantSpare(environment.Id)).IsAsync(HttpStatusCode.NoContent);
        Assert.AreEqual(1, Stack.Desktops.Made.Values.Count(spec => spec.TeamId == team.Id));

        // The task starts on it: no machine is made, and there is a screen at once.
        var task = await StartAsync(ada, team.Id, environment.Id);
        Assert.AreEqual("starting", task.State);
        Assert.AreEqual("agent", task.Step);
        Assert.AreEqual(1, Stack.Desktops.Made.Values.Count(spec => spec.TeamId == team.Id));
        Assert.IsNull(await SpareOfAsync(team.Id));
        Assert.AreEqual(HttpStatusCode.SwitchingProtocols, (await Watching.OpenAsync(task.Id, ada.Token)).Status);

        // The same desktop, with the key it always had, now works on the task.
        var setup = await desktop.SetupAsync();
        Assert.AreEqual(task.Id, setup.TaskId);
        var first = await desktop.ExchangeAsync(working: true, after: 0, Desktop.Agent("Opening the browser.", "activity"));
        Assert.AreEqual("Open example.com and tell me the heading.", first.Prompts.Single().Text);
        Assert.AreEqual("ready", (await ada.GetAsync<TaskDto>($"/api/tasks/{task.Id}")).State);
        await desktop.AskModelAsync().IsAsync(HttpStatusCode.OK);
        // The team's usage counts the task, and one running desktop.
        Assert.AreEqual(1, (await ada.GetAsync<TeamUsage>($"/api/teams/{team.Id}/usage")).RunningDesktops);

        // It stops and starts again as the task's own machine.
        await ada.PostAsync($"/api/tasks/{task.Id}/stop").IsAsync(HttpStatusCode.OK);
        await Stack.Jobs.StopAsync(task.Id, CancellationToken.None);
        Assert.IsFalse(Stack.Desktops.Running[desktop.Host]);
        await ada.PostAsync($"/api/tasks/{task.Id}/start").IsAsync(HttpStatusCode.OK);
        await Stack.Jobs.StartAsync(task.Id, CancellationToken.None);
        Assert.IsTrue(Stack.Desktops.Running[desktop.Host]);
        await desktop.ExchangeAsync();
        Assert.AreEqual("ready", (await ada.GetAsync<TaskDto>($"/api/tasks/{task.Id}")).State);

        // Deleting the task deletes the machine and ends the desktop's key.
        await ada.DeleteAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NoContent);
        await Stack.Jobs.DeleteAsync(task.Id, CancellationToken.None);
        CollectionAssert.Contains(Stack.Desktops.Deleted.ToList(), desktop.Host);
        await desktop.GetAsync("/api/desktop/setup").IsAsync(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task After_a_task_takes_the_spare_the_team_gets_another()
    {
        var (ada, team, environment) = await TeamAsync();
        var (first, desktop) = await ReadySpareAsync(ada, team.Id);
        using var _ = desktop;
        var task = await StartAsync(ada, team.Id, environment.Id);
        // What the start queues, run here: the next spare, for the environment just used.
        await Stack.Jobs.RefillSpareAsync(team.Id, environment.Id, CancellationToken.None);
        var next = await SpareOfAsync(team.Id);
        Assert.IsNotNull(next);
        Assert.AreNotEqual(first.Id, next.Id);
        Assert.IsTrue(Stack.Desktops.Made.Values.Any(spec => spec.DesktopId == next.Id));

        // A team at its limit of running desktops gets none: it could not use it.
        using var second = Desktop.Of(next.Id);
        await second.GetAsync("/api/desktop/setup").IsAsync(HttpStatusCode.NotFound);
        var other = await StartAsync(ada, team.Id, environment.Id, "A second.");
        Assert.AreEqual("agent", other.Step);
        await Stack.Jobs.RefillSpareAsync(team.Id, environment.Id, CancellationToken.None);
        Assert.IsNull(await SpareOfAsync(team.Id));
        await ada.PostAsync($"/api/teams/{team.Id}/spare", new WantSpare(null)).IsAsync(HttpStatusCode.NoContent);
        Assert.IsNull(await SpareOfAsync(team.Id));

        foreach (var id in new[] { task.Id, other.Id })
        {
            await ada.DeleteAsync($"/api/tasks/{id}").IsAsync(HttpStatusCode.NoContent);
            await Stack.Jobs.DeleteAsync(id, CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task Only_someone_who_may_start_tasks_in_the_team_gets_it_a_spare()
    {
        var (ada, team, environment) = await TeamAsync();
        var invitation = await ada.PostAsync<PendingInvitation>($"/api/teams/{team.Id}/invitations", new Invite(Person.NewEmail()));
        using var ben = await Person.SignUpAsync("Ben Shneiderman", invitation.Email, Answers.TokenOf(invitation));
        using var stranger = await Person.SignUpAsync("A Stranger");
        using var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
        using var anonymous = new HttpClient { BaseAddress = Stack.Origin };

        // A stranger and the system administrator are told there is no such team; nobody is nobody.
        await stranger.PostAsync($"/api/teams/{team.Id}/spare", new WantSpare(null)).IsAsync(HttpStatusCode.NotFound);
        await administrator.PostAsync($"/api/teams/{team.Id}/spare", new WantSpare(null)).IsAsync(HttpStatusCode.NotFound);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/teams/{team.Id}/spare", null)).StatusCode);
        Assert.IsNull(await SpareOfAsync(team.Id));

        // A member does, and a desktop's key is not a person's.
        var (spare, desktop) = await ReadySpareAsync(ben, team.Id);
        using var _ = desktop;
        await desktop.PostAsync($"/api/teams/{team.Id}/spare", new WantSpare(null)).IsAsync(HttpStatusCode.Unauthorized);
        // An environment that is not the team's is not taken at their word: the spare is for one of the team's.
        Assert.AreEqual(environment.Id, spare.EnvironmentId);

        await ImpatientSweep().SweepAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task A_spare_nobody_takes_is_removed_with_its_machine_its_key_and_its_time()
    {
        var (ada, team, _) = await TeamAsync();
        var (spare, desktop) = await ReadySpareAsync(ada, team.Id);
        using var __ = desktop;
        var machine = desktop.Host;

        // The system administrator sees it while it runs, and its time as a line of its own.
        using var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
        var overview = await administrator.GetAsync<AdminOverview>("/api/admin/overview");
        Assert.IsTrue(overview.Machines.Spares.Any(x => x.TeamId == team.Id && x.State == "ready"));
        Assert.IsTrue(overview.Machines.ThisMonth.Any(x => x.Purpose == MachinePurpose.Spare));
        // It is no task, so it is not among the desktops running.
        Assert.IsFalse(overview.Desktops.Any(x => x.TeamId == team.Id));

        await Task.Delay(TimeSpan.FromMilliseconds(20));
        await ImpatientSweep().SweepAsync(CancellationToken.None);
        Assert.IsNull(await SpareOfAsync(team.Id));
        CollectionAssert.Contains(Stack.Desktops.Deleted.ToList(), machine);
        await desktop.GetAsync("/api/desktop/setup").IsAsync(HttpStatusCode.Unauthorized);
        // Its time is closed, and written as a spare's, not as the team's use.
        using var scope = Stack.Services.CreateScope();
        var run = await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().DesktopRuns.AsNoTracking().SingleAsync(x => x.TaskId == spare.Id);
        Assert.AreEqual(MachinePurpose.Spare, run.Purpose);
        Assert.IsNotNull(run.EndedAt);
        Assert.AreEqual(0d, (await ada.GetAsync<TeamUsage>($"/api/teams/{team.Id}/usage")).DesktopHours);
    }

    [TestMethod]
    public async Task A_task_that_needs_another_size_of_machine_leaves_the_spare_alone()
    {
        var (ada, team, _) = await TeamAsync();
        var (spare, desktop) = await ReadySpareAsync(ada, team.Id);
        using var __ = desktop;
        var large = await ada.PostAsync<EnvironmentDto>($"/api/teams/{team.Id}/environments",
            new AddEnvironment(Guid.NewGuid().ToString("D"), "Large desktop", ["chrome"], DesktopSizes.Large, null, null, null, null, null));
        var task = await StartAsync(ada, team.Id, large.Id);
        Assert.AreEqual("machine", task.Step);
        using var own = await Desktop.StartAsync(task.Id);
        Assert.AreEqual(DesktopSizes.Large, Stack.Desktops.For(task.Id).Size);
        Assert.AreEqual(spare.Id, (await SpareOfAsync(team.Id))?.Id);

        await ada.DeleteAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NoContent);
        await Stack.Jobs.DeleteAsync(task.Id, CancellationToken.None);
        await ImpatientSweep().SweepAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task The_limits_decide_whether_a_spare_is_made()
    {
        var (ada, teamA, _) = await TeamAsync();
        var (grace, teamB, __) = await TeamAsync("Grace Hopper");
        var limits = Options.Spares;
        var (max, hours, enabled) = (limits.Max, limits.HoursPerDay, limits.Enabled);
        // Whatever another journey left is gone first, so the counts below are this journey's own.
        await ImpatientSweep().SweepAsync(CancellationToken.None);
        try
        {
            // As many at once as the setting says, over every team.
            limits.Max = 1;
            var (_, desktop) = await ReadySpareAsync(ada, teamA.Id);
            using var ___ = desktop;
            await grace.PostAsync($"/api/teams/{teamB.Id}/spare", new WantSpare(null)).IsAsync(HttpStatusCode.NoContent);
            Assert.IsNull(await SpareOfAsync(teamB.Id));
            limits.Max = max;

            // No more hours of spare time in a day than the setting says.
            limits.HoursPerDay = 0;
            await grace.PostAsync($"/api/teams/{teamB.Id}/spare", new WantSpare(null)).IsAsync(HttpStatusCode.NoContent);
            Assert.IsNull(await SpareOfAsync(teamB.Id));
            limits.HoursPerDay = hours;

            // And none at all when spares are off: a task then starts on a machine of its own.
            limits.Enabled = false;
            await grace.PostAsync($"/api/teams/{teamB.Id}/spare", new WantSpare(null)).IsAsync(HttpStatusCode.NoContent);
            Assert.IsNull(await SpareOfAsync(teamB.Id));
            var environment = (await ada.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{teamA.Id}/environments")).Data.Single();
            var task = await StartAsync(ada, teamA.Id, environment.Id);
            Assert.AreEqual("machine", task.Step);
            Assert.IsNotNull(await SpareOfAsync(teamA.Id));
            await ada.DeleteAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NoContent);
            await Stack.Jobs.DeleteAsync(task.Id, CancellationToken.None);
        }
        finally
        {
            (limits.Max, limits.HoursPerDay, limits.Enabled) = (max, hours, enabled);
            await ImpatientSweep().SweepAsync(CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task A_spare_whose_machine_cannot_be_made_is_forgotten()
    {
        var (ada, team, environment) = await TeamAsync();
        await ada.PostAsync($"/api/teams/{team.Id}/spare", new WantSpare(null)).IsAsync(HttpStatusCode.NoContent);
        var spare = (await SpareOfAsync(team.Id))!;
        Stack.Desktops.Refuse[spare.Id] = true;
        await Stack.Jobs.StartSpareAsync(spare.Id, CancellationToken.None);
        Assert.IsNull(await SpareOfAsync(team.Id));
        // The task that follows starts the ordinary way.
        var task = await StartAsync(ada, team.Id, environment.Id);
        Assert.AreEqual("machine", task.Step);
        await ada.DeleteAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NoContent);
        await Stack.Jobs.DeleteAsync(task.Id, CancellationToken.None);
    }
}
