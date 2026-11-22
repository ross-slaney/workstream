using SqlOS.Fga.Interfaces;

namespace Workstream.Api.Domain;

/// <summary>
/// Where a task's desktop is in its life. The machine is billed while it is starting, ready or stopping.
/// People read <see cref="Stopping"/> and <see cref="Stopped"/> as pausing and paused: the names here are the
/// ones the rows already hold.
/// </summary>
public enum DesktopState
{
    /// <summary>The machine is being made or started, or has not yet exchanged with the API.</summary>
    Starting,
    /// <summary>The desktop is up and its agent is exchanging with the API.</summary>
    Ready,
    /// <summary>Asked to pause; the machine is being released.</summary>
    Stopping,
    /// <summary>Paused: the machine is released. Its disk is kept, so starting brings the same desktop back.</summary>
    Stopped,
    /// <summary>Paused for as long as its team allows; its disk is being copied to an archive.</summary>
    Archiving,
    /// <summary>
    /// Archived: the disk is kept as a copy (<see cref="WorkTask.Archive"/>) and the machine and its disk are
    /// deleted. Starting makes a new machine from the copy.
    /// </summary>
    Archived,
    /// <summary>Starting or stopping failed; <see cref="WorkTask.Failure"/> says why.</summary>
    Failed,
    /// <summary>Asked to be deleted; the machine, the conversation and the node are being removed.</summary>
    Deleting,
}

/// <summary>How far a starting desktop has come. It says nothing once the desktop is ready or stopped.</summary>
public enum StartStep
{
    /// <summary>The host is making the machine, or turning on the one the task has.</summary>
    Machine,
    /// <summary>The machine is on and starting up.</summary>
    Boot,
    /// <summary>The display answers, so the desktop can be watched. Its agent has not exchanged with the API yet.</summary>
    Agent,
}

