using System.Collections.Concurrent;
using SqlOS.Email.Contracts;
using SqlOS.Email.Interfaces;

namespace Workstream.Tests;

/// <summary>
/// The mail provider, played: it replaces only delivery. Every email is rendered by real SqlOS from the
/// template the API stored, and this keeps each message by address, so a journey reads what a person would
/// have received. Sign-in in the journeys is by password, and an invitation hands over its link, so the only
/// mail is the API's own.
/// </summary>
public sealed class TestMail : ISqlOSEmailSender
{
    readonly ConcurrentDictionary<string, List<SqlOSEmailMessage>> sent = new(StringComparer.OrdinalIgnoreCase);

    public bool IsConfigured => true;

    /// <summary>Every email sent to this address, oldest first.</summary>
    public IReadOnlyList<SqlOSEmailMessage> Sent(string email)
    {
        if (!sent.TryGetValue(email, out var messages)) return [];
        lock (messages) return [.. messages];
    }

    public Task<SqlOSEmailProviderResult> SendAsync(SqlOSEmailMessage message, CancellationToken cancellationToken = default)
    {
        var messages = sent.GetOrAdd(message.To, _ => []);
        lock (messages) messages.Add(message);
        return Task.FromResult(new SqlOSEmailProviderResult(Guid.NewGuid().ToString("N")));
    }
}

/// <summary>A provider nobody set up, as in development and staging.</summary>
public sealed class NoMail : ISqlOSEmailSender
{
    public bool IsConfigured => false;

    public Task<SqlOSEmailProviderResult> SendAsync(SqlOSEmailMessage message, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Email is not set up.");
}

/// <summary>The clock, some time from now: for what a job does once a wait is over.</summary>
public sealed class Later(TimeSpan by) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + by;
}
