using SqlOS.Fga.Interfaces;

namespace Workstream.Api.Domain;

/// <summary>
/// What an environment is set to. Variables are handled apart, because their values are protected.
/// <see cref="FromFile"/> says the settings came from the repository's <c>workstream.yml</c>, which a task's
/// start then reads again (<see cref="WorkEnvironment.FromFile"/>).
/// </summary>
public sealed record EnvironmentSettings(
    string Name, IReadOnlyList<string> Apps, string Size, string? RepositoryUrl, string? RepositoryBranch, string? SetupScript, string? Instructions,
    bool FromFile = false);

/// <summary>
/// An environment: what a desktop comes with. The simple part is its surfaces and applications; the rest is optional and is what
/// makes a desktop ready for a particular project: a repository to clone, a script to run once, variables the
/// desktop's programs read, and instructions the agent follows. A node under its team. (The class is not named
/// Environment because System.Environment is.)
/// </summary>
public sealed class WorkEnvironment : ISqlOSResourceEntity
{
    public const int MaxScriptLength = 20_000;
    public const int MaxInstructionsLength = 8_000;
    public const int MaxVariables = 50;
    public const int MaxVariableValueLength = 8_000;

    /// <summary>For EF Core.</summary>
    WorkEnvironment()
    {
    }

    /// <summary>Made by the client that added it.</summary>
    public string Id { get; private set; } = "";
    public string TeamId { get; private set; } = "";
    public string ResourceId { get; private set; } = "";
    public string Name { get; private set; } = "";
    /// <summary>
    /// Ids from the catalog (desktop/apps.json): the desktop's surfaces and applications in one list, surfaces
    /// first. Which of the two an id is comes from the catalog (<see cref="AppCatalog.IsSurface"/>).
    /// </summary>
    public List<string> Apps { get; private set; } = [];
    /// <summary>A key of <see cref="DesktopSizes"/>.</summary>
    public string Size { get; private set; } = DesktopSizes.Default;
    public string? RepositoryUrl { get; private set; }
    public string? RepositoryBranch { get; private set; }
    /// <summary>Run once on a new desktop, as the desktop's user, in the repository when there is one.</summary>
    public string? SetupScript { get; private set; }
    /// <summary>Added to what the agent is told on every desktop made from this environment.</summary>
    public string? Instructions { get; private set; }
    /// <summary>The variables' names, in the order given. Their values are in <see cref="VariablesProtected"/>.</summary>
    public List<string> VariableNames { get; private set; } = [];
    /// <summary>The variables as protected JSON (Operations/Environments.cs). Never returned to a person.</summary>
    public string? VariablesProtected { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }
    /// <summary>
    /// Who last saved its settings: a principal id, a fact. A machine preparing the environment reaches GitHub
    /// with this person's token (Operations/GitHub.cs).
    /// </summary>
    public string ChangedBy { get; private set; } = "";
    /// <summary>
    /// The settings are the repository's <c>workstream.yml</c>: filled from it, or saved to it. Each task's
    /// start reads the file again from GitHub and takes what it says (docs/core-model.md, "workstream.yml").
    /// Only an environment whose repository is on GitHub is.
    /// </summary>
    public bool FromFile { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    /// <summary>
    /// A fingerprint of everything a prepared setup depends on: the repository, the branch, the setup script,
    /// the variables with their values, the surfaces and applications, and the size, whose disk the save is. A saved setup is used only while this is what it was
    /// made from (<see cref="EnvironmentSetup"/>). Written by whoever changes the environment, who can read
    /// the variables; null on an environment not changed since this existed, until it is first needed.
    /// </summary>
    public string? SetupInputs { get; private set; }

    /// <summary>
    /// Whether there is anything to prepare: a repository to clone, a script to run, or the iPhone simulator
    /// to install, which takes a desktop twenty minutes and so is done once, for the saved setup.
    /// </summary>
    public bool HasSetup => RepositoryUrl is not null || SetupScript is not null || Apps.Contains(AppCatalog.Iphone);

    public void KeepSetupInputs(string fingerprint) => SetupInputs = fingerprint;

    public static WorkEnvironment Add(string id, Team team, EnvironmentSettings settings, string createdBy, DateTimeOffset now)
    {
        var environment = new WorkEnvironment
        {
            Id = Rules.ClientId(id, "An environment"),
            TeamId = team.Id,
            CreatedBy = Rules.Principal(createdBy, "Whoever added it"),
            CreatedAt = now,
        };
        environment.ResourceId = ResourceIdFor(environment.Id);
        environment.Change(settings, createdBy, now);
        return environment;
    }

    /// <summary>The environment every new team starts with: a plain desktop with the usual surfaces and applications.</summary>
    public static WorkEnvironment Standard(Team team, string createdBy, DateTimeOffset now) =>
        Add(Guid.NewGuid().ToString("D"), team, new EnvironmentSettings("Standard desktop", AppCatalog.Defaults, DesktopSizes.Default, null, null, null, null), createdBy, now);

    public void Change(EnvironmentSettings settings, string changedBy, DateTimeOffset now)
    {
        ChangedBy = Rules.Principal(changedBy, "Whoever changed it");
        Name = Rules.Text(settings.Name, "An environment's name", 100);
        if (settings.Apps.FirstOrDefault(app => !AppCatalog.Has(app)) is { } unknown) throw new DomainRuleException($"'{unknown}' is not a surface or an app a desktop can come with.");
        var apps = AppCatalog.InOrder(settings.Apps).ToList();
        Apps = apps;
        Size = DesktopSizes.Has(settings.Size) ? settings.Size : throw new DomainRuleException($"A desktop's size is one of {string.Join(", ", DesktopSizes.Keys)}.");
        // The simulator alone takes 9 GB of memory, beside the project's own stack.
        if (apps.Contains(AppCatalog.Iphone) && Size != DesktopSizes.Large) throw new DomainRuleException("The iPhone simulator needs a Large desktop.");
        RepositoryUrl = Rules.RepositoryUrl(settings.RepositoryUrl);
        RepositoryBranch = RepositoryUrl is null ? null : Rules.OptionalText(settings.RepositoryBranch, "A branch", 200);
        SetupScript = Rules.OptionalText(settings.SetupScript, "A setup script", MaxScriptLength);
        Instructions = Rules.OptionalText(settings.Instructions, "The agent's instructions", MaxInstructionsLength);
        KeepFromFile(settings.FromFile, now);
    }

    /// <summary>Whether the settings are the repository's workstream.yml from now on. Only for a repository on GitHub.</summary>
    public void KeepFromFile(bool fromFile, DateTimeOffset now)
    {
        FromFile = fromFile && GitHubRepositoryName.Of(RepositoryUrl) is not null;
        UpdatedAt = now;
    }

    /// <summary>The variables after a change: their names, and their values already protected by the caller.</summary>
    public void KeepVariables(IReadOnlyList<string> names, string? variablesProtected, DateTimeOffset now)
    {
        if (names.Count > MaxVariables) throw new DomainRuleException($"An environment holds at most {MaxVariables} variables.");
        VariableNames = [.. names];
        VariablesProtected = names.Count == 0 ? null : variablesProtected;
        UpdatedAt = now;
    }

    public static string ResourceIdFor(string id) => $"environment::{id}";

    string ISqlOSResourceEntity.ResourceTypeId => WorkstreamAuthorization.ResourceTypes.Environment;
    string ISqlOSResourceEntity.ResourceName => Name.Length > 0 ? Name : $"Environment {Id}";
    string? ISqlOSResourceEntity.ParentResourceId => Team.ResourceIdFor(TeamId);
    string? ISqlOSResourceEntity.ResourceDescription => null;
    bool ISqlOSResourceEntity.ResourceIsActive => true;
}

/// <summary>The sizes a desktop comes in. The desktop host maps each to a machine (Desktops/AzureDesktopHost.cs).</summary>
public static class DesktopSizes
{
    public const string Small = "small";
    public const string Standard = "standard";
    public const string Large = "large";
    public const string Default = Standard;

