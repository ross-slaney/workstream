namespace Workstream.Api.Domain;

/// <summary>Where a build stands.</summary>
public enum AppBuildState
{
    /// <summary>A desktop asked for it and has not sent the source yet.</summary>
    Waiting,
    /// <summary>The source is here and the Mac has been asked to build it.</summary>
    Queued,
    /// <summary>The Mac took the source and is building.</summary>
    Building,
    /// <summary>The app is kept, for any desktop of the team to take.</summary>
    Done,
    /// <summary>It did not build; <see cref="AppBuild.Failure"/> says why, and the log is kept.</summary>
    Failed,
}

/// <summary>The phones a project's app is built for on a Mac. Android is built on the desktop itself.</summary>
public static class BuildPlatforms
{
    public const string Ios = "ios";

    public static bool Has(string platform) => platform == Ios;
}

/// <summary>
/// One build of a project's iPhone app, made on a Mac because nothing else can make one. A desktop asks for
/// the build of what it has, named by a hash of everything the native app is made from; the team keeps one
/// build a hash, so a second task with the same native code takes the app that is already there, and two
/// that ask at once get one build. The desktop sends the source, the Mac sends back the app and its log.
///
/// Not a node and nobody's grant: a desktop reaches its team's builds through the task it works on
/// (<c>TASK_RUN_AGENT</c>), and the Mac through <c>TEAM_RUN_BUILD</c>. Its seconds on the Mac are the team's
/// usage, like a desktop's hours.
/// </summary>
public sealed class AppBuild
{
    public const int MaxFailureLength = 500;
    public const int MaxDirectoryLength = 200;
    const int HashLength = 64;

    /// <summary>For EF Core.</summary>
    AppBuild()
    {
    }

    public string Id { get; private set; } = "";
    public string TeamId { get; private set; } = "";
    /// <summary>The task whose desktop asked first. A fact, like <see cref="WorkTask.CreatedBy"/>: it grants nothing.</summary>
    public string TaskId { get; private set; } = "";
    public string Platform { get; private set; } = BuildPlatforms.Ios;
    /// <summary>A SHA-256, in hex, of what the native app is made from, as the desktop worked it out.</summary>
    public string Hash { get; private set; } = "";
    /// <summary>The app's folder inside the source the desktop sends; "." when the source is the app.</summary>
    public string Directory { get; private set; } = ".";
    public AppBuildState State { get; private set; }
    public string? Failure { get; private set; }
    /// <summary>The app's bundle identifier, which is what a simulator starts it by.</summary>
    public string? BundleId { get; private set; }
    public long SourceBytes { get; private set; }
    public long AppBytes { get; private set; }
    public DateTimeOffset AskedAt { get; private set; }
    /// <summary>When the Mac took the source.</summary>
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    /// <summary>How long the Mac had it, from taking the source to saying it was done: the team's usage, and what it cost.</summary>
    public int MacSeconds { get; private set; }
    /// <summary>When a desktop last took the app. A build nobody takes for a while is removed.</summary>
    public DateTimeOffset UsedAt { get; private set; }
    /// <summary>Changes with every attempt, so an attempt that was replaced cannot end the one that replaced it.</summary>
    public string Attempt { get; private set; } = "";

    public bool HasEnded => State is AppBuildState.Done or AppBuildState.Failed;

    public static AppBuild Ask(WorkTask task, string platform, string hash, string? directory, DateTimeOffset now)
    {
        if (!BuildPlatforms.Has(platform)) throw new DomainRuleException("A Mac builds iPhone apps: the platform is ios.");
        hash = (hash ?? "").Trim().ToLowerInvariant();
        if (hash.Length != HashLength || !hash.All(Uri.IsHexDigit)) throw new DomainRuleException("The hash is 64 hex characters.");
        return new AppBuild
        {
            Id = Guid.NewGuid().ToString("D"), TeamId = task.TeamId, TaskId = task.Id, Platform = platform, Hash = hash,
            Directory = DirectoryOf(directory), AskedAt = now, UsedAt = now, Attempt = Guid.NewGuid().ToString("N"),
        };
    }

