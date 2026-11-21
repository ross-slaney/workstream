using SqlOS.Fga.Interfaces;

namespace Workstream.Api.Domain;

/// <summary>
/// A team: the people who share tasks and environments (docs/core-model.md). It is a SqlOS organization, which
/// keeps its name, memberships and invitations; this row is its node in the tree and its two limits. Who is on
/// the team is the team's group, never a column here.
/// </summary>
public sealed class Team : ISqlOSResourceEntity
{
    public const decimal MaxAllowanceUsd = 100_000m;
    public const int MaxDesktops = 200;

    /// <summary>For EF Core.</summary>
    Team()
    {
    }

    /// <summary>The SqlOS organization's id. The team's group has the same id.</summary>
    public string Id { get; private set; } = "";
    public string ResourceId { get; private set; } = "";
    /// <summary>What the team's agents may spend on models in a calendar month, in US dollars.</summary>
    public decimal ModelAllowanceUsd { get; private set; }
    /// <summary>How many of the team's desktops may run at once.</summary>
    public int MaxRunningDesktops { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    internal static Team For(string id, decimal modelAllowanceUsd, int maxRunningDesktops, DateTimeOffset now) => new()
    {
        Id = id,
        ResourceId = ResourceIdFor(id),
        ModelAllowanceUsd = modelAllowanceUsd,
        MaxRunningDesktops = maxRunningDesktops,
        CreatedAt = now,
    };

    /// <summary>A system administrator changes what the team may use.</summary>
    public void SetLimits(decimal modelAllowanceUsd, int maxRunningDesktops)
    {
        if (modelAllowanceUsd < 0 || modelAllowanceUsd > MaxAllowanceUsd) throw new DomainRuleException($"A model allowance is between 0 and {MaxAllowanceUsd:0} dollars.");
        if (maxRunningDesktops < 0 || maxRunningDesktops > MaxDesktops) throw new DomainRuleException($"A team runs between 0 and {MaxDesktops} desktops at once.");
        ModelAllowanceUsd = modelAllowanceUsd;
        MaxRunningDesktops = maxRunningDesktops;
    }

    public static string ResourceIdFor(string id) => $"team::{id}";

    /// <summary>The principal that is everyone on the team: grants name this.</summary>
    public static string GroupSubjectIdFor(string id) => $"group::{id}";

    string ISqlOSResourceEntity.ResourceTypeId => WorkstreamAuthorization.ResourceTypes.Team;
    string ISqlOSResourceEntity.ResourceName => "Team";
    string? ISqlOSResourceEntity.ParentResourceId => "root";
    string? ISqlOSResourceEntity.ResourceDescription => null;
    bool ISqlOSResourceEntity.ResourceIsActive => true;
}
