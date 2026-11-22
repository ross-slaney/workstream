using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Models;
using SqlOS.Fga.Models;
using Workstream.Api.Domain;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api;

// Runs inside every save, before SqlOS writes: identities, memberships, nodes and grants commit together.
// SqlOS has no sign-up or acceptance hook, and every path (headless, admin, the tests) saves through this
// context, so this is the one place the rules in docs/core-model.md "Grants" are applied:
//   a new person       -> a team of their own (a SqlOS organization and their membership in it), unless
//                         they created their account from an invitation: then they join that team only;
//   a new team         -> its node, its group holding team_member, team_admin for whoever created it,
//                         and the standard environment;
//   a membership       -> the person is in the team's group; removed, they leave it and lose team_admin;
//   an invitation taken as an administrator -> team_admin.
internal static class IdentityProvisioning
{
    /// <summary>The membership role SqlOS records for an administrator. Access itself is the team_admin grant.</summary>
    public const string AdminMembership = "admin";
    public const string MemberMembership = "member";

    public static async Task SynchronizeAsync(WorkstreamDbContext db, CancellationToken ct)
    {
        foreach (var entry in db.ChangeTracker.Entries<SqlOSUser>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified).ToArray())
        {
            var user = entry.Entity;
            var subject = await db.Set<SqlOSFgaSubject>().FindAsync([user.Id], ct);
            if (subject is null)
            {
                db.Add(new SqlOSFgaSubject { Id = user.Id, SubjectTypeId = "user", DisplayName = user.DisplayName });
                db.Add(new SqlOSFgaUser { Id = user.Id, SubjectId = user.Id, Email = user.DefaultEmail, IsActive = user.IsActive });
            }
            else
            {
                subject.DisplayName = user.DisplayName;
                var fgaUser = await db.Set<SqlOSFgaUser>().FindAsync([user.Id], ct);
                if (fgaUser is not null) { fgaUser.IsActive = user.IsActive; fgaUser.Email = user.DefaultEmail; }
            }
            if (entry.State != EntityState.Added) continue;
            if (db.Set<SqlOSMembership>().Local.Any(x => x.UserId == user.Id)) continue;
            // Someone creating their account from an invitation joins that team and gets none of their own:
            // SqlOS tracks the sign-in request, bound to the invitation, in this same save. (The emailed link
            // hands the browser to the web app's invite page, so every invitation sign-up is such a request.)
            if (db.ChangeTracker.Entries<SqlOSAuthorizationRequest>().Any(e => e.Entity.InvitationId != null)) continue;
            // Repeat sign-ins never enter this branch, so leaving a team cannot make another.
            var teamId = $"org_{Guid.NewGuid():N}";
            db.Add(new SqlOSOrganization
            {
                Id = teamId, Slug = teamId, Name = TeamNameFor(user.DisplayName), CreatedAt = DateTime.UtcNow,
            });
            db.Add(new SqlOSMembership { OrganizationId = teamId, UserId = user.Id, Role = AdminMembership, CreatedAt = DateTime.UtcNow });
        }

