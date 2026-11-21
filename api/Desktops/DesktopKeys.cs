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
/// A desktop's key: <c>wsd_</c>, the desktop's id as 32 hex characters, then a secret. The agent on the desktop
/// sends it to <c>/api/desktop</c> and to the model gateway. SqlOS holds the service account and the hashed
/// secret; this is the boundary its guide leaves to the app: parse a bounded credential, look the account up by
/// client id, verify the slow hash, attach only the resolved subject.
///
/// The key says which desktop is calling and nothing about what it may do. That is a SqlOS check like any
/// other: the desktop's account holds <c>task_desktop</c> on the one task it works on. A desktop made for a
/// task has the task's id and is granted it as it is made. A spare (<see cref="Spare"/>) has an id of its own
/// and holds nothing until a task takes it, which is one grant: its key, and the machine that holds it, never
/// change.
/// </summary>
public static class DesktopKeys
{
    public const string Scheme = "Workstream.Desktop";
    public const string Policy = "Workstream.Desktop";
    public const string Prefix = "wsd_";
    const int IdLength = 32;
    const int SecretLength = 64;
    /// <summary>Wrong secrets one desktop's key may be tried with per minute before that desktop's key is refused for the rest of it.</summary>
    const int FailuresPerMinute = 10;
    static readonly TimeSpan VerifiedFor = TimeSpan.FromSeconds(60);
    const string ItemKey = "Workstream.Desktop";

    const string SubjectPrefix = "service_account::desktop-";

    public static string SubjectIdFor(string desktopId) => SubjectPrefix + desktopId;

    static string ClientIdFor(string desktopId) => Guid.Parse(desktopId).ToString("N");

    /// <summary>The desktop this request authenticated as, once <see cref="Handler"/> resolved its key.</summary>
    public static Caller? CallerOf(HttpContext http) => http.Items.TryGetValue(ItemKey, out var caller) ? caller as Caller : null;

    /// <summary>
    /// Make a desktop's service account, or give an existing one a new secret. It is granted nothing here. The
    /// key is returned once, for the machine being made, and kept nowhere. The caller saves.
    /// </summary>
    public static async Task<string> IssueAsync(WorkstreamDbContext db, SqlOSCryptoService crypto, string desktopId, string teamId, CancellationToken ct)
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(SecretLength / 2)).ToLowerInvariant();
        var hash = crypto.HashPassword(secret);
        var clientId = ClientIdFor(desktopId);
        var account = await db.Set<SqlOSFgaServiceAccount>().SingleOrDefaultAsync(x => x.ClientId == clientId, ct);
        if (account is null)
        {
            await db.ProvisionServiceAccountSubjectAsync(SubjectIdFor(desktopId), "Desktop", clientId, hash,
                description: $"The desktop {desktopId}.", organizationId: teamId, cancellationToken: ct);
        }
        else
        {
            account.ClientSecretHash = hash;
            account.ExpiresAt = null;
            account.UpdatedAt = DateTime.UtcNow;
        }
        return Prefix + clientId + secret;
    }

    /// <summary>The desktop works on the task from now on: its account holds <c>task_desktop</c> there. The caller saves.</summary>
    public static async Task GrantAsync(WorkstreamDbContext db, string desktopId, WorkTask task, CancellationToken ct) =>
        await db.GrantRoleAsync(SubjectIdFor(desktopId), task, Roles.TaskDesktop, ct);

    /// <summary>End a desktop's key for good. The caller saves.</summary>
    public static async Task RetireAsync(WorkstreamDbContext db, string desktopId, CancellationToken ct)
    {
        var clientId = ClientIdFor(desktopId);
        var account = await db.Set<SqlOSFgaServiceAccount>().SingleOrDefaultAsync(x => x.ClientId == clientId, ct);
        if (account is null) return;
        account.ExpiresAt = DateTime.UtcNow;
        account.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// End the key of every desktop that works on a task: the task is being deleted. Which desktops those are
    /// is read from the grants on the task's node, where it is written. The caller saves.
    /// </summary>
    public static async Task RetireForTaskAsync(WorkstreamDbContext db, WorkTask task, CancellationToken ct)
    {
        var subjects = await db.Set<SqlOSFgaGrant>().AsNoTracking()
            .Where(x => x.ResourceId == task.ResourceId && x.SubjectId.StartsWith(SubjectPrefix)).Select(x => x.SubjectId).Distinct().ToListAsync(ct);
        foreach (var subject in subjects) await RetireAsync(db, subject[SubjectPrefix.Length..], ct);
    }

    public static IServiceCollection AddDesktopKeys(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, Handler>(Scheme, null);
        services.AddAuthorizationBuilder().AddPolicy(Policy, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser());
        return services;
    }

    static bool TryParse(string raw, out string desktopId, out string secret)
    {
        desktopId = "";
        secret = "";
        if (raw.Length != Prefix.Length + IdLength + SecretLength || !raw.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        if (!Guid.TryParseExact(raw.Substring(Prefix.Length, IdLength), "N", out var id)) return false;
        desktopId = id.ToString("D");
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
    /// Resolves a key to its desktop. Every failure is one generic 401. Wrong secrets are counted per desktop, never
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

            if (!TryParse(raw, out var desktopId, out var secret)) return AuthenticateResult.Fail("The key is not a desktop's.");

            var clientId = ClientIdFor(desktopId);
            var account = await db.Set<SqlOSFgaServiceAccount>().AsNoTracking().SingleOrDefaultAsync(x => x.ClientId == clientId, Context.RequestAborted);
            if (account is null || account.ExpiresAt is { } expiry && expiry <= DateTime.UtcNow) return AuthenticateResult.Fail("The key is not a desktop's.");

            // A key verified a moment ago is taken as it is, and stays remembered while its desktop keeps calling
            // (about once a second). So someone guessing at a desktop's key cannot lock the real desktop out: the
            // limit below only ever stops keys that have not been verified.
            var verifiedKey = "wsd:" + crypto.HashToken(raw);
            if (!cache.TryGetValue(verifiedKey, out string? hash) || hash != account.ClientSecretHash)
            {
                if (cache.TryGetValue(FailureKey(desktopId), out int failures) && failures >= FailuresPerMinute)
                {
                    return AuthenticateResult.Fail("Too many wrong keys for this desktop.");
                }
                if (!crypto.VerifyPassword(account.ClientSecretHash, secret)) return Refuse(desktopId);
                cache.Set(verifiedKey, account.ClientSecretHash, new MemoryCacheEntryOptions { SlidingExpiration = VerifiedFor });
            }

            var caller = new Caller(account.SubjectId, null, desktopId);
            Context.Items[ItemKey] = caller;
            var identity = new ClaimsIdentity([new Claim("sub", caller.PrincipalId), new Claim("token_kind", "desktop")], DesktopKeys.Scheme, "sub", null);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), DesktopKeys.Scheme));
        }

        AuthenticateResult Refuse(string desktopId)
        {
            var key = FailureKey(desktopId);
            var failures = cache.TryGetValue(key, out int count) ? count + 1 : 1;
            cache.Set(key, failures, TimeSpan.FromMinutes(1));
            return AuthenticateResult.Fail("The key is not a desktop's.");
        }

        static string FailureKey(string desktopId) => "wsd-failures:" + desktopId;
    }
}
