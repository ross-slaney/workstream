namespace Workstream.Api.Domain;

/// <summary>Where a spare is in its short life.</summary>
public enum SpareState
{
    /// <summary>Its machine is being made, or has not asked the API anything yet.</summary>
    Starting,
    /// <summary>Its desktop is up and its agent is asking for a task: the team's next task can start on it at once.</summary>
    Ready,
}

/// <summary>
/// A desktop made ahead of a task, for one team, while someone on the team is in the workspace: by the time
/// they have written what they want done, it is already running, and the task starts on it instead of waiting
/// for a machine (docs/core-model.md, "A spare desktop").
///
/// A spare is not a node and nobody is granted on it. It is the desktop operator's stock for a team. What makes
/// it a task's desktop is a grant: its service account (<c>service_account::desktop-{Id}</c>) is given
/// <c>task_desktop</c> on the task, and this row is deleted. Until then its key opens nothing. A spare is never
/// given to another team, and one that nothing takes is removed.
/// </summary>
public sealed class Spare
{
    /// <summary>For EF Core.</summary>
    Spare()
    {
    }

    /// <summary>The desktop's id: its service account and its key are named for it.</summary>
    public string Id { get; private set; } = "";
    /// <summary>The one team it is for. A team has at most one.</summary>
    public string TeamId { get; private set; } = "";
    /// <summary>The environment it was made for: the one the team used last.</summary>
    public string EnvironmentId { get; private set; } = "";
    public string Size { get; private set; } = DesktopSizes.Default;
    public SpareState State { get; private set; }
    /// <summary>The machine's name at the desktop host. Null until one is asked for.</summary>
    public string? Host { get; private set; }
    public string? Address { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    /// <summary>When someone on the team was last in the workspace. A spare nobody has wanted for a while is removed.</summary>
    public DateTimeOffset WantedAt { get; private set; }
    /// <summary>When its agent last asked the API for a task.</summary>
    public DateTimeOffset? SeenAt { get; private set; }
    /// <summary>The environment's saved setup it was made from. Null when it was made from the image.</summary>
    public string? Save { get; private set; }

    public static Spare For(Team team, WorkEnvironment environment, string? save, DateTimeOffset now)
    {
        if (environment.TeamId != team.Id) throw new DomainRuleException("The environment belongs to another team.");
        return new Spare
        {
            Id = Guid.NewGuid().ToString("D"),
            TeamId = team.Id,
            EnvironmentId = environment.Id,
            Save = save,
            Size = environment.Size,
            State = SpareState.Starting,
            CreatedAt = now,
            WantedAt = now,
        };
    }

    /// <summary>The desktop host is making the machine: its name there, and where its display will be.</summary>
    public void Made(string host, string address)
    {
        Host = host;
        Address = address;
    }

    /// <summary>Someone on the team is in the workspace again.</summary>
    public void Wanted(DateTimeOffset now) => WantedAt = now;

    /// <summary>Its agent asked the API what it is for: the desktop is up.</summary>
    public void Seen(DateTimeOffset now)
    {
        SeenAt = now;
        if (Address is not null) State = SpareState.Ready;
    }

    /// <summary>
    /// Whether a task from this environment can start on it now. It is the same size of machine; it was made
    /// from what a new machine for the task would be made from (<paramref name="save"/>: the environment's
    /// saved setup, or null for the image), so a task never gets another environment's files or an older
    /// setup; and its agent asked a moment ago, so it is really there.
    /// </summary>
    public bool Serves(WorkEnvironment environment, string? save, DateTimeOffset now) =>
        State == SpareState.Ready && Address is not null && Size == environment.Size && Save == save
        && (save is null || EnvironmentId == environment.Id)
        && SeenAt is { } seen && now - seen < TimeSpan.FromSeconds(30);
}
