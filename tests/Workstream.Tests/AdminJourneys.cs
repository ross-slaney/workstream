using System.Net;
using Workstream.Api;

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
    public async Task The_host_says_it_is_up_and_serves_sign_in()
    {
        using var http = new HttpClient { BaseAddress = Stack.Origin };
        await http.GetAsync("/health").IsAsync(HttpStatusCode.OK);
        await http.GetAsync("/ready").IsAsync(HttpStatusCode.OK, "ready");
        await http.GetAsync("/sqlos/auth/.well-known/openid-configuration").IsAsync(HttpStatusCode.OK);
        await http.GetAsync("/openapi/v1.json").IsAsync(HttpStatusCode.OK, "start_task");
    }
}
