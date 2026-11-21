using System.Net;
using Workstream.Api;

namespace Workstream.Tests;

/// <summary>
/// The model gateway: an agent's model calls go through the API with its desktop's key, reach Foundry with
/// Workstream's own, and are counted against the team's monthly allowance.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ModelJourneys
{
    static async Task<(Person Person, TeamDto Team, Desktop Desktop)> WorkingAsync()
    {
        var (person, team) = await Person.WithTeamAsync();
        var environment = (await person.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{team.Id}/environments")).Data.Single();
        var task = await person.PostAsync<TaskDto>($"/api/teams/{team.Id}/tasks", new StartTask(Guid.NewGuid().ToString("D"), environment.Id, null, "Say done.", Guid.NewGuid().ToString("D")));
        return (person, team, await Desktop.StartAsync(task.Id));
    }

    [TestMethod]
    public async Task A_call_reaches_the_model_with_the_platforms_key_and_is_charged_to_the_team()
    {
        var (ada, team, desktop) = await WorkingAsync();
        Stack.Model.Calls.Clear();
        Stack.Model.Answer = () => FakeModel.Json(FakeModel.Message("claude-opus-5-5", input: 1000, output: 500));
        using var answer = await desktop.AskModelAsync();
        Assert.AreEqual(HttpStatusCode.OK, answer.StatusCode);
        StringAssert.Contains(await answer.Content.ReadAsStringAsync(), "Done.");

        var call = Stack.Model.Calls.Single();
        Assert.AreEqual("http://foundry.test/anthropic/v1/messages", call.Uri.ToString());
        // Foundry sees Workstream's key; the desktop's own never leaves the API.
        Assert.AreEqual(Stack.ModelKey, call.Headers["x-api-key"]);
        Assert.IsFalse(call.Headers.Values.Any(value => value.Contains(desktop.Key)));
        Assert.AreEqual("2023-06-01", call.Headers["anthropic-version"]);
        StringAssert.Contains(call.Body, "Say done.");

        // Opus: $4 a million tokens in, $20 a million out.
        var usage = await ada.GetAsync<TeamUsage>($"/api/teams/{team.Id}/usage");
        Assert.AreEqual(0.014m, usage.ModelSpentUsd);
        Assert.AreEqual(team.ModelAllowanceUsd, usage.ModelAllowanceUsd);

        // The agent's harness may send its key as a bearer token instead.
        using var again = await desktop.AskModelAsync(key: "Authorization");
        Assert.AreEqual(HttpStatusCode.Unauthorized, again.StatusCode, "A bare key is not a bearer token.");
    }

    [TestMethod]
    public async Task A_streamed_answer_is_passed_through_and_counted_from_its_events()
    {
        var (ada, team, desktop) = await WorkingAsync();
        Stack.Model.Answer = () => FakeModel.Events("claude-haiku-5-5", input: 2_000_000, output: 1_000_000);
        using var answer = await desktop.AskModelAsync(stream: true);
        Assert.AreEqual("text/event-stream", answer.Content.Headers.ContentType?.MediaType);
        var events = await answer.Content.ReadAsStringAsync();
        StringAssert.Contains(events, "event: message_stop");
        // Haiku: $0.10 a million in, $0.50 a million out.
        Assert.AreEqual(0.70m, (await ada.GetAsync<TeamUsage>($"/api/teams/{team.Id}/usage")).ModelSpentUsd);
    }

    [TestMethod]
    public async Task When_the_allowance_is_used_up_the_agent_is_told_and_the_model_is_not_called()
    {
        var (ada, team, desktop) = await WorkingAsync();
        var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
        await administrator.PutAsync<TeamDto>($"/api/admin/teams/{team.Id}/limits", new SetLimits(0.01m, 2));

        Stack.Model.Answer = () => FakeModel.Json(FakeModel.Message("claude-opus-5-5", input: 1000, output: 500));
        using var first = await desktop.AskModelAsync();
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, $"The call that crosses the allowance still answers. {await first.Content.ReadAsStringAsync()}");

        Stack.Model.Calls.Clear();
        using var refused = await desktop.AskModelAsync();
        // A plain refusal, in the shape the harness shows to the person as it is.
        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        StringAssert.Contains(await refused.Content.ReadAsStringAsync(), "used its model allowance");
        Assert.AreEqual(0, Stack.Model.Calls.Count);

        // Raising the allowance lets the agent go on.
        await administrator.PutAsync<TeamDto>($"/api/admin/teams/{team.Id}/limits", new SetLimits(50m, 2));
        using var resumed = await desktop.AskModelAsync();
        Assert.AreEqual(HttpStatusCode.OK, resumed.StatusCode);
        Assert.AreEqual(50m, (await ada.GetAsync<TeamUsage>($"/api/teams/{team.Id}/usage")).ModelAllowanceUsd);
    }

    [TestMethod]
    public async Task Only_a_desktops_key_calls_the_model()
    {
        var (ada, _, _) = await WorkingAsync();
        Stack.Model.Calls.Clear();
        var body = new { model = "claude-opus-5-5", max_tokens = 8, messages = new[] { new { role = "user", content = "Hi" } } };
        await ada.PostAsync("/api/model/anthropic/v1/messages", body).IsAsync(HttpStatusCode.Unauthorized);
        using var anonymous = new HttpClient { BaseAddress = Stack.Origin };
        await anonymous.PostAsync("/api/model/anthropic/v1/messages", System.Net.Http.Json.JsonContent.Create(body)).IsAsync(HttpStatusCode.Unauthorized);
        anonymous.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", "wsd_" + new string('0', 96));
        await anonymous.PostAsync("/api/model/anthropic/v1/messages", System.Net.Http.Json.JsonContent.Create(body)).IsAsync(HttpStatusCode.Unauthorized);
        Assert.AreEqual(0, Stack.Model.Calls.Count);
    }
}
