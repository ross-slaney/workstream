using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Email.Interfaces;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using Workstream.Api.Domain;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api;

/// <summary>What the caller may do with a task, for the screen to offer only that.</summary>
public sealed record TaskAccess(bool Prompt, bool Drive, bool Manage);

/// <summary>A person outside the team a task is shared with: their role (<c>watcher</c>, <c>prompter</c>, <c>driver</c>) and when it ends (null: until it is taken back).</summary>
public sealed record SharedPerson(string UserId, string Name, string? Email, string Role, DateTime? Until);

/// <summary>
/// A task shared with someone who has no account yet, waiting for them to take the link. <see cref="Link"/> is
/// there only in the answer to making it, for the sharer to pass on.
/// </summary>
public sealed record TaskInvitationDto(string Id, string Email, string Role, DateTime? Until, DateTime ExpiresAt, string? Link);

/// <summary>Who has a task: its team, and each person and invitation it is shared with.</summary>
public sealed record TaskAccessList(string TeamId, string TeamName, IReadOnlyList<SharedPerson> People, IReadOnlyList<TaskInvitationDto> Invitations);

/// <summary>
/// What sharing made: a grant for someone with an account (<see cref="Person"/>) or an invitation for someone
/// without (<see cref="Invitation"/>). <see cref="Link"/> is the address to send them; <see cref="Emailed"/>
/// says whether Workstream sent it to them as well.
/// </summary>
public sealed record TaskShared(SharedPerson? Person, TaskInvitationDto? Invitation, string Link, bool Emailed);

public static partial class Operations
{
    /// <summary>
    /// Tasks in the teams the caller is not on, newest first: the query "Shared with me" pages with
    /// <c>TASK_VIEW</c>. What the page returns is what a task grant reaches, since the team's grant reaches
    /// only the teams left out here.
    /// </summary>
    public static IQueryable<WorkTask> SharedTasks(WorkstreamDbContext db, IReadOnlyCollection<string> callerTeamIds) =>
        db.Tasks.AsNoTracking().Where(x => !callerTeamIds.Contains(x.TeamId) && x.State != DesktopState.Deleting)
            .OrderByDescending(x => x.CreatedAt);

    /// <summary>The teams this person is on: SqlOS's memberships, which their team groups mirror.</summary>
    public static async Task<List<string>> TeamIdsOfAsync(WorkstreamDbContext db, string userId, CancellationToken ct) =>
        await db.Set<SqlOSMembership>().AsNoTracking().Where(x => x.UserId == userId && x.IsActive).Select(x => x.OrganizationId).ToListAsync(ct);

    /// <summary>What the caller may do with a task they can see.</summary>
    public static async Task<TaskAccess> AccessOfAsync(Caller caller, WorkTask task, ISqlOSFgaAuthService fga) => new(
        await fga.Allows(caller.PrincipalId, Permissions.TaskPrompt, task.ResourceId),
        await fga.Allows(caller.PrincipalId, Permissions.TaskDrive, task.ResourceId),
        await fga.Allows(caller.PrincipalId, Permissions.TaskManage, task.ResourceId));

    static string TaskLink(WorkstreamApp app, string taskId) => $"{app.WebOrigin}/home?open={Uri.EscapeDataString(taskId)}";

    static string InvitationLink(WorkstreamApp app, string token) => $"{app.WebOrigin}{WorkstreamApp.SharePath}?token={Uri.EscapeDataString(token)}";

    static TaskInvitationDto ToDto(TaskInvitation x, string? link = null) => new(x.Id, x.Email, TaskShareRoles.NameOf(x.Role), x.AccessUntil, x.ExpiresAt, link);

    /// <summary>The task-sharing grants a person holds on a task, current or ended.</summary>
    static Task<List<SqlOSFgaGrant>> ShareGrantsAsync(WorkstreamDbContext db, string userId, string node, CancellationToken ct) =>
        db.Set<SqlOSFgaGrant>().Where(g => g.SubjectId == userId && g.ResourceId == node && TaskShareRoles.All.Contains(g.RoleId)).ToListAsync(ct);

    /// <summary>
    /// Give a person this role on the task, with this end, in place of any share they had: a person holds one
    /// of the three on a task at a time.
    /// </summary>
    static async Task GrantShareAsync(WorkstreamDbContext db, string userId, WorkTask task, string role, DateTime? until, CancellationToken ct)
    {
        var held = await ShareGrantsAsync(db, userId, task.ResourceId, ct);
        db.RemoveRange(held.Where(g => g.RoleId != role));
        var grant = held.FirstOrDefault(g => g.RoleId == role) ?? await db.GrantRoleAsync(userId, task.ResourceId, role, ct);
        grant.EffectiveFrom = null;
        grant.EffectiveTo = until;
    }

