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
    public const int MinPauseAfterMinutes = 5;
    public const int MaxPauseAfterMinutes = 240;
    public const int DefaultPauseAfterMinutes = 30;
    public const int MinArchiveAfterDays = 1;
    public const int MaxArchiveAfterDays = 90;
    public const int DefaultArchiveAfterDays = 7;

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
    /// <summary>A desktop nobody has used for this many minutes, whose agent is not working, is paused.</summary>
    public int PauseAfterMinutes { get; private set; } = DefaultPauseAfterMinutes;
    /// <summary>A desktop paused for this many days is archived: its disk is copied, and the machine and disk are deleted.</summary>
    public int ArchiveAfterDays { get; private set; } = DefaultArchiveAfterDays;
    public DateTimeOffset CreatedAt { get; private set; }

    internal static Team For(string id, decimal modelAllowanceUsd, int maxRunningDesktops, DateTimeOffset now,
        int pauseAfterMinutes = DefaultPauseAfterMinutes, int archiveAfterDays = DefaultArchiveAfterDays) => new()
    {
        Id = id,
        ResourceId = ResourceIdFor(id),
        ModelAllowanceUsd = modelAllowanceUsd,
        MaxRunningDesktops = maxRunningDesktops,
        // A deployment's default outside what a team may choose is brought inside it.
        PauseAfterMinutes = Math.Clamp(pauseAfterMinutes, MinPauseAfterMinutes, MaxPauseAfterMinutes),
        ArchiveAfterDays = Math.Clamp(archiveAfterDays, MinArchiveAfterDays, MaxArchiveAfterDays),
        CreatedAt = now,
    };

    /// <summary>A team administrator changes when the team's unused desktops are paused, and when its paused ones are archived.</summary>
    public void SetDesktopTimes(int pauseAfterMinutes, int archiveAfterDays)
    {
        if (pauseAfterMinutes < MinPauseAfterMinutes || pauseAfterMinutes > MaxPauseAfterMinutes)
        {
            throw new DomainRuleException($"A desktop is paused after between {MinPauseAfterMinutes} and {MaxPauseAfterMinutes} minutes without use.");
        }
        if (archiveAfterDays < MinArchiveAfterDays || archiveAfterDays > MaxArchiveAfterDays)
        {
            throw new DomainRuleException($"A paused desktop is archived after between {MinArchiveAfterDays} and {MaxArchiveAfterDays} days.");
        }
        PauseAfterMinutes = pauseAfterMinutes;
        ArchiveAfterDays = archiveAfterDays;
    }

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
