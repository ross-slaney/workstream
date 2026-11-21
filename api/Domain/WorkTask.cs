using SqlOS.Fga.Interfaces;

namespace Workstream.Api.Domain;

/// <summary>Where a task's desktop is in its life. The machine is billed while it is starting, ready or stopping.</summary>
public enum DesktopState
{
    /// <summary>The machine is being made or started, or has not yet exchanged with the API.</summary>
    Starting,
    /// <summary>The desktop is up and its agent is exchanging with the API.</summary>
    Ready,
    /// <summary>Asked to stop; the machine is being released.</summary>
    Stopping,
    /// <summary>The machine is released. Its disk is kept, so starting brings the same desktop back.</summary>
    Stopped,
    /// <summary>Starting or stopping failed; <see cref="WorkTask.Failure"/> says why.</summary>
    Failed,
    /// <summary>Asked to be deleted; the machine, the conversation and the node are being removed.</summary>
    Deleting,
}

/// <summary>
/// A task: one piece of work, with a desktop and a conversation with the agent on it (docs/core-model.md).
/// A node under its team; everyone on the team can see and use it through the team's one grant. This row also
/// keeps the desktop's lifecycle. (The class is not named Task because System.Threading.Tasks.Task is.)
/// </summary>
public sealed class WorkTask : ISqlOSResourceEntity
{
    public const int MaxTitleLength = 120;

    /// <summary>For EF Core.</summary>
    WorkTask()
    {
    }

    /// <summary>Made by the client that started it.</summary>
    public string Id { get; private set; } = "";
    public string TeamId { get; private set; } = "";
    public string EnvironmentId { get; private set; } = "";
    public string ResourceId { get; private set; } = "";
    public string Title { get; private set; } = "";
    /// <summary>Who started it: a fact for the list, never a permission.</summary>
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }

    public DesktopState State { get; private set; }
    public DateTimeOffset StateChangedAt { get; private set; }
    /// <summary>Why the desktop is <see cref="DesktopState.Failed"/>, in words a person can act on.</summary>
    public string? Failure { get; private set; }
    /// <summary>A key of <see cref="DesktopSizes"/>, taken from the environment when the task started.</summary>
    public string Size { get; private set; } = DesktopSizes.Default;
    /// <summary>The machine's name at the desktop host. Null until one is made.</summary>
    public string? Host { get; private set; }
    /// <summary>Where the API reaches the desktop's display: <c>host:port</c> on the private network.</summary>
    public string? Address { get; private set; }

    /// <summary>When the desktop last exchanged with the API.</summary>
    public DateTimeOffset? SeenAt { get; private set; }
    /// <summary>When a person last had the desktop on screen, or anyone last wrote in the conversation.</summary>
    public DateTimeOffset ActiveAt { get; private set; }
    /// <summary>Whether the agent is in the middle of something, as the desktop last said.</summary>
    public bool AgentWorking { get; private set; }
    /// <summary>How many times a person has asked the agent to stop what it is doing. The desktop acts when it changes.</summary>
    public int Interrupts { get; private set; }

    /// <summary>Whether the machine is in use and counts toward the team's limit.</summary>
    public bool IsRunning => State is DesktopState.Starting or DesktopState.Ready or DesktopState.Stopping;

    public static WorkTask Start(string id, Team team, WorkEnvironment environment, string? title, string createdBy, DateTimeOffset now)
    {
        if (environment.TeamId != team.Id) throw new DomainRuleException("The environment belongs to another team.");
        var taskId = Rules.ClientId(id, "A task");
        return new WorkTask
        {
            Id = taskId,
            TeamId = team.Id,
            EnvironmentId = environment.Id,
            ResourceId = ResourceIdFor(taskId),
            Title = Rules.Text(title, "A task's title", MaxTitleLength),
            CreatedBy = Rules.Principal(createdBy, "Whoever started it"),
            CreatedAt = now,
            State = DesktopState.Starting,
            StateChangedAt = now,
            Size = environment.Size,
            ActiveAt = now,
        };
    }

    /// <summary>A title from what the person asked for: its first line, shortened.</summary>
    public static string TitleFrom(string? prompt)
    {
        var line = (prompt ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        line = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length == 0) return "New task";
        return line.Length <= 80 ? line : line[..79].TrimEnd() + "…";
    }

    public void Rename(string? title) => Title = Rules.Text(title, "A task's title", MaxTitleLength);

    /// <summary>The desktop host made the machine, or started it again: where it is.</summary>
    public void DesktopMade(string host, string address)
    {
        Host = host;
        Address = address;
    }

    /// <summary>The desktop exchanged with the API: it is up, and this is what its agent is doing.</summary>
    public void DesktopSeen(bool agentWorking, DateTimeOffset now)
    {
        SeenAt = now;
        AgentWorking = agentWorking;
        if (State == DesktopState.Starting) Become(DesktopState.Ready, now);
    }

    public void Stop(DateTimeOffset now)
    {
        if (State is not (DesktopState.Starting or DesktopState.Ready)) throw new DomainRuleException("Only a running desktop can be stopped.");
        AgentWorking = false;
        Become(DesktopState.Stopping, now);
    }

    public void Stopped(DateTimeOffset now)
    {
        AgentWorking = false;
        Become(DesktopState.Stopped, now);
    }

    public void StartAgain(DateTimeOffset now)
    {
        if (State is not (DesktopState.Stopped or DesktopState.Failed)) throw new DomainRuleException("Only a stopped desktop can be started.");
        ActiveAt = now;
        Become(DesktopState.Starting, now);
    }

    public void Fail(string why, DateTimeOffset now)
    {
        AgentWorking = false;
        Become(DesktopState.Failed, now);
        Failure = why.Length <= 500 ? why : why[..500];
    }

    public void Delete(DateTimeOffset now)
    {
        AgentWorking = false;
        Become(DesktopState.Deleting, now);
    }

    /// <summary>A person has the desktop on screen, or someone wrote in the conversation: it is in use.</summary>
    public void Active(DateTimeOffset now) => ActiveAt = now;

    /// <summary>A person asked the agent to stop what it is doing.</summary>
    public void Interrupt() => Interrupts++;

    void Become(DesktopState state, DateTimeOffset now)
    {
        State = state;
        StateChangedAt = now;
        Failure = null;
    }

    public static string ResourceIdFor(string id) => $"task::{id}";

    string ISqlOSResourceEntity.ResourceTypeId => WorkstreamAuthorization.ResourceTypes.Task;
    string ISqlOSResourceEntity.ResourceName => Title.Length > 0 ? Title : $"Task {Id}";
    string? ISqlOSResourceEntity.ParentResourceId => Team.ResourceIdFor(TeamId);
    string? ISqlOSResourceEntity.ResourceDescription => null;
    bool ISqlOSResourceEntity.ResourceIsActive => State != DesktopState.Deleting;
}

