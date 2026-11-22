using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Workstream.Api.Model;
using Microsoft.AspNetCore.DataProtection;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using Workstream.Api.Desktops;
using Workstream.Api.Domain;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api;

/// <summary>
/// A team, with whether the caller administers it, what it may use, and when its desktops are paused (minutes
/// without use) and archived (days paused).
/// </summary>
public sealed record TeamDto(string Id, string Name, bool Admin, decimal ModelAllowanceUsd, int MaxRunningDesktops, int PauseAfterMinutes, int ArchiveAfterDays);

public sealed record WriteTeam(string Name);

public sealed record SetAdmin(bool Admin);

public sealed record Invite(string Email, bool Admin = false);


/// <summary>The team a person joined by taking an invitation.</summary>

/// <summary>
/// An environment as it is shown, with how its prepared setup stands. Variables are listed by name; their
/// values are never sent. <see cref="FromFile"/>: its settings are its repository's workstream.yml.
/// </summary>
public sealed record EnvironmentDto(
    string Id, string TeamId, string Name, IReadOnlyList<string> Apps, string Size, string? RepositoryUrl, string? RepositoryBranch,
    string? SetupScript, string? Instructions, IReadOnlyList<string> VariableNames, DateTimeOffset UpdatedAt, SetupDto Setup, bool FromFile);

/// <summary>An environment as a person adds it: <see cref="WriteEnvironment"/> with the id the client made.</summary>
public sealed record AddEnvironment(
    string Id, string Name, IReadOnlyList<string>? Apps, string? Size, string? RepositoryUrl, string? RepositoryBranch, string? SetupScript, string? Instructions,
    IReadOnlyList<VariableChange>? Variables, bool? FromFile = null)
{
    public WriteEnvironment Body => new(Name, Apps, Size, RepositoryUrl, RepositoryBranch, SetupScript, Instructions, Variables, FromFile);
}

public sealed record SetLimits(decimal ModelAllowanceUsd, int MaxRunningDesktops);

/// <summary>When a team's desktops are paused, in minutes without use, and archived, in days paused.</summary>
public sealed record SetDesktopTimes(int PauseAfterMinutes, int ArchiveAfterDays);

public static partial class WorkstreamApi
{
    static void MapTeams(RouteGroupBuilder api)
    {
        api.MapGet("/teams", ListTeams).WithName("list_teams").WithTags("Teams").Produces<ListPage<TeamDto>>();
        api.MapPost("/teams", CreateTeam).WithName("create_team").WithTags("Teams").Produces<TeamDto>(201);
        api.MapGet("/teams/{teamId}", GetTeam).WithName("get_team").WithTags("Teams").Produces<TeamDto>();
        api.MapPut("/teams/{teamId}", RenameTeam).WithName("rename_team").WithTags("Teams").Produces<TeamDto>().ProducesProblem(403);
        api.MapPut("/teams/{teamId}/desktop-times", SetTeamDesktopTimes).WithName("set_team_desktop_times").WithTags("Teams").Produces<TeamDto>().ProducesProblem(400).ProducesProblem(403);
        api.MapGet("/teams/{teamId}/usage", GetUsage).WithName("get_team_usage").WithTags("Teams").Produces<TeamUsage>();

        api.MapGet("/teams/{teamId}/people", ListPeople).WithName("list_people").WithTags("People").Produces<IReadOnlyList<TeamPerson>>();
        api.MapPut("/teams/{teamId}/people/{userId}/admin", SetPersonAdmin).WithName("set_person_admin").WithTags("People").ProducesProblem(400).ProducesProblem(403);
        api.MapDelete("/teams/{teamId}/people/{userId}", RemovePerson).WithName("remove_person").WithTags("People").ProducesProblem(400).ProducesProblem(403);

        api.MapGet("/teams/{teamId}/invitations", ListInvitations).WithName("list_invitations").WithTags("People").Produces<IReadOnlyList<PendingInvitation>>();
        api.MapPost("/teams/{teamId}/invitations", InvitePerson).WithName("invite_person").WithTags("People").Produces<PendingInvitation>(201).ProducesProblem(409);
        api.MapDelete("/teams/{teamId}/invitations/{invitationId}", RevokeInvitation).WithName("revoke_invitation").WithTags("People");
    }

