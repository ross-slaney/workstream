using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using Workstream.Api.Desktops;
using Workstream.Api.Domain;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api;

/// <summary>
/// A variable as a person sends it. A null <see cref="Value"/> keeps the value the environment already has for
/// that name, because values are never sent back to be returned; a name left out is removed.
/// </summary>
public sealed record VariableChange(string Name, string? Value);

/// <summary>
/// An environment as a person writes it: the whole state. <see cref="FromFile"/> says the form was filled from
/// the repository's workstream.yml, which each task's start then reads again.
/// </summary>
public sealed record WriteEnvironment(
    string Name, IReadOnlyList<string>? Apps, string? Size, string? RepositoryUrl, string? RepositoryBranch, string? SetupScript, string? Instructions,
    IReadOnlyList<VariableChange>? Variables, bool? FromFile = null);

public static partial class Operations
{
    const string VariablesPurpose = "Workstream.Environment.Variables";

    /// <summary>A team's environments, A to Z: the query its list pages with <c>ENVIRONMENT_VIEW</c>.</summary>
    public static IQueryable<WorkEnvironment> Environments(WorkstreamDbContext db, string teamId) =>
        db.Environments.AsNoTracking().Where(x => x.TeamId == teamId).OrderBy(x => x.Name);

    public static async Task<Outcome<WorkEnvironment>> ViewEnvironmentAsync(Caller caller, string id, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var environment = await db.Environments.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (environment is null || !await fga.Allows(caller.PrincipalId, Permissions.EnvironmentView, environment.ResourceId)) return Outcome<WorkEnvironment>.NotFound();
        return Outcome<WorkEnvironment>.Ok(environment);
    }

