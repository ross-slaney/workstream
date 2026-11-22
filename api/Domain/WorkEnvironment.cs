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
/// An environment: what a desktop comes with. The simple part is its apps; the rest is optional and is what
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
    /// <summary>Ids from the app catalog (desktop/apps.json): what is on the desktop.</summary>
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
    /// the variables with their values, the apps, and the size, whose disk the save is. A saved setup is used only while this is what it was
    /// made from (<see cref="EnvironmentSetup"/>). Written by whoever changes the environment, who can read
    /// the variables; null on an environment not changed since this existed, until it is first needed.
    /// </summary>
    public string? SetupInputs { get; private set; }

    /// <summary>Whether there is anything to prepare: a repository to clone, or a script to run.</summary>
    public bool HasSetup => RepositoryUrl is not null || SetupScript is not null;

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

    /// <summary>The environment every new team starts with: a plain desktop with the usual apps.</summary>
    public static WorkEnvironment Standard(Team team, string createdBy, DateTimeOffset now) =>
        Add(Guid.NewGuid().ToString("D"), team, new EnvironmentSettings("Standard desktop", AppCatalog.Defaults, DesktopSizes.Default, null, null, null, null), createdBy, now);

    public void Change(EnvironmentSettings settings, string changedBy, DateTimeOffset now)
    {
        ChangedBy = Rules.Principal(changedBy, "Whoever changed it");
        Name = Rules.Text(settings.Name, "An environment's name", 100);
        var apps = settings.Apps.Distinct(StringComparer.Ordinal).ToList();
        if (apps.FirstOrDefault(app => !AppCatalog.Has(app)) is { } unknown) throw new DomainRuleException($"'{unknown}' is not an app a desktop can come with.");
        Apps = apps;
        Size = DesktopSizes.Has(settings.Size) ? settings.Size : throw new DomainRuleException($"A desktop's size is one of {string.Join(", ", DesktopSizes.Keys)}.");
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

/// <summary>An app a desktop can come with (desktop/apps.json): the one list the API, the image and the desktop read.</summary>
public sealed record DesktopApp(string Id, string Name, string Description, string Launcher, bool Default);

public static class AppCatalog
{
    public static readonly IReadOnlyList<DesktopApp> All = Read();

    public static readonly IReadOnlyList<string> Defaults = [.. All.Where(app => app.Default).Select(app => app.Id)];

    public static bool Has(string id) => All.Any(app => app.Id == id);

    static IReadOnlyList<DesktopApp> Read()
    {
        using var stream = typeof(AppCatalog).Assembly.GetManifestResourceStream("Workstream.Apps.json")
            ?? throw new InvalidOperationException("desktop/apps.json is not embedded.");
        return System.Text.Json.JsonSerializer.Deserialize<List<DesktopApp>>(stream, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("desktop/apps.json is empty.");
    }
}