    static void MapEnvironments(RouteGroupBuilder api)
    {
        api.MapGet("/teams/{teamId}/environments", ListEnvironments).WithName("list_environments").WithTags("Environments").Produces<ListPage<EnvironmentDto>>();
        api.MapPost("/teams/{teamId}/environments", AddTeamEnvironment).WithName("add_environment").WithTags("Environments").Produces<EnvironmentDto>(201).ProducesProblem(409);
        api.MapGet("/environments/{id}", GetEnvironment).WithName("get_environment").WithTags("Environments").Produces<EnvironmentDto>();
        api.MapPut("/environments/{id}", ChangeEnvironment).WithName("change_environment").WithTags("Environments").Produces<EnvironmentDto>().ProducesProblem(400);
        api.MapDelete("/environments/{id}", DeleteEnvironment).WithName("delete_environment").WithTags("Environments").ProducesProblem(400);
        api.MapPost("/environments/{id}/setup", PrepareEnvironment).WithName("prepare_environment").WithTags("Environments").Produces<EnvironmentDto>().ProducesProblem(400).ProducesProblem(503);
    }

    static void MapAdmin(RouteGroupBuilder api)
    {
        api.MapGet("/admin/overview", GetAdminOverview).WithName("get_admin_overview").WithTags("Admin").Produces<AdminOverview>().ProducesProblem(403);
        api.MapPut("/admin/teams/{teamId}/limits", SetTeamLimits).WithName("set_team_limits").WithTags("Admin").Produces<TeamDto>().ProducesProblem(403);
    }

    static async Task<TeamDto> ToDtoAsync(Team team, string name, Caller caller, ISqlOSFgaAuthService fga) =>
        new(team.Id, name, await fga.Allows(caller.PrincipalId, Permissions.TeamManageMembers, team.ResourceId), team.ModelAllowanceUsd, team.MaxRunningDesktops,
            team.PauseAfterMinutes, team.ArchiveAfterDays);

    static async Task<IResult> ListTeams(string? cursor, int? pageSize, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var caller = CallerOf(http);
        var (page, error) = await AuthorizedPageAsync(Operations.Teams(db), caller.PrincipalId, Permissions.TeamView, pageSize, cursor, ct);
        if (error is not null) return error;
        var names = await Operations.TeamNamesAsync(db, page!.Data.Select(x => x.Id), ct);
        var teams = new List<TeamDto>();
        foreach (var team in page.Data) teams.Add(await ToDtoAsync(team, names.GetValueOrDefault(team.Id, "Team"), caller, fga));
        return Results.Ok(new ListPage<TeamDto>(teams, page.NextCursor));
    }

    static async Task<IResult> TeamResultAsync(Outcome<Team> outcome, Caller caller, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct, bool created = false)
    {
        if (!outcome.IsOk) return ToResult(outcome);
        var team = outcome.Value!;
        var names = await Operations.TeamNamesAsync(db, [team.Id], ct);
        var dto = await ToDtoAsync(team, names.GetValueOrDefault(team.Id, "Team"), caller, fga);
        return created ? Results.Created($"/api/teams/{team.Id}", dto) : Results.Ok(dto);
    }

    static async Task<IResult> CreateTeam(WriteTeam body, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, SqlOSAdminService admin, CancellationToken ct)
    {
        var caller = CallerOf(http);
        return await TeamResultAsync(await Operations.CreateTeamAsync(caller, body.Name, db, admin, ct), caller, db, fga, ct, created: true);
    }

    static async Task<IResult> GetTeam(string teamId, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var caller = CallerOf(http);
        return await TeamResultAsync(await Operations.ViewTeamAsync(caller, teamId, db, fga, ct), caller, db, fga, ct);
    }

