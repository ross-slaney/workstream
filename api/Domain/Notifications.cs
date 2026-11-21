namespace Workstream.Api.Domain;

/// <summary>
/// A person's own settings, kept by their user id (docs/core-model.md, "App data"). A setting is a preference,
/// not access: it relates a person to nothing in the tree. A person with no row has every default.
/// </summary>
public sealed class PersonSettings
{
    /// <summary>For EF Core.</summary>
    PersonSettings()
    {
    }

    /// <summary>The SqlOS user id.</summary>
    public string UserId { get; private set; } = "";
    /// <summary>Whether Workstream emails them when a task they started or wrote in is finished or needs them. On by default.</summary>
    public bool TaskEmails { get; private set; } = true;
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The settings of someone who has not changed any.</summary>
    public static PersonSettings For(string userId, DateTimeOffset now) => new()
    {
        UserId = Rules.Principal(userId, "Whose settings they are"),
        UpdatedAt = now,
    };

    public void ChangeTaskEmails(bool on, DateTimeOffset now)
    {
        TaskEmails = on;
        UpdatedAt = now;
    }
}

/// <summary>
/// When a person last had a task's desktop on screen: the relay writes it when it lets them in and each minute
/// while the stream stays open (Desktops/DesktopRelay.cs). A fact about what someone did, like
/// <see cref="WorkTask.CreatedBy"/>, and never a permission: whether they may watch is <c>TASK_USE</c>. Read
/// only to keep from emailing someone about a task they are looking at. Removed with the task.
/// </summary>
public sealed class TaskWatcher
{
    /// <summary>For EF Core.</summary>
    TaskWatcher()
    {
    }

    public string TaskId { get; private set; } = "";
    /// <summary>The SqlOS user id of the person watching.</summary>
    public string UserId { get; private set; } = "";
    public DateTimeOffset At { get; private set; }

    public static TaskWatcher Of(WorkTask task, string userId, DateTimeOffset now) => new()
    {
        TaskId = task.Id,
        UserId = Rules.Principal(userId, "Who is watching"),
        At = now,
    };

    public void Seen(DateTimeOffset now) => At = now;
}

/// <summary>What the agent's turn ended with, for the email about it.</summary>
public enum TurnEnding
{
    /// <summary>The agent stopped and said what it did.</summary>
    Finished,
    /// <summary>The agent stopped on a question: someone has to answer before it goes on.</summary>
    NeedsYou,
}

/// <summary>The rules about the end of an agent's turn, written once.</summary>
public static class Turns
{
    /// <summary>At most one email a task, however many turns end in this time.</summary>
    public static readonly TimeSpan EmailGap = TimeSpan.FromMinutes(10);
    /// <summary>Someone who had the task's desktop on screen this recently is looking at it already.</summary>
    public static readonly TimeSpan WatchedWithin = TimeSpan.FromMinutes(2);
    /// <summary>How much of the agent's last message an email quotes.</summary>
    public const int ExcerptLength = 600;

    /// <summary>
    /// A turn that ends on a question needs someone: the agent's last words end with a question mark, after any
    /// closing quotes, brackets or emphasis.
    /// </summary>
    public static TurnEnding EndingOf(string lastAgentText)
    {
        var text = (lastAgentText ?? "").TrimEnd();
        var end = text.Length;
        while (end > 0 && (char.IsWhiteSpace(text[end - 1]) || "*_`\"')]”’".Contains(text[end - 1]))) end--;
        return end > 0 && text[end - 1] is '?' or '？' ? TurnEnding.NeedsYou : TurnEnding.Finished;
    }

    /// <summary>The agent's words for an email: whole when short, else cut at a word with an ellipsis.</summary>
    public static string Excerpt(string text)
    {
        var body = (text ?? "").Trim();
        if (body.Length <= ExcerptLength) return body;
        var cut = body.LastIndexOf(' ', ExcerptLength - 1);
        return body[..(cut > ExcerptLength / 2 ? cut : ExcerptLength - 1)].TrimEnd() + "…";
    }
}
