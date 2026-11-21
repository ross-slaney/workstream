namespace Workstream.Api.Domain;

/// <summary>How an environment's setup stands, as a person is told it.</summary>
public enum SetupStanding
{
    /// <summary>The environment has no repository and no setup script: there is nothing to prepare.</summary>
    NothingToPrepare,
    /// <summary>Never prepared, or its save is gone. Tasks clone and run the setup themselves, each time.</summary>
    NotPrepared,
    /// <summary>A machine is doing the setup now, or its disk is being saved.</summary>
    Preparing,
    /// <summary>Tasks start from the save.</summary>
    Prepared,
    /// <summary>There is a save, but it is for other settings or an older desktop. Tasks do the setup themselves until it is prepared again.</summary>
    OutOfDate,
    /// <summary>The last attempt failed. Tasks do the setup themselves.</summary>
    Failed,
}

/// <summary>
/// An environment's prepared setup (docs/core-model.md, "A prepared environment"): cloning the repository and
/// running the setup script are done once, on a machine made only for that, and its disk is saved. Tasks then
/// start from the save, with the repository and everything the script installed already there.
///
/// One row for an environment that has ever been prepared. It holds two things: the save tasks start from,
/// with what it was made from, and the attempt in progress or the way the last one failed. It is not a node:
/// seeing it is seeing the environment, and the machine that prepares it is the desktop operator's.
/// </summary>
public sealed class EnvironmentSetup
{
    public const int MaxOutputLength = 4000;

    /// <summary>For EF Core.</summary>
    EnvironmentSetup()
    {
    }

    public string EnvironmentId { get; private set; } = "";
    public string TeamId { get; private set; } = "";

    /// <summary>The save's name at the desktop host. Null when there is none.</summary>
    public string? Save { get; private set; }
    /// <summary>What the save was made from: the environment's setup inputs then (<see cref="WorkEnvironment.SetupInputs"/>).</summary>
    public string? SaveInputs { get; private set; }
    /// <summary>The desktops' image version the save was made on. A save is only for desktops of that version.</summary>
    public string? SaveImage { get; private set; }
    /// <summary>The repository's commit when it was saved.</summary>
    public string? SaveCommit { get; private set; }
    public DateTimeOffset? SavedAt { get; private set; }
    /// <summary>How much is on the saved disk, which is what keeping it costs.</summary>
    public long SaveBytes { get; private set; }
    /// <summary>When a task, or a spare, last started from the save.</summary>
    public DateTimeOffset? UsedAt { get; private set; }

    /// <summary>When someone, or a task that found the save stale, asked for it to be prepared. Null once an attempt has taken it up.</summary>
    public DateTimeOffset? WantedAt { get; private set; }
    /// <summary>The attempt in progress: the preparing machine's desktop id. Null when none is.</summary>
    public string? RunId { get; private set; }
    /// <summary>The preparing machine's name at the desktop host.</summary>
    public string? RunHost { get; private set; }
    /// <summary>The setup inputs the attempt is for. A change to the environment while it runs makes its save out of date at once.</summary>
    public string? RunInputs { get; private set; }
    public string? RunImage { get; private set; }
    public DateTimeOffset? RunStartedAt { get; private set; }
    /// <summary>The setup ended well, and the machine is being stopped and its disk saved.</summary>
    public bool RunSaving { get; private set; }
    /// <summary>The last lines the setup wrote, during an attempt and after one that failed.</summary>
    public string? Output { get; private set; }
    /// <summary>Why the last attempt failed, in words a person can act on. Null when it did not.</summary>
    public string? Failure { get; private set; }
    /// <summary>The setup inputs the failed attempt was for. It is not tried again by itself until they change.</summary>
    public string? FailedInputs { get; private set; }

    /// <summary>
    /// Whether a task that finds no save to start from should ask for one: there is something to prepare,
    /// nothing is preparing, and the last attempt did not just fail on these same settings.
    /// </summary>
    public bool WorthPreparing(WorkEnvironment environment) =>
        environment.HasSetup && !IsPreparing && (Failure is null || FailedInputs != environment.SetupInputs);

    public static EnvironmentSetup For(WorkEnvironment environment) => new() { EnvironmentId = environment.Id, TeamId = environment.TeamId };