    static async Task<IResult> RenameTeam(string teamId, WriteTeam body, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, SqlOSAdminService admin, CancellationToken ct)
    {
        var caller = CallerOf(http);
        return await TeamResultAsync(await Operations.RenameTeamAsync(caller, teamId, body.Name, db, fga, admin, ct), caller, db, fga, ct);
    }

    static async Task<IResult> SetTeamDesktopTimes(string teamId, SetDesktopTimes body, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var caller = CallerOf(http);
        return await TeamResultAsync(await Operations.SetDesktopTimesAsync(caller, teamId, body.PauseAfterMinutes, body.ArchiveAfterDays, db, fga, ct), caller, db, fga, ct);
    }

    static async Task<IResult> GetUsage(string teamId, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, TimeProvider clock, CancellationToken ct)
    {
        var outcome = await Operations.UsageAsync(CallerOf(http), teamId, db, fga, clock, ct);
        return outcome.IsOk ? Results.Ok(outcome.Value) : ToResult(outcome);
    }

    static async Task<IResult> ListPeople(string teamId, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var outcome = await Operations.PeopleAsync(CallerOf(http), teamId, db, fga, ct);
        return outcome.IsOk ? Results.Ok(outcome.Value) : ToResult(outcome);
    }

    static async Task<IResult> SetPersonAdmin(string teamId, string userId, SetAdmin body, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var outcome = await Operations.SetAdminAsync(CallerOf(http), teamId, userId, body.Admin, db, fga, ct);
        return outcome.IsOk ? Results.NoContent() : ToResult(outcome);
    }

    static async Task<IResult> RemovePerson(string teamId, string userId, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, SqlOSIdentityDeletionService deletion, CancellationToken ct)
    {
        var outcome = await Operations.RemovePersonAsync(CallerOf(http), teamId, userId, db, fga, deletion, ct);
        return outcome.IsOk ? Results.NoContent() : ToResult(outcome);
    }

    static async Task<IResult> ListInvitations(string teamId, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var outcome = await Operations.InvitationsAsync(CallerOf(http), teamId, db, fga, ct);
        return outcome.IsOk ? Results.Ok(outcome.Value) : ToResult(outcome);
    }

    static async Task<IResult> InvitePerson(string teamId, Invite body, HttpContext http, WorkstreamApp app, WorkstreamDbContext db, ISqlOSFgaAuthService fga, SqlOSAuthService auth, CancellationToken ct)
    {
        var outcome = await Operations.InviteAsync(CallerOf(http), teamId, body.Email, body.Admin, http, app, db, fga, auth, ct);
        return outcome.IsOk ? Results.Created($"/api/teams/{teamId}/invitations/{outcome.Value!.Id}", outcome.Value) : ToResult(outcome);
    }

    static async Task<IResult> RevokeInvitation(string teamId, string invitationId, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, SqlOSAuthService auth, CancellationToken ct)
    {
        var outcome = await Operations.RevokeInvitationAsync(CallerOf(http), teamId, invitationId, http, db, fga, auth, ct);
        return outcome.IsOk ? Results.NoContent() : ToResult(outcome);
    }

    static EnvironmentDto ToDto(WorkEnvironment x, EnvironmentSetup? setup, DesktopOptions desktops) => new(
        x.Id, x.TeamId, x.Name, x.Apps, x.Size, x.RepositoryUrl, x.RepositoryBranch, x.SetupScript, x.Instructions, x.VariableNames, x.UpdatedAt,
        Operations.SetupOf(x, setup, desktops), x.FromFile);

    /// <summary>The environment as it is shown, with its setup read now.</summary>
    static async Task<EnvironmentDto> EnvironmentDtoAsync(WorkEnvironment environment, WorkstreamDbContext db, DesktopOptions desktops, CancellationToken ct) =>
        ToDto(environment, await db.Setups.AsNoTracking().SingleOrDefaultAsync(x => x.EnvironmentId == environment.Id, ct), desktops);