    static Task<bool> OnTeamAsync(WorkstreamDbContext db, string teamId, string userId, CancellationToken ct) =>
        db.Set<SqlOSMembership>().AnyAsync(x => x.OrganizationId == teamId && x.UserId == userId && x.IsActive, ct);

    /// <summary>Who has the task: the team, and everyone it is shared with whose access has not ended. For whoever may share it.</summary>
    public static async Task<Outcome<TaskAccessList>> AccessListAsync(Caller caller, string taskId, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var allowed = await TaskWithAsync(caller, taskId, Permissions.TaskManage, "Only the task's team can see who it is shared with.", db, fga, ct);
        if (!allowed.IsOk) return allowed.As<TaskAccessList>();
        var task = allowed.Value!;
        var now = DateTime.UtcNow;
        var people = await db.Set<SqlOSFgaGrant>().AsNoTracking()
            .Where(g => g.ResourceId == task.ResourceId && TaskShareRoles.All.Contains(g.RoleId) && (g.EffectiveTo == null || g.EffectiveTo > now))
            .Join(db.Set<SqlOSUser>(), g => g.SubjectId, u => u.Id, (g, u) => new { g.SubjectId, g.RoleId, g.EffectiveTo, u.DisplayName, u.DefaultEmail })
            .OrderBy(x => x.DisplayName).ThenBy(x => x.SubjectId)
            .ToListAsync(ct);
        var invitations = await db.TaskInvitations.AsNoTracking()
            .Where(x => x.TaskId == task.Id && x.ExpiresAt > now)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);
        var names = await TeamNamesAsync(db, [task.TeamId], ct);
        return Outcome<TaskAccessList>.Ok(new TaskAccessList(task.TeamId, names.GetValueOrDefault(task.TeamId, "Team"),
            [.. people.Select(x => new SharedPerson(x.SubjectId, x.DisplayName, x.DefaultEmail, TaskShareRoles.NameOf(x.RoleId), x.EffectiveTo))],
            [.. invitations.Select(x => ToDto(x))]));
    }

    /// <summary>
    /// Share a task with someone outside its team, by email, with a role and an end. A person whose account has
    /// that address (proved: an emailed code, or a link sent to it) is granted at once. Anyone else gets an
    /// invitation: a link that grants the same once they take it signed in with that address. Either way the
    /// link is in the answer, and is emailed to them where email is set up.
    /// </summary>
    public static async Task<Outcome<TaskShared>> ShareTaskAsync(Caller caller, string taskId, string? email, string? role, string? expiresIn,
        WorkstreamApp app, WorkstreamDbContext db, ISqlOSFgaAuthService fga, ISqlOSTransactionalEmailService mail, CancellationToken ct)
    {
        var allowed = await TaskWithAsync(caller, taskId, Permissions.TaskManage, "Only the task's team can share it.", db, fga, ct);
        if (!allowed.IsOk) return allowed.As<TaskShared>();
        var task = allowed.Value!;
        var address = email?.Trim() ?? "";
        if (address.Length is 0 or > 320 || !address.Contains('@')) return Outcome<TaskShared>.Invalid("Sharing needs an email address.");
        var now = DateTimeOffset.UtcNow;
        string grantedRole;
        DateTime? until;
        try
        {
            grantedRole = TaskShareRoles.RoleFor(role);
            until = TaskShareRoles.Until(expiresIn, now);
        }
        catch (DomainRuleException rule)
        {
            return Outcome<TaskShared>.Invalid(rule);
        }
        var normalized = SqlOSAdminService.NormalizeEmail(address);
        var person = await db.Set<SqlOSUserEmail>().AsNoTracking()
            .Where(x => x.NormalizedEmail == normalized && x.IsVerified && x.User!.IsActive)
            .Select(x => new { x.UserId, x.User!.DisplayName, x.User.DefaultEmail })
            .FirstOrDefaultAsync(ct);
        var onTeam = person is not null
            ? await OnTeamAsync(db, task.TeamId, person.UserId, ct)
            : await db.Set<SqlOSMembership>().AnyAsync(x => x.OrganizationId == task.TeamId && x.IsActive && x.User!.Emails.Any(e => e.NormalizedEmail == normalized), ct);
        if (onTeam) return Outcome<TaskShared>.Exists("They are on the task's team, so they have it already.");

        var sharer = await db.Set<SqlOSUser>().AsNoTracking().Where(x => x.Id == caller.PrincipalId).Select(x => x.DisplayName).SingleOrDefaultAsync(ct) ?? "Someone";
        // An invitation still waiting for this address is replaced: the newest share is the one that counts.
        db.TaskInvitations.RemoveRange(await db.TaskInvitations.Where(x => x.TaskId == task.Id && x.NormalizedEmail == normalized).ToListAsync(ct));

        if (person is not null)
        {
            await GrantShareAsync(db, person.UserId, task, grantedRole, until, ct);
            await db.SaveChangesAsync(ct);
            var link = TaskLink(app, task.Id);
            var emailed = app.Sender is not null
                && await TaskShareEmail.SendAsync(mail, person.DefaultEmail ?? address, sharer, task.Title, grantedRole, until, link, newcomer: false, $"{task.Id}:{person.UserId}:{now.UtcTicks}", ct);
            return Outcome<TaskShared>.Ok(new TaskShared(new SharedPerson(person.UserId, person.DisplayName, person.DefaultEmail, TaskShareRoles.NameOf(grantedRole), until), null, link, emailed));
        }

        var (invitation, token) = TaskInvitation.For(task, address, normalized, grantedRole, until, caller.PrincipalId, now);
        db.TaskInvitations.Add(invitation);
        await db.SaveChangesAsync(ct);
        var invitationLink = InvitationLink(app, token);
        var sent = app.Sender is not null
            && await TaskShareEmail.SendAsync(mail, address, sharer, task.Title, grantedRole, until, invitationLink, newcomer: true, invitation.Id, ct);
        return Outcome<TaskShared>.Ok(new TaskShared(null, ToDto(invitation, invitationLink), invitationLink, sent));
    }

    /// <summary>Take a task back from a person it was shared with. Their open stream closes at its next check.</summary>
    public static async Task<Outcome<bool>> RevokeTaskShareAsync(Caller caller, string taskId, string userId, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var allowed = await TaskWithAsync(caller, taskId, Permissions.TaskManage, "Only the task's team can take it back.", db, fga, ct);
        if (!allowed.IsOk) return allowed.As<bool>();
        var held = await ShareGrantsAsync(db, userId, allowed.Value!.ResourceId, ct);
        if (held.Count == 0) return Outcome<bool>.NotFound();
        db.RemoveRange(held);
        await db.SaveChangesAsync(ct);
        return Outcome<bool>.Ok(true);
    }

    /// <summary>Withdraw an invitation nobody has taken yet: its link opens nothing from now on.</summary>
    public static async Task<Outcome<bool>> WithdrawTaskInvitationAsync(Caller caller, string taskId, string invitationId, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var allowed = await TaskWithAsync(caller, taskId, Permissions.TaskManage, "Only the task's team can withdraw an invitation.", db, fga, ct);
        if (!allowed.IsOk) return allowed.As<bool>();
        var invitation = await db.TaskInvitations.SingleOrDefaultAsync(x => x.Id == invitationId && x.TaskId == taskId, ct);
        if (invitation is null) return Outcome<bool>.NotFound();
        db.TaskInvitations.Remove(invitation);
        await db.SaveChangesAsync(ct);
        return Outcome<bool>.Ok(true);
    }

    /// <summary>
    /// Take an invitation to a task: the link's token, signed in with the address it was sent to. The token
    /// proves the mailbox, the address says whose it is, and the grant is made as the sharer chose it. The
    /// invitation is used up. A link that is wrong, withdrawn, used or past its time is not there.
    /// </summary>
    public static async Task<Outcome<string>> AcceptTaskInvitationAsync(Caller caller, string? token, WorkstreamDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200) return Outcome<string>.NotFound();
        var hash = TaskInvitation.HashOf(token.Trim());
        var invitation = await db.TaskInvitations.SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        var now = DateTimeOffset.UtcNow;
        if (invitation is null || !invitation.Open(now)) return Outcome<string>.NotFound();
        var task = await db.Tasks.SingleOrDefaultAsync(x => x.Id == invitation.TaskId, ct);
        if (task is null || task.State == DesktopState.Deleting) return Outcome<string>.NotFound();
        if (!await db.Set<SqlOSUserEmail>().AnyAsync(x => x.UserId == caller.PrincipalId && x.NormalizedEmail == invitation.NormalizedEmail, ct))
        {
            return Outcome<string>.Forbidden($"This invitation was sent to {invitation.Email}. Sign in with that address to open it.");
        }
        // Someone who joined the team since has the task through it; the invitation is spent either way.
        if (!await OnTeamAsync(db, task.TeamId, caller.PrincipalId, ct)) await GrantShareAsync(db, caller.PrincipalId, task, invitation.Role, invitation.AccessUntil, ct);
        db.TaskInvitations.Remove(invitation);
        await db.SaveChangesAsync(ct);
        return Outcome<string>.Ok(task.Id);
    }
}
