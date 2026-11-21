using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SqlOS.Email.Contracts;
using SqlOS.Email.Interfaces;
using SqlOS.Email.Models;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api;

/// <summary>
/// The email that tells someone a task was shared with them: one SqlOS template the code owns, kept as written
/// here at every start (<see cref="SeedAsync"/>), and sent through SqlOS's transactional email where email is
/// set up. It links to the task, or, for someone with no account yet, to the page that takes the invitation.
/// The link's token opens nothing by itself: whoever takes it must be signed in with the address it went to.
/// </summary>
public static class TaskShareEmail
{
    public const string Key = "task-shared";

    const string Subject = "{sharer} shared a task with you on Workstream";

    const string Html = """
        <p>{sharer} shared the task <strong>{title}</strong> with you. {access}</p>
        <p><a href="{link}">Open the task</a></p>
        <p>{note}</p>
        """;

    const string Text = """
        {sharer} shared the task "{title}" with you. {access}

        Open the task: {link}

        {note}
        """;

    /// <summary>The template as written here, idempotently: a changed text is a new version.</summary>
    public static async Task SeedAsync(WorkstreamDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var template = await db.Set<SqlOSEmailTemplate>().SingleOrDefaultAsync(x => x.Key == Key, ct);
        if (template is null)
        {
            db.Add(new SqlOSEmailTemplate
            {
                Id = $"tmpl_{Guid.NewGuid():N}", Key = Key, DisplayName = "A task was shared", SubjectTemplate = Subject, HtmlBodyTemplate = Html,
                TextBodyTemplate = Text, VariablesJson = """{"sharer":"Ada Lovelace","title":"Fix the sign-up page","access":"You can watch its desktop.","link":"https://example.test","note":""}""",
                IsActive = true, CreatedAt = now.UtcDateTime, UpdatedAt = now.UtcDateTime,
            });
        }
        else if (template.SubjectTemplate != Subject || template.HtmlBodyTemplate != Html || template.TextBodyTemplate != Text || !template.IsActive)
        {
            template.SubjectTemplate = Subject;
            template.HtmlBodyTemplate = Html;
            template.TextBodyTemplate = Text;
            template.IsActive = true;
            template.Version++;
            template.UpdatedAt = now.UtcDateTime;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>What the person may do, and until when, in a sentence.</summary>
    public static string AccessWords(string role, DateTime? until)
    {
        var what = role switch
        {
            Roles.TaskDriver => "You can watch its desktop, write to its agent and use its mouse and keyboard",
            Roles.TaskPrompter => "You can watch its desktop and write to its agent",
            _ => "You can watch its desktop and read its conversation",
        };
        return until is { } end ? $"{what} until {end.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture)} UTC." : $"{what}.";
    }

    /// <summary>Send it. False when it could not be sent; the share stands either way and its link is in the answer.</summary>
    public static async Task<bool> SendAsync(ISqlOSTransactionalEmailService email, string to, string sharer, string title, string role, DateTime? until, string link, bool newcomer, string eventKey, CancellationToken ct)
    {
        var note = newcomer
            ? "Create your Workstream account with this address to open it. The link works for a week."
            : "Sign in to Workstream with this address to open it.";
        try
        {
            var sent = await email.SendAsync(new SqlOSSendEmailRequest(Key, to, new Dictionary<string, object?>
            {
                ["sharer"] = sharer, ["title"] = title, ["access"] = AccessWords(role, until), ["link"] = link, ["note"] = note,
            }, $"task-share:{eventKey}"), ct);
            return sent.Status != SqlOSEmailDeliveryStatuses.Failed;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }
}