    static async Task<IResult> ListEnvironments(string teamId, string? cursor, int? pageSize, HttpContext http, WorkstreamDbContext db, DesktopOptions desktops, CancellationToken ct)
    {
        var (page, error) = await AuthorizedPageAsync(Operations.Environments(db, teamId), CallerOf(http).PrincipalId, Permissions.EnvironmentView, pageSize, cursor, ct);
        if (error is not null) return error;
        var ids = page!.Data.Select(x => x.Id).ToList();
        var setups = await db.Setups.AsNoTracking().Where(x => ids.Contains(x.EnvironmentId)).ToDictionaryAsync(x => x.EnvironmentId, ct);
        return Results.Ok(new ListPage<EnvironmentDto>([.. page.Data.Select(x => ToDto(x, setups.GetValueOrDefault(x.Id), desktops))], page.NextCursor));
    }

    static async Task<IResult> AddTeamEnvironment(string teamId, AddEnvironment body, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IDataProtectionProvider protection,
        IBackgroundJobClient jobs, DesktopOptions desktops, CancellationToken ct)
    {
        var outcome = await Operations.AddEnvironmentAsync(CallerOf(http), teamId, body.Id, body.Body, db, fga, protection, jobs, desktops, ct);
        return outcome.IsOk ? Results.Created($"/api/environments/{outcome.Value!.Id}", await EnvironmentDtoAsync(outcome.Value, db, desktops, ct)) : ToResult(outcome);
    }

    static async Task<IResult> GetEnvironment(string id, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, DesktopOptions desktops, CancellationToken ct)
    {
        var outcome = await Operations.ViewEnvironmentAsync(CallerOf(http), id, db, fga, ct);
        return outcome.IsOk ? Results.Ok(await EnvironmentDtoAsync(outcome.Value!, db, desktops, ct)) : ToResult(outcome);
    }

    static async Task<IResult> ChangeEnvironment(string id, WriteEnvironment body, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IDataProtectionProvider protection,
        IBackgroundJobClient jobs, DesktopOptions desktops, CancellationToken ct)
    {
        var outcome = await Operations.ChangeEnvironmentAsync(CallerOf(http), id, body, db, fga, protection, jobs, desktops, ct);
        return outcome.IsOk ? Results.Ok(await EnvironmentDtoAsync(outcome.Value!, db, desktops, ct)) : ToResult(outcome);
    }

    static async Task<IResult> DeleteEnvironment(string id, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IBackgroundJobClient jobs, CancellationToken ct)
    {
        var outcome = await Operations.DeleteEnvironmentAsync(CallerOf(http), id, db, fga, jobs, ct);
        return outcome.IsOk ? Results.NoContent() : ToResult(outcome);
    }

    /// <summary>"Prepare again": the environment's setup is done again on a machine of its own, and saved.</summary>
    static async Task<IResult> PrepareEnvironment(string id, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IDataProtectionProvider protection,
        IBackgroundJobClient jobs, DesktopOptions desktops, CancellationToken ct)
    {
        var outcome = await Operations.PrepareAgainAsync(CallerOf(http), id, db, fga, protection, jobs, desktops, ct);
        return outcome.IsOk ? Results.Ok(await EnvironmentDtoAsync(outcome.Value!, db, desktops, ct)) : ToResult(outcome);
    }

    static async Task<IResult> GetAdminOverview(HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, DesktopOptions desktops, TimeProvider clock, CancellationToken ct)
    {
        var outcome = await Operations.AdminOverviewAsync(CallerOf(http), db, fga, desktops, clock, ct);
        return outcome.IsOk ? Results.Ok(outcome.Value) : ToResult(outcome);
    }

    static async Task<IResult> SetTeamLimits(string teamId, SetLimits body, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IMemoryCache cache, CancellationToken ct)
    {
        var caller = CallerOf(http);
        var outcome = await Operations.SetTeamLimitsAsync(caller, teamId, body.ModelAllowanceUsd, body.MaxRunningDesktops, db, fga, ct);
        // The gateway remembers what a team has left for a few seconds: a raised allowance lets its agents go on at once.
        if (outcome.IsOk) cache.Remove(ModelGateway.AllowanceKey(teamId));
        return await TeamResultAsync(outcome, caller, db, fga, ct);
    }
}
