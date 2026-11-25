using Hangfire;
using Microsoft.EntityFrameworkCore;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using Workstream.Api.Builds;
using Workstream.Api.Domain;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api;

/// <summary>
/// A desktop asks for the iPhone build of what it has: the platform (<c>ios</c>), the hash of everything the
/// native app is made from, and the app's folder inside the source it will send. <see cref="Again"/> asks
/// for a build that failed to be made again.
/// </summary>
public sealed record AskBuild(string Platform, string Hash, string? Directory, bool Again = false);

/// <summary>
/// A build as a desktop sees it. <see cref="State"/> is <c>waiting</c> (send the source), <c>queued</c>,
/// <c>building</c>, <c>done</c> (take the app) or <c>failed</c> (<see cref="Failure"/> says why; the log has the rest).
/// </summary>
public sealed record BuildDto(string Id, string Platform, string Hash, string State, string? Failure, string? BundleId, long AppBytes, int MacSeconds, DateTimeOffset AskedAt, DateTimeOffset? EndedAt);

/// <summary>What the Mac is told of a build it takes: what to build, and where the app is inside the source.</summary>
public sealed record BuilderBuild(string Id, string Platform, string Directory);

/// <summary>The Mac says how a build ended.</summary>
public sealed record BuildEnd(bool Ok, string? BundleId, string? Failure);

/// <summary>A file of a build on its way out, with its length. The caller disposes it.</summary>
public sealed record BuildFile(Stream Content, long Length);

public static partial class Operations
{
    public static BuildDto ToDto(AppBuild x) => new(
        x.Id, x.Platform, x.Hash, x.State.ToString().ToLowerInvariant(), x.Failure, x.BundleId, x.AppBytes, x.MacSeconds, x.AskedAt, x.EndedAt);

    const string NoBuilds = "iPhone builds are not set up on this Workstream.";

