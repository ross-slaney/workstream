using System.Net;
using Workstream.Api;

namespace Workstream.Tests;

/// <summary>Environments: what a task's desktop comes with, and the variables whose values are never sent back.</summary>
[TestClass]
public sealed class EnvironmentJourneys
{
    static AddEnvironment Draft(string id, string name = "Web app", params VariableChange[] variables) =>
        new(id, name, ["chrome", "vscode", "terminal"], "small", "https://github.com/acme/app", "main", "npm ci", "Run the tests before you say you are done.", variables);

    [TestMethod]
    public async Task Anyone_on_the_team_adds_changes_and_deletes_and_values_stay_secret()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var id = Guid.NewGuid().ToString("D");
        using var added = await ada.PostAsync($"/api/teams/{team.Id}/environments", Draft(id, "Web app", new VariableChange("API_KEY", "s3cret-value"), new VariableChange("REGION", "eu")));
        Assert.AreEqual(HttpStatusCode.Created, added.StatusCode);
        var text = await added.Content.ReadAsStringAsync();
        // The names come back; no value ever does.
        StringAssert.Contains(text, "API_KEY");
        Assert.IsFalse(text.Contains("s3cret-value"), text);

        // The same create again is done already.
        await ada.PostAsync($"/api/teams/{team.Id}/environments", Draft(id)).IsAsync(HttpStatusCode.Conflict);

        // A null value keeps what is stored; a name left out is removed; a new name needs a value.
        var changed = await ada.PutAsync<EnvironmentDto>($"/api/environments/{id}",
            new WriteEnvironment("Web app 2", ["chrome"], "large", null, null, null, null, [new VariableChange("API_KEY", null), new VariableChange("TOKEN", "t")]));
        Assert.AreEqual("Web app 2", changed.Name);
        Assert.AreEqual("large", changed.Size);
        Assert.IsNull(changed.RepositoryUrl);
        CollectionAssert.AreEqual(new[] { "API_KEY", "TOKEN" }, changed.VariableNames.ToArray());
        await ada.PutAsync($"/api/environments/{id}", new WriteEnvironment("Web app 2", ["chrome"], "large", null, null, null, null, [new VariableChange("NEW", null)]))
            .IsAsync(HttpStatusCode.BadRequest, "needs a value");

        // A task started from it gets the kept value on its desktop, and holds the environment in place.
        var task = await ada.PostAsync<TaskDto>($"/api/teams/{team.Id}/tasks", new StartTask(Guid.NewGuid().ToString("D"), id, null, "Check the build.", Guid.NewGuid().ToString("D")));
        using var desktop = await Desktop.StartAsync(task.Id);
        var setup = await desktop.SetupAsync();
        Assert.AreEqual("s3cret-value", setup.Variables["API_KEY"]);
        Assert.AreEqual("t", setup.Variables["TOKEN"]);
        Assert.IsFalse(setup.Variables.ContainsKey("REGION"));
        await ada.DeleteAsync($"/api/environments/{id}").IsAsync(HttpStatusCode.BadRequest, "Delete them first");

        await ada.DeleteAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NoContent);
        await Stack.Jobs.DeleteAsync(task.Id, CancellationToken.None);
        await ada.DeleteAsync($"/api/environments/{id}").IsAsync(HttpStatusCode.NoContent);
        await ada.GetAsync($"/api/environments/{id}").IsAsync(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task The_rules_are_said_in_words()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var path = $"/api/teams/{team.Id}/environments";
        string Id() => Guid.NewGuid().ToString("D");
        await ada.PostAsync(path, Draft(Id(), " ")).IsAsync(HttpStatusCode.BadRequest);
        await ada.PostAsync(path, Draft(Id()) with { Apps = ["chrome", "photoshop"] }).IsAsync(HttpStatusCode.BadRequest);
        await ada.PostAsync(path, Draft(Id()) with { Size = "enormous" }).IsAsync(HttpStatusCode.BadRequest);
        await ada.PostAsync(path, Draft(Id()) with { RepositoryUrl = "ftp://example.com/app" }).IsAsync(HttpStatusCode.BadRequest);
        await ada.PostAsync(path, Draft(Id(), "Web", new VariableChange("not a name", "x"))).IsAsync(HttpStatusCode.BadRequest);
        await ada.PostAsync(path, Draft(Id(), "Web", new VariableChange("A", "1"), new VariableChange("A", "2"))).IsAsync(HttpStatusCode.BadRequest, "listed twice");
        // Nothing was kept from any of them.
        Assert.AreEqual(1, (await ada.GetAsync<ListPage<EnvironmentDto>>(path)).Data.Count);
    }

    [TestMethod]
    public async Task A_stranger_can_neither_see_nor_change_an_environment()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var id = Guid.NewGuid().ToString("D");
        await ada.PostAsync($"/api/teams/{team.Id}/environments", Draft(id)).IsAsync(HttpStatusCode.Created);
        var stranger = await Person.SignUpAsync("Grace Hopper");

        // A list is an authorized page: someone who may see none of it gets an empty one.
        Assert.AreEqual(0, (await stranger.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{team.Id}/environments")).Data.Count);
        await stranger.GetAsync($"/api/environments/{id}").IsAsync(HttpStatusCode.NotFound);
        await stranger.PutAsync($"/api/environments/{id}", Draft(id).Body).IsAsync(HttpStatusCode.NotFound);
        await stranger.DeleteAsync($"/api/environments/{id}").IsAsync(HttpStatusCode.NotFound);
        await stranger.PostAsync($"/api/teams/{team.Id}/environments", Draft(Guid.NewGuid().ToString("D"))).IsAsync(HttpStatusCode.NotFound);
        Assert.AreEqual("Web app", (await ada.GetAsync<EnvironmentDto>($"/api/environments/{id}")).Name);
    }
}
