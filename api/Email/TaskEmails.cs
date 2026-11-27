using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Models;
using SqlOS.Email.Contracts;
using SqlOS.Email.Interfaces;
using SqlOS.Email.Models;
using SqlOS.Email.Services;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using Workstream.Api.Domain;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api.Email;

/// <summary>
/// The words of Workstream's own emails, from <c>client/locales/emails/en.json</c>, embedded in the API. The
/// places SqlOS fills when it sends (<c>{title}</c>, <c>{link}</c>) stay in them as they are.
/// </summary>
public static class EmailWords
{
    static readonly Lazy<JsonObject> English = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Workstream.Emails.en.json")
            ?? throw new InvalidOperationException("The email words (client/locales/emails/en.json) are not in the API.");
        return JsonNode.Parse(stream)!.AsObject();
    });

    /// <summary>A line by its key, <c>finished.subject</c>.</summary>
    public static string Say(string key)
    {
        JsonNode? node = English.Value;
        foreach (var part in key.Split('.')) node = node?[part];
        return node?.GetValue<string>() ?? throw new KeyNotFoundException($"No words for '{key}' in client/locales/emails/en.json.");
    }

    /// <summary>A line for the HTML: its words encoded, its places left for SqlOS, which encodes what it puts in them.</summary>
    public static string Html(string key) => WebUtility.HtmlEncode(Say(key));
}

/// <summary>One email as code owns it, as SqlOS stores it.</summary>
public sealed record CodeTemplate(string Key, string DisplayName, string Subject, string Html, string Text)
{
    /// <summary>Values for SqlOS's preview in its dashboard.</summary>
    public static JsonObject Sample() => new()
    {
        ["title"] = "Fix the sign-in page",
        ["team"] = "Ada's team",
        ["message"] = "The sign-in page works again. Should I also add a test for it?",
        ["link"] = "https://workstream.example/t/team/?open=task",
        ["settingsLink"] = "https://workstream.example/t/team/team",
        ["sharer"] = "Ada Lovelace",
        ["access"] = "You can watch its desktop and write to its agent.",
        ["ends"] = "It ends 28 November 2026, 14:00 UTC.",
    };
}

/// <summary>
/// The two emails about a task: the agent finished, or the agent stopped on a question. Code owns them: they are
/// written into SqlOS's template store at every start (<see cref="ReconcileAsync"/>), the markup here and every
/// word from the locale file. SqlOS fills <c>title</c>, <c>team</c>, <c>message</c>, <c>link</c> and
/// <c>settingsLink</c> when it sends, encoding each.
/// </summary>
public static class TaskEmailTemplates
{
    public const string Finished = "task.finished";
    public const string NeedsYou = "task.needs-you";

    public static string KeyFor(TurnEnding ending) => ending == TurnEnding.NeedsYou ? NeedsYou : Finished;

    public static IReadOnlyList<CodeTemplate> All => [For(Finished, "finished"), For(NeedsYou, "needsYou")];

    static CodeTemplate For(string key, string words) => new(
        key,
        EmailWords.Say($"{words}.displayName"),
        EmailWords.Say($"{words}.subject"),
        Html(words),
        string.Join("\n", EmailWords.Say($"{words}.heading"), "", EmailWords.Say($"{words}.intro"), "", "{message}", "",
            EmailWords.Say($"{words}.buttonText"), "", "--", EmailWords.Say("layout.why"), EmailWords.Say("layout.turnOffText")));

