using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Workstream.Api;
using Workstream.Api.Domain;

namespace Workstream.Tests;

/// <summary>The system administrator's page: every team's numbers, read and changed by them alone.</summary>
[TestClass]
public sealed class AdminJourneys
{
    [TestMethod]
    public async Task The_system_administrator_sees_what_is_running_and_sets_a_teams_limits()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var environment = (await ada.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{team.Id}/environments")).Data.Single();
        var task = await ada.PostAsync<TaskDto>($"/api/teams/{team.Id}/tasks", new StartTask(Guid.NewGuid().ToString("D"), environment.Id, "Quarterly report", "Draft it.", Guid.NewGuid().ToString("D")));
        using var desktop = await Desktop.StartAsync(task.Id);
        await desktop.ExchangeAsync(working: true);

        var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
        Assert.IsTrue((await administrator.GetAsync<MeDto>("/api/me")).SystemAdmin);
        Assert.IsFalse((await ada.GetAsync<MeDto>("/api/me")).SystemAdmin);

        var overview = await administrator.GetAsync<AdminOverview>("/api/admin/overview");
        var running = overview.Desktops.Single(x => x.TaskId == task.Id);
        Assert.AreEqual("Quarterly report", running.Title);
        Assert.AreEqual(team.Name, running.TeamName);
        Assert.IsTrue(running.AgentWorking);
        Assert.IsTrue(overview.RunningDesktops >= 1);
        // A day in half hours, ending now with this desktop counted.
        Assert.AreEqual(49, overview.Running.Count);
        Assert.IsTrue(overview.Running[^1].Running >= 1);
        // What the desktops cost: the machines' time by what they ran for and at which size, at that size's price.
        var tasksTime = overview.Machines.ThisMonth.Single(x => x.Purpose == MachinePurpose.Task && x.Size == DesktopSizes.Standard);
        Assert.IsTrue(tasksTime.Hours >= 0 && tasksTime.Usd >= 0);
        // How long starts take, from what was written down as this one happened: it was a new machine, and it got as far as its agent.
        var fromImage = overview.Starts.Single(x => x.Source == StartSource.Image);
        Assert.IsTrue(fromImage.Starts >= 1);
        Assert.IsNotNull(fromImage.ScreenSeconds);
        Assert.IsNotNull(fromImage.AgentSeconds);
        var numbers = overview.TeamNumbers.Single(x => x.Id == team.Id);
        Assert.AreEqual(1, numbers.People);
        Assert.AreEqual(1, numbers.Tasks);
        Assert.AreEqual(1, numbers.RunningDesktops);

        var changed = await administrator.PutAsync<TeamDto>($"/api/admin/teams/{team.Id}/limits", new SetLimits(75.5m, 5));
        Assert.AreEqual(75.5m, changed.ModelAllowanceUsd);
        Assert.AreEqual(5, (await ada.GetAsync<TeamDto>($"/api/teams/{team.Id}")).MaxRunningDesktops);
        await administrator.PutAsync($"/api/admin/teams/{team.Id}/limits", new SetLimits(-1m, 5)).IsAsync(HttpStatusCode.BadRequest);
        await administrator.PutAsync($"/api/admin/teams/{Guid.NewGuid():N}/limits", new SetLimits(1m, 1)).IsAsync(HttpStatusCode.NotFound);
        // Being the system administrator puts them on no team: they see numbers, not a team's work.
        await administrator.GetAsync($"/api/teams/{team.Id}").IsAsync(HttpStatusCode.NotFound);
        await administrator.GetAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task Nobody_else_reads_or_changes_them()
    {
        var (ada, team) = await Person.WithTeamAsync();
        await ada.GetAsync("/api/admin/overview").IsAsync(HttpStatusCode.Forbidden);
        await ada.PutAsync($"/api/admin/teams/{team.Id}/limits", new SetLimits(1000m, 100)).IsAsync(HttpStatusCode.Forbidden);
        Assert.AreEqual(team.ModelAllowanceUsd, (await ada.GetAsync<TeamDto>($"/api/teams/{team.Id}")).ModelAllowanceUsd);
        using var anonymous = new HttpClient { BaseAddress = Stack.Origin };
        await anonymous.GetAsync("/api/admin/overview").IsAsync(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task The_administrator_is_whoever_the_setting_names_and_nobody_when_it_names_no_one()
    {
        var (ada, _) = await Person.WithTeamAsync();
        using var scope = Stack.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
        try
        {
            // A deploy with another address: the role moves to that person, who already has an account.
            await PlatformSeed.SeedAsync(db, DateTimeOffset.UtcNow, ada.Email, CancellationToken.None);
            Assert.IsTrue((await ada.GetAsync<MeDto>("/api/me")).SystemAdmin);
            await ada.GetAsync("/api/admin/overview").IsAsync(HttpStatusCode.OK);
            var before = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
            await before.GetAsync("/api/admin/overview").IsAsync(HttpStatusCode.Forbidden);

            // A deploy with no address: nobody is.
            await PlatformSeed.SeedAsync(db, DateTimeOffset.UtcNow, null, CancellationToken.None);
            await ada.GetAsync("/api/admin/overview").IsAsync(HttpStatusCode.Forbidden);
            Assert.IsFalse((await ada.GetAsync<MeDto>("/api/me")).SystemAdmin);
        }
        finally
        {
            await PlatformSeed.SeedAsync(db, DateTimeOffset.UtcNow, Stack.AdministratorEmail, CancellationToken.None);
        }
        var again = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
        await again.GetAsync("/api/admin/overview").IsAsync(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task The_host_says_it_is_up_and_serves_sign_in()
    {
        using var http = new HttpClient { BaseAddress = Stack.Origin };
        await http.GetAsync("/health").IsAsync(HttpStatusCode.OK);
        await http.GetAsync("/ready").IsAsync(HttpStatusCode.OK, "ready");
        await http.GetAsync("/sqlos/auth/.well-known/openid-configuration").IsAsync(HttpStatusCode.OK);
        await http.GetAsync("/openapi/v1.json").IsAsync(HttpStatusCode.OK, "start_task");
    }
}
