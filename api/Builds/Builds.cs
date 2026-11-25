using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Azure.Core;
using Azure.Identity;
using Hangfire;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;
using Workstream.Api.Domain;
using Workstream.Api.GitHub;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api.Builds;

/// <summary>
/// iPhone apps are built on a Mac, which a desktop is not. These are the settings for that
/// (<c>Workstream:Builds</c>): the key the Mac calls back with, the GitHub workflow that is the Mac
/// (.github/workflows/ios-build.yml in Workstream's own repository, on GitHub's Mac runners), where the
/// source, the app and the log are kept, and the limits. Without <see cref="Key"/> and
/// <see cref="Repository"/> a deployment builds no iPhone apps, and a desktop that asks is told so.
/// </summary>
public sealed class BuildOptions
{
    /// <summary>The Mac's key: <c>wsb_</c> and 64 hex characters. The same value is the workflow's <c>BUILDER_KEY</c> secret.</summary>
    public string? Key { get; set; }
    /// <summary>The repository whose workflow builds, as <c>owner/name</c>: Workstream's own, never a project's.</summary>
    public string? Repository { get; set; }
    public string Workflow { get; set; } = "ios-build.yml";
    /// <summary>The branch or tag the workflow runs from: the release this deployment was made from.</summary>
    public string Ref { get; set; } = "main";
    /// <summary>The GitHub environment the workflow reads its key from: <c>production</c> or <c>staging</c>.</summary>
    public string Stage { get; set; } = "production";
    /// <summary>
    /// A GitHub token that may run the workflow. Without one, the system administrator's own GitHub token is
    /// used, from their sign-in with GitHub: the deployment's administrator is who owns the repository.
    /// </summary>
    public string? Token { get; set; }
    /// <summary>A folder for the files, in development and the tests.</summary>
    public string? Folder { get; set; }
    public AzureFiles Azure { get; set; } = new();
    /// <summary>A build the Mac has not finished after this long has failed.</summary>
    public int MaxMinutes { get; set; } = 90;
    /// <summary>An app no desktop has taken for this long is removed, with its log.</summary>
    public int KeepDays { get; set; } = 30;
    /// <summary>Builds a team may ask for in a day. A Mac's time is the dearest thing a task uses.</summary>
    public int PerTeamADay { get; set; } = 30;
    public int MaxSourceMb { get; set; } = 500;
    public int MaxAppMb { get; set; } = 1000;
    /// <summary>What a minute of the Mac costs: GitHub's price for its standard Mac runner in a private repository.</summary>
    public decimal MacMinuteUsd { get; set; } = 0.062m;

    public bool Enabled => !string.IsNullOrWhiteSpace(Key) && !string.IsNullOrWhiteSpace(Repository);

    public sealed class AzureFiles
    {
        /// <summary>The blob container's address, <c>https://account.blob.core.windows.net/builds</c>. The API's identity reads and writes it.</summary>
        public string? Container { get; set; }
    }
}

/// <summary>Where a build's files are kept: the source a desktop sent, the app the Mac made, and the Mac's log.</summary>
public interface IBuildFiles
{
    /// <summary>Keep a file of this length, replacing one of the same name.</summary>
    Task WriteAsync(string name, Stream content, long length, CancellationToken ct);
    /// <summary>The file, or nothing when it is not there. The caller disposes it.</summary>
    Task<(Stream Content, long Length)?> ReadAsync(string name, CancellationToken ct);
    Task DeleteAsync(string name, CancellationToken ct);
}

/// <summary>Files in a folder, for development and the tests.</summary>
public sealed class FolderBuildFiles(BuildOptions options) : IBuildFiles
{
    readonly string root = Path.GetFullPath(options.Folder ?? Path.Combine(Path.GetTempPath(), "workstream-builds"));