/// <summary>
/// A task: one piece of work, with a desktop and a conversation with the agent on it (docs/core-model.md).
/// A node under its team; everyone on the team can see and use it through the team's one grant, and someone outside
/// the team it is shared with through a grant on this node alone (Operations/Sharing.cs). This row also
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
    /// <summary>Where the API reaches the desktop's display: <c>host:port</c> on the private network. Null while it has none.</summary>
    public string? Address { get; private set; }
    /// <summary>When, in this start, the host said the machine was on.</summary>
    public DateTimeOffset? OnAt { get; private set; }
    /// <summary>When, in this start, the display first answered.</summary>
    public DateTimeOffset? DisplayAt { get; private set; }

    /// <summary>When the desktop last exchanged with the API.</summary>
    public DateTimeOffset? SeenAt { get; private set; }
    /// <summary>
    /// When the desktop was last used: a person's mouse or keyboard reached it, someone wrote in the
    /// conversation, or its agent was working. Having it on screen is not use.
    /// </summary>
    public DateTimeOffset ActiveAt { get; private set; }
    /// <summary>Whether the agent is in the middle of something, as the desktop last said.</summary>
    public bool AgentWorking { get; private set; }
    /// <summary>How many times a person has asked the agent to stop what it is doing. The desktop acts when it changes.</summary>
    public int Interrupts { get; private set; }
    /// <summary>When the task last emailed people that it was finished or needed them. Null until it first did.</summary>
    public DateTimeOffset? EmailedAt { get; private set; }

    /// <summary>How much is on the desktop's disk, in bytes, as its agent last said. Null until it has.</summary>
    public long? DiskBytes { get; private set; }
    /// <summary>
    /// The archive's name at the desktop host: the copy of the disk an archived desktop comes back from. Null
    /// when there is none. It is kept until the machine made from it has shown its screen.
    /// </summary>
    public string? Archive { get; private set; }
    /// <summary>How much the archive holds, in bytes: what was on the disk when it was copied.</summary>
    public long? ArchiveBytes { get; private set; }

    /// <summary>Whether the machine is in use and counts toward the team's limit.</summary>
    public bool IsRunning => State is DesktopState.Starting or DesktopState.Ready or DesktopState.Stopping;

    /// <summary>How far this start has come, from what has happened so far.</summary>
    public StartStep Step => DisplayAt is not null ? StartStep.Agent : OnAt is not null ? StartStep.Boot : StartStep.Machine;

    /// <summary>
    /// Whether there is a screen to show: the display answers, which is before the agent's first exchange, and
    /// until the desktop is asked to stop.
    /// </summary>
    public bool CanBeWatched => Address is not null && (State == DesktopState.Ready || State == DesktopState.Starting && DisplayAt is not null);

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

    /// <summary>The desktop host is making the machine, or starting it again: its name there, and where its display will be.</summary>
    public void DesktopMade(string host, string address)
    {
        Host = host;
        Address = address;
    }

    /// <summary>
    /// The task starts on a spare, which is already running with its display up: there is a screen to watch at
    /// once, and only the agent is waited for.
    /// </summary>
    public void TakeSpare(Spare spare, DateTimeOffset now)
    {
        if (State != DesktopState.Starting || Host is not null) throw new DomainRuleException("Only a task with no desktop yet can take a spare.");
        if (spare.TeamId != TeamId) throw new DomainRuleException("The spare belongs to another team.");
        Host = spare.Host;
        Address = spare.Address;
        OnAt = now;
        DisplayAt = now;
    }

    /// <summary>The machine is gone from the host: a new one that could not be started was removed. The next start makes another.</summary>
    public void MachineRemoved()
    {
        Host = null;
        Address = null;
    }

    /// <summary>The host said the machine is on.</summary>
    public void MachineOn(DateTimeOffset now)
    {
        if (State == DesktopState.Starting) OnAt ??= now;
    }

    /// <summary>The display answered: from now the desktop can be watched.</summary>
    public void DisplayUp(DateTimeOffset now)
    {
        if (State != DesktopState.Starting) return;
        OnAt ??= now;
        DisplayAt ??= now;
    }

    /// <summary>
    /// The desktop exchanged with the API: it is up, and this is what its agent is doing. Answers whether this
    /// exchange is the one that made a starting desktop ready.
    /// </summary>
    public bool DesktopSeen(bool agentWorking, DateTimeOffset now, long? diskBytes = null)
    {
        SeenAt = now;
        // An agent at work is the desktop in use, and so is the moment it finishes: the idle time counts from there.
        if (agentWorking || AgentWorking) ActiveAt = now;
        AgentWorking = agentWorking;
        if (diskBytes is > 0) DiskBytes = diskBytes;
        if (State != DesktopState.Starting) return false;
        Become(DesktopState.Ready, now);
        return true;
    }

    /// <summary>
    /// Whether the desktop's exchange, saying the agent is or is not working, ends the agent's turn: it was
    /// working and now is not. Asked before <see cref="DesktopSeen"/> records the exchange.
    /// </summary>
    public bool EndsTurn(bool agentWorking) => State == DesktopState.Ready && AgentWorking && !agentWorking;

    /// <summary>
    /// Whether the task may email people about a turn now: at most one email every <paramref name="gap"/>,
    /// however many turns end in between.
    /// </summary>
    public bool MayEmail(DateTimeOffset now, TimeSpan gap) => EmailedAt is not { } last || now - last >= gap;

    /// <summary>The task emailed people about a turn.</summary>
    public void Emailed(DateTimeOffset now) => EmailedAt = now;

    /// <summary>Pause the desktop: a person asked, or it has gone unused for as long as its team allows.</summary>
    public void Stop(DateTimeOffset now)
    {
        if (State is not (DesktopState.Starting or DesktopState.Ready)) throw new DomainRuleException("Only a running desktop can be paused.");
        AgentWorking = false;
        Become(DesktopState.Stopping, now);
    }

    public void Stopped(DateTimeOffset now)
    {
        AgentWorking = false;
        // A stopped machine is nowhere: the next start says where it is again.
        Address = null;
        Become(DesktopState.Stopped, now);
    }

    /// <summary>
    /// Whether the desktop has gone unused for longer than <paramref name="after"/>: nobody's mouse or keyboard,
    /// no message, and no agent at work. A desktop that is only on someone's screen is unused.
    /// </summary>
    public bool IsIdle(DateTimeOffset now, TimeSpan after) => State == DesktopState.Ready && now - ActiveAt > after;

    /// <summary>Whether the desktop is paused, with a machine, and has been for longer than <paramref name="after"/>: its disk is then archived.</summary>
    public bool IsDueForArchive(DateTimeOffset now, TimeSpan after) => State == DesktopState.Stopped && Host is not null && now - StateChangedAt > after;

    /// <summary>
    /// The disk is being copied to an archive. When the desktop was paused is kept (<see cref="StateChangedAt"/>),
    /// so a copy that could not be made is tried again at the next sweep.
    /// </summary>
    public void Archiving()
    {
        if (State != DesktopState.Stopped || Host is null) throw new DomainRuleException("Only a paused desktop can be archived.");
        State = DesktopState.Archiving;
    }

    /// <summary>The copy could not be made: the desktop is paused as it was, with its machine and disk.</summary>
    public void NotArchived()
    {
        if (State == DesktopState.Archiving) State = DesktopState.Stopped;
    }

    /// <summary>
    /// The disk is copied to <paramref name="archive"/>: the machine and its disk are no longer the task's, and
    /// are deleted. The archive holds what was on the disk, which the desktop's agent said; the host's own
    /// figure (<paramref name="hostBytes"/>) stands in for a desktop that never did.
    /// </summary>
    public void Archived(string archive, long? hostBytes, DateTimeOffset now)
    {
        if (State != DesktopState.Archiving) throw new DomainRuleException("Only a desktop being archived can become archived.");
        Archive = archive;
        ArchiveBytes = DiskBytes ?? hostBytes;
        Host = null;
        Address = null;
        Become(DesktopState.Archived, now);
    }

    /// <summary>The machine made from the archive has shown its screen: the archive is no longer needed.</summary>
    public string? Restored()
    {
        if (Host is null || State is not (DesktopState.Starting or DesktopState.Ready)) return null;
        var archive = Archive;
        Archive = null;
        ArchiveBytes = null;
        return archive;
    }

    /// <summary>Whether starting the desktop brings back one that was paused or archived, which whoever uses the task may do.</summary>
    public bool CanResume => State is DesktopState.Stopped or DesktopState.Archived;

    /// <summary>What the next start begins from: the task's own paused machine, its archive, or a new machine.</summary>
    public string StartsFrom => Host is not null ? StartSource.Stopped : Archive is not null ? StartSource.Archive : StartSource.Image;

    public void StartAgain(DateTimeOffset now)
    {
        if (State == DesktopState.Archiving) throw new DomainRuleException("The desktop is being archived. Try again in a minute.");
        if (State is not (DesktopState.Stopped or DesktopState.Archived or DesktopState.Failed)) throw new DomainRuleException("Only a paused desktop can be started.");
        ActiveAt = now;
        Address = null;
        OnAt = null;
        DisplayAt = null;
        Become(DesktopState.Starting, now);
    }

    public void Fail(string why, DateTimeOffset now)
    {
        AgentWorking = false;
        Address = null;
        Become(DesktopState.Failed, now);
        Failure = why.Length <= 500 ? why : why[..500];
    }

    public void Delete(DateTimeOffset now)
    {
        AgentWorking = false;
        Become(DesktopState.Deleting, now);
    }

    /// <summary>A person's mouse or keyboard reached the desktop, or someone wrote in the conversation: it is in use.</summary>
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
/// One entry in a task's conversation. Not a node: a message is never granted on its own, so <c>TASK_PROMPT</c> on
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