    public static readonly IReadOnlyList<string> Keys = [Small, Standard, Large];

    public static bool Has(string? size) => size is not null && Keys.Contains(size);
}

/// <summary>
/// Something a desktop can come with (desktop/apps.json): the one list the API, the image and the desktop read.
/// <see cref="Kind"/> says which of two things it is: a surface, where the product being worked on runs and is
/// seen and used (the browser, a phone), or an application, which the person and the agent work with (the
/// editor, a terminal).
/// </summary>
public sealed record DesktopApp(string Id, string Name, string Description, string Launcher, bool Default, string Kind);

public static class AppCatalog
{
    /// <summary>Where the product being worked on runs: <see cref="DesktopApp.Kind"/> of the browser and the phones.</summary>
    public const string SurfaceKind = "surface";
    /// <summary>What the person and the agent work with: <see cref="DesktopApp.Kind"/> of everything else.</summary>
    public const string ApplicationKind = "app";

    public static readonly IReadOnlyList<DesktopApp> All = Read();

    /// <summary>The surfaces' ids, in the catalog's order.</summary>
    public static readonly IReadOnlyList<string> Surfaces = [.. All.Where(app => app.Kind == SurfaceKind).Select(app => app.Id)];
    /// <summary>The applications' ids, in the catalog's order.</summary>
    public static readonly IReadOnlyList<string> Applications = [.. All.Where(app => app.Kind == ApplicationKind).Select(app => app.Id)];

