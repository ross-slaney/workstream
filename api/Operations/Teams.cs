using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Contracts;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using Workstream.Api.Domain;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api;

/// <summary>A person on a team, as the team's page lists them.</summary>
public sealed record TeamPerson(string UserId, string Name, string? Email, bool Admin, DateTime JoinedAt);

/// <summary>An invitation still waiting. <see cref="Link"/> is given once, when it is made, for the administrator to pass on.</summary>
/// <summary>
/// An invitation waiting to be accepted. <see cref="Link"/> is there only in the answer to making it, for the
/// administrator to pass on; <see cref="Emailed"/> says whether Workstream sent it to them as well.
/// </summary>
public sealed record PendingInvitation(string Id, string Email, bool Admin, DateTime ExpiresAt, string? Link, bool Emailed);

public static partial class Operations
{
    /// <summary>Every team, oldest first: the query "my teams" pages with <c>TEAM_VIEW</c>.</summary>
    public static IQueryable<Team> Teams(WorkstreamDbContext db) =>
        db.Teams.AsNoTracking().OrderBy(x => x.CreatedAt);

    /// <summary>The names SqlOS keeps for these teams.</summary>
    public static async Task<Dictionary<string, string>> TeamNamesAsync(WorkstreamDbContext db, IEnumerable<string> teamIds, CancellationToken ct)
    {
        var ids = teamIds.Distinct().ToList();
        return await db.Set<SqlOSOrganization>().AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
    }

    /// <summary>The team, when the caller may see it. Not visible is not there.</summary>
    public static async Task<Outcome<Team>> ViewTeamAsync(Caller caller, string teamId, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var team = await db.Teams.SingleOrDefaultAsync(x => x.Id == teamId, ct);
        if (team is null || !await fga.Allows(caller.PrincipalId, Permissions.TeamView, team.ResourceId)) return Outcome<Team>.NotFound();
        return Outcome<Team>.Ok(team);
    }

    /// <summary>The team, when the caller may see it and also holds <paramref name="permission"/> on it: 404, then 403.</summary>
    static async Task<Outcome<Team>> TeamWithAsync(Caller caller, string teamId, string permission, string refusal, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var seen = await ViewTeamAsync(caller, teamId, db, fga, ct);
        if (!seen.IsOk) return seen;
        return await fga.Allows(caller.PrincipalId, permission, seen.Value!.ResourceId) ? seen : Outcome<Team>.Forbidden(refusal);
    }

