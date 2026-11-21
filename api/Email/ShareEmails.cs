using System.Globalization;
using SqlOS.Email.Contracts;
using SqlOS.Email.Interfaces;
using SqlOS.Email.Models;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api.Email;

/// <summary>
/// The two emails that say a task was shared: one to someone with an account, linking to the task, and one to
/// someone without, linking to the page that takes the invitation. Code owns them like the task emails
/// (<see cref="TaskEmailTemplates.ReconcileAsync"/>), every word from <c>client/locales/emails/en.json</c>.
/// SqlOS fills <c>sharer</c>, <c>title</c>, <c>access</c>, <c>ends</c> and <c>link</c> when it sends. An
/// invitation's link carries a token, which opens nothing by itself: whoever takes it must be signed in with
/// the address it was sent to.
/// </summary>
public static class ShareEmailTemplates
{
    public const string Shared = "task.shared";
    public const string SharedInvite = "task.shared-invite";

    public static IReadOnlyList<CodeTemplate> All => [For(Shared, "shared"), For(SharedInvite, "sharedInvite")];

    static CodeTemplate For(string key, string words) => new(
        key,
        EmailWords.Say($"{words}.displayName"),
        EmailWords.Say($"{words}.subject"),
        Html(words),
        string.Join("\n", EmailWords.Say($"{words}.heading"), "", "{access} {ends}", "", EmailWords.Say($"{words}.buttonText"), "", "--", EmailWords.Say($"{words}.why")));

    // The light palette (client/components/theme/palette.ts), as the task emails draw it.
    static string Html(string words) => $"""
        <!doctype html>
        <html><body style="margin:0;padding:0;background:#F3F5F8;">
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#F3F5F8;padding:32px 16px;font-family:-apple-system,'Segoe UI',Helvetica,Arial,sans-serif;color:#14171F;">
        <tr><td align="center">
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:560px;background:#FFFFFF;border:1px solid #DDE1E8;border-radius:8px;text-align:left;">
        <tr><td style="padding:24px 28px 0;font-size:15px;font-weight:600;color:#1F4FE0;">{EmailWords.Html("layout.brand")}</td></tr>
        <tr><td style="padding:16px 28px 0;font-size:20px;line-height:28px;font-weight:600;color:#14171F;">{EmailWords.Html($"{words}.heading")}</td></tr>
        <tr><td style="padding:12px 28px 0;font-size:14px;line-height:22px;color:#566070;">{"{access}"} {"{ends}"}</td></tr>
        <tr><td style="padding:24px 28px 28px;"><a href="{"{link}"}" style="display:inline-block;background:#1F4FE0;color:#FFFFFF;text-decoration:none;font-size:14px;font-weight:600;padding:10px 18px;border-radius:6px;">{EmailWords.Html($"{words}.button")}</a></td></tr>
        </table>
        <p style="max-width:560px;margin:16px auto 0;font-size:12px;line-height:18px;color:#566070;text-align:left;">{EmailWords.Html($"{words}.why")}</p>
        </td></tr>
        </table>
        </body></html>
        """;

    /// <summary>What the person may do, in a sentence of the locale's.</summary>
    public static string AccessWords(string role) => EmailWords.Say(role switch
    {
        Roles.TaskDriver => "shareAccess.driver",
        Roles.TaskPrompter => "shareAccess.prompter",
        _ => "shareAccess.watcher",
    });

    /// <summary>When it ends, in a sentence of the locale's.</summary>
    public static string EndsWords(DateTime? until) => until is { } end
        ? EmailWords.Say("shareAccess.ends").Replace("{until}", end.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture), StringComparison.Ordinal)
        : EmailWords.Say("shareAccess.never");
}

/// <summary>Sending one of them.</summary>
public static class ShareEmail
{
    /// <summary>
    /// Send it where email is set up. False when it was not sent; the share stands either way and its link is in
    /// the answer, for the sharer to pass on. <paramref name="eventKey"/> makes a send tried again write once.
    /// </summary>
    public static async Task<bool> SendAsync(ISqlOSEmailSender sender, ISqlOSTransactionalEmailService email, ILogger log, string to, string sharer, string title,
        string role, DateTime? until, string link, bool newcomer, string eventKey, CancellationToken ct)
    {
        if (!sender.IsConfigured) return false;
        try
        {
            var sent = await email.SendAsync(new SqlOSSendEmailRequest(newcomer ? ShareEmailTemplates.SharedInvite : ShareEmailTemplates.Shared, to, new Dictionary<string, object?>
            {
                ["sharer"] = sharer,
                ["title"] = title,
                ["access"] = ShareEmailTemplates.AccessWords(role),
                ["ends"] = ShareEmailTemplates.EndsWords(until),
                ["link"] = link,
            }, $"task-share:{eventKey}"), ct);
            if (sent.Status != SqlOSEmailDeliveryStatuses.Failed) return true;
            log.LogWarning("The email sharing a task could not be sent: {Error}", sent.SanitizedError);
            return false;
        }
        catch (InvalidOperationException error)
        {
            log.LogWarning(error, "The email sharing a task could not be sent.");
            return false;
        }
    }
}
