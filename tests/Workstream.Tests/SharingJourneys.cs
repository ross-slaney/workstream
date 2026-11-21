using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.AuthServer.Models;
using SqlOS.Fga.Models;
using Workstream.Api;
using Workstream.Api.Domain;

namespace Workstream.Tests;

/// <summary>
/// Sharing a task with people outside its team: to watch it, to write to its agent too, or to use its desktop
/// as well, for an hour, a day, a week or until it is taken back. Each share is a grant on the task alone.
/// </summary>
[TestClass]
public sealed class SharingJourneys
{
    static async Task<(Person Person, TeamDto Team, TaskDto Task)> TaskAsync(string name = "Ada Lovelace")
    {
        var (person, team) = await Person.WithTeamAsync(name);
        var environment = (await person.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{team.Id}/environments")).Data.Single();
        var task = await person.PostAsync<TaskDto>($"/api/teams/{team.Id}/tasks",
            new StartTask(Guid.NewGuid().ToString("D"), environment.Id, null, "Open example.com and tell me the heading.", Guid.NewGuid().ToString("D")));
        return (person, team, task);
    }

    /// <summary>Someone who has proved their address, as everyone has in production by signing in with an emailed code.</summary>
    static async Task<Person> ProvedAsync(string name)
    {
        var person = await Person.SignUpAsync(name);
        using var scope = Stack.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
        foreach (var email in await db.Set<SqlOSUserEmail>().Where(x => x.UserId == person.UserId).ToListAsync())
        {
            email.IsVerified = true;
            email.VerifiedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        return person;
    }

    static Task<TaskShared> ShareAsync(Person by, string taskId, string email, string role, string? expiresIn = null) =>
        by.PostAsync<TaskShared>($"/api/tasks/{taskId}/access", new ShareTask(email, role, expiresIn));

    static string TokenOf(TaskShared shared) =>
        QueryHelpers.ParseQuery(new Uri(shared.Invitation?.Link ?? throw new InvalidOperationException("No invitation link.")).Query)["token"].ToString();

    static async Task<TaskAccess> AccessAsync(Person person, string taskId) =>
        (await person.GetAsync<ConversationDto>($"/api/tasks/{taskId}/messages?after=0")).Access;

    static async Task<IReadOnlyList<TaskDto>> SharedWithAsync(Person person) =>
        (await person.GetAsync<ListPage<TaskDto>>("/api/shared-tasks")).Data;

    [TestMethod]
    public async Task A_watcher_sees_the_task_and_its_screen_and_changes_nothing()
    {
        var (ada, team, task) = await TaskAsync();
        using var desktop = await Desktop.StartAsync(task.Id);
        using var grace = await ProvedAsync("Grace Hopper");

        var shared = await ShareAsync(ada, task.Id, grace.Email, "watcher", "day");
        Assert.IsNotNull(shared.Person);
        Assert.IsNull(shared.Invitation);
        Assert.AreEqual(grace.UserId, shared.Person.UserId);
        Assert.AreEqual("watcher", shared.Person.Role);
        Assert.IsTrue(shared.Person.Until > DateTime.UtcNow.AddHours(23) && shared.Person.Until < DateTime.UtcNow.AddHours(25));
        // Someone with an account is sent to the task itself, in their own workspace, and emailed the link.
        Assert.AreEqual($"{Stack.WebOrigin}/home?open={task.Id}", shared.Link);
        Assert.IsTrue(shared.Emailed);
        var email = Stack.Mail.Sent(grace.Email).Single();
        Assert.AreEqual("Ada Lovelace shared a task with you", email.Subject);
        StringAssert.Contains(email.TextBody, task.Title);
        StringAssert.Contains(email.TextBody, "You can watch its desktop and read its conversation.");
        StringAssert.Contains(email.TextBody, "It ends ");
        StringAssert.Contains(email.HtmlBody, $"href=\"{shared.Link}\"");

        // Grace sees it among what is shared with her, reads it and watches its desktop.
        Assert.AreEqual(task.Id, (await SharedWithAsync(grace)).Single().Id);
        await grace.GetAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.OK);
        Assert.AreEqual(new TaskAccess(false, false, false), await AccessAsync(grace, task.Id));
        Assert.AreEqual(HttpStatusCode.SwitchingProtocols, (await Watching.OpenAsync(task.Id, grace.Token)).Status);
        // What she sends the desktop is the picture's business only: her keys, clicks and clipboard never reach it.
        var through = await Watching.SendAsync(task.Id, grace.Token,
            Rfb.Handshake(), Rfb.SetEncodings(7, 0), Rfb.Key(), Rfb.Pointer(), Rfb.CutText("rm -rf ~"), Rfb.UpdateRequest());
        CollectionAssert.AreEqual(Rfb.Handshake().Concat(Rfb.SetEncodings(7, 0)).Concat(Rfb.UpdateRequest()).ToArray(), through);

        // She changes nothing: no writing, no stopping the agent, nothing of the task's own, and no sharing.
        await grace.PostAsync($"/api/tasks/{task.Id}/messages", new Say("Hello?", Guid.NewGuid().ToString("D"))).IsAsync(HttpStatusCode.Forbidden);
        await grace.PostAsync($"/api/tasks/{task.Id}/interrupt").IsAsync(HttpStatusCode.Forbidden);
        await grace.PostAsync($"/api/tasks/{task.Id}/stop").IsAsync(HttpStatusCode.Forbidden);
        await grace.PutAsync($"/api/tasks/{task.Id}", new RenameTask("Mine")).IsAsync(HttpStatusCode.Forbidden);
        await grace.DeleteAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.Forbidden);
        await grace.GetAsync($"/api/tasks/{task.Id}/access").IsAsync(HttpStatusCode.Forbidden);
        await grace.PostAsync($"/api/tasks/{task.Id}/access", new ShareTask(Person.NewEmail(), "driver", null)).IsAsync(HttpStatusCode.Forbidden);
        // The task is all she has: not the team, its environments or its other tasks.
        await grace.GetAsync($"/api/teams/{team.Id}").IsAsync(HttpStatusCode.NotFound);
        Assert.AreEqual(0, (await grace.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{team.Id}/environments")).Data.Count);
        var environment = (await ada.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{team.Id}/environments")).Data.Single();
        var other = await ada.PostAsync<TaskDto>($"/api/teams/{team.Id}/tasks", new StartTask(Guid.NewGuid().ToString("D"), environment.Id, null, "Another", Guid.NewGuid().ToString("D")));
        await grace.GetAsync($"/api/tasks/{other.Id}").IsAsync(HttpStatusCode.NotFound);
        Assert.AreEqual(task.Id, (await grace.GetAsync<ListPage<TaskDto>>($"/api/teams/{team.Id}/tasks")).Data.Single().Id);

        // The team sees who has it. Its own tasks are not "shared with" anyone on it.
        var access = await ada.GetAsync<TaskAccessList>($"/api/tasks/{task.Id}/access");
        Assert.AreEqual(team.Id, access.TeamId);
        Assert.AreEqual(team.Name, access.TeamName);
        Assert.AreEqual("watcher", access.People.Single(x => x.UserId == grace.UserId).Role);
        Assert.AreEqual(0, (await SharedWithAsync(ada)).Count);
        Assert.AreEqual(new TaskAccess(true, true, true), await AccessAsync(ada, task.Id));
        // The team's own keys still reach the desktop.
        var adas = await Watching.SendAsync(task.Id, ada.Token, Rfb.Handshake(), Rfb.Key(), Rfb.Pointer());
        CollectionAssert.AreEqual(Rfb.Handshake().Concat(Rfb.Key()).Concat(Rfb.Pointer()).ToArray(), adas);
    }

    [TestMethod]
    public async Task A_prompter_writes_to_the_agent_and_a_driver_uses_the_desktop_until_it_is_taken_back()
    {
        var (ada, _, task) = await TaskAsync();
        using var desktop = await Desktop.StartAsync(task.Id);
        using var lin = await ProvedAsync("Lin Clark");

        await ShareAsync(ada, task.Id, lin.Email, "prompter", "week");
        Assert.AreEqual(new TaskAccess(true, false, false), await AccessAsync(lin, task.Id));
        await lin.PostAsync($"/api/tasks/{task.Id}/messages", new Say("Try the other tab.", Guid.NewGuid().ToString("D"))).IsAsync(HttpStatusCode.Created);
        await lin.PostAsync($"/api/tasks/{task.Id}/interrupt").IsAsync(HttpStatusCode.NoContent);
        Assert.AreEqual("Try the other tab.", (await desktop.ExchangeAsync()).Prompts.Single(x => x.Text == "Try the other tab.").Text);
        CollectionAssert.AreEqual(Rfb.Handshake(), await Watching.SendAsync(task.Id, lin.Token, Rfb.Handshake(), Rfb.Key()));
        await lin.PostAsync($"/api/tasks/{task.Id}/stop").IsAsync(HttpStatusCode.Forbidden);

        // Sharing again changes the one share she has: now the mouse and keyboard too, until it is taken back.
        var driver = await ShareAsync(ada, task.Id, lin.Email, "driver", "never");
        Assert.IsNull(driver.Person!.Until);
        Assert.AreEqual(new TaskAccess(true, true, false), await AccessAsync(lin, task.Id));
        var keys = await Watching.SendAsync(task.Id, lin.Token, Rfb.Handshake(), Rfb.Key(), Rfb.CutText("hello"));
        CollectionAssert.AreEqual(Rfb.Handshake().Concat(Rfb.Key()).Concat(Rfb.CutText("hello")).ToArray(), keys);
        var held = (await ada.GetAsync<TaskAccessList>($"/api/tasks/{task.Id}/access")).People.Single();
        Assert.AreEqual("driver", held.Role);
        Assert.IsNull(held.Until);

        // Taken back: none of it is hers any more.
        await ada.DeleteAsync($"/api/tasks/{task.Id}/access/people/{lin.UserId}").IsAsync(HttpStatusCode.NoContent);
        await lin.GetAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NotFound);
        Assert.AreEqual(0, (await SharedWithAsync(lin)).Count);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Watching.OpenAsync(task.Id, lin.Token)).Status);
        Assert.AreEqual(0, (await ada.GetAsync<TaskAccessList>($"/api/tasks/{task.Id}/access")).People.Count);
        await ada.DeleteAsync($"/api/tasks/{task.Id}/access/people/{lin.UserId}").IsAsync(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task Someone_without_an_account_takes_the_invitation_by_signing_up_with_its_address()
    {
        var (ada, _, task) = await TaskAsync();
        using var stranger = await ProvedAsync("A Stranger");
        var address = Person.NewEmail();

        var shared = await ShareAsync(ada, task.Id, address, "prompter", "week");
        Assert.IsNull(shared.Person);
        Assert.AreEqual(address, shared.Invitation!.Email);
        Assert.AreEqual("prompter", shared.Invitation.Role);
        StringAssert.StartsWith(shared.Link, $"{Stack.WebOrigin}/share?token=");
        // The email to someone new carries the link that takes the invitation.
        Assert.IsTrue(shared.Emailed);
        var email = Stack.Mail.Sent(address).Single();
        StringAssert.Contains(email.TextBody, shared.Link);
        StringAssert.Contains(email.TextBody, "Create your account with this address");
        StringAssert.Contains(email.TextBody, "You can watch its desktop and write to its agent.");
        var waiting = (await ada.GetAsync<TaskAccessList>($"/api/tasks/{task.Id}/access")).Invitations.Single();
        Assert.AreEqual(address, waiting.Email);
        // The link is given once, to whoever made it.
        Assert.IsNull(waiting.Link);

        // The link is only for its address: someone else signed in is told whose it is.
        await stranger.PostAsync("/api/task-invitations/accept", new AcceptTaskInvitation(TokenOf(shared))).IsAsync(HttpStatusCode.Forbidden, address);
        await stranger.GetAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NotFound);

        // Signing up is the ordinary sign-up: a team of her own. Then the link grants what was shared.
        using var newcomer = await Person.SignUpAsync("Katherine Johnson", address);
        Assert.AreEqual(1, (await newcomer.GetAsync<ListPage<TeamDto>>("/api/teams")).Data.Count);
        var accepted = await newcomer.PostAsync<AcceptedTaskInvitation>("/api/task-invitations/accept", new AcceptTaskInvitation(TokenOf(shared)));
        Assert.AreEqual(task.Id, accepted.TaskId);
        Assert.AreEqual(task.Id, (await SharedWithAsync(newcomer)).Single().Id);
        Assert.AreEqual(new TaskAccess(true, false, false), await AccessAsync(newcomer, task.Id));
        var access = await ada.GetAsync<TaskAccessList>($"/api/tasks/{task.Id}/access");
        Assert.AreEqual(0, access.Invitations.Count);
        Assert.IsTrue(access.People.Single().Until > DateTime.UtcNow.AddDays(6));
        // A link works once.
        await newcomer.PostAsync("/api/task-invitations/accept", new AcceptTaskInvitation(TokenOf(shared))).IsAsync(HttpStatusCode.NotFound);

        // An account whose address nobody proved is not granted on its say-so: it gets the link like anyone new.
        using var eve = await Person.SignUpAsync("Eve Unproved");
        var unproved = await ShareAsync(ada, task.Id, eve.Email, "watcher");
        Assert.IsNull(unproved.Person);
        await eve.GetAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NotFound);
        await eve.PostAsync<AcceptedTaskInvitation>("/api/task-invitations/accept", new AcceptTaskInvitation(TokenOf(unproved)));
        Assert.AreEqual(new TaskAccess(false, false, false), await AccessAsync(eve, task.Id));
    }

