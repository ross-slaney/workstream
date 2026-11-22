using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workstream.Api;
using Workstream.Api.Desktops;
using Workstream.Api.Domain;

namespace Workstream.Tests;

/// <summary>
/// The setup script on a task's own desktop: how long it may run, what the task's screen is shown of it while
/// it runs and after it failed or was stopped, that a task whose setup is still running is neither failed
/// nor stopped by the sweep, and who may say and see any of it.
/// </summary>
[TestClass]
public sealed class TaskSetupJourneys
{
    static AddEnvironment Draft(string script = "npm ci && npm run build") =>
        new(Guid.NewGuid().ToString("D"), "Monorepo", ["chrome", "vscode", "terminal"], "large", "https://github.com/acme/monorepo", "main", script, null,
            [new VariableChange("NPM_TOKEN", "s3cret-value")]);

    /// <summary>A team with an environment that has a setup, and a task started from it whose desktop does the setup itself.</summary>
    static async Task<(Person Ada, TeamDto Team, TaskDto Task, Desktop Desktop)> StartedAsync()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var draft = Draft();
        await ada.PostAsync($"/api/teams/{team.Id}/environments", draft).IsAsync(HttpStatusCode.Created);
        var task = await ada.PostAsync<TaskDto>($"/api/teams/{team.Id}/tasks", new StartTask(Guid.NewGuid().ToString("D"), draft.Id, null, "Run the stack.", Guid.NewGuid().ToString("D")));
        var desktop = await Desktop.StartAsync(task.Id);
        // No save yet: the machine is made from the image, and its agent runs the setup script.
        Assert.IsNull(Stack.Desktops.For(task.Id).Save);
        return (ada, team, task, desktop);
    }

    static Task<ConversationDto> ConversationAsync(Person person, string taskId) => person.GetAsync<ConversationDto>($"/api/tasks/{taskId}/messages?after=0");

    [TestMethod]
    public async Task A_desktop_is_told_how_long_its_setup_may_run_and_the_screen_shows_it_running()
    {
        var (ada, _, task, desktop) = await StartedAsync();
        using (ada)
        using (desktop)
        {
            var setup = await desktop.SetupAsync();
            Assert.AreEqual("npm ci && npm run build", setup.SetupScript);
            Assert.AreEqual("s3cret-value", setup.Variables["NPM_TOKEN"]);
            // Forty-five minutes unless a deployment says otherwise (Workstream:Desktops:SetupMinutes).
            Assert.AreEqual(45, setup.SetupMinutes);
            Assert.AreEqual(45, Stack.Services.GetRequiredService<DesktopOptions>().SetupMinutes);

            // Before the script starts there is nothing to show.
            await desktop.ExchangeAsync(working: true);
            Assert.IsNull((await ConversationAsync(ada, task.Id)).Setup);

            // It starts, and says what it writes every few seconds.
            await desktop.ReportAsync("running", output: "").IsAsync(HttpStatusCode.NoContent);
            var started = (await ConversationAsync(ada, task.Id)).Setup!;
            Assert.AreEqual("running", started.State);
            Assert.IsNull(started.Output);
            Assert.AreEqual(45, started.LimitMinutes);
            Assert.IsNull(started.EndedAt);
            await desktop.ReportAsync("running", output: "npm ci\nadded 2041 packages in 3m").IsAsync(HttpStatusCode.NoContent);
            var running = (await ConversationAsync(ada, task.Id)).Setup!;
            Assert.AreEqual("running", running.State);
            StringAssert.Contains(running.Output, "added 2041 packages");
            Assert.AreEqual(started.StartedAt, running.StartedAt);
            // Only the end of a long output is kept.
            await desktop.ReportAsync("running", output: new string('x', 9000) + "\nthe last line").IsAsync(HttpStatusCode.NoContent);
            var long_ = (await ConversationAsync(ada, task.Id)).Setup!;
            Assert.AreEqual(TaskSetup.MaxOutputLength, long_.Output!.Length);
            StringAssert.EndsWith(long_.Output, "the last line");

            // It ended well.
            await desktop.ReportAsync("done", exit: 0, output: "built in 11m").IsAsync(HttpStatusCode.NoContent);
            var done = (await ConversationAsync(ada, task.Id)).Setup!;
            Assert.AreEqual("done", done.State);
            Assert.AreEqual(0, done.Exit);
            Assert.IsNotNull(done.EndedAt);
            // The task is as it was: ready, with its desktop.
            Assert.AreEqual("ready", (await ConversationAsync(ada, task.Id)).Task.State);
        }
    }

    [TestMethod]
    public async Task A_setup_that_fails_or_runs_too_long_is_said_plainly_with_its_last_lines_and_the_desktop_stays()
    {
        var (ada, _, task, desktop) = await StartedAsync();
        using (ada)
        using (desktop)
        {
            await desktop.ExchangeAsync(working: true);
            await desktop.ReportAsync("running", output: "npm ci").IsAsync(HttpStatusCode.NoContent);
            await desktop.ReportAsync("failed", exit: 2, output: "npm ci\nnpm ERR! code ERESOLVE").IsAsync(HttpStatusCode.NoContent);
            var failed = await ConversationAsync(ada, task.Id);
            Assert.AreEqual("failed", failed.Setup!.State);
            Assert.AreEqual(2, failed.Setup.Exit);
            StringAssert.Contains(failed.Setup.Output, "ERESOLVE");
            Assert.IsNotNull(failed.Setup.EndedAt);
            // A setup that failed leaves a desktop to work on: the task did not fail with it.
            Assert.AreEqual("ready", failed.Task.State);
            Assert.IsNull(failed.Task.Failure);

            // The desktop starts over and runs the script again: a new run, with nothing of the old one's output.
            await desktop.ReportAsync("running", output: "").IsAsync(HttpStatusCode.NoContent);
            var again = (await ConversationAsync(ada, task.Id)).Setup!;
            Assert.AreEqual("running", again.State);
            Assert.IsNull(again.Exit);
            Assert.IsNull(again.Output);
            Assert.IsNull(again.EndedAt);
            Assert.IsTrue(again.StartedAt >= failed.Setup.EndedAt);

            // Stopped by the desktop for running as long as a setup may: 124, as `timeout` says it.
            await desktop.ReportAsync("failed", exit: 124, output: "Waiting for the API to answer...").IsAsync(HttpStatusCode.NoContent);
            var timedOut = await ConversationAsync(ada, task.Id);
            Assert.AreEqual("timed_out", timedOut.Setup!.State);
            Assert.AreEqual(45, timedOut.Setup.LimitMinutes);
            StringAssert.Contains(timedOut.Setup.Output, "Waiting for the API");
            Assert.AreEqual("ready", timedOut.Task.State);

            // A failure that names no exit code failed all the same; a state nobody knows is refused in words.
            await desktop.ReportAsync("failed").IsAsync(HttpStatusCode.NoContent);
            Assert.AreEqual(1, (await ConversationAsync(ada, task.Id)).Setup!.Exit);
            await desktop.ReportAsync("finished").IsAsync(HttpStatusCode.BadRequest, "running, done or failed");
        }
    }

    [TestMethod]
    public async Task A_task_whose_setup_is_still_running_is_not_failed_or_stopped_by_the_sweep()
    {
        var (ada, _, task, desktop) = await StartedAsync();
        using (ada)
        using (desktop)
        {
            // The agent exchanges before it starts the script, which is what makes the desktop ready; while
            // the script runs it says it is at work.
            await desktop.ExchangeAsync(working: true);
            await desktop.ReportAsync("running", output: "Restoring packages...").IsAsync(HttpStatusCode.NoContent);

            // A sweep with no patience at all: late at once, idle at once.
            var impatient = new DesktopJobs(
                Stack.Services.GetRequiredService<IServiceScopeFactory>(), Stack.Desktops, new DesktopOptions { Host = "none", IdleMinutes = 0, StartTimeoutMinutes = 0 },
                TimeProvider.System, Stack.Services.GetRequiredService<ILogger<DesktopJobs>>());
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            await impatient.SweepAsync(CancellationToken.None);

            var swept = await ConversationAsync(ada, task.Id);
            Assert.AreEqual("ready", swept.Task.State);
            Assert.IsNull(swept.Task.Failure);
            Assert.AreEqual("running", swept.Setup!.State);
            Assert.IsTrue(Stack.Desktops.Running[Stack.Desktops.For(task.Id).Name]);
            // And the script goes on being heard.
            await desktop.ExchangeAsync(working: true);
            await desktop.ReportAsync("running", output: "Building...").IsAsync(HttpStatusCode.NoContent);
        }
    }

    [TestMethod]
    public async Task Only_a_tasks_own_desktop_says_how_its_setup_goes_and_only_who_sees_the_task_sees_it()
    {
        var (ada, team, task, desktop) = await StartedAsync();
        using (ada)
        using (desktop)
        {
            await desktop.ExchangeAsync(working: true);
            await desktop.ReportAsync("running", output: "npm ci").IsAsync(HttpStatusCode.NoContent);

            var invitation = await ada.PostAsync<PendingInvitation>($"/api/teams/{team.Id}/invitations", new Invite(Person.NewEmail()));
            using var ben = await Person.SignUpAsync("Ben Shneiderman", invitation.Email, Answers.TokenOf(invitation));
            using var stranger = await Person.SignUpAsync("A Stranger");
            using var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
            using var anonymous = new HttpClient { BaseAddress = Stack.Origin };

            // The team sees it with the task; a stranger and the system administrator see no task; nobody is nobody.
            Assert.AreEqual("running", (await ConversationAsync(ada, task.Id)).Setup!.State);
            Assert.AreEqual("running", (await ConversationAsync(ben, task.Id)).Setup!.State);
            await stranger.GetAsync($"/api/tasks/{task.Id}/messages?after=0").IsAsync(HttpStatusCode.NotFound);
            await administrator.GetAsync($"/api/tasks/{task.Id}/messages?after=0").IsAsync(HttpStatusCode.NotFound);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/tasks/{task.Id}/messages?after=0")).StatusCode);

            // A person's token is not a desktop's key, whoever they are.
            var report = new SetupReport("failed", 1, "not the desktop", null, null);
            await ada.PostAsync("/api/desktop/setup", report).IsAsync(HttpStatusCode.Unauthorized);
            await administrator.PostAsync("/api/desktop/setup", report).IsAsync(HttpStatusCode.Unauthorized);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/desktop/setup", null)).StatusCode);

            // Another task's desktop speaks only of its own task.
            var other = await ada.PostAsync<TaskDto>($"/api/teams/{team.Id}/tasks", new StartTask(Guid.NewGuid().ToString("D"), task.EnvironmentId, null, "Something else.", Guid.NewGuid().ToString("D")));
            using (var second = await Desktop.StartAsync(other.Id))
            {
                await second.ReportAsync("failed", exit: 9, output: "the other desktop").IsAsync(HttpStatusCode.NoContent);
                Assert.AreEqual("failed", (await ConversationAsync(ada, other.Id)).Setup!.State);
                var mine = (await ConversationAsync(ada, task.Id)).Setup!;
                Assert.AreEqual("running", mine.State);
                StringAssert.Contains(mine.Output, "npm ci");
            }

            // Deleting the task removes what was kept of its setup, and ends its desktop's word.
            await ada.DeleteAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NoContent);
            await Stack.Jobs.DeleteAsync(task.Id, CancellationToken.None);
            using var scope = Stack.Services.CreateScope();
            Assert.IsFalse(await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().TaskSetups.AnyAsync(x => x.TaskId == task.Id));
            await desktop.ReportAsync("running", output: "late").IsAsync(HttpStatusCode.Unauthorized);
        }
    }
}

/// <summary>The setup script's run on a task's desktop, without a database.</summary>
[TestClass]
public sealed class TaskSetupTests
{
    [TestMethod]
    public void A_preparing_machine_is_given_the_scripts_limit_and_half_an_hour_more_at_least()
    {
        Assert.AreEqual(75, new DesktopOptions().PrepareMinutes);
        Assert.AreEqual(120, new DesktopOptions { SetupMinutes = 90 }.PrepareMinutes);
        Assert.AreEqual(180, new DesktopOptions { Saves = { MaxMinutes = 180 } }.PrepareMinutes);
    }
}