        foreach (var entry in db.ChangeTracker.Entries<SqlOSOrganization>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified).ToArray())
        {
            var organization = entry.Entity;
            var team = await db.Teams.FindAsync([organization.Id], ct);
            if (team is null)
            {
                var now = DateTimeOffset.UtcNow;
                team = Team.For(organization.Id, WorkstreamDbContext.DefaultModelAllowanceUsd, WorkstreamDbContext.DefaultMaxRunningDesktops, now,
                    WorkstreamDbContext.DefaultPauseAfterMinutes, WorkstreamDbContext.DefaultArchiveAfterDays);
                db.Add(team);
                var group = AddGroup(db, team.Id, organization.Name, "team", team.Id, organization.IsActive);
                Grant(db, group.SubjectId, team.ResourceId, Roles.TeamMember);
                // Whoever created the team administers it. Like everyone, they join its group through their membership, below.
                var founders = db.Set<SqlOSMembership>().Local.Where(x => x.OrganizationId == team.Id).ToArray();
                foreach (var founder in founders) Grant(db, founder.UserId, team.ResourceId, Roles.TeamAdmin);
                db.Add(WorkEnvironment.Standard(team, founders.FirstOrDefault()?.UserId ?? group.SubjectId, now));
            }
            else if (await db.Set<SqlOSFgaUserGroup>().FindAsync([organization.Id], ct) is { } group)
            {
                group.Name = organization.Name;
                group.IsActive = organization.IsActive;
            }
        }

        foreach (var entry in db.ChangeTracker.Entries<SqlOSMembership>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray())
        {
            var membership = entry.Entity;
            var inGroup = db.Set<SqlOSFgaUserGroupMembership>().Local.FirstOrDefault(x => x.SubjectId == membership.UserId && x.UserGroupId == membership.OrganizationId)
                ?? await db.Set<SqlOSFgaUserGroupMembership>().FindAsync([membership.UserId, membership.OrganizationId], ct);
            if (entry.State != EntityState.Deleted && membership.IsActive)
            {
                // Joining the team is joining its group, which holds team_member.
                if (inGroup is null) db.Add(new SqlOSFgaUserGroupMembership { SubjectId = membership.UserId, UserGroupId = membership.OrganizationId });
            }
            else
            {
                if (inGroup is not null) db.Remove(inGroup);
                // The one direct grant a person holds on a team.
                var teamNode = Team.ResourceIdFor(membership.OrganizationId);
                db.RemoveRange(await db.Set<SqlOSFgaGrant>()
                    .Where(g => g.SubjectId == membership.UserId && g.ResourceId == teamNode).ToListAsync(ct));
            }
        }

        // An invitation accepted as an administrator: they administer the team from the moment they join.
        foreach (var entry in db.ChangeTracker.Entries<SqlOSInvitation>().Where(e => e.State == EntityState.Modified).ToArray())
        {
            var invitation = entry.Entity;
            var accepted = entry.OriginalValues[nameof(SqlOSInvitation.AcceptedAt)] is null && invitation.AcceptedAt is not null && invitation.AcceptedByUserId is not null;
            if (accepted && invitation.Role == AdminMembership)
            {
                var teamNode = Team.ResourceIdFor(invitation.OrganizationId);
                var holds = db.Set<SqlOSFgaGrant>().Local.Any(g => g.SubjectId == invitation.AcceptedByUserId && g.ResourceId == teamNode && g.RoleId == Roles.TeamAdmin)
                    || await db.Set<SqlOSFgaGrant>().AnyAsync(g => g.SubjectId == invitation.AcceptedByUserId && g.ResourceId == teamNode && g.RoleId == Roles.TeamAdmin, ct);
                if (!holds) Grant(db, invitation.AcceptedByUserId!, teamNode, Roles.TeamAdmin);
            }
        }
    }

    /// <summary>A person's own team is named after them until they rename it.</summary>
    public static string TeamNameFor(string displayName)
    {
        var name = displayName.Trim();
        if (name.Length == 0) return "My team";
        name = name[..Math.Min(name.Length, 150)];
        return name.EndsWith('s') ? $"{name}' team" : $"{name}'s team";
    }

    // A group's membership id is the team's id; its subject id, used for grants, is group::{id}.
    internal static SqlOSFgaUserGroup AddGroup(WorkstreamDbContext db, string id, string name, string type, string? organizationId, bool isActive)
    {
        var subject = new SqlOSFgaSubject { Id = $"group::{id}", SubjectTypeId = "group", DisplayName = name, OrganizationId = organizationId };
        db.Add(subject);
        var group = new SqlOSFgaUserGroup { Id = id, SubjectId = subject.Id, Name = name, GroupType = type, IsActive = isActive };
        db.Add(group);
        return group;
    }

    internal static void Grant(WorkstreamDbContext db, string subjectId, string resourceId, string roleId) =>
        db.Add(new SqlOSFgaGrant { Id = $"grant_{Guid.NewGuid():N}", SubjectId = subjectId, ResourceId = resourceId, RoleId = roleId });
}