    /// <summary>The surfaces a desktop has when nothing says otherwise.</summary>
    public static readonly IReadOnlyList<string> DefaultSurfaces = [.. All.Where(app => app.Default && app.Kind == SurfaceKind).Select(app => app.Id)];
    /// <summary>The applications a desktop has when nothing says otherwise.</summary>
    public static readonly IReadOnlyList<string> DefaultApplications = [.. All.Where(app => app.Default && app.Kind == ApplicationKind).Select(app => app.Id)];

    /// <summary>What a new environment comes with: the default surfaces, then the default applications.</summary>
    public static readonly IReadOnlyList<string> Defaults = [.. DefaultSurfaces, .. DefaultApplications];

    /// <summary>The iPhone simulator, which only a Large desktop has room for.</summary>
    public const string Iphone = "iphone";
    /// <summary>The Android emulator's window.</summary>
    public const string Android = "android";

    public static bool Has(string id) => All.Any(app => app.Id == id);

    public static bool IsSurface(string id) => Surfaces.Contains(id);

    /// <summary>The ids as an environment keeps them: surfaces first, then applications, each in the order given, each once.</summary>
    public static IReadOnlyList<string> InOrder(IEnumerable<string> ids)
    {
        var once = ids.Distinct(StringComparer.Ordinal).ToList();
        return [.. once.Where(IsSurface), .. once.Where(id => !IsSurface(id))];
    }

    static IReadOnlyList<DesktopApp> Read()
    {
        using var stream = typeof(AppCatalog).Assembly.GetManifestResourceStream("Workstream.Apps.json")
            ?? throw new InvalidOperationException("desktop/apps.json is not embedded.");
        var all = System.Text.Json.JsonSerializer.Deserialize<List<DesktopApp>>(stream, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("desktop/apps.json is empty.");
        if (all.FirstOrDefault(app => app.Kind is not (SurfaceKind or ApplicationKind)) is { } unsorted)
        {
            throw new InvalidOperationException($"desktop/apps.json: the kind of {unsorted.Id} is {SurfaceKind} or {ApplicationKind}.");
        }
        return all;
    }
}