/// <summary>What a machine was running for. Each is a line of what the desktops cost.</summary>
public static class MachinePurpose
{
    /// <summary>A task's desktop.</summary>
    public const string Task = "task";
    /// <summary>A spare, kept ready for a team's next task (<see cref="Spare"/>).</summary>
    public const string Spare = "spare";
    /// <summary>A machine doing an environment's setup once, to be saved (<see cref="EnvironmentSetup"/>).</summary>
    public const string Prepare = "prepare";
}

/// <summary>What a task's desktop started from, which is most of how long the start took.</summary>
public static class StartSource
{
    /// <summary>A new machine, made from the image.</summary>
    public const string Image = "image";
    /// <summary>A spare that was already running.</summary>
    public const string Spare = "spare";
    /// <summary>A new machine, made from the environment's saved setup.</summary>
    public const string Save = "save";
    /// <summary>The task's own machine, paused earlier.</summary>
    public const string Stopped = "stopped";
    /// <summary>A new machine, made from the task's archive.</summary>
    public const string Archive = "archive";
}

/// <summary>
/// One stretch of a machine running: from when it was asked for until it stopped, failed, was deleted or, for
/// a spare, was taken by a task. Every hour a machine runs is in one of these, with what it ran for, so what
/// the desktops cost can be added up by purpose (<see cref="DesktopOptions"/> has the prices). A team's desktop
/// hours are its task runs. A task's run also keeps when each step of the start happened, which is how long a
/// start really takes.
/// </summary>
public sealed class DesktopRun
{
    /// <summary>For EF Core.</summary>
    DesktopRun()
    {
    }

