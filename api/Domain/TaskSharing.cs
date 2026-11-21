using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api.Domain;

/// <summary>
/// What a person outside the team may do with a task shared with them (docs/core-model.md, "Sharing a task").
/// Each is a role granted on the task's node, and each carries everything the one before it does.
/// </summary>
public static class TaskShareRoles
{
    /// <summary>The words the API takes and answers with, and the role each one is.</summary>
    public static readonly IReadOnlyDictionary<string, string> ByName = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["watcher"] = Roles.TaskWatcher,
        ["prompter"] = Roles.TaskPrompter,
        ["driver"] = Roles.TaskDriver,
    };

    /// <summary>Every role a task can be shared with, so a person holds at most one of them on a task.</summary>
    public static readonly IReadOnlyList<string> All = [Roles.TaskWatcher, Roles.TaskPrompter, Roles.TaskDriver];

    public static string RoleFor(string? name) =>
        name is not null && ByName.TryGetValue(name.Trim().ToLowerInvariant(), out var role)
            ? role
            : throw new DomainRuleException("Share it to watch, to watch and write to the agent, or to use the desktop too.");

    public static string NameOf(string role) => ByName.First(x => x.Value == role).Key;

    /// <summary>
    /// When the access ends, from how long the sharer gave it: an hour, a day, a week, or until it is taken
    /// back (null). It is the grant's <c>EffectiveTo</c>.
    /// </summary>
    public static DateTime? Until(string? expiresIn, DateTimeOffset now) => expiresIn?.Trim().ToLowerInvariant() switch
    {
        "hour" => now.UtcDateTime.AddHours(1),
        "day" => now.UtcDateTime.AddDays(1),
        "week" => now.UtcDateTime.AddDays(7),
        null or "" or "never" => null,
        _ => throw new DomainRuleException("Access lasts an hour, a day, a week, or until you take it back."),
    };
}

/// <summary>
/// A task shared with someone who has no account yet. SqlOS's invitations are memberships of an organization,
/// and this person is not joining the team, so the grant waits here: the address it is for, the role and the
/// end the sharer chose, and the hash of the link's token. Taking the link signed in as that address makes the
/// grant and deletes this row. Someone with an account is granted at once and never has one of these.
/// </summary>
public sealed class TaskInvitation
{
    /// <summary>How long a link can be taken, when the access itself does not end sooner.</summary>
    public static readonly TimeSpan LinkLasts = TimeSpan.FromDays(7);

    /// <summary>For EF Core.</summary>
    TaskInvitation()
    {
    }

    public string Id { get; private set; } = "";
    public string TaskId { get; private set; } = "";
    /// <summary>The address as the sharer wrote it, for the list and the email.</summary>
    public string Email { get; private set; } = "";
    /// <summary>SqlOS's lookup key for the address: who may take the link.</summary>
    public string NormalizedEmail { get; private set; } = "";
    /// <summary>One of <see cref="TaskShareRoles.All"/>.</summary>
    public string Role { get; private set; } = "";
    /// <summary>When the access ends; null when it lasts until it is taken back. The grant's <c>EffectiveTo</c>.</summary>
    public DateTime? AccessUntil { get; private set; }
    /// <summary>SHA-256 of the link's token, in hex. The token itself is only ever in the link.</summary>
    public string TokenHash { get; private set; } = "";
    /// <summary>Who shared it: a fact for the list and the email, never a permission.</summary>
    public string InvitedBy { get; private set; } = "";
    public DateTime CreatedAt { get; private set; }
    /// <summary>After this the link opens nothing.</summary>
    public DateTime ExpiresAt { get; private set; }

    /// <summary>A new invitation and the token for its link, which is not kept.</summary>
    public static (TaskInvitation Invitation, string Token) For(WorkTask task, string email, string normalizedEmail, string role, DateTime? accessUntil, string invitedBy, DateTimeOffset now)
    {
        if (!TaskShareRoles.All.Contains(role)) throw new DomainRuleException("Share it to watch, to watch and write to the agent, or to use the desktop too.");
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var created = now.UtcDateTime;
        var linkEnds = created + LinkLasts;
        return (new TaskInvitation
        {
            Id = Guid.NewGuid().ToString("D"),
            TaskId = task.Id,
            Email = email,
            NormalizedEmail = normalizedEmail,
            Role = role,
            AccessUntil = accessUntil,
            TokenHash = HashOf(token),
            InvitedBy = Rules.Principal(invitedBy, "Whoever shared it"),
            CreatedAt = created,
            ExpiresAt = accessUntil is { } until && until < linkEnds ? until : linkEnds,
        }, token);
    }

    public static string HashOf(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public bool Open(DateTimeOffset now) => ExpiresAt > now.UtcDateTime;
}