    /// <summary>
    /// A person creates another team. SqlOS makes the organization and their membership; provisioning, inside
    /// those saves, makes the node, the group, its grant and the standard environment. They administer it.
    /// </summary>
    public static async Task<Outcome<Team>> CreateTeamAsync(Caller caller, string? name, WorkstreamDbContext db, SqlOSAdminService admin, CancellationToken ct)
    {
        string teamName;
        try
        {
            teamName = Rules.Text(name, "A team's name", 100);
        }
        catch (DomainRuleException rule)
        {
            return Outcome<Team>.Invalid(rule);
        }
        var strategy = db.Database.CreateExecutionStrategy();
        var teamId = await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var organization = await admin.CreateOrganizationAsync(new SqlOSCreateOrganizationRequest(teamName, null), ct);
            await admin.CreateMembershipAsync(organization.Id, new SqlOSCreateMembershipRequest(caller.PrincipalId, IdentityProvisioning.AdminMembership), ct);
            await db.GrantRoleAsync(caller.PrincipalId, Team.ResourceIdFor(organization.Id), Roles.TeamAdmin, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return organization.Id;
        });
        return Outcome<Team>.Ok(await db.Teams.SingleAsync(x => x.Id == teamId, ct));
    }

    public static async Task<Outcome<Team>> RenameTeamAsync(Caller caller, string teamId, string? name, WorkstreamDbContext db, ISqlOSFgaAuthService fga, SqlOSAdminService admin, CancellationToken ct)
    {
        var allowed = await TeamWithAsync(caller, teamId, Permissions.TeamManageSettings, "Only a team administrator can rename the team.", db, fga, ct);
        if (!allowed.IsOk) return allowed;
        try
        {
            var teamName = Rules.Text(name, "A team's name", 100);
            var current = await db.Set<SqlOSOrganization>().AsNoTracking().SingleAsync(x => x.Id == teamId, ct);
            await admin.UpdateOrganizationAsync(teamId, new SqlOSUpdateOrganizationRequest(teamName, current.Slug, current.PrimaryDomain, current.IsActive), ct);
            return allowed;
        }
        catch (DomainRuleException rule)
        {
            return Outcome<Team>.Invalid(rule);
        }
    }

    /// <summary>Who is on the team: SqlOS's memberships, with who administers it read from the grants.</summary>
    public static async Task<Outcome<IReadOnlyList<TeamPerson>>> PeopleAsync(Caller caller, string teamId, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var seen = await ViewTeamAsync(caller, teamId, db, fga, ct);
        if (!seen.IsOk) return seen.As<IReadOnlyList<TeamPerson>>();
        var people = await db.Set<SqlOSMembership>().AsNoTracking()
            .Where(x => x.OrganizationId == teamId && x.IsActive)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.UserId)
            .Select(x => new { x.UserId, x.User!.DisplayName, x.User.DefaultEmail, x.CreatedAt })
            .ToListAsync(ct);
        var admins = await AdminsAsync(db, teamId, ct);
        return Outcome<IReadOnlyList<TeamPerson>>.Ok([.. people.Select(x => new TeamPerson(x.UserId, x.DisplayName, x.DefaultEmail, admins.Contains(x.UserId), x.CreatedAt))]);
    }

    /// <summary>The people holding <c>team_admin</c> on the team who are still on it.</summary>
    static async Task<HashSet<string>> AdminsAsync(WorkstreamDbContext db, string teamId, CancellationToken ct)
    {
        var node = Team.ResourceIdFor(teamId);
        var holders = await db.Set<SqlOSFgaGrant>().AsNoTracking()
            .Where(g => g.ResourceId == node && g.RoleId == Roles.TeamAdmin
                && db.Set<SqlOSMembership>().Any(m => m.OrganizationId == teamId && m.UserId == g.SubjectId && m.IsActive))
            .Select(g => g.SubjectId).ToListAsync(ct);
        return [.. holders];
    }

    /// <summary>Make someone on the team an administrator, or take it back. Never from the last one.</summary>
    public static async Task<Outcome<bool>> SetAdminAsync(Caller caller, string teamId, string userId, bool admin, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var allowed = await TeamWithAsync(caller, teamId, Permissions.TeamManageMembers, "Only a team administrator can change who administers the team.", db, fga, ct);
        if (!allowed.IsOk) return allowed.As<bool>();
        var membership = await db.Set<SqlOSMembership>().SingleOrDefaultAsync(x => x.OrganizationId == teamId && x.UserId == userId && x.IsActive, ct);
        if (membership is null) return Outcome<bool>.NotFound();
        var node = allowed.Value!.ResourceId;
        if (admin)
        {
            await db.GrantRoleAsync(userId, node, Roles.TeamAdmin, ct);
        }
        else
        {
            var admins = await AdminsAsync(db, teamId, ct);
            if (admins.Contains(userId) && admins.Count == 1) return Outcome<bool>.Invalid("A team keeps at least one administrator. Make someone else one first.");
            await db.RevokeRoleAsync(userId, node, Roles.TeamAdmin, ct);
        }
        membership.Role = admin ? IdentityProvisioning.AdminMembership : IdentityProvisioning.MemberMembership;
        await db.SaveChangesAsync(ct);
        return Outcome<bool>.Ok(true);
    }

    /// <summary>
    /// Remove someone from the team, or leave it. SqlOS ends their sessions for the team and their membership;
    /// provisioning takes them out of the group and removes their grant. Never the last administrator.
    /// </summary>
    public static async Task<Outcome<bool>> RemovePersonAsync(Caller caller, string teamId, string userId, WorkstreamDbContext db, ISqlOSFgaAuthService fga,
        SqlOSIdentityDeletionService deletion, CancellationToken ct)
    {
        var seen = await ViewTeamAsync(caller, teamId, db, fga, ct);
        if (!seen.IsOk) return seen.As<bool>();
        var leaving = userId == caller.PrincipalId;
        if (!leaving && !await fga.Allows(caller.PrincipalId, Permissions.TeamManageMembers, seen.Value!.ResourceId))
        {
            return Outcome<bool>.Forbidden("Only a team administrator can remove someone.");
        }
        if (!await db.Set<SqlOSMembership>().AnyAsync(x => x.OrganizationId == teamId && x.UserId == userId && x.IsActive, ct)) return Outcome<bool>.NotFound();
        var admins = await AdminsAsync(db, teamId, ct);
        if (admins.Contains(userId) && admins.Count == 1)
        {
            return Outcome<bool>.Invalid(leaving
                ? "You are the team's only administrator. Make someone else one before you leave."
                : "A team keeps at least one administrator.");
        }
        await deletion.RemoveMembershipAsync(teamId, userId, caller.PrincipalId, confirm: true, ct);
        return Outcome<bool>.Ok(true);
    }

    /// <summary>
    /// Invite someone by email. SqlOS keeps the invitation, bound to that address, and sends the email where
    /// email is set up; the link is also returned so the administrator can pass it on themselves. Where email
    /// is set up and the send fails, the invitation SqlOS kept is withdrawn: nobody has its link, so an
    /// invitation either reached them or does not exist, and inviting again is all there is to do.
    /// </summary>
    public static async Task<Outcome<PendingInvitation>> InviteAsync(Caller caller, string teamId, string? email, bool admin, HttpContext http,
        WorkstreamApp app, WorkstreamDbContext db, ISqlOSFgaAuthService fga, SqlOSAuthService auth, CancellationToken ct)
    {
        var allowed = await TeamWithAsync(caller, teamId, Permissions.TeamManageMembers, "Only a team administrator can invite people.", db, fga, ct);
        if (!allowed.IsOk) return allowed.As<PendingInvitation>();
        var address = email?.Trim() ?? "";
        if (address.Length is 0 or > 320 || !address.Contains('@')) return Outcome<PendingInvitation>.Invalid("An invitation needs an email address.");
        var normalized = SqlOSAdminService.NormalizeEmail(address);
        if (await db.Set<SqlOSMembership>().AnyAsync(x => x.OrganizationId == teamId && x.IsActive && x.User!.Emails.Any(e => e.NormalizedEmail == normalized), ct))
        {
            return Outcome<PendingInvitation>.Exists("They are already on the team.");
        }
        var emails = app.Sender is not null;
        var began = DateTime.UtcNow;
        try
        {
            var invitation = await auth.CreateEmailInvitationAsync(new SqlOSCreateEmailInvitationRequest(
                teamId, address, admin ? IdentityProvisioning.AdminMembership : IdentityProvisioning.MemberMembership,
                ClientId: WorkstreamApp.AppClientId, RedirectUri: $"{app.WebOrigin}{WorkstreamApp.WebRedirectPath}", InvitedByUserId: caller.PrincipalId,
                SendEmail: emails), http, ct);
            return Outcome<PendingInvitation>.Ok(new PendingInvitation(invitation.Id, invitation.Email, admin, invitation.ExpiresAt, invitation.InviteUrl, emails));
        }
        catch (InvalidOperationException refused)
        {
            var kept = await db.Set<SqlOSInvitation>().AsNoTracking()
                .Where(x => x.OrganizationId == teamId && x.InvitedByUserId == caller.PrincipalId && x.CreatedAt >= began && x.AcceptedAt == null && x.RevokedAt == null)
                .Select(x => x.Id).ToListAsync(ct);
            foreach (var id in kept) await auth.RevokeEmailInvitationAsync(new SqlOSRevokeEmailInvitationRequest(id, "send_failed"), http, ct);
            return Outcome<PendingInvitation>.Invalid(kept.Count > 0 ? "The invitation email could not be sent. Try again." : refused.Message);
        }
    }

    public static async Task<Outcome<IReadOnlyList<PendingInvitation>>> InvitationsAsync(Caller caller, string teamId, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var allowed = await TeamWithAsync(caller, teamId, Permissions.TeamManageMembers, "Only a team administrator can see invitations.", db, fga, ct);
        if (!allowed.IsOk) return allowed.As<IReadOnlyList<PendingInvitation>>();
        var now = DateTime.UtcNow;
        var waiting = await db.Set<SqlOSInvitation>().AsNoTracking()
            .Where(x => x.OrganizationId == teamId && x.AcceptedAt == null && x.RevokedAt == null && x.ExpiresAt > now)
            .OrderByDescending(x => x.CreatedAt).Take(200)
            .Select(x => new PendingInvitation(x.Id, x.InvitedEmail, x.Role == IdentityProvisioning.AdminMembership, x.ExpiresAt, null, x.LastSentAt != null))
            .ToListAsync(ct);
        return Outcome<IReadOnlyList<PendingInvitation>>.Ok(waiting);
    }

    public static async Task<Outcome<bool>> RevokeInvitationAsync(Caller caller, string teamId, string invitationId, HttpContext http,
        WorkstreamDbContext db, ISqlOSFgaAuthService fga, SqlOSAuthService auth, CancellationToken ct)
    {
        var allowed = await TeamWithAsync(caller, teamId, Permissions.TeamManageMembers, "Only a team administrator can withdraw an invitation.", db, fga, ct);
        if (!allowed.IsOk) return allowed.As<bool>();
        if (!await db.Set<SqlOSInvitation>().AnyAsync(x => x.Id == invitationId && x.OrganizationId == teamId && x.AcceptedAt == null && x.RevokedAt == null, ct))
        {
            return Outcome<bool>.NotFound();
        }
        await auth.RevokeEmailInvitationAsync(new SqlOSRevokeEmailInvitationRequest(invitationId, "withdrawn"), http, ct);
        return Outcome<bool>.Ok(true);
    }
}