    public string Id { get; private set; } = "";
    /// <summary>The task. For a spare's run, the spare's own id.</summary>
    public string TaskId { get; private set; } = "";
    public string TeamId { get; private set; } = "";
    public string Size { get; private set; } = "";
    /// <summary>One of <see cref="MachinePurpose"/>.</summary>
    public string Purpose { get; private set; } = MachinePurpose.Task;
    /// <summary>One of <see cref="StartSource"/>. A spare's own run started from the image.</summary>
    public string Source { get; private set; } = StartSource.Image;
    public DateTimeOffset StartedAt { get; private set; }
    /// <summary>When the host said the machine was on.</summary>
    public DateTimeOffset? OnAt { get; private set; }
    /// <summary>When the display first answered: the person had a screen to watch.</summary>
    public DateTimeOffset? DisplayAt { get; private set; }
    /// <summary>When the agent first exchanged with the API.</summary>
    public DateTimeOffset? ReadyAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }

    public static DesktopRun Begin(WorkTask task, DateTimeOffset now, string source = StartSource.Image) => new()
    {
        Id = Guid.NewGuid().ToString("D"), TaskId = task.Id, TeamId = task.TeamId, Size = task.Size, StartedAt = now, Source = source,
    };

    /// <summary>A preparing machine's time, from when it is asked for until its disk is saved or the attempt fails.</summary>
    public static DesktopRun BeginPrepare(string runId, WorkEnvironment environment, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid().ToString("D"), TaskId = runId, TeamId = environment.TeamId, Size = environment.Size, StartedAt = now, Purpose = MachinePurpose.Prepare,
    };

    /// <summary>The task's machine was made from the environment's saved setup.</summary>
    public void FromSave() => Source = StartSource.Save;

    /// <summary>A spare's time, from when its machine is asked for until a task takes it or it is removed.</summary>
    public static DesktopRun Begin(Spare spare, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid().ToString("D"), TaskId = spare.Id, TeamId = spare.TeamId, Size = spare.Size, StartedAt = now, Purpose = MachinePurpose.Spare,
    };

    public void MachineOn(DateTimeOffset now) => OnAt ??= now;

    public void DisplayUp(DateTimeOffset now)
    {
        OnAt ??= now;
        DisplayAt ??= now;
    }

    public void Ready(DateTimeOffset now) => ReadyAt ??= now;

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
