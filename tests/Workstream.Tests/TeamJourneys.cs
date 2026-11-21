using System.Net;
using Workstream.Api;

namespace Workstream.Tests;

/// <summary>Teams and the people on them: who gets one, who may see it, and who may change who is on it.</summary>
[TestClass]
public sealed class TeamJourneys
{
    [TestMethod]
    public async Task Signing_up_gives_a_team_of_your_own_with_a_standard_environment()
    {
        var (ada, team) = await Person.WithTeamAsync("Ada Lovelace");
        Assert.AreEqual("Ada Lovelace's team", team.Name);
        Assert.IsTrue(team.Admin);

        var environments = await ada.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{team.Id}/environments");
        var standard = environments.Data.Single();
        Assert.AreEqual("Standard desktop", standard.Name);
        CollectionAssert.Contains(standard.Apps.ToList(), "chrome");

        var people = await ada.GetAsync<List<TeamPerson>>($"/api/teams/{team.Id}/people");
        Assert.AreEqual(ada.UserId, people.Single().UserId);
        Assert.IsTrue(people.Single().Admin);
    }

    [TestMethod]
    public async Task A_stranger_can_neither_see_nor_change_a_team()
    {
        var (_, team) = await Person.WithTeamAsync();
        var stranger = await Person.SignUpAsync("Grace Hopper");

        await stranger.GetAsync($"/api/teams/{team.Id}").IsAsync(HttpStatusCode.NotFound);
        await stranger.PutAsync($"/api/teams/{team.Id}", new WriteTeam("Mine now")).IsAsync(HttpStatusCode.NotFound);
        await stranger.GetAsync($"/api/teams/{team.Id}/people").IsAsync(HttpStatusCode.NotFound);
        await stranger.GetAsync($"/api/teams/{team.Id}/usage").IsAsync(HttpStatusCode.NotFound);
        await stranger.PostAsync($"/api/teams/{team.Id}/invitations", new Invite(Person.NewEmail())).IsAsync(HttpStatusCode.NotFound);
        // Their own list has their own team and no other.
        Assert.IsFalse((await stranger.GetAsync<ListPage<TeamDto>>("/api/teams")).Data.Any(x => x.Id == team.Id));
    }