    // The light palette (client/components/theme/palette.ts): canvas, card, border, foreground, muted foreground, primary.
    static string Html(string words) => $"""
        <!doctype html>
        <html><body style="margin:0;padding:0;background:#F3F5F8;">
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#F3F5F8;padding:32px 16px;font-family:-apple-system,'Segoe UI',Helvetica,Arial,sans-serif;color:#14171F;">
        <tr><td align="center">
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:560px;background:#FFFFFF;border:1px solid #DDE1E8;border-radius:8px;text-align:left;">
        <tr><td style="padding:24px 28px 0;font-size:15px;font-weight:600;color:#1F4FE0;">{EmailWords.Html("layout.brand")}</td></tr>
        <tr><td style="padding:16px 28px 0;font-size:20px;line-height:28px;font-weight:600;color:#14171F;">{EmailWords.Html($"{words}.heading")}</td></tr>
        <tr><td style="padding:12px 28px 0;font-size:14px;line-height:22px;color:#566070;">{EmailWords.Html($"{words}.intro")}</td></tr>
        <tr><td style="padding:12px 28px 0;"><div style="border-left:3px solid #DDE1E8;padding:4px 0 4px 14px;font-size:14px;line-height:22px;color:#14171F;white-space:pre-wrap;">{"{message}"}</div></td></tr>
        <tr><td style="padding:24px 28px 28px;"><a href="{"{link}"}" style="display:inline-block;background:#1F4FE0;color:#FFFFFF;text-decoration:none;font-size:14px;font-weight:600;padding:10px 18px;border-radius:6px;">{EmailWords.Html($"{words}.button")}</a></td></tr>
        </table>
        <p style="max-width:560px;margin:16px auto 0;font-size:12px;line-height:18px;color:#566070;text-align:left;">{EmailWords.Html("layout.why")} <a href="{"{settingsLink}"}" style="color:#566070;">{EmailWords.Html("layout.turnOff")}</a></p>
        </td></tr>
        </table>
        </body></html>
        """;

    /// <summary>
    /// Writes the templates into SqlOS's store: a missing one is created, a changed one updated (SqlOS raises its
    /// version only when the content differs), an equal one left alone. Answers the keys it wrote.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ReconcileAsync(WorkstreamDbContext db, SqlOSEmailAdminService admin, CancellationToken ct)
    {
        // These and the emails that share a task (ShareEmails.cs): every email the API sends.
        IReadOnlyList<CodeTemplate> owned = [.. All, .. ShareEmailTemplates.All];
        var keys = owned.Select(x => x.Key).ToList();
        var existing = await db.Set<SqlOSEmailTemplate>().AsNoTracking().Where(x => keys.Contains(x.Key)).ToDictionaryAsync(x => x.Key, ct);
        var written = new List<string>();
        foreach (var template in owned)
        {
            if (!existing.TryGetValue(template.Key, out var row))
            {
                await admin.CreateTemplateAsync(new SqlOSCreateEmailTemplateRequest(template.Key, template.DisplayName, template.Subject, template.Html, template.Text, CodeTemplate.Sample()), ct);
                written.Add(template.Key);
                continue;
            }
            if (row.IsActive && row.DisplayName == template.DisplayName && row.SubjectTemplate == template.Subject.Trim()
                && row.HtmlBodyTemplate == template.Html.Trim() && row.TextBodyTemplate == template.Text.Trim())
            {
                continue;
            }
            await admin.UpdateTemplateAsync(row.Id, new SqlOSUpdateEmailTemplateRequest(template.Key, template.DisplayName, template.Subject, template.Html, template.Text, CodeTemplate.Sample()), ct);
            written.Add(template.Key);
        }
        return written;
    }
}

/// <summary>
/// The job that emails a task's people when the agent's turn ends (docs/core-model.md, "Hear when a task is
/// finished or needs you"). The exchange that ends a turn queues it a few seconds ahead; it runs as
/// <c>agent::task-notifier</c>, which holds <c>TASK_NOTIFY</c> on every task and nothing else. It sends
/// nothing where email is not set up (development, staging), and nothing when the agent is working again, when
/// someone has already answered, when the agent's last word was a line about what it did rather than
/// something it said, or when the task emailed in the last ten minutes. Each person gets it who started the
/// task or wrote in it, may still see it (<c>TASK_VIEW</c>), has not turned these emails off and has not had
/// the task's desktop on screen in the last two minutes.
/// </summary>
public sealed class TaskEmails(IServiceScopeFactory scopes, ISqlOSEmailSender sender, WorkstreamApp app, TimeProvider clock, ILogger<TaskEmails> log)
{
    const string Notifier = PlatformSeed.NotifierSubjectId;
    /// <summary>How long after a turn ends the job runs: time for the person to answer, or the agent to go on, first.</summary>
    public static readonly TimeSpan After = TimeSpan.FromSeconds(15);

