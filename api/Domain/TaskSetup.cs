namespace Workstream.Api.Domain;

/// <summary>How the setup script stands on a task's own desktop.</summary>
public enum TaskSetupState
{
    /// <summary>The script is running on the desktop now.</summary>
    Running,
    /// <summary>It ended well.</summary>
    Done,
    /// <summary>It stopped with an error; <see cref="TaskSetup.Exit"/> is its exit code.</summary>
    Failed,
    /// <summary>It ran for as long as a setup may (<c>Workstream:Desktops:SetupMinutes</c>) and the desktop stopped it.</summary>
    TimedOut,
}

/// <summary>
/// The environment's setup script as it runs on one task's desktop: a task whose desktop started from the
/// image does the setup itself, where the person can watch, and this is what the task's screen shows of it
/// while it runs and after it failed. The desktop says how it is going (<c>POST /api/desktop/setup</c>, the
/// same words a preparing machine uses for an environment's saved setup). Not a node and nobody's grant:
/// seeing it is seeing the task. One row a task that ever ran a setup, deleted with the task.
/// </summary>
public sealed class TaskSetup
{
    public const int MaxOutputLength = EnvironmentSetup.MaxOutputLength;
    /// <summary>The exit code a script is given when it is stopped for running too long, as <c>timeout</c> gives.</summary>
    public const int TimedOutExit = 124;

    /// <summary>For EF Core.</summary>
    TaskSetup()
    {
    }

    public string TaskId { get; private set; } = "";
    public TaskSetupState State { get; private set; }
    /// <summary>The script's exit code once it ended. Null while it runs.</summary>
    public int? Exit { get; private set; }
    /// <summary>The last lines the script wrote.</summary>
    public string? Output { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    /// <summary>How long the script was allowed when it started, in minutes: what "it did not finish in time" means for this run.</summary>
    public int LimitMinutes { get; private set; }

    public static TaskSetup Begin(WorkTask task, int limitMinutes, DateTimeOffset now) => new() { TaskId = task.Id, LimitMinutes = limitMinutes, StartedAt = now };

    /// <summary>
    /// What the script has written so far. Said after it ended, the script is running again (the desktop
    /// started over before the setup was done), and this is a new run.
    /// </summary>
    public void Wrote(string? output, int limitMinutes, DateTimeOffset now)
    {
        if (State != TaskSetupState.Running)
        {
            State = TaskSetupState.Running;
            StartedAt = now;
            EndedAt = null;
            Exit = null;
            Output = null;
            LimitMinutes = limitMinutes;
        }
        Output = Tail(output) ?? Output;
    }

    /// <summary>The script ended: well with 0, stopped for running too long with <see cref="TimedOutExit"/>, with an error otherwise.</summary>
    public void Ended(int exit, string? output, DateTimeOffset now)
    {
        State = exit switch
        {
            0 => TaskSetupState.Done,
            TimedOutExit => TaskSetupState.TimedOut,
            _ => TaskSetupState.Failed,
        };
        Exit = exit;
        Output = Tail(output) ?? Output;
        EndedAt = now;
    }

    static string? Tail(string? output)
    {
        var text = (output ?? "").TrimEnd();
        if (text.Length == 0) return null;
        return text.Length <= MaxOutputLength ? text : text[^MaxOutputLength..];
    }
}
