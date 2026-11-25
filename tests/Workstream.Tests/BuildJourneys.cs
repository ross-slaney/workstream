using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlOS.Fga.Models;
using Workstream.Api;
using Workstream.Api.Builds;
using Workstream.Api.Domain;

namespace Workstream.Tests;

/// <summary>
/// iPhone builds: a desktop asks for the build of what it has and sends the source, a Mac builds it and
/// hands back the app, and the team keeps one build a hash. Who may ask, who may build, what each team sees
/// of another's, what a failed build leaves, and what it all counts as.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class BuildJourneys
{
    static string NewHash() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    static readonly byte[] Source = [.. Enumerable.Range(0, 4000).Select(i => (byte)(i % 251))];
    static readonly byte[] App = [.. Enumerable.Range(0, 9000).Select(i => (byte)(i % 239))];

    /// <summary>A team with a task whose desktop is up.</summary>
    static async Task<(Person Ada, TeamDto Team, TaskDto Task, Desktop Desktop)> StartedAsync(Person? person = null, TeamDto? team = null)
    {
        if (person is null || team is null) (person, team) = await Person.WithTeamAsync();
        var environment = (await person.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{team.Id}/environments")).Data[0];
        var task = await person.PostAsync<TaskDto>($"/api/teams/{team.Id}/tasks", new StartTask(Guid.NewGuid().ToString("D"), environment.Id, null, "Run the app on a phone.", Guid.NewGuid().ToString("D")));
        return (person, team, task, await Desktop.StartAsync(task.Id));
    }

    static async Task<BuildDto> AskAsync(Desktop desktop, string hash, string? directory = "mobile", bool again = false) =>
        await Person.ReadAsync<BuildDto>(await desktop.PostAsync("/api/desktop/builds", new AskBuild("ios", hash, directory, again)));

    static Task<HttpResponseMessage> SendSourceAsync(Desktop desktop, string buildId, byte[]? source = null) =>
        desktop.PutAsync($"/api/desktop/builds/{buildId}/source", FakeMac.Bytes(source ?? Source));

    static async Task<BuildDto> ReadAsync(Desktop desktop, string buildId) => await Person.ReadAsync<BuildDto>(await desktop.GetAsync($"/api/desktop/builds/{buildId}"));

    [TestMethod]
    public async Task A_desktop_asks_and_sends_the_source_a_Mac_builds_it_and_the_desktop_takes_the_app()
    {
        var (ada, team, _, desktop) = await StartedAsync();
        using (ada)
        using (desktop)
        {
            var hash = NewHash();
            var asked = await AskAsync(desktop, hash);
            Assert.AreEqual("waiting", asked.State);
            Assert.AreEqual(hash, asked.Hash);
            // Nothing to take yet.
            await desktop.GetAsync($"/api/desktop/builds/{asked.Id}/app").IsAsync(HttpStatusCode.NotFound);

            // The source arrives, and the Mac is asked once.
            Assert.AreEqual("queued", (await Person.ReadAsync<BuildDto>(await SendSourceAsync(desktop, asked.Id))).State);
            await FakeMac.AskForAsync(asked.Id);
            CollectionAssert.Contains(Stack.Mac.Asked.ToList(), asked.Id);
            // Sent again, it is refused: a build has one source.
            await SendSourceAsync(desktop, asked.Id).IsAsync(HttpStatusCode.Conflict);

            // The Mac takes it, reads exactly what the desktop sent, and hands back the log and the app.
            var seen = await FakeMac.BuildAsync(asked.Id, App, "Compiling 412 files.\nBuilt Acme.app.", "com.acme.app");
            CollectionAssert.AreEqual(Source, seen);

            var done = await ReadAsync(desktop, asked.Id);
            Assert.AreEqual("done", done.State);
            Assert.AreEqual("com.acme.app", done.BundleId);
            Assert.AreEqual(App.Length, done.AppBytes);
            Assert.IsNotNull(done.EndedAt);
            var app = await (await desktop.GetAsync($"/api/desktop/builds/{asked.Id}/app")).Content.ReadAsByteArrayAsync();
            CollectionAssert.AreEqual(App, app);
            StringAssert.Contains(await (await desktop.GetAsync($"/api/desktop/builds/{asked.Id}/log")).Content.ReadAsStringAsync(), "Built Acme.app.");

            // The source was kept only to be built.
            using (var mac = FakeMac.Client()) await mac.GetAsync($"/api/builder/builds/{asked.Id}/source").IsAsync(HttpStatusCode.NotFound);

            // Asking again for the same hash is answered with the build that is there, and no Mac is asked.
            var before = Stack.Mac.Asked.Count;
            var again = await AskAsync(desktop, hash);
            Assert.AreEqual(asked.Id, again.Id);
            Assert.AreEqual("done", again.State);
            Assert.AreEqual(before, Stack.Mac.Asked.Count);

            // The Mac's time is the team's usage, and Workstream's cost.
            var usage = await ada.GetAsync<TeamUsage>($"/api/teams/{team.Id}/usage");
            Assert.IsTrue(usage.MacBuildMinutes >= 0);
            using var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
            var overview = await administrator.GetAsync<AdminOverview>("/api/admin/overview");
            Assert.IsTrue(overview.Machines.Mac!.Builds >= 1);
        }
    }

    [TestMethod]
    public async Task A_team_keeps_one_build_a_hash_and_another_team_sees_none_of_it()
    {
        var (ada, team, _, first) = await StartedAsync();
        var (_, _, _, second) = await StartedAsync(ada, team);
        var (grace, _, _, other) = await StartedAsync();
        using (ada)
        using (grace)
        using (first)
        using (second)
        using (other)
        {
            var hash = NewHash();
            var asked = await AskAsync(first, hash);
            // The team's second desktop, with the same native code, is given the same build while it waits.
            Assert.AreEqual(asked.Id, (await AskAsync(second, hash)).Id);
            await SendSourceAsync(first, asked.Id).IsAsync(HttpStatusCode.OK);
            await FakeMac.BuildAsync(asked.Id, App);
            Assert.AreEqual("done", (await AskAsync(second, hash)).State);
            CollectionAssert.AreEqual(App, await (await second.GetAsync($"/api/desktop/builds/{asked.Id}/app")).Content.ReadAsByteArrayAsync());

            // Another team's desktop: the build is not there for it, by id or by hash.
            await other.GetAsync($"/api/desktop/builds/{asked.Id}").IsAsync(HttpStatusCode.NotFound);
            await other.GetAsync($"/api/desktop/builds/{asked.Id}/app").IsAsync(HttpStatusCode.NotFound);
            await other.GetAsync($"/api/desktop/builds/{asked.Id}/log").IsAsync(HttpStatusCode.NotFound);
            await SendSourceAsync(other, asked.Id).IsAsync(HttpStatusCode.NotFound);
            var theirs = await AskAsync(other, hash);
            Assert.AreNotEqual(asked.Id, theirs.Id);
            Assert.AreEqual("waiting", theirs.State);
        }
    }

    [TestMethod]
    public async Task Only_a_desktop_asks_and_only_the_Mac_builds()
    {
        var (ada, team, _, desktop) = await StartedAsync();
        using (ada)
        using (desktop)
        {
            var asked = await AskAsync(desktop, NewHash());
            await SendSourceAsync(desktop, asked.Id).IsAsync(HttpStatusCode.OK);
            var ask = new AskBuild("ios", NewHash(), "mobile");

            // People: the team's administrator, a member, a stranger and the system administrator hold a
            // person's token, which is not a desktop's key and not the Mac's.
            var invitation = await ada.PostAsync<PendingInvitation>($"/api/teams/{team.Id}/invitations", new Invite(Person.NewEmail()));
            using var member = await Person.SignUpAsync("Grace Hopper", invitation.Email, Answers.TokenOf(invitation));
            using var stranger = await Person.SignUpAsync("Edsger Dijkstra");
            using var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
            foreach (var person in new[] { ada, member, stranger, administrator })
            {
                await person.PostAsync("/api/desktop/builds", ask).IsAsync(HttpStatusCode.Unauthorized);
                await person.GetAsync($"/api/desktop/builds/{asked.Id}").IsAsync(HttpStatusCode.Unauthorized);
                await person.GetAsync($"/api/desktop/builds/{asked.Id}/app").IsAsync(HttpStatusCode.Unauthorized);
                await person.PostAsync($"/api/builder/builds/{asked.Id}/take").IsAsync(HttpStatusCode.Unauthorized);
                await person.GetAsync($"/api/builder/builds/{asked.Id}/source").IsAsync(HttpStatusCode.Unauthorized);
            }

            // A desktop's key is not the Mac's: it cannot take a build, its own team's included.
            await desktop.PostAsync($"/api/builder/builds/{asked.Id}/take", new { }).IsAsync(HttpStatusCode.Unauthorized);
            await desktop.GetAsync($"/api/builder/builds/{asked.Id}/source").IsAsync(HttpStatusCode.Unauthorized);

            // The Mac's key is not a desktop's.
            using (var mac = FakeMac.Client())
            {
                await mac.PostAsJsonAsync("/api/desktop/builds", ask, Person.Json).IsAsync(HttpStatusCode.Unauthorized);
                await mac.GetAsync($"/api/desktop/builds/{asked.Id}").IsAsync(HttpStatusCode.Unauthorized);
                // A build that is not there.
                await mac.PostAsync($"/api/builder/builds/{Guid.NewGuid():D}/take", null).IsAsync(HttpStatusCode.NotFound);
            }

            // A wrong key, a key of the wrong shape, and nobody.
            using (var wrong = FakeMac.Client("wsb_" + new string('f', 64))) await wrong.PostAsync($"/api/builder/builds/{asked.Id}/take", null).IsAsync(HttpStatusCode.Unauthorized);
            using (var odd = FakeMac.Client("wsb_short")) await odd.PostAsync($"/api/builder/builds/{asked.Id}/take", null).IsAsync(HttpStatusCode.Unauthorized);
            using (var nobody = FakeMac.Client(null))
            {
                await nobody.PostAsync($"/api/builder/builds/{asked.Id}/take", null).IsAsync(HttpStatusCode.Unauthorized);
                await nobody.GetAsync($"/api/builder/builds/{asked.Id}/source").IsAsync(HttpStatusCode.Unauthorized);
                await nobody.PostAsJsonAsync("/api/desktop/builds", ask, Person.Json).IsAsync(HttpStatusCode.Unauthorized);
                await nobody.GetAsync($"/api/desktop/builds/{asked.Id}").IsAsync(HttpStatusCode.Unauthorized);
            }

            // The Mac is one account, granted one role on root, and nothing in any team by name.
            using var scope = Stack.Services.CreateScope();
            var grants = await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Set<SqlOSFgaGrant>().AsNoTracking()
                .Where(x => x.SubjectId == BuilderKeys.SubjectId).Select(x => new { x.ResourceId, x.RoleId }).ToListAsync();
            Assert.AreEqual(1, grants.Count);
            Assert.AreEqual("root", grants[0].ResourceId);
            Assert.AreEqual("app_builder", grants[0].RoleId);
        }
    }

    [TestMethod]
    public async Task A_build_that_fails_keeps_its_log_and_is_built_again_only_when_asked()
    {
        var (ada, _, _, desktop) = await StartedAsync();
        using (ada)
        using (desktop)
        {
            var hash = NewHash();
            var asked = await AskAsync(desktop, hash);
            await SendSourceAsync(desktop, asked.Id).IsAsync(HttpStatusCode.OK);
            using var mac = FakeMac.Client();
            // Before the Mac takes it there is no source for it to read.
            await mac.GetAsync($"/api/builder/builds/{asked.Id}/source").IsAsync(HttpStatusCode.NotFound);
            await mac.PostAsync($"/api/builder/builds/{asked.Id}/take", null).IsAsync(HttpStatusCode.OK);
            Assert.AreEqual("building", (await ReadAsync(desktop, asked.Id)).State);
            await mac.PutAsync($"/api/builder/builds/{asked.Id}/log", FakeMac.Bytes("error: no such module 'Acme'"u8.ToArray())).IsAsync(HttpStatusCode.NoContent);
            await mac.PostAsJsonAsync($"/api/builder/builds/{asked.Id}/end", new BuildEnd(false, null, "The app did not compile."), Person.Json).IsAsync(HttpStatusCode.NoContent);

            var failed = await ReadAsync(desktop, asked.Id);
            Assert.AreEqual("failed", failed.State);
            Assert.AreEqual("The app did not compile.", failed.Failure);
            StringAssert.Contains(await (await desktop.GetAsync($"/api/desktop/builds/{asked.Id}/log")).Content.ReadAsStringAsync(), "no such module");
            await desktop.GetAsync($"/api/desktop/builds/{asked.Id}/app").IsAsync(HttpStatusCode.NotFound);
            // Ended, it takes nothing more from the Mac.
            await mac.PutAsync($"/api/builder/builds/{asked.Id}/app", FakeMac.Bytes(App)).IsAsync(HttpStatusCode.BadRequest);
            await mac.PostAsJsonAsync($"/api/builder/builds/{asked.Id}/end", new BuildEnd(true, "com.acme.app", null), Person.Json).IsAsync(HttpStatusCode.BadRequest);

            // Asked for as before, it is still the build that failed. Asked for again, it starts over.
            Assert.AreEqual("failed", (await AskAsync(desktop, hash)).State);
            var again = await AskAsync(desktop, hash, again: true);
            Assert.AreEqual(asked.Id, again.Id);
            Assert.AreEqual("waiting", again.State);
            Assert.IsNull(again.Failure);
            await SendSourceAsync(desktop, asked.Id).IsAsync(HttpStatusCode.OK);
            await FakeMac.BuildAsync(asked.Id, App);
            Assert.AreEqual("done", (await ReadAsync(desktop, asked.Id)).State);

            // A build that says it worked and sent no app did not work.
            var empty = await AskAsync(desktop, NewHash());
            await SendSourceAsync(desktop, empty.Id).IsAsync(HttpStatusCode.OK);
            await mac.PostAsync($"/api/builder/builds/{empty.Id}/take", null).IsAsync(HttpStatusCode.OK);
            await mac.PostAsJsonAsync($"/api/builder/builds/{empty.Id}/end", new BuildEnd(true, "com.acme.app", null), Person.Json).IsAsync(HttpStatusCode.NoContent);
            Assert.AreEqual("failed", (await ReadAsync(desktop, empty.Id)).State);
        }
    }

    [TestMethod]
    public async Task What_a_desktop_asks_for_is_checked_and_a_team_has_a_limit_for_the_day()
    {
        var (ada, _, _, desktop) = await StartedAsync();
        using (ada)
        using (desktop)
        {
            await desktop.PostAsync("/api/desktop/builds", new AskBuild("android", NewHash(), "mobile")).IsAsync(HttpStatusCode.BadRequest, "ios");
            await desktop.PostAsync("/api/desktop/builds", new AskBuild("ios", "not-a-hash", "mobile")).IsAsync(HttpStatusCode.BadRequest, "64 hex");
            await desktop.PostAsync("/api/desktop/builds", new AskBuild("ios", NewHash(), "../other")).IsAsync(HttpStatusCode.BadRequest, "inside the source");

            var asked = await AskAsync(desktop, NewHash(), directory: null);
            // The Mac cannot take a build whose source has not arrived.
            using (var mac = FakeMac.Client()) await mac.PostAsync($"/api/builder/builds/{asked.Id}/take", null).IsAsync(HttpStatusCode.BadRequest);
            // The source is sent with its length, and is not empty.
            using (var chunked = new HttpRequestMessage(HttpMethod.Put, $"/api/desktop/builds/{asked.Id}/source") { Content = new StreamContent(new MemoryStream(Source)) })
            {
                chunked.Headers.TransferEncodingChunked = true;
                await desktop.SendAsync(chunked).IsAsync(HttpStatusCode.BadRequest, "length");
            }
            await SendSourceAsync(desktop, asked.Id, []).IsAsync(HttpStatusCode.BadRequest);

            // Six builds a day for a team in these journeys (Workstream:Builds:PerTeamADay); one is asked for already.
            for (var i = 0; i < 5; i++) Assert.AreEqual("waiting", (await AskAsync(desktop, NewHash())).State);
            await desktop.PostAsync("/api/desktop/builds", new AskBuild("ios", NewHash(), "mobile")).IsAsync(HttpStatusCode.PaymentRequired, "limit");
            // What is already there is still answered.
            Assert.AreEqual(asked.Id, (await AskAsync(desktop, asked.Hash, directory: null)).Id);
        }
    }

    [TestMethod]
    public async Task A_Mac_that_cannot_be_asked_or_never_finishes_fails_the_build_and_old_builds_are_removed()
    {
        var (ada, _, _, desktop) = await StartedAsync();
        using (ada)
        using (desktop)
        {
            // The Mac cannot be asked: the build fails with the reason.
            var refused = await AskAsync(desktop, NewHash());
            await SendSourceAsync(desktop, refused.Id).IsAsync(HttpStatusCode.OK);
            Stack.Mac.Refuse = "Nobody can start the Mac: the system administrator has not signed in with GitHub, and no token is set for builds.";
            try
            {
                await FakeMac.AskForAsync(refused.Id);
            }
            finally
            {
                Stack.Mac.Refuse = null;
            }
            var failed = await ReadAsync(desktop, refused.Id);
            Assert.AreEqual("failed", failed.State);
            StringAssert.Contains(failed.Failure, "signed in with GitHub");

            // A build the Mac took and never finished fails at the sweep, and the Mac's time until then counts.
            var late = await AskAsync(desktop, NewHash());
            await SendSourceAsync(desktop, late.Id).IsAsync(HttpStatusCode.OK);
            using (var mac = FakeMac.Client()) await mac.PostAsync($"/api/builder/builds/{late.Id}/take", null).IsAsync(HttpStatusCode.OK);
            var done = await AskAsync(desktop, NewHash());
            await SendSourceAsync(desktop, done.Id).IsAsync(HttpStatusCode.OK);
            await FakeMac.BuildAsync(done.Id, App);
            using (var scope = Stack.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
                var twoHoursAgo = DateTimeOffset.UtcNow.AddHours(-2);
                await db.Builds.Where(x => x.Id == late.Id).ExecuteUpdateAsync(x => x.SetProperty(b => b.AskedAt, twoHoursAgo).SetProperty(b => b.StartedAt, twoHoursAgo));
                var longAgo = DateTimeOffset.UtcNow.AddDays(-40);
                await db.Builds.Where(x => x.Id == done.Id).ExecuteUpdateAsync(x => x.SetProperty(b => b.UsedAt, longAgo));
            }
            await Stack.Builds.SweepAsync(CancellationToken.None);
            var swept = await ReadAsync(desktop, late.Id);
            Assert.AreEqual("failed", swept.State);
            StringAssert.Contains(swept.Failure, "did not finish");
            Assert.IsTrue(swept.MacSeconds >= 7000);
            // An app nobody has taken for longer than a deployment keeps it is gone, with its row.
            await desktop.GetAsync($"/api/desktop/builds/{done.Id}").IsAsync(HttpStatusCode.NotFound);
        }
    }

    [TestMethod]
    public async Task The_Mac_is_started_as_the_system_administrator_on_GitHub()
    {
        var (ada, _, _, desktop) = await StartedAsync();
        using (ada)
        using (desktop)
        using (var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword))
        {
            var asked = await AskAsync(desktop, NewHash());
            await SendSourceAsync(desktop, asked.Id).IsAsync(HttpStatusCode.OK);
            var options = Stack.Services.GetRequiredService<BuildOptions>();
            var repositoryBefore = options.Repository;
            using var scope = Stack.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            var build = await db.Builds.AsNoTracking().SingleAsync(x => x.Id == asked.Id);
            var mac = ActivatorUtilities.CreateInstance<GitHubMacBuilder>(scope.ServiceProvider);
            try
            {
                // The administrator never signed in with GitHub, and the deployment has no token for builds.
                Stack.GitHubTokens.Forget(administrator.UserId);
                var refused = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => mac.AskAsync(build, CancellationToken.None));
                StringAssert.Contains(refused.Message, "has not signed in with GitHub");

                // Signed in with GitHub, their token runs the workflow in Workstream's repository, for this build.
                var token = Stack.GitHub.NewAccount("Workstream administrator");
                var repository = Stack.GitHub.Give(token, "workstream");
                Stack.GitHubTokens.Give(administrator.UserId, token);
                options.Repository = repository.FullName;
                await mac.AskAsync(build, CancellationToken.None);
                Assert.IsTrue(repository.Runs.TryDequeue(out var run));
                Assert.AreEqual("ios-build.yml", run.Workflow);
                Assert.AreEqual("main", run.Ref);
                StringAssert.Contains(run.Inputs, asked.Id);
                StringAssert.Contains(run.Inputs, Stack.Origin.GetLeftPart(UriPartial.Authority));
                StringAssert.Contains(run.Inputs, "production");
            }
            finally
            {
                options.Repository = repositoryBefore;
                Stack.GitHubTokens.Forget(administrator.UserId);
            }
        }
    }

    [TestMethod]
    public async Task The_iPhone_simulator_needs_a_large_desktop_and_is_prepared_once()
    {
        var (ada, team) = await Person.WithTeamAsync();
        using (ada)
        {
            AddEnvironment Draft(string size) => new(Guid.NewGuid().ToString("D"), "Phones", ["chrome", "terminal", "android", "iphone"], size, null, null, null, null, []);
            await ada.PostAsync($"/api/teams/{team.Id}/environments", Draft("standard")).IsAsync(HttpStatusCode.BadRequest, "Large desktop");
            // Android alone runs on any size.
            await ada.PostAsync($"/api/teams/{team.Id}/environments", new AddEnvironment(Guid.NewGuid().ToString("D"), "Android", ["chrome", "android"], "standard", null, null, null, null, [])).IsAsync(HttpStatusCode.Created);
            var draft = Draft("large");
            var added = await ada.PostAsync<EnvironmentDto>($"/api/teams/{team.Id}/environments", draft);
            CollectionAssert.Contains(added.Apps.ToList(), "iphone");
            // With no repository and no script it still has something to prepare: the simulator, which takes a desktop twenty minutes.
            Assert.AreNotEqual("nothing", added.Setup.State);
            var apps = await ada.GetAsync<List<DesktopApp>>("/api/apps");
            Assert.IsTrue(apps.Any(x => x.Id == "iphone" && !x.Default));
            Assert.IsTrue(apps.Any(x => x.Id == "android" && !x.Default));
        }
    }
}