public enum MessageAuthor
{
    Person,
    Agent,
}

public enum MessageKind
{
    /// <summary>Something said.</summary>
    Text,
    /// <summary>Something the agent did, in a line: a command it ran, a file it changed, a page it opened.</summary>
    Activity,
}

/// <summary>
/// One entry in a task's conversation. Not a node: a message is never granted on its own, so <c>TASK_USE</c> on
/// the task writes one and <c>TASK_VIEW</c> reads them. <see cref="ResourceId"/> is the task's node, so the
/// conversation is one authorized page over this table. <see cref="Id"/> is also the conversation's order.
/// </summary>
public sealed class TaskMessage : IHasResourceId
{
    public const int MaxPersonLength = 20_000;
    public const int MaxAgentLength = 100_000;

    /// <summary>For EF Core.</summary>
    TaskMessage()
    {
    }

    /// <summary>Given by the database; later messages have larger ids.</summary>
    public long Id { get; private set; }
    public string TaskId { get; private set; } = "";
    /// <summary>The task's node.</summary>
    public string ResourceId { get; private set; } = "";
    /// <summary>The principal that wrote it: a person, or the task's desktop for the agent and for someone typing at the desktop.</summary>
    public string AuthorId { get; private set; } = "";
    public MessageAuthor Author { get; private set; }
    public MessageKind Kind { get; private set; }
    public string Text { get; private set; } = "";
    /// <summary>Made by whoever sent it, so a send that is tried again writes the message once.</summary>
    public string ClientKey { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }

    public static TaskMessage Write(WorkTask task, string authorId, MessageAuthor author, MessageKind kind, string? text, string clientKey, DateTimeOffset now)
    {
        var max = author == MessageAuthor.Person ? MaxPersonLength : MaxAgentLength;
        var body = (text ?? "").Trim();
        if (body.Length == 0) throw new DomainRuleException("A message says something.");
        if (author == MessageAuthor.Person && body.Length > max) throw new DomainRuleException($"A message can be at most {max} characters.");
        return new TaskMessage
        {
            TaskId = task.Id,
            ResourceId = task.ResourceId,
            AuthorId = Rules.Principal(authorId, "Whoever wrote it"),
            Author = author,
            Kind = kind,
            // The agent's words are kept as far as they fit rather than refused.
            Text = body.Length <= max ? body : body[..max],
            ClientKey = Rules.ClientId(clientKey, "A message"),
            CreatedAt = now,
        };
    }
}

/// <summary>
/// One stretch of a desktop running: from when it was asked to start until it stopped, failed or was deleted.
/// What a team's desktop hours are summed from, and what the administrator's graph is drawn from.
/// </summary>
public sealed class DesktopRun
{
    /// <summary>For EF Core.</summary>
    DesktopRun()
    {
    }

    public string Id { get; private set; } = "";
    public string TaskId { get; private set; } = "";
    public string TeamId { get; private set; } = "";
    public string Size { get; private set; } = "";
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }

    public static DesktopRun Begin(WorkTask task, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid().ToString("D"), TaskId = task.Id, TeamId = task.TeamId, Size = task.Size, StartedAt = now,
    };

    public void End(DateTimeOffset now) => EndedAt ??= now;
}

/// <summary>One model call an agent made through the gateway: what it used and what that costs (Model/ModelGateway.cs).</summary>
public sealed class ModelUsage
{
    /// <summary>For EF Core.</summary>
    ModelUsage()
    {
    }

    public long Id { get; private set; }
    public string TeamId { get; private set; } = "";
    public string TaskId { get; private set; } = "";
    public string Model { get; private set; } = "";
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long CacheReadTokens { get; private set; }
    public long CacheWriteTokens { get; private set; }
    public decimal CostUsd { get; private set; }
    public DateTimeOffset At { get; private set; }

    public static ModelUsage Record(string teamId, string taskId, string model, long input, long output, long cacheRead, long cacheWrite, decimal costUsd, DateTimeOffset now) => new()
    {
        TeamId = teamId, TaskId = taskId, Model = model.Length <= 100 ? model : model[..100],
        InputTokens = input, OutputTokens = output, CacheReadTokens = cacheRead, CacheWriteTokens = cacheWrite, CostUsd = costUsd, At = now,
    };
}
