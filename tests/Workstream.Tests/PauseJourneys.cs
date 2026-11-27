using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workstream.Api;
using Workstream.Api.Desktops;
using Workstream.Api.Domain;

namespace Workstream.Tests;

/// <summary>
/// Running, paused, archived: a desktop nobody uses is paused after its team's time, a paused one is archived
/// after its team's days, and either comes back when someone clicks it or writes to its agent. Having the pane
/// open is not use, and nothing but a person deletes a task.
/// </summary>
[TestClass]
public sealed class PauseJourneys
{
    static async Task<(Person Person, TeamDto Team, string EnvironmentId)> TeamAsync(string name = "Ada Lovelace")
    {
        var (person, team) = await Person.WithTeamAsync(name);
        var environment = (await person.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{team.Id}/environments")).Data.Single();
        return (person, team, environment.Id);
    }

    static Task<TaskDto> StartAsync(Person person, string teamId, string environmentId, string prompt = "Open example.com and tell me the heading.") =>
        person.PostAsync<TaskDto>($"/api/teams/{teamId}/tasks", new StartTask(Guid.NewGuid().ToString("D"), environmentId, null, prompt, Guid.NewGuid().ToString("D")));

    /// <summary>A task whose desktop is up and whose agent has joined.</summary>
    static async Task<(TaskDto Task, Desktop Desktop)> ReadyAsync(Person person, string teamId, string environmentId)
    {
        var task = await StartAsync(person, teamId, environmentId);
        var desktop = await Desktop.StartAsync(task.Id);
        await desktop.ExchangeAsync();
        return (task, desktop);
    }

    static Task<TaskDto> TaskAsync(Person person, string taskId) => person.GetAsync<TaskDto>($"/api/tasks/{taskId}");

    static async Task<WorkTask> RowAsync(string taskId)
    {
        using var scope = Stack.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Tasks.AsNoTracking().SingleAsync(x => x.Id == taskId);
    }

    /// <summary>Time passes for one task: the last moment it was used is this long ago.</summary>
    static async Task UnusedForAsync(string taskId, TimeSpan time)
    {
        using var scope = Stack.Services.CreateScope();
        var then = DateTimeOffset.UtcNow - time;
        await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Tasks.Where(x => x.Id == taskId).ExecuteUpdateAsync(x => x.SetProperty(task => task.ActiveAt, then));
    }

    /// <summary>Time passes for one task: it has been in the state it is in for this long.</summary>
    static async Task InStateForAsync(string taskId, TimeSpan time)
    {
        using var scope = Stack.Services.CreateScope();
        var then = DateTimeOffset.UtcNow - time;
        await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Tasks.Where(x => x.Id == taskId).ExecuteUpdateAsync(x => x.SetProperty(task => task.StateChangedAt, then));
    }

    static Task SweepAsync() => Stack.Jobs.SweepAsync(CancellationToken.None);

    /// <summary>Pause a ready desktop the way a person does, and let the job release its machine.</summary>
    static async Task PauseAsync(Person person, string taskId)
    {
        Assert.AreEqual("stopping", (await person.PostAsync<TaskDto>($"/api/tasks/{taskId}/stop")).State);
        await Stack.Jobs.StopAsync(taskId, CancellationToken.None);
        Assert.AreEqual("stopped", (await TaskAsync(person, taskId)).State);
    }

    /// <summary>
    /// Whether the relay writes down that the desktop was used, which it does within a second or two of the
    /// input. Waiting three seconds and finding nothing is how a journey knows nothing counted as use.
    /// </summary>
    static async Task<bool> UsedSoonAsync(string taskId, int seconds = 6)
    {
        for (var asked = 0; asked < seconds * 10; asked++)
        {
            if (DateTimeOffset.UtcNow - (await RowAsync(taskId)).ActiveAt < TimeSpan.FromMinutes(1)) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
        return false;
    }

    [TestMethod]
    public async Task An_open_pane_does_not_keep_a_desktop_running()
    {
        var (ada, team, environment) = await TeamAsync();
        var (task, desktop) = await ReadyAsync(ada, team.Id, environment);
        using var _ = desktop;
        Assert.AreEqual(30, team.PauseAfterMinutes);

        // Nobody has touched it for longer than the team's half hour. Someone opens its pane, reads its
        // conversation and asks for its picture, over and over, as a tab left open does.
        await UnusedForAsync(task.Id, TimeSpan.FromMinutes(31));
        Assert.AreEqual(HttpStatusCode.SwitchingProtocols, (await Watching.OpenAsync(task.Id, ada.Token)).Status);
        await Watching.SendAsync(task.Id, ada.Token, Rfb.Handshake(), Rfb.SetPixelFormat(), Rfb.UpdateRequest(), Rfb.UpdateRequest());
        await ada.GetAsync<ConversationDto>($"/api/tasks/{task.Id}/messages?after=0");
        Assert.IsFalse(await UsedSoonAsync(task.Id, seconds: 3));

        await SweepAsync();
        Assert.AreEqual("stopped", (await TaskAsync(ada, task.Id)).State);
        Assert.IsFalse(Stack.Desktops.Running[desktop.Host]);
        // Paused, not gone: the machine and its disk are still the task's.
        Assert.IsTrue(Stack.Desktops.Made.ContainsKey(desktop.Host));
        CollectionAssert.DoesNotContain(Stack.Desktops.Deleted.ToList(), desktop.Host);

        // The pane still open does not bring it back either: reading and trying the stream change nothing.
        await ada.GetAsync<ConversationDto>($"/api/tasks/{task.Id}/messages?after=0");
        Assert.AreEqual(HttpStatusCode.Conflict, (await Watching.OpenAsync(task.Id, ada.Token)).Status);
        Assert.AreEqual("stopped", (await TaskAsync(ada, task.Id)).State);
    }

    [TestMethod]
    public async Task A_persons_mouse_or_keyboard_keeps_a_desktop_running()
    {
        var (ada, team, environment) = await TeamAsync();
        var (task, desktop) = await ReadyAsync(ada, team.Id, environment);
        using var _ = desktop;

        await UnusedForAsync(task.Id, TimeSpan.FromMinutes(31));
        await Watching.SendAsync(task.Id, ada.Token, Rfb.Handshake(), Rfb.UpdateRequest(), Rfb.Pointer());
        Assert.IsTrue(await UsedSoonAsync(task.Id), "The relay never wrote down that the desktop was used.");
        await SweepAsync();
        Assert.AreEqual("ready", (await TaskAsync(ada, task.Id)).State);

        await UnusedForAsync(task.Id, TimeSpan.FromMinutes(31));
        await Watching.SendAsync(task.Id, ada.Token, Rfb.Handshake(), Rfb.Key());
        Assert.IsTrue(await UsedSoonAsync(task.Id));
        await SweepAsync();
        Assert.AreEqual("ready", (await TaskAsync(ada, task.Id)).State);
    }

    [TestMethod]
    public async Task Input_from_someone_who_may_only_watch_is_not_use()
    {
        var (ada, team, environment) = await TeamAsync();
        var (task, desktop) = await ReadyAsync(ada, team.Id, environment);
        using var _ = desktop;
        using var grace = await ProvedAsync("Grace Hopper");
        await ada.PostAsync($"/api/tasks/{task.Id}/access", new ShareTask(grace.Email, "watcher", "day")).IsAsync(HttpStatusCode.Created);

        // The relay drops a watcher's keys and pointer, so they never reach the desktop and are not use.
        await UnusedForAsync(task.Id, TimeSpan.FromMinutes(31));
        await Watching.SendAsync(task.Id, grace.Token, Rfb.Handshake(), Rfb.Pointer(), Rfb.Key());
        Assert.IsFalse(await UsedSoonAsync(task.Id, seconds: 3));
        await SweepAsync();
        Assert.AreEqual("stopped", (await TaskAsync(ada, task.Id)).State);
    }

    [TestMethod]
    public async Task A_message_and_an_agent_at_work_keep_a_desktop_running()
    {
        var (ada, team, environment) = await TeamAsync();
        var (task, desktop) = await ReadyAsync(ada, team.Id, environment);
        using var _ = desktop;

        // A person writes: that is use.
        await UnusedForAsync(task.Id, TimeSpan.FromMinutes(31));
        await ada.PostAsync($"/api/tasks/{task.Id}/messages", new Say("Now open the About page.", Guid.NewGuid().ToString("D"))).IsAsync(HttpStatusCode.Created);
        await SweepAsync();
        Assert.AreEqual("ready", (await TaskAsync(ada, task.Id)).State);

        // The agent is working, with nobody there: that is use, for as long as it says so.
        await UnusedForAsync(task.Id, TimeSpan.FromMinutes(31));
        await desktop.ExchangeAsync(working: true);
        await SweepAsync();
        Assert.AreEqual("ready", (await TaskAsync(ada, task.Id)).State);

        // It finishes. The idle time counts from that moment, not from when a person was last there.
        await UnusedForAsync(task.Id, TimeSpan.FromMinutes(31));
        await desktop.ExchangeAsync(working: false);
        await SweepAsync();
        Assert.AreEqual("ready", (await TaskAsync(ada, task.Id)).State);

        // An agent that is idle exchanges every second, and that is not use.
        await UnusedForAsync(task.Id, TimeSpan.FromMinutes(31));
        await desktop.ExchangeAsync(working: false);
        await SweepAsync();
        Assert.AreEqual("stopped", (await TaskAsync(ada, task.Id)).State);
    }

    [TestMethod]
    public async Task A_desktop_is_paused_after_its_own_teams_time()
    {
        var (ada, team, environment) = await TeamAsync();
        var (grace, hers, herEnvironment) = await TeamAsync("Grace Hopper");
        var (task, desktop) = await ReadyAsync(ada, team.Id, environment);
        var (other, otherDesktop) = await ReadyAsync(grace, hers.Id, herEnvironment);
        using var _ = desktop;
        using var __ = otherDesktop;

        // Ada's team pauses after ten minutes; Grace's keeps the half hour a team starts with.
        var changed = await ada.PutAsync<TeamDto>($"/api/teams/{team.Id}/desktop-times", new SetDesktopTimes(10, 7));
        Assert.AreEqual(10, changed.PauseAfterMinutes);
        await UnusedForAsync(task.Id, TimeSpan.FromMinutes(9));
        await UnusedForAsync(other.Id, TimeSpan.FromMinutes(11));
        await SweepAsync();
        Assert.AreEqual("ready", (await TaskAsync(ada, task.Id)).State);
        Assert.AreEqual("ready", (await TaskAsync(grace, other.Id)).State);

        await UnusedForAsync(task.Id, TimeSpan.FromMinutes(11));
        await SweepAsync();
        Assert.AreEqual("stopped", (await TaskAsync(ada, task.Id)).State);
        Assert.AreEqual("ready", (await TaskAsync(grace, other.Id)).State);
    }

    [TestMethod]
    public async Task A_paused_desktop_comes_back_when_someone_clicks_it()
    {
        var (ada, team, environment) = await TeamAsync();
        var (task, desktop) = await ReadyAsync(ada, team.Id, environment);
        using var _ = desktop;
        await UnusedForAsync(task.Id, TimeSpan.FromMinutes(31));
        await SweepAsync();
        Assert.AreEqual("stopped", (await TaskAsync(ada, task.Id)).State);
        Assert.AreEqual(0, (await ada.GetAsync<TeamUsage>($"/api/teams/{team.Id}/usage")).RunningDesktops);

        // Clicking the pane starts the same machine again.
        Assert.AreEqual("starting", (await ada.PostAsync<TaskDto>($"/api/tasks/{task.Id}/start")).State);
        await Stack.Jobs.StartAsync(task.Id, CancellationToken.None);
        Assert.IsTrue(Stack.Desktops.Running[desktop.Host]);
        await desktop.ExchangeAsync();
        Assert.AreEqual("ready", (await TaskAsync(ada, task.Id)).State);
        using var scope = Stack.Services.CreateScope();
        var runs = await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().DesktopRuns.AsNoTracking().Where(x => x.TaskId == task.Id).OrderBy(x => x.StartedAt).ToListAsync();
        Assert.AreEqual(2, runs.Count);
        Assert.IsNotNull(runs[0].EndedAt);
        Assert.AreEqual(StartSource.Stopped, runs[1].Source);
        // A desktop just brought back has its team's whole time before it is paused again.
        await SweepAsync();
        Assert.AreEqual("ready", (await TaskAsync(ada, task.Id)).State);
    }

    [TestMethod]
    public async Task A_message_to_a_paused_task_starts_its_desktop_and_reaches_the_agent()
    {
        var (ada, team, environment) = await TeamAsync();
        var (task, desktop) = await ReadyAsync(ada, team.Id, environment);
        using var _ = desktop;
        var read = (await desktop.ExchangeAsync()).Prompts.Single().Id;
        await PauseAsync(ada, task.Id);

        await ada.PostAsync($"/api/tasks/{task.Id}/messages", new Say("Are you still there?", Guid.NewGuid().ToString("D"))).IsAsync(HttpStatusCode.Created);
        Assert.AreEqual("starting", (await TaskAsync(ada, task.Id)).State);
        // A second message while it starts is kept too, and starts nothing more.
        await ada.PostAsync($"/api/tasks/{task.Id}/messages", new Say("Take your time.", Guid.NewGuid().ToString("D"))).IsAsync(HttpStatusCode.Created);
        await Stack.Jobs.StartAsync(task.Id, CancellationToken.None);
        Assert.IsTrue(Stack.Desktops.Running[desktop.Host]);

        // The agent comes up and asks for what was written after the last message it read, which it kept on its disk.
        var reply = await desktop.ExchangeAsync(after: read);
        CollectionAssert.AreEqual(new[] { "Are you still there?", "Take your time." }, reply.Prompts.Select(x => x.Text).ToArray());
        Assert.AreEqual("ready", (await TaskAsync(ada, task.Id)).State);
    }

    [TestMethod]
    public async Task Resuming_keeps_to_the_teams_limit_of_running_desktops()
    {
        var (ada, team, environment) = await TeamAsync();
        Assert.AreEqual(2, team.MaxRunningDesktops);
        var (paused, desktop) = await ReadyAsync(ada, team.Id, environment);
        using var _ = desktop;
        await PauseAsync(ada, paused.Id);
        var second = await StartAsync(ada, team.Id, environment, "A second.");
        await StartAsync(ada, team.Id, environment, "A third.");

        // Two are running. Clicking the paused one is refused, in words that say what to do.
        await ada.PostAsync($"/api/tasks/{paused.Id}/start").IsAsync(HttpStatusCode.PaymentRequired, "Pause one to start another");
        // So is writing to it, and the message is not kept waiting for a desktop that is not coming.
        var key = Guid.NewGuid().ToString("D");
        await ada.PostAsync($"/api/tasks/{paused.Id}/messages", new Say("Carry on.", key)).IsAsync(HttpStatusCode.PaymentRequired, "limit_reached");
        Assert.AreEqual("stopped", (await TaskAsync(ada, paused.Id)).State);
        Assert.IsFalse((await ada.GetAsync<ConversationDto>($"/api/tasks/{paused.Id}/messages?after=0")).Messages.Any(x => x.Text == "Carry on."));

        // Pausing one makes room, and the same send goes through.
        await ada.PostAsync($"/api/tasks/{second.Id}/stop").IsAsync(HttpStatusCode.OK);
        await Stack.Jobs.StopAsync(second.Id, CancellationToken.None);
        await ada.PostAsync($"/api/tasks/{paused.Id}/messages", new Say("Carry on.", key)).IsAsync(HttpStatusCode.Created);
        Assert.AreEqual("starting", (await TaskAsync(ada, paused.Id)).State);
    }

    /// <summary>Someone who has proved their address, as everyone has in production by signing in with an emailed code.</summary>
    static async Task<Person> ProvedAsync(string name)
    {
        var person = await Person.SignUpAsync(name);
        using var scope = Stack.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
        foreach (var email in await db.Set<SqlOS.AuthServer.Models.SqlOSUserEmail>().Where(x => x.UserId == person.UserId).ToListAsync())
        {
            email.IsVerified = true;
            email.VerifiedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        return person;
    }

    [TestMethod]
    public async Task Who_may_bring_a_paused_desktop_back()
    {
        var (ada, team, environment) = await TeamAsync();
        var invitation = await ada.PostAsync<PendingInvitation>($"/api/teams/{team.Id}/invitations", new Invite(Person.NewEmail()));
        using var member = await Person.SignUpAsync("Lin Clark", invitation.Email, Answers.TokenOf(invitation));
        using var watcher = await ProvedAsync("Grace Hopper");
        using var prompter = await ProvedAsync("Barbara Liskov");
        using var driver = await ProvedAsync("Margaret Hamilton");
        using var stranger = await Person.SignUpAsync("A Stranger");
        using var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
        using var nobody = new HttpClient { BaseAddress = Stack.Origin };
        var (task, desktop) = await ReadyAsync(ada, team.Id, environment);
        using var _ = desktop;
        await ada.PostAsync($"/api/tasks/{task.Id}/access", new ShareTask(watcher.Email, "watcher", "day")).IsAsync(HttpStatusCode.Created);
        await ada.PostAsync($"/api/tasks/{task.Id}/access", new ShareTask(prompter.Email, "prompter", "day")).IsAsync(HttpStatusCode.Created);
        await ada.PostAsync($"/api/tasks/{task.Id}/access", new ShareTask(driver.Email, "driver", "day")).IsAsync(HttpStatusCode.Created);
        var start = $"/api/tasks/{task.Id}/start";
        var say = $"/api/tasks/{task.Id}/messages";
        Say Message() => new("Hello again.", Guid.NewGuid().ToString("D"));

        async Task BackToPausedAsync()
        {
            Assert.AreEqual("starting", (await TaskAsync(ada, task.Id)).State);
            await Stack.Jobs.StartAsync(task.Id, CancellationToken.None);
            await desktop.ExchangeAsync();
            await PauseAsync(ada, task.Id);
        }

        // Pausing is the team's: nobody a task is shared with pauses it.
        foreach (var shared in new[] { watcher, prompter, driver }) await shared.PostAsync($"/api/tasks/{task.Id}/stop").IsAsync(HttpStatusCode.Forbidden);
        await PauseAsync(ada, task.Id);

        // Nobody, a desktop's key, a stranger and the system administrator reach none of it.
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await nobody.PostAsync(start, null)).StatusCode);
        await desktop.PostAsync(start, new { }).IsAsync(HttpStatusCode.Unauthorized);
        await stranger.PostAsync(start).IsAsync(HttpStatusCode.NotFound);
        await stranger.PostAsync(say, Message()).IsAsync(HttpStatusCode.NotFound);
        await administrator.PostAsync(start).IsAsync(HttpStatusCode.NotFound);
        await administrator.PostAsync(say, Message()).IsAsync(HttpStatusCode.NotFound);

        // Someone who only watches brings nothing back.
        await watcher.PostAsync(start).IsAsync(HttpStatusCode.Forbidden);
        await watcher.PostAsync(say, Message()).IsAsync(HttpStatusCode.Forbidden);
        Assert.AreEqual("stopped", (await TaskAsync(ada, task.Id)).State);

        // Someone who may write to the agent brings it back by writing, not by clicking the desktop they may not use.
        await prompter.PostAsync(start).IsAsync(HttpStatusCode.Forbidden);
        Assert.AreEqual("stopped", (await TaskAsync(ada, task.Id)).State);
        await prompter.PostAsync(say, Message()).IsAsync(HttpStatusCode.Created);
        await BackToPausedAsync();

        // Someone who may use the desktop brings it back either way.
        await driver.PostAsync(start).IsAsync(HttpStatusCode.OK);
        await BackToPausedAsync();
        await driver.PostAsync(say, Message()).IsAsync(HttpStatusCode.Created);
        await BackToPausedAsync();

        // And so does anyone on the team.
        await member.PostAsync(start).IsAsync(HttpStatusCode.OK);
        await BackToPausedAsync();
        await member.PostAsync(say, Message()).IsAsync(HttpStatusCode.Created);
        Assert.AreEqual("starting", (await TaskAsync(ada, task.Id)).State);
    }

    [TestMethod]
    public async Task A_desktop_paused_for_its_teams_days_is_archived_and_comes_back_from_its_archive()
    {
        var (ada, team, environment) = await TeamAsync();
        Assert.AreEqual(7, team.ArchiveAfterDays);
        var (task, desktop) = await ReadyAsync(ada, team.Id, environment);
        var machine = desktop.Host;
        var firstKey = desktop.Key;
        // The agent says how much is on its disk, which is what the archive will hold.
        const long onDisk = 7_516_192_768;
        var read = (await desktop.PostAsync("/api/desktop/exchange", new DesktopExchange(false, 0, null, onDisk))).EnsureSuccessStatusCode();
        read.Dispose();
        using var _ = desktop;
        await PauseAsync(ada, task.Id);

        // Six days paused is not yet the team's week.
        await InStateForAsync(task.Id, TimeSpan.FromDays(6));
        await SweepAsync();
        Assert.AreEqual("stopped", (await TaskAsync(ada, task.Id)).State);
        Assert.IsNull((await RowAsync(task.Id)).Archive);

        // The team archives after five days instead: at the next sweep the disk is copied, and the machine and its disk are deleted.
        await ada.PutAsync<TeamDto>($"/api/teams/{team.Id}/desktop-times", new SetDesktopTimes(30, 5));
        await SweepAsync();
        Assert.AreEqual("archived", (await TaskAsync(ada, task.Id)).State);
        var archived = await RowAsync(task.Id);
        Assert.IsNull(archived.Host);
        Assert.IsNotNull(archived.Archive);
        StringAssert.StartsWith(archived.Archive, DesktopHosting.ArchivePrefix);
        Assert.AreEqual(onDisk, archived.ArchiveBytes);
        Assert.AreEqual(machine, Stack.Desktops.Archives[archived.Archive].Machine);
        CollectionAssert.Contains(Stack.Desktops.Deleted.ToList(), machine);
        Assert.IsFalse(Stack.Desktops.Made.ContainsKey(machine));
        // The deleted machine's key died with it.
        await desktop.GetAsync("/api/desktop/setup").IsAsync(HttpStatusCode.Unauthorized);
        // Nothing is running and there is no screen; the task, its conversation and its place in the list are all there.
        Assert.AreEqual(0, (await ada.GetAsync<TeamUsage>($"/api/teams/{team.Id}/usage")).RunningDesktops);
        Assert.AreEqual(HttpStatusCode.Conflict, (await Watching.OpenAsync(task.Id, ada.Token)).Status);
        Assert.AreEqual(task.Id, (await ada.GetAsync<ListPage<TaskDto>>($"/api/teams/{team.Id}/tasks")).Data.Single().Id);
        Assert.AreEqual(1, (await ada.GetAsync<ConversationDto>($"/api/tasks/{task.Id}/messages?after=0")).Messages.Count);
        // Later sweeps leave it as it is: its archive is the task's however old, and the sweep never deletes a task.
        Stack.Desktops.Archives[archived.Archive] = (machine, DateTimeOffset.UtcNow.AddDays(-400));
        await InStateForAsync(task.Id, TimeSpan.FromDays(400));
        await SweepAsync();
        Assert.AreEqual("archived", (await TaskAsync(ada, task.Id)).State);
        Assert.IsTrue(Stack.Desktops.Archives.ContainsKey(archived.Archive));

        // Clicking it makes a new machine from the archive, with a key of its own.
        Assert.AreEqual("starting", (await ada.PostAsync<TaskDto>($"/api/tasks/{task.Id}/start")).State);
        using var back = await Desktop.StartAsync(task.Id);
        var spec = Stack.Desktops.For(task.Id);
        Assert.AreEqual(archived.Archive, spec.Archive);
        Assert.IsNull(spec.Save);
        Assert.AreNotEqual(firstKey, spec.Key);
        Assert.AreEqual(task.Id, (await back.SetupAsync()).TaskId);
        await desktop.GetAsync("/api/desktop/setup").IsAsync(HttpStatusCode.Unauthorized);
        await back.ExchangeAsync();
        Assert.AreEqual("ready", (await TaskAsync(ada, task.Id)).State);
        // The machine has shown its screen, so the archive is no longer needed and is gone.
        var restored = await RowAsync(task.Id);
        Assert.AreEqual(spec.Name, restored.Host);
        Assert.IsNull(restored.Archive);
        Assert.IsNull(restored.ArchiveBytes);
        Assert.IsFalse(Stack.Desktops.Archives.ContainsKey(archived.Archive));
        using var scope = Stack.Services.CreateScope();
        var last = await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().DesktopRuns.AsNoTracking().Where(x => x.TaskId == task.Id).OrderByDescending(x => x.StartedAt).FirstAsync();
        Assert.AreEqual(StartSource.Archive, last.Source);
    }

    [TestMethod]
    public async Task A_task_that_started_on_a_spare_is_archived_and_comes_back_on_a_machine_of_its_own()
    {
        var (ada, team, environment) = await TeamAsync();
        // The team's spare, up and asking what it is for, and the task that starts on it.
        await ada.PostAsync($"/api/teams/{team.Id}/spare", new WantSpare(null)).IsAsync(HttpStatusCode.NoContent);
        string spareId;
        using (var scope = Stack.Services.CreateScope())
        {
            spareId = (await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Spares.AsNoTracking().SingleAsync(x => x.TeamId == team.Id)).Id;
        }
        await Stack.Jobs.StartSpareAsync(spareId, CancellationToken.None);
        using var spare = Desktop.Of(spareId);
        await spare.GetAsync("/api/desktop/setup").IsAsync(HttpStatusCode.NotFound);
        var task = await StartAsync(ada, team.Id, environment);
        await spare.ExchangeAsync();
        Assert.AreEqual("ready", (await TaskAsync(ada, task.Id)).State);
        Assert.AreEqual(spare.Host, (await RowAsync(task.Id)).Host);

        await PauseAsync(ada, task.Id);
        await InStateForAsync(task.Id, TimeSpan.FromDays(8));
        await SweepAsync();
        Assert.AreEqual("archived", (await TaskAsync(ada, task.Id)).State);
        CollectionAssert.Contains(Stack.Desktops.Deleted.ToList(), spare.Host);
        // The spare's key, which worked on the task, is ended with its machine.
        await spare.GetAsync("/api/desktop/setup").IsAsync(HttpStatusCode.Unauthorized);

        // It comes back as the task's own desktop: a machine named for the task, with a key of its own.
        await ada.PostAsync($"/api/tasks/{task.Id}/start").IsAsync(HttpStatusCode.OK);
        using var back = await Desktop.StartAsync(task.Id);
        Assert.AreEqual(DesktopHosting.NameFor(task.Id), back.Host);
        Assert.AreNotEqual(spare.Host, back.Host);
        Assert.AreEqual(task.Id, (await back.SetupAsync()).TaskId);
        await back.ExchangeAsync();
        Assert.AreEqual("ready", (await TaskAsync(ada, task.Id)).State);
        await spare.GetAsync("/api/desktop/setup").IsAsync(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task A_message_to_an_archived_task_brings_its_desktop_back_from_the_archive()
    {
        var (ada, team, environment) = await TeamAsync();
        var (task, desktop) = await ReadyAsync(ada, team.Id, environment);
        var read = (await desktop.ExchangeAsync()).Prompts.Single().Id;
        desktop.Dispose();
        await PauseAsync(ada, task.Id);
        await InStateForAsync(task.Id, TimeSpan.FromDays(8));
        await SweepAsync();
        var archive = (await RowAsync(task.Id)).Archive;
        Assert.IsNotNull(archive);

        await ada.PostAsync($"/api/tasks/{task.Id}/messages", new Say("Pick this up again.", Guid.NewGuid().ToString("D"))).IsAsync(HttpStatusCode.Created);
        Assert.AreEqual("starting", (await TaskAsync(ada, task.Id)).State);
        using var back = await Desktop.StartAsync(task.Id);
        Assert.AreEqual(archive, Stack.Desktops.For(task.Id).Archive);
        // The archive is a copy of the disk, so the agent still knows which message it read last.
        Assert.AreEqual("Pick this up again.", (await back.ExchangeAsync(after: read)).Prompts.Single().Text);
    }

    [TestMethod]
    public async Task A_desktop_that_cannot_come_back_from_its_archive_keeps_the_archive()
    {
        var (ada, team, environment) = await TeamAsync();
        var (task, desktop) = await ReadyAsync(ada, team.Id, environment);
        desktop.Dispose();
        await PauseAsync(ada, task.Id);
        await InStateForAsync(task.Id, TimeSpan.FromDays(8));
        await SweepAsync();
        var archive = (await RowAsync(task.Id)).Archive!;

        // The host takes the request for the new machine and then cannot start it.
        var machine = DesktopHosting.NameFor(task.Id);
        Stack.Desktops.Broken[machine] = "Allocation failed. We do not have sufficient capacity for the requested VM size in this region.";
        await ada.PostAsync($"/api/tasks/{task.Id}/start").IsAsync(HttpStatusCode.OK);
        await Stack.Jobs.StartAsync(task.Id, CancellationToken.None);
        Assert.AreEqual("failed", (await TaskAsync(ada, task.Id)).State);
        var failed = await RowAsync(task.Id);
        // The half-made machine is removed, and the archive is still the task's only copy of its disk.
        Assert.IsNull(failed.Host);
        Assert.AreEqual(archive, failed.Archive);
        Assert.IsTrue(Stack.Desktops.Archives.ContainsKey(archive));

        // Starting again makes another machine from the same archive.
        Stack.Desktops.Broken.TryRemove(machine, out _);
        await ada.PostAsync($"/api/tasks/{task.Id}/start").IsAsync(HttpStatusCode.OK);
        using var back = await Desktop.StartAsync(task.Id);
        Assert.AreEqual(archive, Stack.Desktops.For(task.Id).Archive);
        await back.ExchangeAsync();
        Assert.AreEqual("ready", (await TaskAsync(ada, task.Id)).State);
        Assert.IsFalse(Stack.Desktops.Archives.ContainsKey(archive));
    }

    [TestMethod]
    public async Task A_disk_that_cannot_be_archived_leaves_the_desktop_paused_and_is_tried_again()
    {
        var (ada, team, environment) = await TeamAsync();
        var (task, desktop) = await ReadyAsync(ada, team.Id, environment);
        using var _ = desktop;
        await PauseAsync(ada, task.Id);
        await InStateForAsync(task.Id, TimeSpan.FromDays(8));

        Stack.Desktops.RefuseArchive[desktop.Host] = true;
        await SweepAsync();
        Assert.AreEqual("stopped", (await TaskAsync(ada, task.Id)).State);
        var paused = await RowAsync(task.Id);
        Assert.AreEqual(desktop.Host, paused.Host);
        Assert.IsNull(paused.Archive);
        Assert.IsTrue(Stack.Desktops.Made.ContainsKey(desktop.Host));
        // It can still be brought back as a paused desktop is.
        Stack.Desktops.RefuseArchive.TryRemove(desktop.Host, out bool _);
        await SweepAsync();
        Assert.AreEqual("archived", (await TaskAsync(ada, task.Id)).State);
    }

    [TestMethod]
    public async Task Deleting_a_task_deletes_its_archive()
    {
        var (ada, team, environment) = await TeamAsync();
        var (task, desktop) = await ReadyAsync(ada, team.Id, environment);
        desktop.Dispose();
        await PauseAsync(ada, task.Id);
        await InStateForAsync(task.Id, TimeSpan.FromDays(8));
        await SweepAsync();
        var archive = (await RowAsync(task.Id)).Archive!;
        Assert.IsTrue(Stack.Desktops.Archives.ContainsKey(archive));

        await ada.DeleteAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NoContent);
        // The sweep that runs before the delete job still knows whose the archive is, however old it is.
        Stack.Desktops.Archives[archive] = (Stack.Desktops.Archives[archive].Machine, DateTimeOffset.UtcNow.AddHours(-2));
        await SweepAsync();
        Assert.IsTrue(Stack.Desktops.Archives.ContainsKey(archive));
        await Stack.Jobs.DeleteAsync(task.Id, CancellationToken.None);
        Assert.IsFalse(Stack.Desktops.Archives.ContainsKey(archive));
        await ada.GetAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task The_sweep_removes_an_archive_no_task_owns_once_it_has_been_there_a_while()
    {
        // One left behind long ago by something that went wrong; the other a minute old, which may be the
        // copy a sweep is making right now.
        Stack.Desktops.Archives["wsa-0000000000000000-stray000"] = ("ws-gone", DateTimeOffset.UtcNow.AddHours(-2));
        Stack.Desktops.Archives["wsa-0000000000000000-new00000"] = ("ws-gone", DateTimeOffset.UtcNow.AddMinutes(-1));
        await SweepAsync();
        Assert.IsFalse(Stack.Desktops.Archives.ContainsKey("wsa-0000000000000000-stray000"));
        Assert.IsTrue(Stack.Desktops.Archives.ContainsKey("wsa-0000000000000000-new00000"));
        await Stack.Desktops.DeleteArchiveAsync("wsa-0000000000000000-new00000", CancellationToken.None);
    }

    [TestMethod]
    public async Task A_teams_administrator_sets_when_its_desktops_are_paused_and_archived()
    {
        var (ada, team, _) = await TeamAsync();
        var invitation = await ada.PostAsync<PendingInvitation>($"/api/teams/{team.Id}/invitations", new Invite(Person.NewEmail()));
        using var member = await Person.SignUpAsync("Lin Clark", invitation.Email, Answers.TokenOf(invitation));
        using var stranger = await Person.SignUpAsync("A Stranger");
        using var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
        using var nobody = new HttpClient { BaseAddress = Stack.Origin };
        var path = $"/api/teams/{team.Id}/desktop-times";

        // A team starts with half an hour and a week, and everyone on it sees them.
        Assert.AreEqual(30, team.PauseAfterMinutes);
        Assert.AreEqual(7, team.ArchiveAfterDays);
        Assert.AreEqual(30, (await member.GetAsync<TeamDto>($"/api/teams/{team.Id}")).PauseAfterMinutes);

        var changed = await ada.PutAsync<TeamDto>(path, new SetDesktopTimes(45, 14));
        Assert.AreEqual(45, changed.PauseAfterMinutes);
        Assert.AreEqual(14, changed.ArchiveAfterDays);
        var seen = await member.GetAsync<TeamDto>($"/api/teams/{team.Id}");
        Assert.AreEqual(45, seen.PauseAfterMinutes);
        Assert.AreEqual(14, seen.ArchiveAfterDays);
        // The ends of what a team may choose are allowed.
        await ada.PutAsync(path, new SetDesktopTimes(5, 1)).IsAsync(HttpStatusCode.OK);
        await ada.PutAsync(path, new SetDesktopTimes(240, 90)).IsAsync(HttpStatusCode.OK);
        // Past them is refused, in words, and changes nothing.
        await ada.PutAsync(path, new SetDesktopTimes(4, 7)).IsAsync(HttpStatusCode.BadRequest, "between 5 and 240 minutes");
        await ada.PutAsync(path, new SetDesktopTimes(241, 7)).IsAsync(HttpStatusCode.BadRequest);
        await ada.PutAsync(path, new SetDesktopTimes(30, 0)).IsAsync(HttpStatusCode.BadRequest, "between 1 and 90 days");
        await ada.PutAsync(path, new SetDesktopTimes(30, 91)).IsAsync(HttpStatusCode.BadRequest);
        Assert.AreEqual(240, (await ada.GetAsync<TeamDto>($"/api/teams/{team.Id}")).PauseAfterMinutes);

        // A member sees and may not change; a stranger and the system administrator are told there is no such team.
        await member.PutAsync(path, new SetDesktopTimes(60, 7)).IsAsync(HttpStatusCode.Forbidden);
        await stranger.PutAsync(path, new SetDesktopTimes(60, 7)).IsAsync(HttpStatusCode.NotFound);
        await administrator.PutAsync(path, new SetDesktopTimes(60, 7)).IsAsync(HttpStatusCode.NotFound);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await nobody.PutAsync(path, System.Net.Http.Json.JsonContent.Create(new SetDesktopTimes(60, 7)))).StatusCode);
        Assert.AreEqual(240, (await ada.GetAsync<TeamDto>($"/api/teams/{team.Id}")).PauseAfterMinutes);
    }

    [TestMethod]
    public async Task A_desktops_key_changes_no_teams_times()
    {
        var (ada, team, environment) = await TeamAsync();
        var (_, desktop) = await ReadyAsync(ada, team.Id, environment);
        using var _d = desktop;
        using var http = new HttpClient { BaseAddress = Stack.Origin };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", desktop.Key);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.PutAsync($"/api/teams/{team.Id}/desktop-times", System.Net.Http.Json.JsonContent.Create(new SetDesktopTimes(60, 7)))).StatusCode);
        Assert.AreEqual(30, (await ada.GetAsync<TeamDto>($"/api/teams/{team.Id}")).PauseAfterMinutes);
    }

    /// <summary>A task whose desktop is up on a host that gives up a paused machine and keeps its disk, as Azure does.</summary>
    static async Task<(TaskDto Task, Desktop Desktop)> ReadyOnAHostThatGivesUpAsync(Person person, string teamId, string environmentId)
    {
        var task = await StartAsync(person, teamId, environmentId);
        Stack.Desktops.GivesUp[task.Id] = true;
        var desktop = await Desktop.StartAsync(task.Id);
        await desktop.ExchangeAsync();
        return (task, desktop);
    }

    [TestMethod]
    public async Task A_paused_desktop_gives_up_its_machine_and_comes_back_on_its_own_disk()
    {
        var (ada, team, environment) = await TeamAsync();
        var (task, desktop) = await ReadyOnAHostThatGivesUpAsync(ada, team.Id, environment);
        using var _ = desktop;
        var (machine, firstKey) = (desktop.Host, desktop.Key);
        await desktop.ExchangeAsync(false, 0, Desktop.Agent("The heading is Example Domain."));

        await PauseAsync(ada, task.Id);
        // The machine is gone, so it counts for nothing at the host, and the desktop is its disk.
        var paused = await RowAsync(task.Id);
        Assert.IsNull(paused.Host);
        Assert.AreEqual(machine, paused.Disk);
        Assert.IsFalse(Stack.Desktops.Made.ContainsKey(machine));
        Assert.IsTrue(Stack.Desktops.Disks.ContainsKey(machine));
        CollectionAssert.DoesNotContain(Stack.Desktops.Deleted.ToList(), machine);
        // No machine holds the key any more.
        await desktop.GetAsync("/api/desktop/setup").IsAsync(HttpStatusCode.Unauthorized);
        Assert.AreEqual(0, (await ada.GetAsync<TeamUsage>($"/api/teams/{team.Id}/usage")).RunningDesktops);
        // The disk is still kept, and still counted as the team's.
        using (var scope = Stack.Services.CreateScope())
        {
            var kept = KeptStorage.Of(paused, 64).Single();
            Assert.AreEqual(StorageKind.Disk, kept.Kind);
        }

        // Resuming makes a machine from that disk, under the same name, with a key of its own.
        Assert.AreEqual("starting", (await ada.PostAsync<TaskDto>($"/api/tasks/{task.Id}/start")).State);
        using var back = await Desktop.StartAsync(task.Id);
        var spec = Stack.Desktops.For(task.Id);
        Assert.IsTrue(spec.Kept);
        Assert.AreEqual(machine, spec.Name);
        Assert.IsNull(spec.Save);
        Assert.IsNull(spec.Archive);
        Assert.AreNotEqual(firstKey, back.Key);
        await back.ExchangeAsync();
        Assert.AreEqual("ready", (await TaskAsync(ada, task.Id)).State);
        var resumed = await RowAsync(task.Id);
        Assert.AreEqual(machine, resumed.Host);
        Assert.IsNull(resumed.Disk);
        Assert.IsFalse(Stack.Desktops.Disks.ContainsKey(machine));
        // The conversation is as it was.
        var conversation = await ada.GetAsync<ConversationDto>($"/api/tasks/{task.Id}/messages?after=0");
        Assert.IsTrue(conversation.Messages.Any(x => x.Text == "The heading is Example Domain."));

        // And it pauses and comes back again the same way.
        await PauseAsync(ada, task.Id);
        Assert.AreEqual(machine, (await RowAsync(task.Id)).Disk);
        await ada.PostAsync($"/api/tasks/{task.Id}/start").IsAsync(HttpStatusCode.OK);
        using var again = await Desktop.StartAsync(task.Id);
        await again.ExchangeAsync();
        Assert.AreEqual("ready", (await TaskAsync(ada, task.Id)).State);
    }

    [TestMethod]
    public async Task A_desktop_that_cannot_come_back_on_its_disk_keeps_the_disk()
    {
        var (ada, team, environment) = await TeamAsync();
        var (task, desktop) = await ReadyOnAHostThatGivesUpAsync(ada, team.Id, environment);
        var machine = desktop.Host;
        desktop.Dispose();
        await PauseAsync(ada, task.Id);

        // The host makes the machine from the disk and then cannot start it.
        Stack.Desktops.Broken[machine] = "Allocation failed. We do not have sufficient capacity for the requested VM size in this region.";
        await ada.PostAsync($"/api/tasks/{task.Id}/start").IsAsync(HttpStatusCode.OK);
        await Stack.Jobs.StartAsync(task.Id, CancellationToken.None);
        Assert.AreEqual("failed", (await TaskAsync(ada, task.Id)).State);
        var failed = await RowAsync(task.Id);
        // The half-made machine is given up again, never deleted: the disk is the desktop.
        Assert.IsNull(failed.Host);
        Assert.AreEqual(machine, failed.Disk);
        Assert.IsTrue(Stack.Desktops.Disks.ContainsKey(machine));
        Assert.IsFalse(Stack.Desktops.Made.ContainsKey(machine));
        CollectionAssert.DoesNotContain(Stack.Desktops.Deleted.ToList(), machine);

        // The host has no room at all for the next try: the disk is not touched.
        Stack.Desktops.Broken.TryRemove(machine, out var _);
        Stack.Desktops.Refuse[task.Id] = true;
        await ada.PostAsync($"/api/tasks/{task.Id}/start").IsAsync(HttpStatusCode.OK);
        await Stack.Jobs.StartAsync(task.Id, CancellationToken.None);
        Assert.AreEqual("failed", (await TaskAsync(ada, task.Id)).State);
        Assert.AreEqual(machine, (await RowAsync(task.Id)).Disk);
        Assert.IsTrue(Stack.Desktops.Disks.ContainsKey(machine));

        // Starting again makes a machine from the same disk.
        Stack.Desktops.Refuse.TryRemove(task.Id, out var _);
        await ada.PostAsync($"/api/tasks/{task.Id}/start").IsAsync(HttpStatusCode.OK);
        using var back = await Desktop.StartAsync(task.Id);
        Assert.IsTrue(Stack.Desktops.For(task.Id).Kept);
        await back.ExchangeAsync();
        Assert.AreEqual("ready", (await TaskAsync(ada, task.Id)).State);
        Assert.AreEqual(machine, (await RowAsync(task.Id)).Host);
    }

    [TestMethod]
    public async Task A_desktop_paused_on_its_disk_is_archived_and_deleted_like_any_other()
    {
        var (ada, team, environment) = await TeamAsync();
        var (task, desktop) = await ReadyOnAHostThatGivesUpAsync(ada, team.Id, environment);
        var machine = desktop.Host;
        desktop.Dispose();
        await PauseAsync(ada, task.Id);

        // After the team's days the disk is copied to an archive and deleted.
        await InStateForAsync(task.Id, TimeSpan.FromDays(8));
        await SweepAsync();
        Assert.AreEqual("archived", (await TaskAsync(ada, task.Id)).State);
        var archived = await RowAsync(task.Id);
        Assert.IsNull(archived.Disk);
        Assert.IsNull(archived.Host);
        Assert.AreEqual(machine, Stack.Desktops.Archives[archived.Archive!].Machine);
        Assert.IsFalse(Stack.Desktops.Disks.ContainsKey(machine));

        // It comes back from the archive, as any archived desktop does, and is paused on its disk again.
        await ada.PostAsync($"/api/tasks/{task.Id}/start").IsAsync(HttpStatusCode.OK);
        using var back = await Desktop.StartAsync(task.Id);
        Assert.AreEqual(archived.Archive, Stack.Desktops.For(task.Id).Archive);
        Assert.IsFalse(Stack.Desktops.For(task.Id).Kept);
        await back.ExchangeAsync();
        await PauseAsync(ada, task.Id);
        var again = (await RowAsync(task.Id)).Disk!;
        Assert.IsTrue(Stack.Desktops.Disks.ContainsKey(again));

        // Deleting the task deletes the disk it was kept on.
        await ada.DeleteAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NoContent);
        await Stack.Jobs.DeleteAsync(task.Id, CancellationToken.None);
        Assert.IsFalse(Stack.Desktops.Disks.ContainsKey(again));
        CollectionAssert.Contains(Stack.Desktops.Deleted.ToList(), again);
    }