    string PathOf(string name)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidOperationException("A build's file is inside the builds folder.");
        return path;
    }

    public async Task WriteAsync(string name, Stream content, long length, CancellationToken ct)
    {
        var path = PathOf(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + ".part";
        await using (var file = File.Create(part)) await content.CopyToAsync(file, ct);
        File.Move(part, path, overwrite: true);
    }

    public Task<(Stream Content, long Length)?> ReadAsync(string name, CancellationToken ct)
    {
        var path = PathOf(name);
        if (!File.Exists(path)) return Task.FromResult<(Stream, long)?>(null);
        var file = File.OpenRead(path);
        return Task.FromResult<(Stream, long)?>((file, file.Length));
    }

    public Task DeleteAsync(string name, CancellationToken ct)
    {
        var path = PathOf(name);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Files in an Azure blob container, read and written as the API's own identity, which holds Storage Blob
/// Data Contributor on the storage account (infra/data.bicep). Plain REST, like the desktops' host: three
/// calls do not need a library.
/// </summary>
public sealed class AzureBuildFiles(HttpClient http, TokenCredential credential, BuildOptions options) : IBuildFiles
{
    public const string ClientName = "build-files";
    const string Version = "2023-11-03";
    static readonly string[] Scopes = ["https://storage.azure.com/.default"];

    async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string name, CancellationToken ct)
    {
        var address = options.Azure.Container!.TrimEnd('/') + "/" + string.Join('/', name.Split('/').Select(Uri.EscapeDataString));
        var request = new HttpRequestMessage(method, address);
        var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Headers.TryAddWithoutValidation("x-ms-version", Version);
        return request;
    }

    public async Task WriteAsync(string name, Stream content, long length, CancellationToken ct)
    {
        using var request = await RequestAsync(HttpMethod.Put, name, ct);
        request.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentLength = length;
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var answer = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!answer.IsSuccessStatusCode) throw new InvalidOperationException($"The build's file was not kept: storage answered {(int)answer.StatusCode}.");
    }

    public async Task<(Stream Content, long Length)?> ReadAsync(string name, CancellationToken ct)
    {
        using var request = await RequestAsync(HttpMethod.Get, name, ct);
        var answer = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (answer.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            answer.Dispose();
            return null;
        }
        if (!answer.IsSuccessStatusCode)
        {
            var status = (int)answer.StatusCode;
            answer.Dispose();
            throw new InvalidOperationException($"The build's file was not read: storage answered {status}.");
        }
        return (await answer.Content.ReadAsStreamAsync(ct), answer.Content.Headers.ContentLength ?? 0);
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        using var request = await RequestAsync(HttpMethod.Delete, name, ct);
        using var answer = await http.SendAsync(request, ct);
        if (!answer.IsSuccessStatusCode && answer.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"The build's file was not removed: storage answered {(int)answer.StatusCode}.");
        }
    }
}

/// <summary>The Mac that builds: asked to build, it calls the API back with its key for the source, and with the app.</summary>
public interface IMacBuilder
{
    Task AskAsync(AppBuild build, CancellationToken ct);
}

/// <summary>
/// The Mac is a run of one workflow in Workstream's own repository on GitHub's Mac runners. Asking is one
/// request to GitHub: run the workflow for this build. The run is told the build's id and this API's
/// address, which are no secret; its key is the repository's own secret.
/// </summary>
public sealed class GitHubMacBuilder(GitHubClient github, IGitHubTokens tokens, WorkstreamDbContext db, BuildOptions options, WorkstreamApp app) : IMacBuilder
{
    public async Task AskAsync(AppBuild build, CancellationToken ct)
    {
        var parts = (options.Repository ?? "").Split('/');
        var repository = parts.Length == 2 ? GitHubRepositoryName.Parse(parts[0], parts[1]) : null;
        if (repository is null) throw new InvalidOperationException("Workstream:Builds:Repository is not owner/name.");
        var token = options.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            var administrators = await db.Set<SqlOSFgaUserGroupMembership>().AsNoTracking()
                .Where(x => x.UserGroupId == PlatformSeed.SystemAdminsGroupId).Select(x => x.SubjectId).ToListAsync(ct);
            foreach (var administrator in administrators)
            {
                token = await tokens.ForUserAsync(administrator, ct);
                if (!string.IsNullOrWhiteSpace(token)) break;
            }
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Nobody can start the Mac: the system administrator has not signed in with GitHub, and no token is set for builds.");
        }
        await github.RunWorkflowAsync(token, repository, options.Workflow, options.Ref,
            new Dictionary<string, string> { ["build"] = build.Id, ["api"] = app.Origin, ["stage"] = options.Stage }, ct);
    }
}

/// <summary>
/// The Mac's key. One service account, <c>service_account::app-builder</c>, whose secret is the deployment's
/// setting (<see cref="BuildOptions.Key"/>) and the workflow's secret. Like a desktop's key it says who is
/// calling and nothing about what they may do: that is the grant <c>app_builder</c> on root, which carries
/// <c>TEAM_RUN_BUILD</c>.
/// </summary>
public static class BuilderKeys
{
    public const string Scheme = "Workstream.Builder";
    public const string Policy = "Workstream.Builder";
    public const string Prefix = "wsb_";
    public const string SubjectId = "service_account::app-builder";
    public const string ClientId = "app-builder";
    const int SecretLength = 64;
    const int FailuresPerMinute = 10;
    const string ItemKey = "Workstream.Builder";
    static readonly TimeSpan VerifiedFor = TimeSpan.FromMinutes(5);