    [TestMethod]
    public async Task A_withdrawn_invitation_and_an_ended_share_open_nothing()
    {
        var (ada, _, task) = await TaskAsync();
        using var desktop = await Desktop.StartAsync(task.Id);
        var address = Person.NewEmail();
        var invitation = await ShareAsync(ada, task.Id, address, "watcher", "hour");
        // An invitation's link lasts no longer than the access it would give.
        Assert.IsTrue(invitation.Invitation!.ExpiresAt <= DateTime.UtcNow.AddHours(1));
        await ada.DeleteAsync($"/api/tasks/{task.Id}/access/invitations/{invitation.Invitation.Id}").IsAsync(HttpStatusCode.NoContent);
        using var newcomer = await Person.SignUpAsync("Too Late", address);
        await newcomer.PostAsync("/api/task-invitations/accept", new AcceptTaskInvitation(TokenOf(invitation))).IsAsync(HttpStatusCode.NotFound);
        await ada.DeleteAsync($"/api/tasks/{task.Id}/access/invitations/{invitation.Invitation.Id}").IsAsync(HttpStatusCode.NotFound);

        // An hour's share, once the hour is over: the grant's end is SqlOS's, so every check and page agrees.
        using var grace = await ProvedAsync("Grace Hopper");
        await ShareAsync(ada, task.Id, grace.Email, "driver", "hour");
        Assert.AreEqual(HttpStatusCode.SwitchingProtocols, (await Watching.OpenAsync(task.Id, grace.Token)).Status);
        using (var scope = Stack.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            var node = WorkTask.ResourceIdFor(task.Id);
            var grant = await db.Set<SqlOSFgaGrant>().SingleAsync(g => g.SubjectId == grace.UserId && g.ResourceId == node);
            grant.EffectiveTo = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        await grace.GetAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NotFound);
        Assert.AreEqual(0, (await SharedWithAsync(grace)).Count);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Watching.OpenAsync(task.Id, grace.Token)).Status);
        Assert.AreEqual(0, (await ada.GetAsync<TaskAccessList>($"/api/tasks/{task.Id}/access")).People.Count);
        // Sharing again gives it back.
        await ShareAsync(ada, task.Id, grace.Email, "watcher", "day");
        await grace.GetAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task The_team_shares_its_tasks_and_nobody_else_does()
    {
        var (ada, team, task) = await TaskAsync();
        var member = await ada.PostAsync<PendingInvitation>($"/api/teams/{team.Id}/invitations", new Invite(Person.NewEmail()));
        using var ben = await Person.SignUpAsync("Ben Shneiderman", member.Email, Answers.TokenOf(member));
        using var stranger = await ProvedAsync("A Stranger");
        using var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
        using var desktop = await Desktop.StartAsync(task.Id);
        using var nobody = new HttpClient { BaseAddress = Stack.Origin };
        using var grace = await ProvedAsync("Grace Hopper");

        // A member who is not an administrator shares as well as the administrator does.
        await ben.PostAsync($"/api/tasks/{task.Id}/access", new ShareTask(grace.Email, "watcher", "day")).IsAsync(HttpStatusCode.Created);
        await ben.GetAsync($"/api/tasks/{task.Id}/access").IsAsync(HttpStatusCode.OK);
        await ben.DeleteAsync($"/api/tasks/{task.Id}/access/people/{grace.UserId}").IsAsync(HttpStatusCode.NoContent);
        // Nobody on the team needs a share, the sharer included.
        await ada.PostAsync($"/api/tasks/{task.Id}/access", new ShareTask(ben.Email, "watcher", null)).IsAsync(HttpStatusCode.Conflict);
        await ada.PostAsync($"/api/tasks/{task.Id}/access", new ShareTask(ada.Email, "driver", null)).IsAsync(HttpStatusCode.Conflict);
        // What is asked for has to be one of the choices.
        await ada.PostAsync($"/api/tasks/{task.Id}/access", new ShareTask(grace.Email, "owner", null)).IsAsync(HttpStatusCode.BadRequest);
        await ada.PostAsync($"/api/tasks/{task.Id}/access", new ShareTask(grace.Email, "watcher", "month")).IsAsync(HttpStatusCode.BadRequest);
        await ada.PostAsync($"/api/tasks/{task.Id}/access", new ShareTask("not an address", "watcher", null)).IsAsync(HttpStatusCode.BadRequest);

        // A stranger and the system administrator are told there is no such task.
        foreach (var outsider in new[] { stranger, administrator })
        {
            await outsider.GetAsync($"/api/tasks/{task.Id}/access").IsAsync(HttpStatusCode.NotFound);
            await outsider.PostAsync($"/api/tasks/{task.Id}/access", new ShareTask(outsider.Email, "driver", null)).IsAsync(HttpStatusCode.NotFound);
            await outsider.DeleteAsync($"/api/tasks/{task.Id}/access/people/{grace.UserId}").IsAsync(HttpStatusCode.NotFound);
            await outsider.DeleteAsync($"/api/tasks/{task.Id}/access/invitations/{Guid.NewGuid():D}").IsAsync(HttpStatusCode.NotFound);
            Assert.AreEqual(0, (await SharedWithAsync(outsider)).Count);
        }
        await stranger.PostAsync("/api/task-invitations/accept", new AcceptTaskInvitation("not-a-token")).IsAsync(HttpStatusCode.NotFound);

        // A desktop's key is not a person's token, and nobody is nobody.
        foreach (var path in new[] { "/api/shared-tasks", $"/api/tasks/{task.Id}/access" })
        {
            await desktop.GetAsync(path).IsAsync(HttpStatusCode.Unauthorized);
            await nobody.GetAsync(path).IsAsync(HttpStatusCode.Unauthorized);
        }
        await desktop.PostAsync($"/api/tasks/{task.Id}/access", new ShareTask(grace.Email, "driver", null)).IsAsync(HttpStatusCode.Unauthorized);
        await desktop.PostAsync("/api/task-invitations/accept", new AcceptTaskInvitation("x")).IsAsync(HttpStatusCode.Unauthorized);
        await nobody.PostAsJsonAsync("/api/task-invitations/accept", new AcceptTaskInvitation("x")).IsAsync(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task Deleting_a_shared_task_takes_it_from_everyone_it_was_shared_with()
    {
        var (ada, _, task) = await TaskAsync();
        using var grace = await ProvedAsync("Grace Hopper");
        await ShareAsync(ada, task.Id, grace.Email, "driver", null);
        var invitation = await ShareAsync(ada, task.Id, Person.NewEmail(), "watcher", "week");

        await ada.DeleteAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NoContent);
        // Gone from every list at once; the job then removes the rest.
        Assert.AreEqual(0, (await SharedWithAsync(grace)).Count);
        await Stack.Jobs.DeleteAsync(task.Id, CancellationToken.None);
        await grace.GetAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NotFound);
        using var scope = Stack.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
        Assert.IsFalse(await db.TaskInvitations.AnyAsync(x => x.TaskId == task.Id));
        Assert.IsFalse(await db.Set<SqlOSFgaGrant>().AnyAsync(g => g.ResourceId == WorkTask.ResourceIdFor(task.Id)));
        Assert.IsNotNull(invitation.Invitation);
    }
}
