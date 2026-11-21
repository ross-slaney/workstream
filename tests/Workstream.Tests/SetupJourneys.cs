using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workstream.Api;
using Workstream.Api.Desktops;
using Workstream.Api.Domain;

namespace Workstream.Tests;

/// <summary>
/// Saved setups: an environment's setup done once on a machine of its own, its disk saved, and tasks that
/// start from it; what makes a save out of date, what happens when the setup fails, who may ask for it and
/// who may speak for it, and the limits and the sweep that keep what it costs. These change settings everyone
/// shares, so they run alone.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SetupJourneys
{
    const string Commit = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";
    const long TenGigabytes = 10L * 1024 * 1024 * 1024;

    static DesktopOptions Options => Stack.Services.GetRequiredService<DesktopOptions>();

    static AddEnvironment Draft(string script = "npm ci") =>
        new(Guid.NewGuid().ToString("D"), "Web app", ["chrome", "vscode", "terminal"], "small", "https://github.com/acme/app", "main", script, null,
            [new VariableChange("API_KEY", "s3cret-value")]);

    static WriteEnvironment Body(AddEnvironment draft) => draft.Body;

    static async Task<EnvironmentSetup?> SetupOfAsync(string environmentId)
    {
        using var scope = Stack.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Setups.AsNoTracking().SingleOrDefaultAsync(x => x.EnvironmentId == environmentId);
    }

    static async Task<WorkEnvironment> LoadEnvironmentAsync(string id)
    {
        using var scope = Stack.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Environments.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    static Task<EnvironmentDto> EnvironmentAsync(Person person, string id) => person.GetAsync<EnvironmentDto>($"/api/environments/{id}");

    static Task<TaskDto> StartAsync(Person person, string teamId, string environmentId) =>
        person.PostAsync<TaskDto>($"/api/teams/{teamId}/tasks", new StartTask(Guid.NewGuid().ToString("D"), environmentId, null, "Check the build.", Guid.NewGuid().ToString("D")));

    static async Task DeleteTaskAsync(Person person, string taskId)
    {
        await person.DeleteAsync($"/api/tasks/{taskId}").IsAsync(HttpStatusCode.NoContent);
        await Stack.Jobs.DeleteAsync(taskId, CancellationToken.None);
    }

    /// <summary>What the operator does once an environment wants preparing, and the preparing machine doing the setup and saying it ended well.</summary>
    static async Task<string> PrepareAsync(string environmentId, string output = "added 312 packages")
    {
        await Stack.Jobs.PrepareAsync(environmentId, CancellationToken.None);
        using var preparing = Desktop.Preparing(environmentId);
        Assert.IsTrue((await preparing.SetupAsync()).Prepare);
        await preparing.ReportAsync("running", output: "npm ci").IsAsync(HttpStatusCode.NoContent);
        await preparing.ReportAsync("done", exit: 0, output: output, commit: Commit, bytes: TenGigabytes).IsAsync(HttpStatusCode.NoContent);
        var setup = (await SetupOfAsync(environmentId))!;
        Assert.IsTrue(setup.RunSaving);
        await Stack.Jobs.SavePreparedAsync(environmentId, setup.RunId!, CancellationToken.None);
        return (await SetupOfAsync(environmentId))!.Save!;
    }

    /// <summary>A sweep whose settings for saves are these.</summary>
    static DesktopJobs SweepWith(Action<DesktopOptions.SaveOptions> saves)
    {
        var options = new DesktopOptions { Host = "none", ApiUrl = Options.ApiUrl, Saves = { AtOnce = 1000, HoursPerDay = 100000 } };
        saves(options.Saves);
        return new DesktopJobs(Stack.Services.GetRequiredService<IServiceScopeFactory>(), Stack.Desktops, options, TimeProvider.System, Stack.Services.GetRequiredService<ILogger<DesktopJobs>>());
    }

    [TestMethod]
    public async Task An_environment_is_prepared_once_and_its_tasks_start_from_the_save()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var draft = Draft();
        var added = await ada.PostAsync<EnvironmentDto>($"/api/teams/{team.Id}/environments", draft);
        // Adding an environment with a setup asks for it to be prepared; a machine is waited for.
        Assert.AreEqual("preparing", added.Setup.State);
        Assert.IsNull(added.Setup.StartedAt);

        await Stack.Jobs.PrepareAsync(added.Id, CancellationToken.None);
        var made = Stack.Desktops.Preparing(added.Id)!;
        Assert.AreEqual(MachinePurpose.Prepare, made.Purpose);
        Assert.AreEqual(team.Id, made.TeamId);
        Assert.IsNull(made.Save);
        using var preparing = Desktop.Preparing(added.Id);
        // It is told the environment's setup with its variables, and nothing of a task: no conversation, no model.
        var setup = await preparing.SetupAsync();
        Assert.IsTrue(setup.Prepare);
        Assert.AreEqual("", setup.TaskId);
        Assert.AreEqual("npm ci", setup.SetupScript);
        Assert.AreEqual("https://github.com/acme/app", setup.Repository!.Url);
        Assert.AreEqual("s3cret-value", setup.Variables["API_KEY"]);
        Assert.IsNull(setup.Model);
        await preparing.PostAsync("/api/desktop/exchange", new DesktopExchange(false, 0, null)).IsAsync(HttpStatusCode.NotFound);
        await preparing.AskModelAsync().IsAsync(HttpStatusCode.Unauthorized);

        // While it runs, the page shows what the setup writes.
        await preparing.ReportAsync("running", output: "npm ci\nadded 12 packages").IsAsync(HttpStatusCode.NoContent);
        var running = (await EnvironmentAsync(ada, added.Id)).Setup;
        Assert.AreEqual("preparing", running.State);
        Assert.IsNotNull(running.StartedAt);
        StringAssert.Contains(running.Output, "added 12 packages");

        // It ended well: the machine is stopped, its disk saved, the machine removed and its key ended.
        await preparing.ReportAsync("done", exit: 0, output: "added 312 packages", commit: Commit, bytes: TenGigabytes).IsAsync(HttpStatusCode.NoContent);
        await preparing.ReportAsync("done", exit: 0).IsAsync(HttpStatusCode.NotFound);
        var runId = (await SetupOfAsync(added.Id))!.RunId!;
        await Stack.Jobs.SavePreparedAsync(added.Id, runId, CancellationToken.None);
        var save = (await SetupOfAsync(added.Id))!.Save!;
        Assert.AreEqual(made.Name, Stack.Desktops.Saves[save].Machine);
        CollectionAssert.Contains(Stack.Desktops.Deleted.ToList(), made.Name);
        await preparing.GetAsync("/api/desktop/setup").IsAsync(HttpStatusCode.Unauthorized);

        var prepared = (await EnvironmentAsync(ada, added.Id)).Setup;
        Assert.AreEqual("prepared", prepared.State);
        Assert.AreEqual(Commit, prepared.Commit);
        Assert.AreEqual(TenGigabytes, prepared.Bytes);
        Assert.AreEqual(0.50m, prepared.MonthlyUsd);
        Assert.IsNotNull(prepared.PreparedAt);

        // A task's machine is made from the save, and its run says so.
        var task = await StartAsync(ada, team.Id, added.Id);
        using var desktop = await Desktop.StartAsync(task.Id);
        Assert.AreEqual(save, Stack.Desktops.For(task.Id).Save);
        var taskSetup = await desktop.SetupAsync();
        Assert.IsFalse(taskSetup.Prepare);
        Assert.AreEqual(task.Id, taskSetup.TaskId);
        using (var scope = Stack.Services.CreateScope())
        {
            var run = await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().DesktopRuns.AsNoTracking().SingleAsync(x => x.TaskId == task.Id);
            Assert.AreEqual(StartSource.Save, run.Source);
            var preparingRun = await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().DesktopRuns.AsNoTracking().SingleAsync(x => x.TaskId == runId);
            Assert.AreEqual(MachinePurpose.Prepare, preparingRun.Purpose);
            Assert.IsNotNull(preparingRun.EndedAt);
        }
        // A fresh save is not made again.
        Assert.IsNull((await SetupOfAsync(added.Id))!.WantedAt);

        // The system administrator sees the preparing time as a line of its own, the save kept, and the start from it.
        using (var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword))
        {
            var overview = await administrator.GetAsync<AdminOverview>("/api/admin/overview");
            Assert.IsTrue(overview.Machines.ThisMonth.Any(x => x.Purpose == MachinePurpose.Prepare));
            Assert.IsTrue(overview.Machines.Saves >= 1);
            Assert.IsTrue(overview.Machines.SavesMonthlyUsd >= 0.50m);
            Assert.IsTrue(overview.Starts.Any(x => x.Source == StartSource.Save));
        }
        // The team's usage is its tasks' desktops: preparing is Workstream's cost.
        Assert.AreEqual(1, (await ada.GetAsync<TeamUsage>($"/api/teams/{team.Id}/usage")).RunningDesktops);

        // Deleting the environment deletes its save.
        await DeleteTaskAsync(ada, task.Id);
        await ada.DeleteAsync($"/api/environments/{added.Id}").IsAsync(HttpStatusCode.NoContent);
        Assert.IsNull(await SetupOfAsync(added.Id));
        await Stack.Jobs.DeleteSaveAsync(save, CancellationToken.None);
        Assert.IsFalse(Stack.Desktops.Saves.ContainsKey(save));
    }

    [TestMethod]
    public async Task A_save_is_used_only_for_the_settings_and_desktops_it_was_made_for()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var draft = Draft();
        await ada.PostAsync($"/api/teams/{team.Id}/environments", draft).IsAsync(HttpStatusCode.Created);
        var first = await PrepareAsync(draft.Id);

        // A new name changes nothing a setup depends on: the save stays good.
        await ada.PutAsync($"/api/environments/{draft.Id}", Body(draft) with { Name = "Web app, renamed" }).IsAsync(HttpStatusCode.OK);
        Assert.AreEqual("prepared", (await EnvironmentAsync(ada, draft.Id)).Setup.State);
        Assert.IsNull((await SetupOfAsync(draft.Id))!.WantedAt);

        // Newer desktops: the save is out of date, and a task starts from the image and asks for it again.
        var image = Options.Docker.Image;
        try
        {
            Options.Docker.Image = "workstream-desktop:newer";
            var stale = (await EnvironmentAsync(ada, draft.Id)).Setup;
            Assert.AreEqual("out_of_date", stale.State);
            Assert.AreEqual("desktop", stale.Because);
            var task = await StartAsync(ada, team.Id, draft.Id);
            using var _ = await Desktop.StartAsync(task.Id);
            Assert.IsNull(Stack.Desktops.For(task.Id).Save);
            Assert.IsNotNull((await SetupOfAsync(draft.Id))!.WantedAt);
            Assert.AreEqual("preparing", (await EnvironmentAsync(ada, draft.Id)).Setup.State);
            await DeleteTaskAsync(ada, task.Id);
        }
        finally
        {
            Options.Docker.Image = image;
        }

        // A changed variable is a changed setup: it is prepared again, and the new save replaces the old.
        await ada.PutAsync($"/api/environments/{draft.Id}", Body(draft) with { Variables = [new VariableChange("API_KEY", "another-value")] }).IsAsync(HttpStatusCode.OK);
        var changed = await SetupOfAsync(draft.Id);
        Assert.IsNotNull(changed!.WantedAt);
        Assert.AreEqual(first, changed.Save);
        // The save there is says why it is not used: the setup changed.
        Assert.AreEqual("setup", changed.OutOfDateBecause(await LoadEnvironmentAsync(draft.Id), Options.ImageVersion));
        var second = await PrepareAsync(draft.Id);
        Assert.AreNotEqual(first, second);
        Assert.IsFalse(Stack.Desktops.Saves.ContainsKey(first));
        Assert.IsTrue(Stack.Desktops.Saves.ContainsKey(second));
        Assert.AreEqual("prepared", (await EnvironmentAsync(ada, draft.Id)).Setup.State);

        // Nothing left to prepare: the save goes.
        await ada.PutAsync($"/api/environments/{draft.Id}", Body(draft) with { RepositoryUrl = null, SetupScript = null }).IsAsync(HttpStatusCode.OK);
        Assert.AreEqual("nothing", (await EnvironmentAsync(ada, draft.Id)).Setup.State);
        Assert.IsNull((await SetupOfAsync(draft.Id))!.Save);
        await Stack.Jobs.DeleteSaveAsync(second, CancellationToken.None);
        Assert.IsFalse(Stack.Desktops.Saves.ContainsKey(second));
        await ada.DeleteAsync($"/api/environments/{draft.Id}").IsAsync(HttpStatusCode.NoContent);
    }

    [TestMethod]
    public async Task A_setup_that_fails_says_why_and_tasks_still_work()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var draft = Draft("npm run missing");
        await ada.PostAsync($"/api/teams/{team.Id}/environments", draft).IsAsync(HttpStatusCode.Created);
        await Stack.Jobs.PrepareAsync(draft.Id, CancellationToken.None);
        using var preparing = Desktop.Preparing(draft.Id);
        var machine = preparing.Host;
        await preparing.ReportAsync("failed", exit: 1, output: "npm ERR! Missing script: \"missing\"").IsAsync(HttpStatusCode.NoContent);

        var failed = (await EnvironmentAsync(ada, draft.Id)).Setup;
        Assert.AreEqual("failed", failed.State);
        StringAssert.Contains(failed.Failure, "exit 1");
        StringAssert.Contains(failed.Output, "Missing script");
        // Nothing is saved; the machine goes (the job the report queued) and its key has ended already.
        Assert.IsNull((await SetupOfAsync(draft.Id))!.Save);
        await preparing.GetAsync("/api/desktop/setup").IsAsync(HttpStatusCode.Unauthorized);
        await Stack.Jobs.RemovePreparingMachineAsync(machine, CancellationToken.None);
        CollectionAssert.Contains(Stack.Desktops.Deleted.ToList(), machine);

        // A task starts the way it did before saves: from the image, doing the setup itself. It does not ask
        // for the same settings to be tried again.
        var task = await StartAsync(ada, team.Id, draft.Id);
        using (await Desktop.StartAsync(task.Id))
        {
            Assert.IsNull(Stack.Desktops.For(task.Id).Save);
        }
        Assert.IsNull((await SetupOfAsync(draft.Id))!.WantedAt);
        await DeleteTaskAsync(ada, task.Id);

        // A person can ask again, and it begins; asked again while it runs, it is already preparing.
        Assert.AreEqual("preparing", (await ada.PostAsync<EnvironmentDto>($"/api/environments/{draft.Id}/setup")).Setup.State);
        await ada.PostAsync($"/api/environments/{draft.Id}/setup").IsAsync(HttpStatusCode.BadRequest, "being prepared");
        await Stack.Jobs.PrepareAsync(draft.Id, CancellationToken.None);
        using var again = Desktop.Preparing(draft.Id);
        // A clone that failed has no exit code.
        await again.ReportAsync("failed", output: "fatal: repository not found").IsAsync(HttpStatusCode.NoContent);
        StringAssert.Contains((await EnvironmentAsync(ada, draft.Id)).Setup.Failure, "could not be cloned");
        await Stack.Jobs.RemovePreparingMachineAsync(again.Host, CancellationToken.None);
        await ada.DeleteAsync($"/api/environments/{draft.Id}").IsAsync(HttpStatusCode.NoContent);
    }

    [TestMethod]
    public async Task Only_the_team_asks_for_a_preparation_and_only_the_preparing_desktop_speaks_for_it()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var invitation = await ada.PostAsync<PendingInvitation>($"/api/teams/{team.Id}/invitations", new Invite(Person.NewEmail()));
        using var ben = await Person.SignUpAsync("Ben Shneiderman", invitation.Email, Answers.TokenOf(invitation));
        using var stranger = await Person.SignUpAsync("A Stranger");
        using var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
        using var anonymous = new HttpClient { BaseAddress = Stack.Origin };
        var draft = Draft();
        await ada.PostAsync($"/api/teams/{team.Id}/environments", draft).IsAsync(HttpStatusCode.Created);
        var plain = (await ada.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{team.Id}/environments")).Data.Single(x => x.Id != draft.Id);
        Assert.AreEqual("nothing", plain.Setup.State);
        var path = $"/api/environments/{draft.Id}/setup";

        // A stranger and the system administrator are told there is no such environment; nobody is nobody.
        await stranger.PostAsync(path).IsAsync(HttpStatusCode.NotFound);
        await administrator.PostAsync(path).IsAsync(HttpStatusCode.NotFound);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(path, null)).StatusCode);
        // There is nothing to prepare in an environment with no repository and no script.
        await ada.PostAsync($"/api/environments/{plain.Id}/setup").IsAsync(HttpStatusCode.BadRequest, "nothing to prepare");

        await Stack.Jobs.PrepareAsync(draft.Id, CancellationToken.None);
        using var preparing = Desktop.Preparing(draft.Id);
        // A desktop's key is not a person's, and a person's token is not a desktop's.
        await preparing.PostAsync(path, new { }).IsAsync(HttpStatusCode.Unauthorized);
        await ada.PostAsync("/api/desktop/setup", new SetupReport("done", 0, null, null, null)).IsAsync(HttpStatusCode.Unauthorized);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/desktop/setup")).StatusCode);
        // A task's desktop prepares nothing: it can neither read nor report an environment's preparing.
        var task = await StartAsync(ada, team.Id, plain.Id);
        using (var desktop = await Desktop.StartAsync(task.Id))
        {
            Assert.IsFalse((await desktop.SetupAsync()).Prepare);
            await desktop.ReportAsync("done", exit: 0).IsAsync(HttpStatusCode.NotFound);
        }
        await DeleteTaskAsync(ada, task.Id);
        // A state it does not know is refused in words.
        await preparing.ReportAsync("finished").IsAsync(HttpStatusCode.BadRequest);

        // A member asks for it as well as an administrator, once it is no longer preparing.
        await preparing.ReportAsync("failed", exit: 2).IsAsync(HttpStatusCode.NoContent);
        await Stack.Jobs.RemovePreparingMachineAsync(preparing.Host, CancellationToken.None);
        Assert.AreEqual("preparing", (await ben.PostAsync<EnvironmentDto>(path)).Setup.State);
        // The old machine's key is not the new attempt's.
        await Stack.Jobs.PrepareAsync(draft.Id, CancellationToken.None);
        await preparing.GetAsync("/api/desktop/setup").IsAsync(HttpStatusCode.Unauthorized);
        using var next = Desktop.Preparing(draft.Id);
        Assert.AreEqual("Web app", (await next.SetupAsync()).Title);

        // Deleting the environment ends the attempt: its key, its time and, by the job it queues, its machine.
        await ada.DeleteAsync($"/api/environments/{draft.Id}").IsAsync(HttpStatusCode.NoContent);
        await next.GetAsync("/api/desktop/setup").IsAsync(HttpStatusCode.Unauthorized);
        await Stack.Jobs.RemovePreparingMachineAsync(next.Host, CancellationToken.None);
        CollectionAssert.Contains(Stack.Desktops.Deleted.ToList(), next.Host);
    }

    [TestMethod]
    public async Task The_limits_decide_when_a_machine_prepares()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var limits = Options.Saves;
        var (atOnce, hours, enabled) = (limits.AtOnce, limits.HoursPerDay, limits.Enabled);
        try
        {
            // No more machines preparing at once than the setting says: what is wanted waits for one.
            limits.AtOnce = 0;
            var waiting = Draft();
            await ada.PostAsync($"/api/teams/{team.Id}/environments", waiting).IsAsync(HttpStatusCode.Created);
            await Stack.Jobs.PrepareAsync(waiting.Id, CancellationToken.None);
            Assert.IsNull(Stack.Desktops.Preparing(waiting.Id));
            var state = (await EnvironmentAsync(ada, waiting.Id)).Setup;
            Assert.AreEqual("preparing", state.State);
            Assert.IsNull(state.StartedAt);
            limits.AtOnce = atOnce;

            // No more hours of preparing in a day than the setting says.
            limits.HoursPerDay = 0;
            await Stack.Jobs.PrepareAsync(waiting.Id, CancellationToken.None);
            Assert.IsNull(Stack.Desktops.Preparing(waiting.Id));
            limits.HoursPerDay = hours;

            // With room again, the sweep begins what waits.
            await Stack.Jobs.SweepAsync(CancellationToken.None);
            Assert.IsNotNull(Stack.Desktops.Preparing(waiting.Id));

            // An attempt that runs longer than the setting allows fails, and its machine goes.
            var machine = Stack.Desktops.Preparing(waiting.Id)!.Name;
            await SweepWith(saves => saves.MaxMinutes = 0).SweepAsync(CancellationToken.None);
            var timedOut = (await EnvironmentAsync(ada, waiting.Id)).Setup;
            Assert.AreEqual("failed", timedOut.State);
            StringAssert.Contains(timedOut.Failure, "did not finish");
            CollectionAssert.Contains(Stack.Desktops.Deleted.ToList(), machine);

            // Turned off, nothing is prepared and nothing asks.
            limits.Enabled = false;
            var off = Draft();
            var added = await ada.PostAsync<EnvironmentDto>($"/api/teams/{team.Id}/environments", off);
            Assert.AreEqual("not_prepared", added.Setup.State);
            await ada.PostAsync($"/api/environments/{off.Id}/setup").IsAsync(HttpStatusCode.ServiceUnavailable);
            var task = await StartAsync(ada, team.Id, off.Id);
            using (await Desktop.StartAsync(task.Id))
            {
                Assert.IsNull(Stack.Desktops.For(task.Id).Save);
            }
            Assert.IsNull(await SetupOfAsync(off.Id));
            await DeleteTaskAsync(ada, task.Id);
        }
        finally
        {
            (limits.AtOnce, limits.HoursPerDay, limits.Enabled) = (atOnce, hours, enabled);
        }
    }

    [TestMethod]
    public async Task A_save_older_than_a_week_is_made_again_behind_the_task_that_uses_it()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var draft = Draft();
        await ada.PostAsync($"/api/teams/{team.Id}/environments", draft).IsAsync(HttpStatusCode.Created);
        var save = await PrepareAsync(draft.Id);
        using (var scope = Stack.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Setups.Where(x => x.EnvironmentId == draft.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(setup => setup.SavedAt, DateTimeOffset.UtcNow.AddDays(-8)));
        }
        var task = await StartAsync(ada, team.Id, draft.Id);
        using (await Desktop.StartAsync(task.Id))
        {
            // The task uses the old save; a new one is asked for behind it.
            Assert.AreEqual(save, Stack.Desktops.For(task.Id).Save);
        }
        Assert.IsNotNull((await SetupOfAsync(draft.Id))!.WantedAt);
        var fresh = await PrepareAsync(draft.Id);
        Assert.AreNotEqual(save, fresh);
        Assert.IsFalse(Stack.Desktops.Saves.ContainsKey(save));
        await DeleteTaskAsync(ada, task.Id);
        await ada.DeleteAsync($"/api/environments/{draft.Id}").IsAsync(HttpStatusCode.NoContent);
        await Stack.Jobs.DeleteSaveAsync(fresh, CancellationToken.None);
    }

    [TestMethod]
    public async Task The_sweep_removes_saves_nobody_uses_and_anything_no_environment_knows()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var draft = Draft();
        await ada.PostAsync($"/api/teams/{team.Id}/environments", draft).IsAsync(HttpStatusCode.Created);
        var save = await PrepareAsync(draft.Id);

        // A save and a preparing machine no environment knows, left behind long ago.
        var straySave = EnvironmentSetup.SaveNameFor(Guid.NewGuid().ToString("D"));
        Stack.Desktops.Saves[straySave] = ("ws-gone", DateTimeOffset.UtcNow.AddDays(-1));
        var strayMachine = DesktopHosting.NameFor(Guid.NewGuid().ToString("D"));
        await Stack.Desktops.CreateAsync(new DesktopSpec(strayMachine, EnvironmentSetup.DesktopIdFor(Guid.NewGuid().ToString("D")), team.Id, "small", Options.ApiUrl, "key", MachinePurpose.Prepare), CancellationToken.None);
        Stack.Desktops.MadeAt[strayMachine] = DateTimeOffset.UtcNow.AddHours(-2);
        await Stack.Jobs.SweepAsync(CancellationToken.None);
        Assert.IsFalse(Stack.Desktops.Saves.ContainsKey(straySave));
        CollectionAssert.Contains(Stack.Desktops.Deleted.ToList(), strayMachine);
        // What an environment knows stays.
        Assert.IsTrue(Stack.Desktops.Saves.ContainsKey(save));
        Assert.AreEqual("prepared", (await EnvironmentAsync(ada, draft.Id)).Setup.State);

        // A save nothing has started from for as long as the setting says is removed; it is not prepared again until a task asks.
        await SweepWith(saves => saves.UnusedDays = 0).SweepAsync(CancellationToken.None);
        Assert.IsFalse(Stack.Desktops.Saves.ContainsKey(save));
        var gone = (await EnvironmentAsync(ada, draft.Id)).Setup;
        Assert.AreEqual("not_prepared", gone.State);
        var task = await StartAsync(ada, team.Id, draft.Id);
        using (await Desktop.StartAsync(task.Id))
        {
            Assert.IsNull(Stack.Desktops.For(task.Id).Save);
        }
        Assert.AreEqual("preparing", (await EnvironmentAsync(ada, draft.Id)).Setup.State);
        await DeleteTaskAsync(ada, task.Id);
        await ada.DeleteAsync($"/api/environments/{draft.Id}").IsAsync(HttpStatusCode.NoContent);
    }
}