    public bool IsPreparing => RunId is not null;

    /// <summary>Whether a task of this environment, on desktops of this image version, starts from the save.</summary>
    public bool Serves(WorkEnvironment environment, string image) => Save is not null && SaveInputs == environment.SetupInputs && SaveImage == image;

    public SetupStanding StandingFor(WorkEnvironment environment, string image)
    {
        if (!environment.HasSetup) return SetupStanding.NothingToPrepare;
        if (IsPreparing) return SetupStanding.Preparing;
        if (Serves(environment, image)) return SetupStanding.Prepared;
        if (Failure is not null) return SetupStanding.Failed;
        return Save is not null ? SetupStanding.OutOfDate : SetupStanding.NotPrepared;
    }

    /// <summary>Why a save that is there is not used. Null when it is used, or there is none.</summary>
    public string? OutOfDateBecause(WorkEnvironment environment, string image) =>
        Save is null || Serves(environment, image) ? null
        : SaveInputs != environment.SetupInputs ? "setup"
        : "desktop";

    /// <summary>Someone asked for it to be prepared, or something found it needs to be.</summary>
    public void Want(DateTimeOffset now) => WantedAt ??= now;

    /// <summary>An attempt begins: a machine is being made to do the setup.</summary>
    public void Begin(string runId, WorkEnvironment environment, string image, DateTimeOffset now)
    {
        if (IsPreparing) throw new DomainRuleException("The environment is already being prepared.");
        RunId = runId;
        RunHost = null;
        RunInputs = environment.SetupInputs;
        RunImage = image;
        RunStartedAt = now;
        RunSaving = false;
        WantedAt = null;
        Output = null;
        Failure = null;
        FailedInputs = null;
    }

    public void RunMade(string host) => RunHost = host;

    /// <summary>What the setup has written so far.</summary>
    public void Wrote(string? output) => Output = Tail(output);

    /// <summary>The setup ended well: what is left is to stop the machine and save its disk.</summary>
    public void SetupDone(string? output, string? commit, long bytes)
    {
        Output = Tail(output);
        SaveCommitPending = commit is { Length: > 0 } ? commit[..Math.Min(commit.Length, 64)] : null;
        SaveBytesPending = Math.Max(0, bytes);
        RunSaving = true;
    }

    /// <summary>Held between the setup ending and the disk being saved.</summary>
    public string? SaveCommitPending { get; private set; }
    public long SaveBytesPending { get; private set; }

    /// <summary>The disk is saved: tasks start from it. Answers the save it replaces, for the caller to remove.</summary>
    public string? Saved(string save, DateTimeOffset now)
    {
        var replaced = Save;
        Save = save;
        SaveInputs = RunInputs;
        SaveImage = RunImage;
        SaveCommit = SaveCommitPending;
        SaveBytes = SaveBytesPending;
        SavedAt = now;
        UsedAt = now;
        EndRun();
        return replaced;
    }

    /// <summary>The attempt failed. The save there was, if any, is kept: it may still be the right one.</summary>
    public void Fail(string why, string? output)
    {
        Failure = why.Length <= 500 ? why : why[..500];
        FailedInputs = RunInputs;
        if (output is not null) Output = Tail(output);
        WantedAt = null;
        EndRun();
    }

    /// <summary>A task, or a spare, started from the save.</summary>
    public void Used(DateTimeOffset now) => UsedAt = now;

    /// <summary>The save is removed: nobody used it for a long time, or its environment's settings moved on and it was replaced by nothing.</summary>
    public string? Forget()
    {
        var removed = Save;
        Save = null;
        SaveInputs = null;
        SaveImage = null;
        SaveCommit = null;
        SavedAt = null;
        SaveBytes = 0;
        return removed;
    }

    void EndRun()
    {
        RunId = null;
        RunHost = null;
        RunInputs = null;
        RunImage = null;
        RunStartedAt = null;
        RunSaving = false;
        SaveCommitPending = null;
        SaveBytesPending = 0;
    }

    static string? Tail(string? output)
    {
        var text = (output ?? "").TrimEnd();
        if (text.Length == 0) return null;
        return text.Length <= MaxOutputLength ? text : text[^MaxOutputLength..];
    }
}
