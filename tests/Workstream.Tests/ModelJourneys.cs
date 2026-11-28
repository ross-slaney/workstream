using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workstream.Api;
using Workstream.Api.Domain;

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
        // Foundry refuses a request that does not say how long it is, however the desktop sent it.
        Assert.AreEqual(System.Text.Encoding.UTF8.GetByteCount(call.Body), call.ContentLength);
        Stack.Model.Calls.Clear();
        using var inChunks = await desktop.AskModelAsync(chunked: true);
        Assert.AreEqual(HttpStatusCode.OK, inChunks.StatusCode);
        var counted = Stack.Model.Calls.Single();
        Assert.AreEqual(System.Text.Encoding.UTF8.GetByteCount(counted.Body), counted.ContentLength);

        // Opus: $4 a million tokens in, $20 a million out; two calls.
        var usage = await ada.GetAsync<TeamUsage>($"/api/teams/{team.Id}/usage");
        Assert.AreEqual(0.028m, usage.ModelSpentUsd);
        Assert.AreEqual(team.ModelAllowanceUsd, usage.ModelAllowanceUsd);

        // The agent's harness may send its key as a bearer token instead.
        using var again = await desktop.AskModelAsync(key: "Authorization");
        Assert.AreEqual(HttpStatusCode.Unauthorized, again.StatusCode, "A bare key is not a bearer token.");
    }

    [TestMethod]
    public async Task A_streamed_answer_is_passed_through_and_counted_from_its_events()
    {
        var (ada, team, desktop) = await WorkingAsync();
        Stack.Model.Answer = () => FakeModel.Events("claude-haiku-5-5", input: 50_000, output: 20_000);
        using var answer = await desktop.AskModelAsync(stream: true);
        Assert.AreEqual("text/event-stream", answer.Content.Headers.ContentType?.MediaType);
        var events = await answer.Content.ReadAsStringAsync();
        StringAssert.Contains(events, "event: message_stop");
        // Haiku, a prompt under 100,000 tokens: $0.10 a million in, $0.50 a million out.
        Assert.AreEqual(0.015m, (await ada.GetAsync<TeamUsage>($"/api/teams/{team.Id}/usage")).ModelSpentUsd);
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

    static async Task<Desktop> DesktopOfANewTaskAsync(Person person, TeamDto team, string? prompt = "Say done.")
    {
        var environment = (await person.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{team.Id}/environments")).Data.First();
        var task = await person.PostAsync<TaskDto>($"/api/teams/{team.Id}/tasks", new StartTask(Guid.NewGuid().ToString("D"), environment.Id, null, prompt, Guid.NewGuid().ToString("D")));
        return await Desktop.StartAsync(task.Id);
    }

    static Task<ModelReport> ReportAsync(Person person, string teamId) => person.GetAsync<ModelReport>($"/api/teams/{teamId}/usage/models");

    [TestMethod]
    public async Task Each_call_is_counted_to_its_person_and_its_tool_and_an_administrator_sees_everyones()
    {
        var (ada, team, adas) = await WorkingAsync();
        var address = Person.NewEmail();
        var invitation = await ada.PostAsync<PendingInvitation>($"/api/teams/{team.Id}/invitations", new Invite(address));
        using var lin = await Person.SignUpAsync("Lin Clark", address, Answers.TokenOf(invitation));
        using var lins = await DesktopOfANewTaskAsync(lin, team);

        // Ada's desktop: Workstream's own agent calls once, and OpenCode in a terminal calls once on Sonnet.
        Stack.Model.Calls.Clear();
        Stack.Model.Answer = () => FakeModel.Json(FakeModel.Message("claude-opus-5-5", input: 1000, output: 500));
        (await adas.AskModelAsync()).Dispose();
        Stack.Model.Answer = () => FakeModel.Json(FakeModel.Message("claude-sonnet-5-5", input: 1000, output: 500));
        (await adas.AskModelAsync(tool: "OpenCode/1.19.6 (linux)")).Dispose();
        // What the desktop says about its tool stays here: the model is not told.
        Assert.IsFalse(Stack.Model.Calls.Any(call => call.Headers.Keys.Any(name => name.Equals("x-workstream-tool", StringComparison.OrdinalIgnoreCase))));
        // Lin's desktop: one call on Opus.
        Stack.Model.Answer = () => FakeModel.Json(FakeModel.Message("claude-opus-5-5", input: 1000, output: 500));
        (await lins.AskModelAsync()).Dispose();

        // The administrator sees the whole team: Opus is $0.014 a call, Sonnet $0.007.
        var everyones = await ReportAsync(ada, team.Id);
        Assert.IsTrue(everyones.Everyone);
        Assert.AreEqual(0.035m, everyones.CostUsd);
        Assert.AreEqual(3, everyones.Calls);
        Assert.AreEqual(2, everyones.People.Count);
        var adasUse = everyones.People.Single(x => x.UserId == ada.UserId);
        Assert.AreEqual("Ada Lovelace", adasUse.Name);
        Assert.AreEqual(0.021m, adasUse.CostUsd);
        Assert.AreEqual(2, adasUse.Calls);
        Assert.AreEqual(1, adasUse.Tasks);
        Assert.AreEqual(ada.UserId, everyones.People[0].UserId, "Whoever used most comes first.");
        var linsUse = everyones.People.Single(x => x.UserId == lin.UserId);
        Assert.AreEqual("Lin Clark", linsUse.Name);
        Assert.AreEqual(0.014m, linsUse.CostUsd);
        CollectionAssert.AreEquivalent(new[] { "workstream", "opencode" }, everyones.Tools.Select(x => x.Tool).ToList());
        Assert.AreEqual(0.028m, everyones.Tools.Single(x => x.Tool == "workstream").CostUsd);
        Assert.AreEqual(1, everyones.Tools.Single(x => x.Tool == "opencode").Calls);
        Assert.AreEqual(2, everyones.Models.Single(x => x.Model == "claude-opus-5-5").Calls);
        Assert.AreEqual(1000, everyones.Models.Single(x => x.Model == "claude-sonnet-5-5").InputTokens);
        // A day for each day of the month so far, with today's use on the last.
        Assert.AreEqual(DateTimeOffset.UtcNow.Day, everyones.Days.Count);
        Assert.AreEqual(0.035m, everyones.Days[^1].CostUsd);
        Assert.AreEqual(2, everyones.Tasks.Count);
        Assert.AreEqual("Ada Lovelace", everyones.Tasks[0].UserName);
        Assert.AreEqual("Say done.", everyones.Tasks[0].Title);

        // A member sees their own use and nobody else's.
        var own = await ReportAsync(lin, team.Id);
        Assert.IsFalse(own.Everyone);
        Assert.AreEqual(0.014m, own.CostUsd);
        Assert.AreEqual(lin.UserId, own.People.Single().UserId);
        Assert.AreEqual("workstream", own.Tools.Single().Tool);
        Assert.AreEqual(1, own.Tasks.Count);
        // The team's total is still theirs to see, as before.
        Assert.AreEqual(0.035m, (await lin.GetAsync<TeamUsage>($"/api/teams/{team.Id}/usage")).ModelSpentUsd);

        // Nobody outside the team: a stranger, the system administrator, a desktop with its key, and nobody.
        using var stranger = await Person.SignUpAsync("Grace Hopper");
        await stranger.GetAsync($"/api/teams/{team.Id}/usage/models").IsAsync(HttpStatusCode.NotFound);
        using var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
        await administrator.GetAsync($"/api/teams/{team.Id}/usage/models").IsAsync(HttpStatusCode.NotFound);
        using var keyed = new HttpClient { BaseAddress = Stack.Origin };
        keyed.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adas.Key);
        await keyed.GetAsync($"/api/teams/{team.Id}/usage/models").IsAsync(HttpStatusCode.Unauthorized);
        using var anonymous = new HttpClient { BaseAddress = Stack.Origin };
        await anonymous.GetAsync($"/api/teams/{team.Id}/usage/models").IsAsync(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task A_deleted_tasks_calls_stay_with_the_person_who_made_them()
    {
        var (ada, team, desktop) = await WorkingAsync();
        Stack.Model.Answer = () => FakeModel.Json(FakeModel.Message("claude-opus-5-5", input: 1000, output: 500));
        (await desktop.AskModelAsync(tool: "opencode")).Dispose();
        var taskId = (await ReportAsync(ada, team.Id)).Tasks.Single().TaskId;
        // The task's row is gone, as it is once a deleted task's desktop is removed: the call names a task
        // nothing knows any more, and still says whose it was.
        var gone = Guid.NewGuid().ToString("D");
        using (var scope = Stack.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().ModelUsage.Where(x => x.TaskId == taskId).ExecuteUpdateAsync(x => x.SetProperty(use => use.TaskId, gone));
        }
        var report = await ReportAsync(ada, team.Id);
        Assert.AreEqual(0.014m, report.People.Single(x => x.UserId == ada.UserId).CostUsd);
        Assert.IsNull(report.Tasks.Single().Title);
        Assert.AreEqual("Ada Lovelace", report.Tasks.Single().UserName);
    }

    [TestMethod]
    public async Task A_model_call_counts_as_use_of_the_desktop()
    {
        var (_, _, desktop) = await WorkingAsync();
        string taskId;
        using (var scope = Stack.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            taskId = (await db.Tasks.AsNoTracking().OrderByDescending(x => x.CreatedAt).FirstAsync()).Id;
            var longAgo = DateTimeOffset.UtcNow.AddMinutes(-20);
            await db.Tasks.Where(x => x.Id == taskId).ExecuteUpdateAsync(x => x.SetProperty(task => task.ActiveAt, longAgo));
        }
        // A tool the person left working in a terminal: nobody touches the desktop, and its agent is idle.
        Stack.Model.Answer = () => FakeModel.Json(FakeModel.Message("claude-opus-5-5", input: 1000, output: 500));
        (await desktop.AskModelAsync(tool: "opencode")).Dispose();
        using var after = Stack.Services.CreateScope();
        var row = await after.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Tasks.AsNoTracking().SingleAsync(x => x.Id == taskId);
        Assert.IsTrue(DateTimeOffset.UtcNow - row.ActiveAt < TimeSpan.FromMinutes(1), "The desktop was used just now.");
    }

    [TestMethod]
    public void A_tools_name_is_the_first_word_of_what_it_called_itself()
    {
        Assert.AreEqual("opencode", ModelUsage.ToolNamed("opencode/1.19.6 ai-sdk/anthropic/2.0"));
        Assert.AreEqual("claude-cli", ModelUsage.ToolNamed("Claude-CLI/2.1.0 (external, cli)"));
        Assert.AreEqual("workstream", ModelUsage.ToolNamed(null));
        Assert.AreEqual("workstream", ModelUsage.ToolNamed("  "));
        Assert.AreEqual("workstream", ModelUsage.ToolNamed("<<>>"), "Nothing is left of it.");
        Assert.AreEqual("my_tool.v2", ModelUsage.ToolNamed("My_Tool.v2;x"));
        Assert.AreEqual(ModelUsage.MaxToolLength, ModelUsage.ToolNamed(new string('a', 200)).Length);
    }

    [TestMethod]
    public async Task A_desktop_opened_with_nothing_asked_is_told_so_until_someone_writes()
    {
        var (ada, team) = await Person.WithTeamAsync();
        using var opened = await DesktopOfANewTaskAsync(ada, team, prompt: null);
        Assert.IsTrue((await opened.SetupAsync()).Opened);
        using var asked = await DesktopOfANewTaskAsync(ada, team);
        Assert.IsFalse((await asked.SetupAsync()).Opened);
    }
}