    [TestMethod]
    public async Task A_task_that_started_on_a_spare_comes_back_on_the_disk_it_was_paused_on()
    {
        var (ada, team, environment) = await TeamAsync();
        await ada.PostAsync($"/api/teams/{team.Id}/spare", new WantSpare(null)).IsAsync(HttpStatusCode.NoContent);
        string spareId;
        using (var scope = Stack.Services.CreateScope())
        {
            spareId = (await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Spares.AsNoTracking().SingleAsync(x => x.TeamId == team.Id)).Id;
        }
        Stack.Desktops.GivesUp[spareId] = true;
        await Stack.Jobs.StartSpareAsync(spareId, CancellationToken.None);
        using var spare = Desktop.Of(spareId);
        await spare.GetAsync("/api/desktop/setup").IsAsync(HttpStatusCode.NotFound);
        var task = await StartAsync(ada, team.Id, environment);
        Stack.Desktops.GivesUp[task.Id] = true;
        await spare.ExchangeAsync();
        var machine = (await RowAsync(task.Id)).Host!;
        Assert.AreEqual(spare.Host, machine);

        await PauseAsync(ada, task.Id);
        Assert.AreEqual(machine, (await RowAsync(task.Id)).Disk);
        // The spare's key was the machine's, and the machine is gone.
        await spare.GetAsync("/api/desktop/setup").IsAsync(HttpStatusCode.Unauthorized);

        // The machine that takes the disk has the disk's name, which is the spare's and not the task's own.
        await ada.PostAsync($"/api/tasks/{task.Id}/start").IsAsync(HttpStatusCode.OK);
        using var back = await Desktop.StartAsync(task.Id);
        var spec = Stack.Desktops.For(task.Id);
        Assert.IsTrue(spec.Kept);
        Assert.AreEqual(machine, spec.Name);
        Assert.AreNotEqual(DesktopHosting.NameFor(task.Id), spec.Name);
        await back.ExchangeAsync();
        Assert.AreEqual("ready", (await TaskAsync(ada, task.Id)).State);
        Assert.AreEqual(task.Id, (await back.SetupAsync()).TaskId);
    }
}