    public static async Task<Outcome<WorkEnvironment>> AddEnvironmentAsync(Caller caller, string teamId, string id, WriteEnvironment body,
        WorkstreamDbContext db, ISqlOSFgaAuthService fga, IDataProtectionProvider protection, IBackgroundJobClient jobs, DesktopOptions options, CancellationToken ct)
    {
        var allowed = await TeamWithAsync(caller, teamId, Permissions.TeamCreateEnvironment, "You can't add environments to this team.", db, fga, ct);
        if (!allowed.IsOk) return allowed.As<WorkEnvironment>();
        if (await db.Environments.AnyAsync(x => x.Id == id, ct)) return Outcome<WorkEnvironment>.Exists("That environment already exists.");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var environment = WorkEnvironment.Add(id, allowed.Value!, SettingsOf(body), caller.PrincipalId, now);
            KeepVariables(environment, body.Variables, protection, now);
            db.Environments.Add(environment);
            await db.SaveChangesAsync(ct);
            await KeepSetupAsync(environment, null, protection, db, jobs, options, ct);
            return Outcome<WorkEnvironment>.Ok(environment);
        }
        catch (DomainRuleException rule)
        {
            return Outcome<WorkEnvironment>.Invalid(rule);
        }
    }

    public static async Task<Outcome<WorkEnvironment>> ChangeEnvironmentAsync(Caller caller, string id, WriteEnvironment body,
        WorkstreamDbContext db, ISqlOSFgaAuthService fga, IDataProtectionProvider protection, IBackgroundJobClient jobs, DesktopOptions options, CancellationToken ct)
    {
        var seen = await ViewEnvironmentAsync(caller, id, db, fga, ct);
        if (!seen.IsOk) return seen;
        var environment = seen.Value!;
        if (!await fga.Allows(caller.PrincipalId, Permissions.EnvironmentEdit, environment.ResourceId)) return Outcome<WorkEnvironment>.Forbidden("You can't change this environment.");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var before = environment.SetupInputs;
            environment.Change(SettingsOf(body), caller.PrincipalId, now);
            KeepVariables(environment, body.Variables, protection, now);
            await db.SaveChangesAsync(ct);
            await KeepSetupAsync(environment, before, protection, db, jobs, options, ct);
            return seen;
        }
        catch (DomainRuleException rule)
        {
            return Outcome<WorkEnvironment>.Invalid(rule);
        }
    }

    /// <summary>
    /// Delete the environment. Its saved setup goes with it, and so does a machine preparing it, whose key ends
    /// now.
    /// </summary>
    public static async Task<Outcome<bool>> DeleteEnvironmentAsync(Caller caller, string id, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IBackgroundJobClient jobs, CancellationToken ct)
    {
        var seen = await ViewEnvironmentAsync(caller, id, db, fga, ct);
        if (!seen.IsOk) return seen.As<bool>();
        var environment = seen.Value!;
        if (!await fga.Allows(caller.PrincipalId, Permissions.EnvironmentEdit, environment.ResourceId)) return Outcome<bool>.Forbidden("You can't delete this environment.");
        // A task's desktop reads its setup from its environment every time it starts.
        if (await db.Tasks.AnyAsync(x => x.EnvironmentId == id, ct)) return Outcome<bool>.Invalid("Tasks were started from this environment. Delete them first.");
        var setup = await db.Setups.SingleOrDefaultAsync(x => x.EnvironmentId == id, ct);
        var save = setup?.Save;
        var machine = setup is { IsPreparing: true } ? setup.RunHost ?? DesktopHosting.NameFor(setup.RunId!) : null;
        if (setup is not null)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var run in await db.DesktopRuns.Where(x => x.TaskId == setup.RunId && x.EndedAt == null).ToListAsync(ct)) run.End(now);
            await DesktopKeys.RetireAsync(db, EnvironmentSetup.DesktopIdFor(id), ct);
            db.Setups.Remove(setup);
        }
        db.Environments.Remove(environment);
        await db.SaveChangesAsync(ct);
        if (save is not null) jobs.Enqueue<DesktopJobs>(job => job.DeleteSaveAsync(save, CancellationToken.None));
        if (machine is not null) jobs.Enqueue<DesktopJobs>(job => job.RemovePreparingMachineAsync(machine, CancellationToken.None));
        return Outcome<bool>.Ok(true);
    }

    static EnvironmentSettings SettingsOf(WriteEnvironment body) => new(
        body.Name, body.Apps ?? AppCatalog.Defaults, string.IsNullOrWhiteSpace(body.Size) ? DesktopSizes.Default : body.Size,
        body.RepositoryUrl, body.RepositoryBranch, body.SetupScript, body.Instructions, body.FromFile ?? false);

    /// <summary>The environment's variables after a change: new values protected, kept values carried over.</summary>
    static void KeepVariables(WorkEnvironment environment, IReadOnlyList<VariableChange>? changes, IDataProtectionProvider protection, DateTimeOffset now)
    {
        var current = VariablesOf(environment, protection);
        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var change in changes ?? [])
        {
            var name = Rules.VariableName(change.Name);
            if (next.ContainsKey(name)) throw new DomainRuleException($"The variable {name} is listed twice.");
            var value = change.Value ?? (current.TryGetValue(name, out var kept) ? kept : throw new DomainRuleException($"The variable {name} needs a value."));
            if (value.Length > WorkEnvironment.MaxVariableValueLength) throw new DomainRuleException($"The variable {name} can be at most {WorkEnvironment.MaxVariableValueLength} characters.");
            next[name] = value;
            names.Add(name);
        }
        environment.KeepVariables(names, next.Count == 0 ? null : protection.CreateProtector(VariablesPurpose).Protect(JsonSerializer.Serialize(next)), now);
        // Here, where the values are readable, is the one place what a saved setup depends on can be known.
        environment.KeepSetupInputs(SetupInputsOf(environment, next));
    }

    /// <summary>The environment's variables, readable: only for the desktop that is being set up from it.</summary>
    internal static Dictionary<string, string> VariablesOf(WorkEnvironment environment, IDataProtectionProvider protection)
    {
        if (environment.VariablesProtected is not { Length: > 0 } sealedValues) return new(StringComparer.Ordinal);
        var json = protection.CreateProtector(VariablesPurpose).Unprotect(sealedValues);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(StringComparer.Ordinal);
    }
}
