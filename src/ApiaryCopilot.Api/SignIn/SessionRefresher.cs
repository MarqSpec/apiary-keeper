using System.Security.Claims;

namespace ApiaryCopilot.Api.SignIn;

/// <summary>What a request's cookie session becomes after <see cref="SessionRefresher.CheckAsync"/>.</summary>
public abstract record SessionCheck
{
    private SessionCheck()
    {
    }

    /// <summary>The ticket's principal is current: carry on.</summary>
    public sealed record Keep : SessionCheck;

    /// <summary>The session holds a newer principal than the ticket: use it, and write it back to the ticket.</summary>
    public sealed record Replace(ClaimsPrincipal Principal, int Generation) : SessionCheck;

    /// <summary>The session is over: Keycloak refused its renewal, or it has nothing to renew with. Sign it out.</summary>
    public sealed record Ended(string Why) : SessionCheck;

    /// <summary>The renewal is overdue and Keycloak cannot be asked: refuse this request, keep the session for the next.</summary>
    public sealed record Deny : SessionCheck;
}

/// <summary>
/// The token renewal written in the API (ARCHITECTURE.md D-19; no library): on each request a cookie session makes, when
/// its access token is within <see cref="RefreshMargin"/> of expiring, the server-held refresh token is presented to
/// Keycloak and the principal rebuilt from the new, validated ID token, so a role or account change reaches the session at
/// the next renewal, and a disabled user, a revoked or expired refresh token or an ended Keycloak session ends it. One
/// renewal per session at a time: with rotation on a refresh token is good once, so concurrent requests wait for the one
/// renewing and adopt its result rather than presenting a spent token.
/// </summary>
public sealed partial class SessionRefresher(
    ISessionTokenStore store,
    IKeycloakTokens keycloak,
    IIdTokenValidator idTokens,
    TimeProvider clock,
    ILogger<SessionRefresher> logger)
{
    /// <summary>How long before the access token expires its renewal is due.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(30);

    // Striped, so memory stays bounded however many sessions there are; two sessions sharing a stripe only wait on each other.
    private readonly SemaphoreSlim[] _locks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    /// <summary>
    /// Renews the session if it is due and says what the request's principal becomes. <paramref name="ticketGeneration"/> is the
    /// generation the ticket's principal was built from. The call to Keycloak is bounded by its own timeout and is not
    /// cancelled with the request, since a renewal Keycloak completed but this side dropped would leave a spent token behind.
    /// </summary>
    public async Task<SessionCheck> CheckAsync(string sessionId, int ticketGeneration)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        var tokens = await store.GetAsync(sessionId, CancellationToken.None);
        if (tokens is null)
        {
            return new SessionCheck.Ended("the session holds no tokens");
        }

        if (!IsDue(tokens))
        {
            return Current(tokens, ticketGeneration);
        }

        var gate = _locks[(int)((uint)StringComparer.Ordinal.GetHashCode(sessionId) % (uint)_locks.Length)];
        await gate.WaitAsync();
        try
        {
            // Another request may have renewed it, or ended it, while this one waited.
            tokens = await store.GetAsync(sessionId, CancellationToken.None);
            if (tokens is null)
            {
                return new SessionCheck.Ended("the session ended while the request waited");
            }

            return IsDue(tokens) ? await RenewAsync(sessionId, tokens, ticketGeneration) : Current(tokens, ticketGeneration);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SessionCheck> RenewAsync(string sessionId, SessionTokens tokens, int ticketGeneration)
    {
        switch (await keycloak.RefreshAsync(tokens.RefreshToken, CancellationToken.None))
        {
            case TokenRefresh.Renewed renewed:
                var principal = await idTokens.ValidateAsync(renewed.IdToken, CancellationToken.None);
                if (principal is null || principal.FindFirstValue(ApiaryClaims.Subject) != tokens.Subject)
                {
                    LogRefused(logger, "invalid_id_token");
                    await store.RemoveAsync(sessionId, CancellationToken.None);
                    return new SessionCheck.Ended("the renewed ID token is not valid for this session");
                }

                var next = SessionTokens.Begin(principal, renewed.RefreshToken ?? tokens.RefreshToken, clock.GetUtcNow() + renewed.AccessTokenLifetime, tokens.Started)
                    with
                { Generation = tokens.Generation + 1 };
                await store.SetAsync(sessionId, next, CancellationToken.None);
                return new SessionCheck.Replace(principal, next.Generation);

            case TokenRefresh.Refused refused:
                LogRefused(logger, refused.Error);
                await store.RemoveAsync(sessionId, CancellationToken.None);
                return new SessionCheck.Ended("Keycloak refused the renewal");

            case TokenRefresh.Unavailable unavailable:
                LogUnavailable(logger, unavailable.Reason);
                // Inside the margin the claims are still within the access token's life; past it, nothing is served on them.
                return clock.GetUtcNow() < tokens.AccessTokenExpires ? Current(tokens, ticketGeneration) : new SessionCheck.Deny();

            default:
                throw new InvalidOperationException("Unhandled refresh outcome.");
        }
    }

    private bool IsDue(SessionTokens tokens) => clock.GetUtcNow() >= tokens.AccessTokenExpires - RefreshMargin;

    private static SessionCheck Current(SessionTokens tokens, int ticketGeneration) =>
        tokens.Generation == ticketGeneration ? new SessionCheck.Keep() : new SessionCheck.Replace(tokens.ToPrincipal(), tokens.Generation);

    [LoggerMessage(Level = LogLevel.Information, Message = "A session's renewal was refused ({Error}); the session ends and the user signs in again.")]
    private static partial void LogRefused(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keycloak could not renew a session ({Reason}); it is tried again on the next request.")]
    private static partial void LogUnavailable(ILogger logger, string reason);
}