    /// <summary>Answers how many people it wrote to.</summary>
    [AutomaticRetry(Attempts = 2)]
    public async Task<int> SendAsync(string taskId, CancellationToken ct)
    {
        if (!sender.IsConfigured) return 0;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
        var fga = scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>();
        var task = await db.Tasks.SingleOrDefaultAsync(x => x.Id == taskId, ct);
        if (task is null || task.State != DesktopState.Ready || task.AgentWorking) return 0;
        if (!await fga.Allows(Notifier, Permissions.TaskNotify, task.ResourceId))
        {
            log.LogError("The task notifier may not email about task {TaskId}.", taskId);
            return 0;
        }
        var now = clock.GetUtcNow();
        if (!task.MayEmail(now, Turns.EmailGap)) return 0;
        // The turn ended on the agent's words, and nobody has answered them yet.
        // A picture the desktop added after the report is part of the same hand-over, not something said since.
        var last = await db.TaskMessages.AsNoTracking().Where(x => x.TaskId == taskId && x.Kind != MessageKind.Picture).OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        if (last is not { Author: MessageAuthor.Agent, Kind: MessageKind.Text }) return 0;

        var writers = await db.TaskMessages.AsNoTracking().Where(x => x.TaskId == taskId && x.Author == MessageAuthor.Person).Select(x => x.AuthorId).Distinct().ToListAsync(ct);
        var candidates = writers.Append(task.CreatedBy).Distinct().ToList();
        // Someone typing at the desktop writes as the desktop, which is no person and has no address.
        var people = await db.Set<SqlOSUser>().AsNoTracking()
            .Where(x => candidates.Contains(x.Id) && x.IsActive && x.DefaultEmail != null)
            .Select(x => new { x.Id, Email = x.DefaultEmail! }).ToListAsync(ct);
        var off = await db.PersonSettings.AsNoTracking().Where(x => candidates.Contains(x.UserId) && !x.TaskEmails).Select(x => x.UserId).ToListAsync(ct);
        var since = now - Turns.WatchedWithin;
        var watching = await db.TaskWatchers.AsNoTracking().Where(x => x.TaskId == taskId && x.At > since).Select(x => x.UserId).ToListAsync(ct);
        var recipients = new List<(string Id, string Email)>();
        foreach (var person in people.Where(x => !off.Contains(x.Id) && !watching.Contains(x.Id)))
        {
            // Only someone who may still see the task: a person who left the team hears nothing more of it.
            if (await fga.Allows(person.Id, Permissions.TaskView, task.ResourceId)) recipients.Add((person.Id, person.Email));
        }
        if (recipients.Count == 0) return 0;

        var team = (await Operations.TeamNamesAsync(db, [task.TeamId], ct)).GetValueOrDefault(task.TeamId) ?? "";
        var teamPath = $"{app.WebOrigin}/t/{Uri.EscapeDataString(task.TeamId)}";
        var variables = new Dictionary<string, object?>
        {
            ["title"] = task.Title,
            ["team"] = team,
            ["message"] = Turns.Excerpt(last.Text),
            ["link"] = $"{teamPath}?open={Uri.EscapeDataString(task.Id)}",
            ["settingsLink"] = $"{teamPath}/team",
        };
        var template = TaskEmailTemplates.KeyFor(Turns.EndingOf(last.Text));
        var email = scope.ServiceProvider.GetRequiredService<ISqlOSTransactionalEmailService>();
        var sent = 0;
        foreach (var (id, address) in recipients)
        {
            // Someone the task is shared with is not on its team: the task opens in their own workspace, and the
            // switch is on their own team's page.
            var theirs = variables;
            if (!await db.Set<SqlOSMembership>().AnyAsync(x => x.OrganizationId == task.TeamId && x.UserId == id && x.IsActive, ct))
            {
                var own = await db.Set<SqlOSMembership>().AsNoTracking().Where(x => x.UserId == id && x.IsActive).OrderBy(x => x.CreatedAt).Select(x => x.OrganizationId).FirstOrDefaultAsync(ct);
                theirs = new Dictionary<string, object?>(variables)
                {
                    ["link"] = $"{app.WebOrigin}/home?open={Uri.EscapeDataString(task.Id)}",
                    ["settingsLink"] = own is null ? $"{app.WebOrigin}/home" : $"{app.WebOrigin}/t/{Uri.EscapeDataString(own)}/team",
                };
            }
            // One email a person a turn, however many times the job runs.
            var result = await email.SendAsync(new SqlOSSendEmailRequest(template, address, theirs, $"task:{task.Id}:turn:{last.Id}:to:{id}"), ct);
            if (result.Status == SqlOSEmailDeliveryStatuses.Failed) log.LogWarning("The email about task {TaskId} could not be sent: {Error}", taskId, result.SanitizedError);
            else sent++;
        }
        task.Emailed(now);
        await db.SaveChangesAsync(ct);
        return sent;
    }
}
