using SqlOS.AuthServer.Extensions;
using Workstream.Api.Desktops;
using Workstream.Api.Domain;

namespace Workstream.Api;

/// <summary>How an operation ended, in the terms the REST adapter turns into status codes.</summary>
public enum OutcomeKind
{
    Ok,
    /// <summary>A rule was broken; the message is the rule's words (400).</summary>
    Invalid,
    /// <summary>The caller may not do this to that node (403).</summary>
    Forbidden,
    /// <summary>Not there, or not visible to the caller (404).</summary>
    NotFound,
    /// <summary>Already exists, so a retried create is done (409).</summary>
    Exists,
    /// <summary>The team's limit is reached; the message says which (402).</summary>
    LimitReached,
    /// <summary>This environment is not set up for it (503).</summary>
    Unavailable,
}

/// <summary>
/// What an operation answers. One implementation of each verb lives in <see cref="Operations"/>; the REST
/// handlers are adapters over it, so a rule and its words are written once.
/// </summary>
public sealed record Outcome<T>(OutcomeKind Kind, T? Value, string? Message)
{
    public bool IsOk => Kind == OutcomeKind.Ok;

    public static Outcome<T> Ok(T value) => new(OutcomeKind.Ok, value, null);
    public static Outcome<T> Invalid(string message) => new(OutcomeKind.Invalid, default, message);
    public static Outcome<T> Invalid(DomainRuleException rule) => Invalid(rule.Message);
    public static Outcome<T> Forbidden(string message) => new(OutcomeKind.Forbidden, default, message);
    public static Outcome<T> NotFound() => new(OutcomeKind.NotFound, default, null);
    public static Outcome<T> Exists(string message) => new(OutcomeKind.Exists, default, message);
    public static Outcome<T> LimitReached(string message) => new(OutcomeKind.LimitReached, default, message);
    public static Outcome<T> Unavailable(string message) => new(OutcomeKind.Unavailable, default, message);

    /// <summary>The same failure, for another value type.</summary>
    public Outcome<TOther> As<TOther>() => new(Kind, default, Message);
}

/// <summary>
/// The principal calling, from the credential validated for this request and never from what the request says:
/// a person (the token SqlOS validated) or a desktop (the key <see cref="DesktopKeys"/> resolved). A desktop
/// is known by its own id. Which task it works on is not part of who it is: that is a grant.
/// </summary>
public sealed record Caller(string PrincipalId, string? Scope, string? DesktopId = null)
{
    public bool IsDesktop => DesktopId is not null;

    /// <summary>Whether the credential was granted this scope: the client's ceiling, never a per-node decision.</summary>
    public bool HasScope(string scope) =>
        Scope is not null && Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope, StringComparer.Ordinal);

    /// <summary>A person, from the token SqlOS validated. 401 when it names nobody.</summary>
    public static Caller Of(HttpContext http)
    {
        var token = http.GetSqlOSValidatedToken();
        var userId = token?.UserId ?? throw new BadHttpRequestException("Missing subject.", StatusCodes.Status401Unauthorized);
        return new Caller(userId, token.Scope);
    }

    /// <summary>A desktop, from its key. 401 when the request carries none.</summary>
    public static Caller Desktop(HttpContext http) =>
        DesktopKeys.CallerOf(http) ?? throw new BadHttpRequestException("Missing desktop key.", StatusCodes.Status401Unauthorized);
}

/// <summary>Every verb, written once. Each file in Operations/ adds its part.</summary>
public static partial class Operations
{
}