    public static bool IsKey(string? key) =>
        key is not null && key.Length == Prefix.Length + SecretLength && key.StartsWith(Prefix, StringComparison.Ordinal) && key[Prefix.Length..].All(Uri.IsHexDigit);

    /// <summary>The Mac this request authenticated as.</summary>
    public static Caller CallerOf(HttpContext http) =>
        http.Items.TryGetValue(ItemKey, out var caller) && caller is Caller builder ? builder : throw new BadHttpRequestException("Missing builder key.", StatusCodes.Status401Unauthorized);

    /// <summary>
    /// The account follows the setting: made the first time a key is set, given the key's hash when the key
    /// changed, and expired when there is none. Granted <c>app_builder</c> on root once. The caller saves.
    /// </summary>
    public static async Task SeedAsync(WorkstreamDbContext db, SqlOSCryptoService crypto, BuildOptions options, CancellationToken ct)
    {
        var account = await db.Set<SqlOSFgaServiceAccount>().SingleOrDefaultAsync(x => x.ClientId == ClientId, ct);
        if (!options.Enabled)
        {
            if (account is not null && account.ExpiresAt is null)
            {
                account.ExpiresAt = DateTime.UtcNow;
                account.UpdatedAt = DateTime.UtcNow;
            }
            return;
        }
        if (!IsKey(options.Key)) throw new InvalidOperationException("Workstream:Builds:Key is wsb_ followed by 64 hex characters.");
        var secret = options.Key![Prefix.Length..];
        if (account is null)
        {
            await db.ProvisionServiceAccountSubjectAsync(SubjectId, "Mac builder", ClientId, crypto.HashPassword(secret),
                description: "The Mac that builds iPhone apps.", organizationId: null, cancellationToken: ct);
        }
        else if (account.ExpiresAt is not null || !crypto.VerifyPassword(account.ClientSecretHash, secret))
        {
            account.ClientSecretHash = crypto.HashPassword(secret);
            account.ExpiresAt = null;
            account.UpdatedAt = DateTime.UtcNow;
        }
        if (!await db.Set<SqlOSFgaGrant>().AnyAsync(x => x.SubjectId == SubjectId && x.ResourceId == PlatformSeed.RootResourceId && x.RoleId == Roles.AppBuilder, ct))
        {
            IdentityProvisioning.Grant(db, SubjectId, PlatformSeed.RootResourceId, Roles.AppBuilder);
        }
    }

    public static IServiceCollection AddBuilderKeys(this IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, Handler>(Scheme, null);
        services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser());
        return services;
    }

    /// <summary>Resolves the key to the builder's account. Every failure is one generic 401, and wrong keys are counted.</summary>
    public sealed class Handler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        WorkstreamDbContext db, SqlOSCryptoService crypto, IMemoryCache cache)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        const string FailuresKey = "wsb-failures";

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer " + Prefix, StringComparison.Ordinal)) return AuthenticateResult.NoResult();
            var raw = header["Bearer ".Length..].Trim();
            if (!IsKey(raw)) return AuthenticateResult.Fail("The key is not the builder's.");

            var account = await db.Set<SqlOSFgaServiceAccount>().AsNoTracking().SingleOrDefaultAsync(x => x.ClientId == ClientId, Context.RequestAborted);
            if (account is null || account.ExpiresAt is { } expiry && expiry <= DateTime.UtcNow) return AuthenticateResult.Fail("The key is not the builder's.");

            var verifiedKey = "wsb:" + crypto.HashToken(raw);
            if (!cache.TryGetValue(verifiedKey, out string? hash) || hash != account.ClientSecretHash)
            {
                if (cache.TryGetValue(FailuresKey, out int failures) && failures >= FailuresPerMinute) return AuthenticateResult.Fail("Too many wrong keys.");
                if (!crypto.VerifyPassword(account.ClientSecretHash, raw[Prefix.Length..]))
                {
                    cache.Set(FailuresKey, failures + 1, TimeSpan.FromMinutes(1));
                    return AuthenticateResult.Fail("The key is not the builder's.");
                }
                cache.Set(verifiedKey, account.ClientSecretHash, new MemoryCacheEntryOptions { SlidingExpiration = VerifiedFor });
            }

            Context.Items[ItemKey] = new Caller(account.SubjectId, null);
            var identity = new ClaimsIdentity([new Claim("sub", account.SubjectId), new Claim("token_kind", "builder")], BuilderKeys.Scheme, "sub", null);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), BuilderKeys.Scheme));
        }
    }
}

