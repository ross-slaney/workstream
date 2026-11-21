using Microsoft.EntityFrameworkCore;
using Workstream.Api.Domain;

namespace Workstream.Api;

/// <summary>
/// A person's own settings. <see cref="TaskEmails"/>: whether Workstream emails them when a task they started
/// or wrote in is finished or needs them.
/// </summary>
public sealed record MySettings(bool TaskEmails);

public static partial class Operations
{
    /// <summary>
    /// The caller's own settings. There is nothing to check in SqlOS: the row is the caller's by their own id,
    /// taken from their token, and nobody can name another person's.
    /// </summary>
    public static async Task<Outcome<MySettings>> MySettingsAsync(Caller caller, WorkstreamDbContext db, CancellationToken ct)
    {
        var settings = await db.PersonSettings.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == caller.PrincipalId, ct)
            ?? PersonSettings.For(caller.PrincipalId, DateTimeOffset.UtcNow);
        return Outcome<MySettings>.Ok(new MySettings(settings.TaskEmails));
    }

    public static async Task<Outcome<MySettings>> ChangeMySettingsAsync(Caller caller, MySettings body, WorkstreamDbContext db, CancellationToken ct)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var settings = await db.PersonSettings.SingleOrDefaultAsync(x => x.UserId == caller.PrincipalId, ct);
            if (settings is null)
            {
                settings = PersonSettings.For(caller.PrincipalId, now);
                db.PersonSettings.Add(settings);
            }
            settings.ChangeTaskEmails(body.TaskEmails, now);
            await db.SaveChangesAsync(ct);
            return Outcome<MySettings>.Ok(new MySettings(settings.TaskEmails));
        }
        catch (DomainRuleException rule)
        {
            return Outcome<MySettings>.Invalid(rule);
        }
    }
}
