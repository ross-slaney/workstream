using Hangfire;
using Microsoft.AspNetCore.Http.Features;
using SqlOS.Fga.Interfaces;
using Workstream.Api.Builds;
using Workstream.Api.Domain;

namespace Workstream.Api;

public static partial class WorkstreamApi
{
    /// <summary>A desktop's side of an iPhone build: ask, send the source, see how it stands, take the app or the log.</summary>
    static void MapDesktopBuilds(RouteGroupBuilder desktop)
    {
        desktop.MapPost("/builds", AskBuild).ExcludeFromDescription();
        desktop.MapGet("/builds/{id}", GetDesktopBuild).ExcludeFromDescription();
        desktop.MapPut("/builds/{id}/source", PutBuildSource).ExcludeFromDescription();
        desktop.MapGet("/builds/{id}/app", (string id, HttpContext http, WorkstreamDbContext db, IBuildFiles files, TimeProvider clock, CancellationToken ct) =>
            GetDesktopBuildFile(id, AppBuild.AppFile, "application/gzip", http, db, files, clock, ct)).ExcludeFromDescription();
        desktop.MapGet("/builds/{id}/log", (string id, HttpContext http, WorkstreamDbContext db, IBuildFiles files, TimeProvider clock, CancellationToken ct) =>
            GetDesktopBuildFile(id, AppBuild.LogFile, "text/plain; charset=utf-8", http, db, files, clock, ct)).ExcludeFromDescription();
    }

    /// <summary>The Mac's side, with its key: take a build, read its source, hand back the log and the app, say how it ended.</summary>
    static void MapBuilder(RouteGroupBuilder builder)
    {
        builder.MapPost("/builds/{id}/take", TakeBuild).ExcludeFromDescription();
        builder.MapGet("/builds/{id}/source", GetBuilderSource).ExcludeFromDescription();
        builder.MapPut("/builds/{id}/log", (string id, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IBuildFiles files, BuildOptions options, CancellationToken ct) =>
            PutBuilderFile(id, AppBuild.LogFile, http, db, fga, files, options, ct)).ExcludeFromDescription();
        builder.MapPut("/builds/{id}/app", (string id, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IBuildFiles files, BuildOptions options, CancellationToken ct) =>
            PutBuilderFile(id, AppBuild.AppFile, http, db, fga, files, options, ct)).ExcludeFromDescription();
        builder.MapPost("/builds/{id}/end", EndBuild).ExcludeFromDescription();
    }

    static async Task<IResult> AskBuild(AskBuild body, HttpContext http, WorkstreamDbContext db, BuildOptions options, TimeProvider clock, CancellationToken ct)
    {
        var outcome = await Operations.AskBuildAsync(Caller.Desktop(http), body, db, options, clock, ct);
        return outcome.IsOk ? Results.Ok(Operations.ToDto(outcome.Value!)) : ToResult(outcome);
    }

    static async Task<IResult> GetDesktopBuild(string id, HttpContext http, WorkstreamDbContext db, CancellationToken ct)
    {
        var outcome = await Operations.DesktopBuildAsync(Caller.Desktop(http), id, db, ct);
        return outcome.IsOk ? Results.Ok(Operations.ToDto(outcome.Value!)) : ToResult(outcome);
    }

    static async Task<IResult> PutBuildSource(string id, HttpContext http, WorkstreamDbContext db, IBuildFiles files, IBackgroundJobClient jobs, BuildOptions options, TimeProvider clock, CancellationToken ct)
    {
        AllowLarge(http, options.MaxSourceMb);
        var outcome = await Operations.SendSourceAsync(Caller.Desktop(http), id, http.Request.Body, http.Request.ContentLength, db, files, jobs, options, clock, ct);
        return outcome.IsOk ? Results.Ok(Operations.ToDto(outcome.Value!)) : ToResult(outcome);
    }

    static async Task<IResult> GetDesktopBuildFile(string id, string file, string type, HttpContext http, WorkstreamDbContext db, IBuildFiles files, TimeProvider clock, CancellationToken ct)
    {
        var outcome = await Operations.DesktopBuildFileAsync(Caller.Desktop(http), id, file, db, files, clock, ct);
        return outcome.IsOk ? FileResult(http, outcome.Value!, type) : ToResult(outcome);
    }

    static async Task<IResult> TakeBuild(string id, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, TimeProvider clock, CancellationToken ct)
    {
        var outcome = await Operations.TakeBuildAsync(BuilderKeys.CallerOf(http), id, db, fga, clock, ct);
        return outcome.IsOk ? Results.Ok(outcome.Value) : ToResult(outcome);
    }

    static async Task<IResult> GetBuilderSource(string id, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IBuildFiles files, CancellationToken ct)
    {
        var outcome = await Operations.BuilderSourceAsync(BuilderKeys.CallerOf(http), id, db, fga, files, ct);
        return outcome.IsOk ? FileResult(http, outcome.Value!, "application/gzip") : ToResult(outcome);
    }

    static async Task<IResult> PutBuilderFile(string id, string file, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IBuildFiles files, BuildOptions options, CancellationToken ct)
    {
        AllowLarge(http, options.MaxAppMb);
        var outcome = await Operations.BuilderSendAsync(BuilderKeys.CallerOf(http), id, file, http.Request.Body, http.Request.ContentLength, db, fga, files, options, ct);
        return outcome.IsOk ? Results.NoContent() : ToResult(outcome);
    }

    static async Task<IResult> EndBuild(string id, BuildEnd body, HttpContext http, WorkstreamDbContext db, ISqlOSFgaAuthService fga, IBuildFiles files, TimeProvider clock, CancellationToken ct)
    {
        var outcome = await Operations.EndBuildAsync(BuilderKeys.CallerOf(http), id, body, db, fga, files, clock, ct);
        return outcome.IsOk ? Results.NoContent() : ToResult(outcome);
    }

    /// <summary>A build's file is larger than a request usually is: this route takes up to what the deployment allows, and a little more for the refusal to be ours.</summary>
    static void AllowLarge(HttpContext http, int maxMb)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = (maxMb + 1) * 1024L * 1024L;
    }

    /// <summary>The file as the answer, closed when the answer is sent.</summary>
    static IResult FileResult(HttpContext http, BuildFile file, string type)
    {
        http.Response.RegisterForDispose(file.Content);
        if (file.Length > 0) http.Response.ContentLength = file.Length;
        return Results.Stream(file.Content, type);
    }
}