    [TestMethod]
    public async Task Nobody_signed_out_reaches_the_api()
    {
        using var anonymous = new HttpClient { BaseAddress = Stack.Origin };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/teams");
        request.Headers.TryAddWithoutValidation("Origin", Stack.WebOrigin);
        using var refused = await anonymous.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Unauthorized, refused.StatusCode);
        // The web app reads the refusal only if it carries the CORS headers: it is how it knows to sign in again.
        Assert.AreEqual(Stack.WebOrigin, refused.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [TestMethod]
    public async Task An_invited_person_joins_the_team_and_gets_none_of_their_own()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var address = Person.NewEmail();
        var invitation = await ada.PostAsync<PendingInvitation>($"/api/teams/{team.Id}/invitations", new Invite(address));
        Assert.AreEqual(address, invitation.Email);
        // No email is set up here, so the administrator passes the link on themselves.
        Assert.IsFalse(invitation.Emailed);
        Assert.AreEqual(1, (await ada.GetAsync<List<PendingInvitation>>($"/api/teams/{team.Id}/invitations")).Count);

        var lin = await Person.SignUpAsync("Lin Clark", address, Answers.TokenOf(invitation));
        var teams = (await lin.GetAsync<ListPage<TeamDto>>("/api/teams")).Data;
        Assert.AreEqual(team.Id, teams.Single().Id);
        Assert.IsFalse(teams.Single().Admin);
        Assert.AreEqual(2, (await ada.GetAsync<List<TeamPerson>>($"/api/teams/{team.Id}/people")).Count);
        Assert.AreEqual(0, (await ada.GetAsync<List<PendingInvitation>>($"/api/teams/{team.Id}/invitations")).Count);

        // A member sees and uses everything in the team, and changes nothing about who is on it.
        Assert.AreEqual(1, (await lin.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{team.Id}/environments")).Data.Count);
        await lin.GetAsync($"/api/teams/{team.Id}/usage").IsAsync(HttpStatusCode.OK);
        await lin.PutAsync($"/api/teams/{team.Id}", new WriteTeam("Lin's now")).IsAsync(HttpStatusCode.Forbidden);
        await lin.PostAsync($"/api/teams/{team.Id}/invitations", new Invite(Person.NewEmail())).IsAsync(HttpStatusCode.Forbidden);
        await lin.GetAsync($"/api/teams/{team.Id}/invitations").IsAsync(HttpStatusCode.Forbidden);
        await lin.PutAsync($"/api/teams/{team.Id}/people/{ada.UserId}/admin", new SetAdmin(false)).IsAsync(HttpStatusCode.Forbidden);
        await lin.DeleteAsync($"/api/teams/{team.Id}/people/{ada.UserId}").IsAsync(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task An_invitation_is_for_one_address_once_and_can_be_withdrawn()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var address = Person.NewEmail();
        var invitation = await ada.PostAsync<PendingInvitation>($"/api/teams/{team.Id}/invitations", new Invite(address));
        var token = Answers.TokenOf(invitation);

        // Someone else cannot take it with their own address.
        await Assert.ThrowsExactlyAsync<SignInRefused>(() => Person.SignUpAsync("Mallory", Person.NewEmail(), token));

        using var lin = await Person.SignUpAsync("Lin Clark", address, token);
        // Used once: the same link does nothing for a second account.
        await Assert.ThrowsExactlyAsync<SignInRefused>(() => Person.SignUpAsync("Lin Again", Person.NewEmail(), token));
        // Someone already on the team is not invited again.
        await ada.PostAsync($"/api/teams/{team.Id}/invitations", new Invite(address)).IsAsync(HttpStatusCode.Conflict, "already on the team");

        var second = await ada.PostAsync<PendingInvitation>($"/api/teams/{team.Id}/invitations", new Invite(Person.NewEmail()));
        await ada.DeleteAsync($"/api/teams/{team.Id}/invitations/{second.Id}").IsAsync(HttpStatusCode.NoContent);
        await Assert.ThrowsExactlyAsync<SignInRefused>(() => Person.SignUpAsync("Too Late", second.Email, Answers.TokenOf(second)));
        await ada.DeleteAsync($"/api/teams/{team.Id}/invitations/{second.Id}").IsAsync(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task An_administrator_promotes_demotes_and_removes_and_the_last_one_stays()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var invitation = await ada.PostAsync<PendingInvitation>($"/api/teams/{team.Id}/invitations", new Invite(Person.NewEmail()));
        var lin = await Person.SignUpAsync("Lin Clark", invitation.Email, Answers.TokenOf(invitation));

        // The only administrator can neither step down nor leave.
        await ada.PutAsync($"/api/teams/{team.Id}/people/{ada.UserId}/admin", new SetAdmin(false)).IsAsync(HttpStatusCode.BadRequest);
        await ada.DeleteAsync($"/api/teams/{team.Id}/people/{ada.UserId}").IsAsync(HttpStatusCode.BadRequest);

        await ada.PutAsync($"/api/teams/{team.Id}/people/{lin.UserId}/admin", new SetAdmin(true)).IsAsync(HttpStatusCode.NoContent);
        Assert.IsTrue((await lin.GetAsync<TeamDto>($"/api/teams/{team.Id}")).Admin);
        await lin.PutAsync<TeamDto>($"/api/teams/{team.Id}", new WriteTeam("Analytical Engines"));
        Assert.AreEqual("Analytical Engines", (await ada.GetAsync<TeamDto>($"/api/teams/{team.Id}")).Name);

        await ada.PutAsync($"/api/teams/{team.Id}/people/{lin.UserId}/admin", new SetAdmin(false)).IsAsync(HttpStatusCode.NoContent);
        await lin.PutAsync($"/api/teams/{team.Id}", new WriteTeam("Mine")).IsAsync(HttpStatusCode.Forbidden);

        await ada.DeleteAsync($"/api/teams/{team.Id}/people/{lin.UserId}").IsAsync(HttpStatusCode.NoContent);
        // SqlOS ends the session that was for this team. Signing in again, they are on no team.
        await lin.GetAsync($"/api/teams/{team.Id}").IsAsync(HttpStatusCode.Unauthorized);
        var back = await Person.SignInAsync(lin.Email);
        await back.GetAsync($"/api/teams/{team.Id}").IsAsync(HttpStatusCode.NotFound);
        Assert.AreEqual(0, (await back.GetAsync<ListPage<TeamDto>>("/api/teams")).Data.Count);
        Assert.AreEqual(1, (await ada.GetAsync<List<TeamPerson>>($"/api/teams/{team.Id}/people")).Count);
    }

    [TestMethod]
    public async Task A_person_can_leave_a_team_and_create_another()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var invitation = await ada.PostAsync<PendingInvitation>($"/api/teams/{team.Id}/invitations", new Invite(Person.NewEmail(), Admin: true));
        var lin = await Person.SignUpAsync("Lin Clark", invitation.Email, Answers.TokenOf(invitation));
        // Invited as an administrator: they administer the team from the moment they join.
        Assert.IsTrue((await lin.GetAsync<TeamDto>($"/api/teams/{team.Id}")).Admin);

        await lin.DeleteAsync($"/api/teams/{team.Id}/people/{lin.UserId}").IsAsync(HttpStatusCode.NoContent);
        // Their session was for the team they left, so it ended with it; they sign in again, on no team.
        await lin.GetAsync($"/api/teams/{team.Id}").IsAsync(HttpStatusCode.Unauthorized);
        var back = await Person.SignInAsync(lin.Email);
        await back.GetAsync($"/api/teams/{team.Id}").IsAsync(HttpStatusCode.NotFound);
        Assert.AreEqual(0, (await back.GetAsync<ListPage<TeamDto>>("/api/teams")).Data.Count);

        var own = await back.PostAsync<TeamDto>("/api/teams", new WriteTeam("Side projects"));
        Assert.IsTrue(own.Admin);
        Assert.AreEqual("Side projects", (await back.GetAsync<ListPage<TeamDto>>("/api/teams")).Data.Single().Name);
        Assert.AreEqual("Standard desktop", (await back.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{own.Id}/environments")).Data.Single().Name);
        await back.PostAsync("/api/teams", new WriteTeam("  ")).IsAsync(HttpStatusCode.BadRequest);
    }
}