/// <summary>
/// The operator's part of a build, as <c>agent::desktop-operator</c> (<c>TEAM_OPERATE_BUILD</c>): asking the
/// Mac once the source is here, and the sweep that fails a build the Mac never finished and removes what
/// nobody has taken for a while.
/// </summary>
public sealed class BuildJobs(IServiceScopeFactory scopes, BuildOptions options, TimeProvider clock, ILogger<BuildJobs> log)
{
    public const string SweepId = "builds-sweep";
    public const string SweepCron = "*/10 * * * *";
    const string Operator = PlatformSeed.OperatorSubjectId;

    /// <summary>The source of this attempt arrived: ask the Mac. An attempt that was replaced, or already taken, asks nothing.</summary>
    public async Task AskAsync(string buildId, string attempt, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
        var build = await db.Builds.SingleOrDefaultAsync(x => x.Id == buildId, ct);
        if (build is null || build.Attempt != attempt || build.State != AppBuildState.Queued) return;
        if (!await scope.ServiceProvider.GetRequiredService<ISqlOSFgaAuthService>().Allows(Operator, Permissions.TeamOperateBuild, Team.ResourceIdFor(build.TeamId))) return;
        try
        {
            await scope.ServiceProvider.GetRequiredService<IMacBuilder>().AskAsync(build, ct);
        }
        catch (Exception error) when (error is InvalidOperationException or GitHubRefused or HttpRequestException or TaskCanceledException)
        {
            log.LogWarning(error, "The Mac was not asked to build {Build}.", build.Id);
            build.Fail(error is InvalidOperationException ? error.Message : "The Mac could not be started. Try again in a few minutes.", clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Every ten minutes: fail what a Mac never finished, forget what never sent its source, remove what nobody takes.</summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
        var files = scope.ServiceProvider.GetRequiredService<IBuildFiles>();
        var now = clock.GetUtcNow();
        var late = now.AddMinutes(-options.MaxMinutes);
        foreach (var build in await db.Builds.Where(x => (x.State == AppBuildState.Queued || x.State == AppBuildState.Building || x.State == AppBuildState.Waiting) && x.AskedAt < late).ToListAsync(ct))
        {
            build.Fail(build.State == AppBuildState.Waiting ? "The desktop never sent the source." : $"The Mac did not finish in {options.MaxMinutes} minutes.", now);
            await files.DeleteAsync(build.FileName(AppBuild.SourceFile), ct);
        }
        await db.SaveChangesAsync(ct);

        var old = now.AddDays(-options.KeepDays);
        foreach (var build in await db.Builds.Where(x => (x.State == AppBuildState.Done || x.State == AppBuildState.Failed) && x.UsedAt < old).Take(200).ToListAsync(ct))
        {
            foreach (var file in new[] { AppBuild.SourceFile, AppBuild.AppFile, AppBuild.LogFile }) await files.DeleteAsync(build.FileName(file), ct);
            db.Builds.Remove(build);
        }
        await db.SaveChangesAsync(ct);
    }
}

public static class BuildHosting
{
    public static WebApplicationBuilder AddBuilds(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("Workstream:Builds").Get<BuildOptions>() ?? new BuildOptions();
        builder.Services.AddSingleton(options);
        builder.Services.AddBuilderKeys();
        builder.Services.AddTransient<BuildJobs>();
        builder.Services.AddScoped<IMacBuilder, GitHubMacBuilder>();
        if (!string.IsNullOrWhiteSpace(options.Azure.Container))
        {
            builder.Services.TryAddSingletonCredential();
            builder.Services.AddHttpClient<IBuildFiles, AzureBuildFiles>(client => client.Timeout = TimeSpan.FromMinutes(30));
        }
        else
        {
            builder.Services.AddSingleton<IBuildFiles, FolderBuildFiles>();
        }
        return builder;
    }

    /// <summary>The identity the API calls Azure as, when the desktops' host has not already registered it.</summary>
    static void TryAddSingletonCredential(this IServiceCollection services)
    {
        if (services.All(x => x.ServiceType != typeof(TokenCredential))) services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
    }
}
