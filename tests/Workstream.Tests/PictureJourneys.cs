using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.AuthServer.Models;
using Workstream.Api;
using Workstream.Api.Builds;
using Workstream.Api.Domain;

namespace Workstream.Tests;

/// <summary>
/// Pictures in a task's conversation: a desktop adds a picture of each of its surfaces when its agent hands
/// work over, and whoever may see the task sees them.
/// </summary>
[TestClass]
public sealed class PictureJourneys
{
    /// <summary>The start of a JPEG, and enough after it to be a file.</summary>
    static byte[] Jpeg(int length = 2048)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        (bytes[0], bytes[1], bytes[2], bytes[3]) = (0xFF, 0xD8, 0xFF, 0xE0);
        return bytes;
    }

    static HttpContent Picture(byte[] bytes) => new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("image/jpeg") } };

    static async Task<(Person Person, TeamDto Team, TaskDto Task)> TaskAsync(string name = "Ada Lovelace")
    {
        var (person, team) = await Person.WithTeamAsync(name);
        var environment = (await person.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{team.Id}/environments")).Data.Single();
        var task = await person.PostAsync<TaskDto>($"/api/teams/{team.Id}/tasks",
            new StartTask(Guid.NewGuid().ToString("D"), environment.Id, null, "Change the heading and show me.", Guid.NewGuid().ToString("D")));
        return (person, team, task);
    }

    static string NewKey() => Guid.NewGuid().ToString("D");

    [TestMethod]
    public async Task A_desktop_adds_a_picture_of_a_surface_and_the_team_sees_it_in_the_conversation()
    {
        var (ada, team, task) = await TaskAsync();
        using var desktop = await Desktop.StartAsync(task.Id);
        await desktop.ExchangeAsync();
        var (key, bytes) = (NewKey(), Jpeg());

        await desktop.PutAsync($"/api/desktop/pictures/{key}?caption={Uri.EscapeDataString("The iPhone")}", Picture(bytes)).IsAsync(HttpStatusCode.NoContent);
        // Sent again, as after a lost answer, it is kept once.
        await desktop.PutAsync($"/api/desktop/pictures/{key}?caption={Uri.EscapeDataString("The iPhone")}", Picture(bytes)).IsAsync(HttpStatusCode.NoContent);

        var conversation = await ada.GetAsync<ConversationDto>($"/api/tasks/{task.Id}/messages?after=0");
        var picture = conversation.Messages.Single(x => x.Kind == "picture");
        Assert.AreEqual("agent", picture.Author);
        Assert.AreEqual("The iPhone", picture.Text);

        using var read = await ada.GetAsync($"/api/tasks/{task.Id}/pictures/{picture.Id}");
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        Assert.AreEqual("image/jpeg", read.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("nosniff", read.Headers.GetValues("X-Content-Type-Options").Single());
        CollectionAssert.AreEqual(bytes, await read.Content.ReadAsByteArrayAsync());

        // A teammate sees it too.
        var invitation = await ada.PostAsync<PendingInvitation>($"/api/teams/{team.Id}/invitations", new Invite(Person.NewEmail()));
        var lin = await Person.SignUpAsync("Lin Clark", invitation.Email, Answers.TokenOf(invitation));
        await lin.GetAsync($"/api/tasks/{task.Id}/pictures/{picture.Id}").IsAsync(HttpStatusCode.OK);

        // A message that is not a picture has none, and neither has a picture of another task.
        var said = conversation.Messages.First(x => x.Kind == "text");
        await ada.GetAsync($"/api/tasks/{task.Id}/pictures/{said.Id}").IsAsync(HttpStatusCode.NotFound);
        var (_, _, other) = await TaskAsync("Katherine Johnson");
        await ada.GetAsync($"/api/tasks/{other.Id}/pictures/{picture.Id}").IsAsync(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task Only_those_who_may_see_the_task_see_its_pictures()
    {
        var (ada, _, task) = await TaskAsync();
        using var desktop = await Desktop.StartAsync(task.Id);
        await desktop.ExchangeAsync();
        await desktop.PutAsync($"/api/desktop/pictures/{NewKey()}?caption=The%20browser", Picture(Jpeg())).IsAsync(HttpStatusCode.NoContent);
        var picture = (await ada.GetAsync<ConversationDto>($"/api/tasks/{task.Id}/messages?after=0")).Messages.Single(x => x.Kind == "picture");
        var path = $"/api/tasks/{task.Id}/pictures/{picture.Id}";

        // A stranger, and the system administrator, who has nothing inside a team.
        var stranger = await Person.SignUpAsync("Grace Hopper");
        await stranger.GetAsync(path).IsAsync(HttpStatusCode.NotFound);
        var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
        await administrator.GetAsync(path).IsAsync(HttpStatusCode.NotFound);
        // Nobody, and a desktop: its key is not a person's token, its own task's pictures included.
        using var anonymous = new HttpClient { BaseAddress = Stack.Origin };
        await anonymous.GetAsync(path).IsAsync(HttpStatusCode.Unauthorized);
        await desktop.GetAsync(path).IsAsync(HttpStatusCode.Unauthorized);

        // Someone the task is shared with to watch sees its pictures, until the share is taken back.
        var watcher = await Person.SignUpAsync("Hedy Lamarr");
        using (var scope = Stack.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            foreach (var email in await db.Set<SqlOSUserEmail>().Where(x => x.UserId == watcher.UserId).ToListAsync())
            {
                email.IsVerified = true;
                email.VerifiedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
        }
        await ada.PostAsync($"/api/tasks/{task.Id}/access", new ShareTask(watcher.Email, "watcher", "day")).IsAsync(HttpStatusCode.Created);
        await watcher.GetAsync(path).IsAsync(HttpStatusCode.OK);
        await ada.DeleteAsync($"/api/tasks/{task.Id}/access/people/{watcher.UserId}").IsAsync(HttpStatusCode.NoContent);
        await watcher.GetAsync(path).IsAsync(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task Only_a_desktop_adds_pictures_and_only_to_its_own_task()
    {
        var (ada, team, task) = await TaskAsync();
        var environment = (await ada.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{team.Id}/environments")).Data.Single();
        var second = await ada.PostAsync<TaskDto>($"/api/teams/{team.Id}/tasks", new StartTask(Guid.NewGuid().ToString("D"), environment.Id, null, "Another task.", Guid.NewGuid().ToString("D")));
        using var desktop = await Desktop.StartAsync(task.Id);
        using var other = await Desktop.StartAsync(second.Id);
        await desktop.ExchangeAsync();
        await other.ExchangeAsync();
        var path = $"/api/desktop/pictures/{NewKey()}?caption=The%20browser";

        // A person's token is not a desktop's key, whoever the person is; and nobody adds one.
        await ada.PutAsync(path, Picture(Jpeg())).IsAsync(HttpStatusCode.Unauthorized);
        var stranger = await Person.SignUpAsync("Grace Hopper");
        await stranger.PutAsync(path, Picture(Jpeg())).IsAsync(HttpStatusCode.Unauthorized);
        var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
        await administrator.PutAsync(path, Picture(Jpeg())).IsAsync(HttpStatusCode.Unauthorized);
        using var anonymous = new HttpClient { BaseAddress = Stack.Origin };
        await anonymous.PutAsync(path, Picture(Jpeg())).IsAsync(HttpStatusCode.Unauthorized);

        // The route names no task: a desktop's picture goes to the task its key is for.
        await other.PutAsync(path, Picture(Jpeg())).IsAsync(HttpStatusCode.NoContent);
        Assert.AreEqual(0, (await ada.GetAsync<ConversationDto>($"/api/tasks/{task.Id}/messages?after=0")).Messages.Count(x => x.Kind == "picture"));
        Assert.AreEqual(1, (await ada.GetAsync<ConversationDto>($"/api/tasks/{second.Id}/messages?after=0")).Messages.Count(x => x.Kind == "picture"));
    }

    [TestMethod]
    public async Task What_is_not_a_small_JPEG_with_a_caption_is_refused()
    {
        var (ada, _, task) = await TaskAsync();
        using var desktop = await Desktop.StartAsync(task.Id);
        await desktop.ExchangeAsync();

        var page = "<script>alert(1)</script>"u8.ToArray();
        await desktop.PutAsync($"/api/desktop/pictures/{NewKey()}?caption=The%20browser", Picture(page)).IsAsync(HttpStatusCode.BadRequest);
        await desktop.PutAsync($"/api/desktop/pictures/{NewKey()}?caption=The%20browser", Picture(Jpeg(TaskMessage.MaxPictureBytes + 1))).IsAsync(HttpStatusCode.BadRequest);
        await desktop.PutAsync($"/api/desktop/pictures/{NewKey()}", Picture(Jpeg())).IsAsync(HttpStatusCode.BadRequest);
        await desktop.PutAsync($"/api/desktop/pictures/{NewKey()}?caption={new string('a', TaskMessage.MaxCaptionLength + 1)}", Picture(Jpeg())).IsAsync(HttpStatusCode.BadRequest);
        await desktop.PutAsync("/api/desktop/pictures/..%2F..%2Fsomewhere?caption=The%20browser", Picture(Jpeg())).IsAsync(HttpStatusCode.BadRequest);
        // Sent without its length.
        using (var chunked = new HttpRequestMessage(HttpMethod.Put, $"/api/desktop/pictures/{NewKey()}?caption=The%20browser") { Content = new StreamContent(new MemoryStream(Jpeg())) })
        {
            chunked.Headers.TransferEncodingChunked = true;
            await desktop.SendAsync(chunked).IsAsync(HttpStatusCode.BadRequest);
        }
        Assert.AreEqual(0, (await ada.GetAsync<ConversationDto>($"/api/tasks/{task.Id}/messages?after=0")).Messages.Count(x => x.Kind == "picture"));
    }

    [TestMethod]
    public async Task A_picture_added_after_the_report_does_not_stop_the_email_that_the_agent_finished()
    {
        var (ada, _, task) = await TaskAsync();
        using var desktop = await Desktop.StartAsync(task.Id);
        var reply = await desktop.ExchangeAsync(working: true);
        await desktop.ExchangeAsync(working: false, reply.Prompts.Max(x => x.Id), Desktop.Agent("The heading is changed in the browser."));
        await desktop.PutAsync($"/api/desktop/pictures/{NewKey()}?caption=The%20browser", Picture(Jpeg())).IsAsync(HttpStatusCode.NoContent);

        Assert.AreEqual(1, await Stack.Emails.SendAsync(task.Id, CancellationToken.None));
        StringAssert.Contains(Stack.Mail.Sent(ada.Email).Single().TextBody, "The heading is changed in the browser.");
    }

    [TestMethod]
    public async Task A_deleted_tasks_pictures_are_deleted_with_it()
    {
        var (ada, _, task) = await TaskAsync();
        using var desktop = await Desktop.StartAsync(task.Id);
        await desktop.ExchangeAsync();
        var key = NewKey();
        await desktop.PutAsync($"/api/desktop/pictures/{key}?caption=The%20browser", Picture(Jpeg())).IsAsync(HttpStatusCode.NoContent);
        var files = Stack.Services.GetRequiredService<IBuildFiles>();
        var kept = await files.ReadAsync(TaskMessage.PictureFile(task.Id, key), CancellationToken.None);
        Assert.IsNotNull(kept);
        await kept.Value.Content.DisposeAsync();

        await ada.DeleteAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NoContent);
        await Stack.Jobs.DeleteAsync(task.Id, CancellationToken.None);
        Assert.IsNull(await files.ReadAsync(TaskMessage.PictureFile(task.Id, key), CancellationToken.None));
    }
}
