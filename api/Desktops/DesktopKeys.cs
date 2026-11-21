using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SqlOS.AuthServer.Services;
using SqlOS.Extensions;
using SqlOS.Fga.Models;
using Workstream.Api.Domain;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api.Desktops;

/// <summary>
/// A desktop's key: <c>wsd_</c>, its task's id as 32 hex characters, then a secret. The agent on the desktop
/// sends it to <c>/api/desktop</c> and to the model gateway. SqlOS holds the service account and the hashed
/// secret; this is the boundary its guide leaves to the app: parse a bounded credential, look the account up by
/// client id, verify the slow hash, attach only the resolved subject. What the key may do is then a SqlOS check
/// like any other: the account holds <c>task_desktop</c> on its one task.
/// </summary>
public static class DesktopKeys
{
    public const string Scheme = "Workstream.Desktop";
    public const string Policy = "Workstream.Desktop";
    public const string Prefix = "wsd_";
    const int IdLength = 32;
    const int SecretLength = 64;
    /// <summary>Wrong secrets one task's key may be tried with per minute before that task's key is refused for the rest of it.</summary>
    const int FailuresPerMinute = 10;
    static readonly TimeSpan VerifiedFor = TimeSpan.FromSeconds(60);
    const string ItemKey = "Workstream.Desktop";

    public static string SubjectIdFor(string taskId) => $"service_account::desktop-{taskId}";

    static string ClientIdFor(string taskId) => Guid.Parse(taskId).ToString("N");

    /// <summary>The desktop this request authenticated as, once <see cref="Handler"/> resolved its key.</summary>
    public static Caller? CallerOf(HttpContext http) => http.Items.TryGetValue(ItemKey, out var caller) ? caller as Caller : null;

    /// <summary>
    /// Make the desktop's service account, or give an existing one a new secret, and grant it
    /// <c>task_desktop</c> on its task. The key is returned once, for the machine being made, and kept nowhere.
    /// The caller saves.
    /// </summary>
    public static async Task<string> IssueAsync(WorkstreamDbContext db, SqlOSCryptoService crypto, WorkTask task, CancellationToken ct)
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(SecretLength / 2)).ToLowerInvariant();
        var hash = crypto.HashPassword(secret);
        var clientId = ClientIdFor(task.Id);
        var subjectId = SubjectIdFor(task.Id);
        var account = await db.Set<SqlOSFgaServiceAccount>().SingleOrDefaultAsync(x => x.ClientId == clientId, ct);
        if (account is null)
        {
            await db.ProvisionServiceAccountSubjectAsync(subjectId, "Task desktop", clientId, hash,
                description: $"The desktop of task {task.Id}.", organizationId: task.TeamId, cancellationToken: ct);
        }
        else
        {
            account.ClientSecretHash = hash;
            account.ExpiresAt = null;
            account.UpdatedAt = DateTime.UtcNow;
        }
        await db.GrantRoleAsync(subjectId, task.ResourceId, Roles.TaskDesktop, ct);
        return Prefix + clientId + secret;
    }

    /// <summary>End the desktop's key for good: the task is being deleted. The caller saves.</summary>
    public static async Task RetireAsync(WorkstreamDbContext db, string taskId, CancellationToken ct)
    {
        var clientId = ClientIdFor(taskId);
        var account = await db.Set<SqlOSFgaServiceAccount>().SingleOrDefaultAsync(x => x.ClientId == clientId, ct);
        if (account is null) return;
        account.ExpiresAt = DateTime.UtcNow;
        account.UpdatedAt = DateTime.UtcNow;
    }

    public static IServiceCollection AddDesktopKeys(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, Handler>(Scheme, null);
        services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser());
        return services;
    }

    static bool TryParse(string raw, out string taskId, out string secret)
    {
        taskId = "";
        secret = "";
        if (raw.Length != Prefix.Length + IdLength + SecretLength || !raw.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        if (!Guid.TryParseExact(raw.Substring(Prefix.Length, IdLength), "N", out var id)) return false;
        taskId = id.ToString("D");
        secret = raw[(Prefix.Length + IdLength)..];
        return true;
    }

    /// <summary>The key, wherever the caller put it: the agent's own exchanges send a bearer token, and the model clients it runs send an API key header.</summary>
    static string? ReadKey(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return header["Bearer ".Length..].Trim();
        if (request.Headers.TryGetValue("x-api-key", out var apiKey) && apiKey.Count > 0) return apiKey.ToString().Trim();
        if (request.Headers.TryGetValue("api-key", out var azureKey) && azureKey.Count > 0) return azureKey.ToString().Trim();
        return null;
    }

    /// <summary>
    /// Resolves a key to its desktop. Every failure is one generic 401. Wrong secrets are counted per task, never
    /// per address (every desktop reaches the API from the same address), and the count stops only keys not yet
    /// verified; a verified secret is remembered by its hash for as long as it keeps being used, so the slow hash
    /// runs once, while the account's expiry and its current secret are read fresh on every request.
    /// </summary>
    public sealed class Handler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        WorkstreamDbContext db, SqlOSCryptoService crypto, IMemoryCache cache)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (ReadKey(Request) is not { } raw || !raw.StartsWith(Prefix, StringComparison.Ordinal)) return AuthenticateResult.NoResult();

            if (!TryParse(raw, out var taskId, out var secret)) return AuthenticateResult.Fail("The key is not a desktop's.");

            var clientId = ClientIdFor(taskId);
            var account = await db.Set<SqlOSFgaServiceAccount>().AsNoTracking().SingleOrDefaultAsync(x => x.ClientId == clientId, Context.RequestAborted);
            if (account is null || account.ExpiresAt is { } expiry && expiry <= DateTime.UtcNow) return AuthenticateResult.Fail("The key is not a desktop's.");

            // A key verified a moment ago is taken as it is, and stays remembered while its desktop keeps calling
            // (about once a second). So someone guessing at a task's key cannot lock the real desktop out: the
            // limit below only ever stops keys that have not been verified.
            var verifiedKey = "wsd:" + crypto.HashToken(raw);
            if (!cache.TryGetValue(verifiedKey, out string? hash) || hash != account.ClientSecretHash)
            {
                if (cache.TryGetValue(FailureKey(taskId), out int failures) && failures >= FailuresPerMinute)
                {
                    return AuthenticateResult.Fail("Too many wrong keys for this desktop.");
                }
                if (!crypto.VerifyPassword(account.ClientSecretHash, secret)) return Refuse(taskId);
                cache.Set(verifiedKey, account.ClientSecretHash, new MemoryCacheEntryOptions { SlidingExpiration = VerifiedFor });
            }

            var caller = new Caller(account.SubjectId, null, taskId);
            Context.Items[ItemKey] = caller;
            var identity = new ClaimsIdentity([new Claim("sub", caller.PrincipalId), new Claim("token_kind", "desktop")], DesktopKeys.Scheme, "sub", null);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), DesktopKeys.Scheme));
        }

        AuthenticateResult Refuse(string taskId)
        {
            var key = FailureKey(taskId);
            var failures = cache.TryGetValue(key, out int count) ? count + 1 : 1;
            cache.Set(key, failures, TimeSpan.FromMinutes(1));
            return AuthenticateResult.Fail("The key is not a desktop's.");
        }

        static string FailureKey(string taskId) => "wsd-failures:" + taskId;
    }
}
