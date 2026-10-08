using System.Collections.Concurrent;
using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;

namespace ApiaryCopilot.Api.Accounts;

/// <summary>
/// Asks the request's <see cref="ITenantDirectory"/> and remembers only a <see cref="TenantStatus.Ready"/> answer, per
/// account and subject, for <see cref="ReadyLifetime"/>. A failure is never remembered, so a user whose row (or subject) is
/// set after a refusal is served on the next request; the cost is one or two cheap indexed reads per request while it is
/// missing, and none while it is cached. A row removed is noticed within <see cref="ReadyLifetime"/>.
/// </summary>
public sealed class CachedTenantReadiness(ITenantDirectory directory, ReadyTenants ready, TimeProvider clock) : ITenantReadiness
{
    /// <summary>How long a ready answer is trusted before the database is asked again.</summary>
    public static readonly TimeSpan ReadyLifetime = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async Task<TenantLookup> ResolveAsync(AccountId account, string subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var key = (account, subject);
        if (ready.Entries.TryGetValue(key, out var entry) && clock.GetUtcNow() < entry.Until)
        {
            return TenantLookup.Ready(entry.UserId);
        }

        var lookup = await directory.FindActingUserAsync(subject, cancellationToken);
        if (lookup is { Status: TenantStatus.Ready, UserId: { } userId })
        {
            ready.Entries[key] = (userId, clock.GetUtcNow() + ReadyLifetime);
        }
        else
        {
            ready.Entries.TryRemove(key, out _);
        }

        return lookup;
    }
}

/// <summary>The ready answers <see cref="CachedTenantReadiness"/> remembers, one per account and subject that has signed in.</summary>
public sealed class ReadyTenants
{
    /// <summary>The acting user per account and subject, and until when it is trusted.</summary>
    public ConcurrentDictionary<(AccountId Account, string Subject), (UserId UserId, DateTimeOffset Until)> Entries { get; } = new();
}
