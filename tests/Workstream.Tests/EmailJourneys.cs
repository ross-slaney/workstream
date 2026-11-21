using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlOS.Email.Interfaces;
using Workstream.Api;
using Workstream.Api.Email;

namespace Workstream.Tests;

/// <summary>
/// The email a task's people get when the agent's turn ends: who gets it, who does not, and when none is sent.
/// The mail provider is played (<see cref="TestMail"/>); every message is rendered by SqlOS from the stored
/// template, so these read what a person would.
/// </summary>
[TestClass]
public sealed class EmailJourneys
{
    static async Task<(Person Person, TeamDto Team, string EnvironmentId)> TeamAsync(string name = "Ada Lovelace")
    {
        var (person, team) = await Person.WithTeamAsync(name);
        var environment = (await person.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{team.Id}/environments")).Data.Single();
        return (person, team, environment.Id);
    }

    static Task<TaskDto> StartAsync(Person person, string teamId, string environmentId, string prompt = "Open example.com and tell me the heading.") =>
        person.PostAsync<TaskDto>($"/api/teams/{teamId}/tasks", new StartTask(Guid.NewGuid().ToString("D"), environmentId, null, prompt, Guid.NewGuid().ToString("D")));

    static async Task<Person> JoinAsync(Person admin, string teamId, string name)
    {
        var invitation = await admin.PostAsync<PendingInvitation>($"/api/teams/{teamId}/invitations", new Invite(Person.NewEmail()));
        return await Person.SignUpAsync(name, invitation.Email, Answers.TokenOf(invitation));
    }

    /// <summary>One turn of the agent: it reads what people wrote and works, then stops with these messages. Answers where it is in the conversation.</summary>
    static async Task<long> TurnAsync(Desktop desktop, long after, params DesktopMessage[] last)
    {
        var reply = await desktop.ExchangeAsync(working: true, after);
        after = reply.Prompts.Select(x => x.Id).DefaultIfEmpty(after).Max();
        await desktop.ExchangeAsync(working: false, after, last);
        return after;
    }

    /// <summary>The job as the stack makes it, with another clock or another provider.</summary>
    static TaskEmails Emails(TimeProvider clock, ISqlOSEmailSender? sender = null) => new(
        Stack.Services.GetRequiredService<IServiceScopeFactory>(), sender ?? Stack.Mail, Stack.Services.GetRequiredService<WorkstreamApp>(),
        clock, Stack.Services.GetRequiredService<ILogger<TaskEmails>>());

    [TestMethod]
    public async Task A_finished_turn_emails_whoever_started_the_task_and_whoever_wrote_in_it()
    {
        var (ada, team, environment) = await TeamAsync();
        using var lin = await JoinAsync(ada, team.Id, "Lin Clark");
        using var ben = await JoinAsync(ada, team.Id, "Ben Shneiderman");
        using var stranger = await Person.SignUpAsync("A Stranger");
        var task = await StartAsync(ada, team.Id, environment);
        await lin.PostAsync($"/api/tasks/{task.Id}/messages", new Say("Check the About page too.", Guid.NewGuid().ToString("D"))).IsAsync(HttpStatusCode.Created);
        using var desktop = await Desktop.StartAsync(task.Id);
        await desktop.ExchangeAsync(working: true);
        await TurnAsync(desktop, 0, Desktop.Agent("The heading is Example Domain.\nThe About page says the same."));

        Assert.AreEqual(2, await Stack.Emails.SendAsync(task.Id, CancellationToken.None));
        foreach (var person in new[] { ada, lin })
        {
            var email = Stack.Mail.Sent(person.Email).Single();
            Assert.AreEqual($"Finished: {task.Title}", email.Subject);
            StringAssert.Contains(email.TextBody, "The heading is Example Domain.");
            // The link opens the task in the team's workspace.
            StringAssert.Contains(email.TextBody, $"{Stack.WebOrigin}/t/{team.Id}?open={task.Id}");
            StringAssert.Contains(email.HtmlBody, $"href=\"{Stack.WebOrigin}/t/{team.Id}?open={task.Id}\"");
            StringAssert.Contains(email.HtmlBody, $"{Stack.WebOrigin}/t/{team.Id}/team");
            StringAssert.Contains(email.HtmlBody, "Ada Lovelace&#39;s team");
        }
        // A teammate who neither started it nor wrote in it, and someone of another team, hear nothing.
        Assert.AreEqual(0, Stack.Mail.Sent(ben.Email).Count);
        Assert.AreEqual(0, Stack.Mail.Sent(stranger.Email).Count);

        // The job run again sends nothing more.
        Assert.AreEqual(0, await Stack.Emails.SendAsync(task.Id, CancellationToken.None));
        Assert.AreEqual(1, Stack.Mail.Sent(ada.Email).Count);
    }

    [TestMethod]
    public async Task A_turn_that_ends_on_a_question_says_the_task_needs_you()
    {
        var (ada, team, environment) = await TeamAsync();
        var task = await StartAsync(ada, team.Id, environment);
        using var desktop = await Desktop.StartAsync(task.Id);
        await desktop.ExchangeAsync(working: true);
        await TurnAsync(desktop, 0, Desktop.Agent("The page has two headings. Which one do you mean, the **first or the second?**"));

        Assert.AreEqual(1, await Stack.Emails.SendAsync(task.Id, CancellationToken.None));
        var email = Stack.Mail.Sent(ada.Email).Single();
        Assert.AreEqual($"Needs you: {task.Title}", email.Subject);
        StringAssert.Contains(email.TextBody, "waiting for an answer");
        StringAssert.Contains(email.TextBody, "Which one do you mean");
    }

    [TestMethod]
    public async Task A_task_emails_at_most_once_in_ten_minutes()
    {
        var (ada, team, environment) = await TeamAsync();
        var task = await StartAsync(ada, team.Id, environment);
        using var desktop = await Desktop.StartAsync(task.Id);
        await desktop.ExchangeAsync(working: true);
        var after = await TurnAsync(desktop, 0, Desktop.Agent("Done."));
        Assert.AreEqual(1, await Stack.Emails.SendAsync(task.Id, CancellationToken.None));

        // Another turn a moment later: no second email.
        await ada.PostAsync($"/api/tasks/{task.Id}/messages", new Say("Now the footer.", Guid.NewGuid().ToString("D"))).IsAsync(HttpStatusCode.Created);
        await TurnAsync(desktop, after, Desktop.Agent("The footer is fixed."));
        Assert.AreEqual(0, await Stack.Emails.SendAsync(task.Id, CancellationToken.None));
        // Ten minutes on, the same turn is worth one.
        Assert.AreEqual(1, await Emails(new Later(TimeSpan.FromMinutes(11))).SendAsync(task.Id, CancellationToken.None));
        Assert.AreEqual(2, Stack.Mail.Sent(ada.Email).Count);
        StringAssert.Contains(Stack.Mail.Sent(ada.Email)[^1].TextBody, "The footer is fixed.");
    }

    [TestMethod]
    public async Task Someone_who_has_the_desktop_on_screen_is_not_emailed_about_it()
    {
        var (ada, team, environment) = await TeamAsync();
        var task = await StartAsync(ada, team.Id, environment);
        using var desktop = await Desktop.StartAsync(task.Id);
        await desktop.ExchangeAsync(working: true);
        Assert.AreEqual(HttpStatusCode.SwitchingProtocols, (await Watching.OpenAsync(task.Id, ada.Token)).Status);
        await TurnAsync(desktop, 0, Desktop.Agent("Done."));

        Assert.AreEqual(0, await Stack.Emails.SendAsync(task.Id, CancellationToken.None));
        Assert.AreEqual(0, Stack.Mail.Sent(ada.Email).Count);
        // Three minutes after she last had it on screen, she is away from it.
        Assert.AreEqual(1, await Emails(new Later(TimeSpan.FromMinutes(3))).SendAsync(task.Id, CancellationToken.None));
    }

    [TestMethod]
    public async Task Someone_who_turned_them_off_or_left_the_team_is_not_emailed()
    {
        var (ada, team, environment) = await TeamAsync();
        using var lin = await JoinAsync(ada, team.Id, "Lin Clark");
        using var ben = await JoinAsync(ada, team.Id, "Ben Shneiderman");
        var task = await StartAsync(ada, team.Id, environment);
        await lin.PostAsync($"/api/tasks/{task.Id}/messages", new Say("Me too.", Guid.NewGuid().ToString("D"))).IsAsync(HttpStatusCode.Created);
        await ben.PostAsync($"/api/tasks/{task.Id}/messages", new Say("And me.", Guid.NewGuid().ToString("D"))).IsAsync(HttpStatusCode.Created);
        Assert.IsFalse((await lin.PutAsync<MySettings>("/api/me/settings", new MySettings(TaskEmails: false))).TaskEmails);
        await ada.DeleteAsync($"/api/teams/{team.Id}/people/{ben.UserId}").IsAsync(HttpStatusCode.NoContent);

        using var desktop = await Desktop.StartAsync(task.Id);
        await desktop.ExchangeAsync(working: true);
        await TurnAsync(desktop, 0, Desktop.Agent("Done."));
        Assert.AreEqual(1, await Stack.Emails.SendAsync(task.Id, CancellationToken.None));
        Assert.AreEqual(1, Stack.Mail.Sent(ada.Email).Count);
        Assert.AreEqual(0, Stack.Mail.Sent(lin.Email).Count);
        Assert.AreEqual(0, Stack.Mail.Sent(ben.Email).Count);
    }

    [TestMethod]
    public async Task Nothing_is_sent_without_email_set_up_or_unless_the_turn_ended_on_the_agents_unanswered_words()
    {
        var (ada, team, environment) = await TeamAsync();
        var task = await StartAsync(ada, team.Id, environment);
        using var desktop = await Desktop.StartAsync(task.Id);
        await desktop.ExchangeAsync(working: true);
        var after = await TurnAsync(desktop, 0, Desktop.Agent("Done."));

        // Where no provider is set up (development, staging), the job does nothing.
        Assert.AreEqual(0, await Emails(TimeProvider.System, new NoMail()).SendAsync(task.Id, CancellationToken.None));
        // Someone already answered.
        await ada.PostAsync($"/api/tasks/{task.Id}/messages", new Say("Thanks, one more thing.", Guid.NewGuid().ToString("D"))).IsAsync(HttpStatusCode.Created);
        Assert.AreEqual(0, await Stack.Emails.SendAsync(task.Id, CancellationToken.None));
        // The agent went back to work.
        var reply = await desktop.ExchangeAsync(working: true, after);
        after = reply.Prompts.Single().Id;
        Assert.AreEqual(0, await Stack.Emails.SendAsync(task.Id, CancellationToken.None));
        // Its last word was a line about what it did, not something it said.
        await desktop.ExchangeAsync(working: false, after, Desktop.Agent("Ran npm test", "activity"));
        Assert.AreEqual(0, await Stack.Emails.SendAsync(task.Id, CancellationToken.None));
        // A stopped desktop's task says nothing more.
        await ada.PostAsync($"/api/tasks/{task.Id}/stop").IsAsync(HttpStatusCode.OK);
        Assert.AreEqual(0, await Stack.Emails.SendAsync(task.Id, CancellationToken.None));
        Assert.AreEqual(0, Stack.Mail.Sent(ada.Email).Count);
    }

    [TestMethod]
    public async Task A_persons_email_setting_is_their_own_and_only_a_person_reaches_it()
    {
        var (ada, team, environment) = await TeamAsync();
        using var lin = await JoinAsync(ada, team.Id, "Lin Clark");
        // On until they turn it off.
        Assert.IsTrue((await ada.GetAsync<MySettings>("/api/me/settings")).TaskEmails);
        Assert.IsFalse((await ada.PutAsync<MySettings>("/api/me/settings", new MySettings(TaskEmails: false))).TaskEmails);
        Assert.IsFalse((await ada.GetAsync<MySettings>("/api/me/settings")).TaskEmails);
        Assert.IsTrue((await ada.PutAsync<MySettings>("/api/me/settings", new MySettings(TaskEmails: true))).TaskEmails);
        await ada.PutAsync("/api/me/settings", new MySettings(TaskEmails: false)).IsAsync(HttpStatusCode.OK);
        // Each person has their own: a teammate's, and the administrator's of the whole system, are untouched.
        Assert.IsTrue((await lin.GetAsync<MySettings>("/api/me/settings")).TaskEmails);
        using var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
        Assert.IsTrue((await administrator.GetAsync<MySettings>("/api/me/settings")).TaskEmails);

        // A desktop's key is not a person, and nobody is nobody.
        var task = await StartAsync(ada, team.Id, environment);
        using var desktop = await Desktop.StartAsync(task.Id);
        await desktop.GetAsync("/api/me/settings").IsAsync(HttpStatusCode.Unauthorized);
        using var anonymous = new HttpClient { BaseAddress = Stack.Origin };
        await anonymous.GetAsync("/api/me/settings").IsAsync(HttpStatusCode.Unauthorized);
        await anonymous.PutAsJsonAsync("/api/me/settings", new MySettings(TaskEmails: true)).IsAsync(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public void The_emails_are_stored_with_every_word_from_the_locale_file()
    {
        var templates = TaskEmailTemplates.All;
        CollectionAssert.AreEquivalent(new[] { TaskEmailTemplates.Finished, TaskEmailTemplates.NeedsYou }, templates.Select(x => x.Key).ToArray());
        foreach (var template in templates)
        {
            StringAssert.Contains(template.Html, EmailWords.Html("layout.why"));
            StringAssert.Contains(template.Text, "{message}");
            StringAssert.Contains(template.Html, "href=\"{link}\"");
        }
    }
}
