using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Models;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.Fga.Models;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api;

/// <summary>
/// What the platform needs in the tree before anyone signs in, seeded on every start after SqlOS's bootstrap,
/// idempotently: the <c>system_admins</c> group, granted <c>system_admin</c> on <c>root</c>; the one system
/// administrator (<see cref="WorkstreamApp.AdministratorEmail"/>) as that group's only member, or nobody where
/// no address is set; and the desktop operator, the agent the background jobs act as, granted
/// <c>desktop_operator</c> on <c>root</c>.
/// </summary>
public static class PlatformSeed
{
    /// <summary>SqlOS's root resource, as this app leaves it configured.</summary>
    public const string RootResourceId = "root";

    public const string SystemAdminsGroupId = "system_admins";
    public const string SystemAdminsSubjectId = "group::system_admins";

    public const string AdministratorName = "Workstream administrator";

    /// <summary>
    /// The desktop operator: the agent principal the jobs act as when they start, stop and delete desktops
    /// (Desktops/DesktopJobs.cs). One grant, on root. It needs no credential: the jobs run in this process.
    /// </summary>
    public const string OperatorSubjectId = "agent::desktop-operator";
    public const string OperatorName = "Desktop operator";

    public static async Task SeedAsync(WorkstreamDbContext db, DateTimeOffset now, string? administratorEmail, CancellationToken ct)
    {
        await db.ProvisionAgentSubjectAsync(OperatorSubjectId, OperatorName, agentType: "operator",
            description: "Starts, stops and deletes desktops.", cancellationToken: ct);
        if (!await db.Set<SqlOSFgaGrant>().AnyAsync(x => x.SubjectId == OperatorSubjectId && x.ResourceId == RootResourceId && x.RoleId == Roles.DesktopOperator, ct))
        {
            IdentityProvisioning.Grant(db, OperatorSubjectId, RootResourceId, Roles.DesktopOperator);
        }

        if (await db.Set<SqlOSFgaUserGroup>().FindAsync([SystemAdminsGroupId], ct) is null)
        {
            IdentityProvisioning.AddGroup(db, SystemAdminsGroupId, "System administrators", "system", organizationId: null, isActive: true);
        }
        if (!await db.Set<SqlOSFgaGrant>().AnyAsync(x => x.SubjectId == SystemAdminsSubjectId && x.ResourceId == RootResourceId && x.RoleId == Roles.SystemAdmin, ct))
        {
            IdentityProvisioning.Grant(db, SystemAdminsSubjectId, RootResourceId, Roles.SystemAdmin);
        }

        // The administrator is a person like any other, with a team of their own (IdentityProvisioning), who is
        // also in the system administrators' group. Someone who already signed up with the address is used as is.
        string? administratorId = null;
        if (administratorEmail is not null)
        {
            var normalized = SqlOSAdminService.NormalizeEmail(administratorEmail);
            administratorId = await db.Set<SqlOSUserEmail>().Where(x => x.NormalizedEmail == normalized).Select(x => x.UserId).FirstOrDefaultAsync(ct);
            if (administratorId is null)
            {
                administratorId = $"usr_{Guid.NewGuid():N}";
                var at = now.UtcDateTime;
                db.Add(new SqlOSUser { Id = administratorId, DisplayName = AdministratorName, DefaultEmail = administratorEmail, IsActive = true, CreatedAt = at, UpdatedAt = at });
                db.Add(new SqlOSUserEmail
                {
                    Id = $"email_{Guid.NewGuid():N}", UserId = administratorId, Email = administratorEmail, NormalizedEmail = normalized,
                    IsPrimary = true, IsVerified = true, VerifiedAt = at, CreatedAt = at,
                });
            }
        }

        // The group is exactly the configured administrator: a changed address moves the role, and no address
        // leaves the group empty. The setting is the state; the membership follows it.
        var members = await db.Set<SqlOSFgaUserGroupMembership>().Where(x => x.UserGroupId == SystemAdminsGroupId).ToListAsync(ct);
        db.RemoveRange(members.Where(x => x.SubjectId != administratorId));
        if (administratorId is not null && members.All(x => x.SubjectId != administratorId))
        {
            db.Add(new SqlOSFgaUserGroupMembership { SubjectId = administratorId, UserGroupId = SystemAdminsGroupId });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Give the administrator this password, where the environment signs in with one (development, never
    /// production). Idempotent: a password that already matches is left alone.
    /// </summary>
    public static async Task<bool> SetAdministratorPasswordAsync(WorkstreamDbContext db, SqlOSCryptoService crypto, string administratorEmail, string password, DateTimeOffset now, CancellationToken ct)
    {
        var normalized = SqlOSAdminService.NormalizeEmail(administratorEmail);
        var userId = await db.Set<SqlOSUserEmail>().Where(x => x.NormalizedEmail == normalized).Select(x => x.UserId).FirstAsync(ct);
        var credential = await db.Set<SqlOSCredential>().SingleOrDefaultAsync(x => x.UserId == userId && x.Type == "password" && x.RevokedAt == null, ct);
        if (credential is not null && crypto.VerifyPassword(credential.SecretHash, password)) return false;
        if (credential is null)
        {
            db.Add(new SqlOSCredential { Id = crypto.GenerateId("cred"), UserId = userId, Type = "password", SecretHash = crypto.HashPassword(password), CreatedAt = now.UtcDateTime });
        }
        else
        {
            credential.SecretHash = crypto.HashPassword(password);
            credential.SecretVersion++;
        }
        await db.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>
/// Seeds the platform (<see cref="PlatformSeed"/>) once SqlOS has bootstrapped its own tables. Registered after
/// <c>AddSqlOS</c>, so it starts after it. Idempotent, on every start.
/// </summary>
public sealed class PlatformSeedHostedService(IServiceProvider services, ILogger<PlatformSeedHostedService> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
        var app = scope.ServiceProvider.GetRequiredService<WorkstreamApp>();
        await PlatformSeed.SeedAsync(db, DateTimeOffset.UtcNow, app.AdministratorEmail, ct);
        if (app is { AdministratorEmail: { } administrator, AdministratorPassword: { } password }
            && await PlatformSeed.SetAdministratorPasswordAsync(db, scope.ServiceProvider.GetRequiredService<SqlOSCryptoService>(), administrator, password, DateTimeOffset.UtcNow, ct))
        {
            log.LogInformation("The system administrator's password was set from configuration.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