    /// <summary>
    /// A desktop asks for a build. The team keeps one a hash: one that is there is answered as it is, whatever
    /// its state, so a second desktop with the same native code waits for the first's build or takes its app.
    /// A new one, or a failed one asked for again, waits for the source and counts against the team's builds
    /// for the day.
    /// </summary>
    public static async Task<Outcome<AppBuild>> AskBuildAsync(Caller desktop, AskBuild body, WorkstreamDbContext db, BuildOptions options, TimeProvider clock, CancellationToken ct)
    {
        if (await DesktopTaskAsync(desktop, db, ct, tracked: false) is not { } task) return Outcome<AppBuild>.NotFound();
        if (!options.Enabled) return Outcome<AppBuild>.Unavailable(NoBuilds);
        var now = clock.GetUtcNow();
        try
        {
            var asked = AppBuild.Ask(task, body.Platform ?? "", body.Hash ?? "", body.Directory, now);
            var build = await db.Builds.SingleOrDefaultAsync(x => x.TeamId == task.TeamId && x.Platform == asked.Platform && x.Hash == asked.Hash, ct);
            if (build is not null && !(build.State == AppBuildState.Failed && body.Again)) return Outcome<AppBuild>.Ok(build);

            var dayAgo = now.AddDays(-1);
            if (await db.Builds.CountAsync(x => x.TeamId == task.TeamId && x.AskedAt >= dayAgo, ct) >= options.PerTeamADay)
            {
                return Outcome<AppBuild>.LimitReached($"Your team has asked for {options.PerTeamADay} iPhone builds in the last day, which is its limit.");
            }
            if (build is null) db.Builds.Add(build = asked);
            else build.TryAgain(task, body.Directory, now);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Another desktop of the team asked for the same hash in the same moment: theirs is the build.
                db.Entry(build).State = EntityState.Detached;
                build = await db.Builds.SingleAsync(x => x.TeamId == task.TeamId && x.Platform == asked.Platform && x.Hash == asked.Hash, ct);
            }
            return Outcome<AppBuild>.Ok(build);
        }
        catch (DomainRuleException rule)
        {
            return Outcome<AppBuild>.Invalid(rule);
        }
    }

    /// <summary>A build of the team whose task this desktop works on. Another team's is not there.</summary>
    public static async Task<Outcome<AppBuild>> DesktopBuildAsync(Caller desktop, string id, WorkstreamDbContext db, CancellationToken ct)
    {
        if (await DesktopTaskAsync(desktop, db, ct, tracked: false) is not { } task) return Outcome<AppBuild>.NotFound();
        var build = await db.Builds.SingleOrDefaultAsync(x => x.Id == id && x.TeamId == task.TeamId, ct);
        return build is null ? Outcome<AppBuild>.NotFound() : Outcome<AppBuild>.Ok(build);
    }

    /// <summary>The desktop sends the source of a build that waits for it. Then the Mac is asked.</summary>
    public static async Task<Outcome<AppBuild>> SendSourceAsync(Caller desktop, string id, Stream source, long? length, WorkstreamDbContext db, IBuildFiles files,
        IBackgroundJobClient jobs, BuildOptions options, TimeProvider clock, CancellationToken ct)
    {
        var seen = await DesktopBuildAsync(desktop, id, db, ct);
        if (!seen.IsOk) return seen;
        var build = seen.Value!;
        if (!options.Enabled) return Outcome<AppBuild>.Unavailable(NoBuilds);
        if (build.State != AppBuildState.Waiting) return Outcome<AppBuild>.Exists("This build already has its source.");
        if (LengthRefusal(length, options.MaxSourceMb, "source") is { } refusal) return Outcome<AppBuild>.Invalid(refusal);
        await files.WriteAsync(build.FileName(AppBuild.SourceFile), source, length!.Value, ct);
        try
        {
            build.SourceArrived(length.Value, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }
        catch (DomainRuleException rule)
        {
            return Outcome<AppBuild>.Invalid(rule);
        }
        var (buildId, attempt) = (build.Id, build.Attempt);
        jobs.Enqueue<BuildJobs>(job => job.AskAsync(buildId, attempt, CancellationToken.None));
        return Outcome<AppBuild>.Ok(build);
    }

    /// <summary>The app of a build that is done, or the log of one that has ended, for a desktop of its team.</summary>
    public static async Task<Outcome<BuildFile>> DesktopBuildFileAsync(Caller desktop, string id, string file, WorkstreamDbContext db, IBuildFiles files, TimeProvider clock, CancellationToken ct)
    {
        var seen = await DesktopBuildAsync(desktop, id, db, ct);
        if (!seen.IsOk) return seen.As<BuildFile>();
        var build = seen.Value!;
        if (file == AppBuild.AppFile && build.State != AppBuildState.Done) return Outcome<BuildFile>.NotFound("This build has no app yet.");
        if (await files.ReadAsync(build.FileName(file), ct) is not { } kept) return Outcome<BuildFile>.NotFound(file == AppBuild.LogFile ? "This build has no log yet." : "This build's app is no longer kept. Ask for it again.");
        if (file == AppBuild.AppFile)
        {
            build.Used(clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }
        return Outcome<BuildFile>.Ok(new BuildFile(kept.Content, kept.Length));
    }

    /// <summary>A build, for the Mac: any team's, after <c>TEAM_RUN_BUILD</c> on that team.</summary>
    static async Task<Outcome<AppBuild>> BuilderBuildAsync(Caller builder, string id, WorkstreamDbContext db, ISqlOSFgaAuthService fga, CancellationToken ct)
    {
        var build = await db.Builds.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (build is null) return Outcome<AppBuild>.NotFound();
        return await fga.Allows(builder.PrincipalId, Permissions.TeamRunBuild, Team.ResourceIdFor(build.TeamId))
            ? Outcome<AppBuild>.Ok(build)
            : Outcome<AppBuild>.Forbidden("This key does not build for that team.");
    }

    /// <summary>The Mac takes a build that waits for one: from now it is being built, and its time counts.</summary>
    public static async Task<Outcome<BuilderBuild>> TakeBuildAsync(Caller builder, string id, WorkstreamDbContext db, ISqlOSFgaAuthService fga, TimeProvider clock, CancellationToken ct)
    {
        var seen = await BuilderBuildAsync(builder, id, db, fga, ct);
        if (!seen.IsOk) return seen.As<BuilderBuild>();
        var build = seen.Value!;
        try
        {
            build.Take(clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            return Outcome<BuilderBuild>.Ok(new BuilderBuild(build.Id, build.Platform, build.Directory));
        }
        catch (DomainRuleException rule)
        {
            return Outcome<BuilderBuild>.Invalid(rule);
        }
    }

    /// <summary>The source of a build the Mac has taken.</summary>
    public static async Task<Outcome<BuildFile>> BuilderSourceAsync(Caller builder, string id, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IBuildFiles files, CancellationToken ct)
    {
        var seen = await BuilderBuildAsync(builder, id, db, fga, ct);
        if (!seen.IsOk) return seen.As<BuildFile>();
        var build = seen.Value!;
        if (build.State != AppBuildState.Building) return Outcome<BuildFile>.NotFound("This build is not being built.");
        return await files.ReadAsync(build.FileName(AppBuild.SourceFile), ct) is { } kept
            ? Outcome<BuildFile>.Ok(new BuildFile(kept.Content, kept.Length))
            : Outcome<BuildFile>.NotFound("This build's source is gone.");
    }

    /// <summary>The Mac hands over the log or the app of the build it is working on.</summary>
    public static async Task<Outcome<AppBuild>> BuilderSendAsync(Caller builder, string id, string file, Stream content, long? length, WorkstreamDbContext db, ISqlOSFgaAuthService fga,
        IBuildFiles files, BuildOptions options, CancellationToken ct)
    {
        var seen = await BuilderBuildAsync(builder, id, db, fga, ct);
        if (!seen.IsOk) return seen;
        var build = seen.Value!;
        if (build.State != AppBuildState.Building) return Outcome<AppBuild>.Invalid("This build is not being built.");
        if (LengthRefusal(length, file == AppBuild.AppFile ? options.MaxAppMb : 50, file == AppBuild.AppFile ? "app" : "log") is { } refusal) return Outcome<AppBuild>.Invalid(refusal);
        await files.WriteAsync(build.FileName(file), content, length!.Value, ct);
        if (file != AppBuild.AppFile) return Outcome<AppBuild>.Ok(build);
        try
        {
            build.AppArrived(length.Value);
            await db.SaveChangesAsync(ct);
            return Outcome<AppBuild>.Ok(build);
        }
        catch (DomainRuleException rule)
        {
            return Outcome<AppBuild>.Invalid(rule);
        }
    }

    /// <summary>The Mac is done with a build. The source is removed: it was kept only to be built.</summary>
    public static async Task<Outcome<AppBuild>> EndBuildAsync(Caller builder, string id, BuildEnd body, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IBuildFiles files, TimeProvider clock, CancellationToken ct)
    {
        var seen = await BuilderBuildAsync(builder, id, db, fga, ct);
        if (!seen.IsOk) return seen;
        var build = seen.Value!;
        try
        {
            build.End(body.Ok, body.BundleId, body.Failure, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }
        catch (DomainRuleException rule)
        {
            return Outcome<AppBuild>.Invalid(rule);
        }
        await files.DeleteAsync(build.FileName(AppBuild.SourceFile), ct);
        return Outcome<AppBuild>.Ok(build);
    }

    /// <summary>A file is sent with its length, and is not larger than a deployment allows.</summary>
    static string? LengthRefusal(long? length, int maxMb, string what) => length switch
    {
        null => $"Send the {what} with its length (Content-Length).",
        <= 0 => $"The {what} is empty.",
        var bytes when bytes > maxMb * 1024L * 1024L => $"The {what} is larger than {maxMb} MB.",
        _ => null,
    };

    /// <summary>The minutes of Mac time a team's builds took since <paramref name="since"/>, counted where each ended.</summary>
    public static async Task<double> MacMinutesAsync(WorkstreamDbContext db, string teamId, DateTimeOffset since, CancellationToken ct) =>
        Math.Round((await db.Builds.Where(x => x.TeamId == teamId && x.EndedAt >= since).SumAsync(x => (int?)x.MacSeconds, ct) ?? 0) / 60.0, 1);
}