    /// <summary>A folder inside the source, never a way out of it.</summary>
    static string DirectoryOf(string? directory)
    {
        var folder = (directory ?? "").Trim().Replace('\\', '/').Trim('/');
        if (folder.Length == 0 || folder == ".") return ".";
        if (folder.Length > MaxDirectoryLength || folder.Split('/').Any(part => part is "" or "." or "..") || folder.Any(char.IsControl))
        {
            throw new DomainRuleException("The app's folder is a path inside the source.");
        }
        return folder;
    }

    /// <summary>The source arrived: the Mac can be asked.</summary>
    public void SourceArrived(long bytes, DateTimeOffset now)
    {
        if (State != AppBuildState.Waiting) throw new DomainRuleException("This build already has its source.");
        if (bytes <= 0) throw new DomainRuleException("The source is empty.");
        SourceBytes = bytes;
        State = AppBuildState.Queued;
        AskedAt = now;
    }

    /// <summary>The Mac took the source. Taking it twice is the same Mac trying again.</summary>
    public void Take(DateTimeOffset now)
    {
        if (State is not (AppBuildState.Queued or AppBuildState.Building)) throw new DomainRuleException("This build is not waiting for a Mac.");
        State = AppBuildState.Building;
        StartedAt ??= now;
    }

    public void AppArrived(long bytes)
    {
        if (State != AppBuildState.Building) throw new DomainRuleException("This build is not being built.");
        if (bytes <= 0) throw new DomainRuleException("The app is empty.");
        AppBytes = bytes;
    }

    /// <summary>The Mac is done. A build that says it worked and sent no app did not work.</summary>
    public void End(bool ok, string? bundleId, string? failure, DateTimeOffset now)
    {
        if (State != AppBuildState.Building) throw new DomainRuleException("This build is not being built.");
        MacSeconds = SecondsSince(StartedAt, now);
        EndedAt = now;
        if (ok && AppBytes > 0)
        {
            State = AppBuildState.Done;
            BundleId = string.IsNullOrWhiteSpace(bundleId) ? null : bundleId.Trim();
            Failure = null;
            UsedAt = now;
        }
        else
        {
            State = AppBuildState.Failed;
            Failure = Short(string.IsNullOrWhiteSpace(failure) ? "The app did not build. Its log says why." : failure);
        }
    }

    /// <summary>It never got to a Mac, or the Mac never answered.</summary>
    public void Fail(string failure, DateTimeOffset now)
    {
        if (HasEnded) return;
        // A Mac that took it and never answered still had it for that long.
        if (State == AppBuildState.Building) MacSeconds = SecondsSince(StartedAt, now);
        State = AppBuildState.Failed;
        Failure = Short(failure);
        EndedAt = now;
    }

    static int SecondsSince(DateTimeOffset? from, DateTimeOffset now) => from is { } start && now > start ? (int)Math.Ceiling((now - start).TotalSeconds) : 0;

    /// <summary>A desktop asks for a failed build again, after reading why: it starts over and waits for the source.</summary>
    public void TryAgain(WorkTask task, string? directory, DateTimeOffset now)
    {
        if (State != AppBuildState.Failed) throw new DomainRuleException("Only a build that failed is built again.");
        TaskId = task.Id;
        Directory = DirectoryOf(directory);
        State = AppBuildState.Waiting;
        Failure = null;
        BundleId = null;
        SourceBytes = 0;
        AppBytes = 0;
        MacSeconds = 0;
        AskedAt = now;
        StartedAt = null;
        EndedAt = null;
        UsedAt = now;
        Attempt = Guid.NewGuid().ToString("N");
    }

    public void Used(DateTimeOffset now) => UsedAt = now;

    static string Short(string text) => text.Length <= MaxFailureLength ? text : text[..MaxFailureLength];

    /// <summary>Where this build's files are kept: under its team, so nothing of one team's is beside another's.</summary>
    public string FileName(string file) => $"{TeamId}/{Id}/{file}";

    public const string SourceFile = "source.tar.gz";
    public const string AppFile = "app.tar.gz";
    public const string LogFile = "build.log";
}
