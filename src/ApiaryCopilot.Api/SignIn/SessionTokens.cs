using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace ApiaryCopilot.Api.SignIn;

/// <summary>
/// What a browser session holds server-side beside its ticket: Keycloak's refresh token, when the access token issued
/// with it expires, and the principal the last validated ID token gave. It is the session's one source of truth for
/// renewal: written only under the session's lock, it is never sent to the browser, never logged, and protected before
/// it is cached. <see cref="Generation"/> counts renewals, so a request holding an older ticket adopts the newer principal.
/// </summary>
/// <param name="RefreshToken">Keycloak's refresh token. Secret: never logged, echoed or returned.</param>
/// <param name="AccessTokenExpires">When the access token issued with it expires; a renewal is due shortly before.</param>
/// <param name="Started">When the session was signed in, which bounds the entry's life by the maximum session length.</param>
/// <param name="Generation">The number of renewals so far: 0 at sign-in.</param>
/// <param name="Subject">The principal's <c>sub</c>, which a renewal may never change.</param>
/// <param name="AccountId">The principal's <c>account_id</c>.</param>
/// <param name="Roles">The principal's application roles.</param>
public sealed record SessionTokens(
    string RefreshToken,
    DateTimeOffset AccessTokenExpires,
    DateTimeOffset Started,
    int Generation,
    string Subject,
    string AccountId,
    IReadOnlyList<string> Roles)
{
    /// <summary>The ticket's item naming the session's entry: a random id, not a secret, unrelated to the cookie's key.</summary>
    public const string SessionIdItem = ".apiary-keeper.session.id";

    /// <summary>The ticket's item holding the <see cref="Generation"/> its principal was built from.</summary>
    public const string GenerationItem = ".apiary-keeper.session.generation";

    /// <summary>The state for a new session, from the principal its validated ID token gave.</summary>
    public static SessionTokens Begin(ClaimsPrincipal principal, string refreshToken, DateTimeOffset accessTokenExpires, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new SessionTokens(
            refreshToken,
            accessTokenExpires,
            now,
            0,
            principal.FindFirstValue(ApiaryClaims.Subject)!,
            principal.FindFirstValue(ApiaryClaims.AccountId)!,
            principal.FindAll(ApiaryClaims.Role).Select(c => c.Value).ToList());
    }

    /// <summary>
    /// The state for a session Keycloak's token response starts, or <see langword="null"/> when the response carries no
    /// refresh token or no positive access-token lifetime: such a session could never be renewed, so the sign-in fails closed.
    /// </summary>
    public static SessionTokens? FromSignIn(ClaimsPrincipal principal, string? refreshToken, string? expiresIn, DateTimeOffset now) =>
        !string.IsNullOrEmpty(refreshToken)
        && int.TryParse(expiresIn, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
        && seconds > 0
            ? Begin(principal, refreshToken, now + TimeSpan.FromSeconds(seconds), now)
            : null;

    /// <summary>A new random session id.</summary>
    public static string NewSessionId() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>The session id a ticket names, or <see langword="null"/>.</summary>
    public static string? SessionIdOf(AuthenticationProperties properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return properties.Items.TryGetValue(SessionIdItem, out var id) && !string.IsNullOrEmpty(id) ? id : null;
    }

    /// <summary>The generation a ticket's principal was built from, or -1 when it names none.</summary>
    public static int GenerationOf(AuthenticationProperties properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return properties.Items.TryGetValue(GenerationItem, out var value) && int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var generation)
            ? generation
            : -1;
    }

    /// <summary>Points a ticket at a session's entry and the generation its principal was built from.</summary>
    public static void Stamp(AuthenticationProperties properties, string sessionId, int generation)
    {
        ArgumentNullException.ThrowIfNull(properties);
        properties.Items[SessionIdItem] = sessionId;
        properties.Items[GenerationItem] = generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The principal this state holds: the same shape the tokens give (<see cref="ApiaryClaims"/>).</summary>
    public ClaimsPrincipal ToPrincipal() =>
        ApiaryClaims.FromToken(
            new[] { new Claim(ApiaryClaims.Subject, Subject), new Claim(ApiaryClaims.AccountId, AccountId) }
                .Concat(Roles.Select(r => new Claim(ApiaryClaims.TokenRoles, r))))
        ?? throw new InvalidOperationException("A session's state holds no valid principal.");

    /// <inheritdoc />
    public override string ToString() => $"SessionTokens {{ Generation = {Generation} }}";
}

/// <summary>Where each session's <see cref="SessionTokens"/> are kept, server-side.</summary>
public interface ISessionTokenStore
{
    /// <summary>The session's state, or <see langword="null"/> when it has none (ended, expired or unreadable).</summary>
    Task<SessionTokens?> GetAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Writes the session's state, expiring after the idle timeout or at the maximum session length.</summary>
    Task SetAsync(string sessionId, SessionTokens tokens, CancellationToken cancellationToken);

    /// <summary>Removes the session's state.</summary>
    Task RemoveAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ISessionTokenStore"/> behind <see cref="IDistributedCache"/>, beside the sessions (in memory today), each
/// entry protected with Data Protection before it is cached, so a cache moved out of process (Redis) holds no refresh
/// token in the clear.
/// </summary>
public sealed class DistributedCacheSessionTokenStore(
    IDistributedCache cache,
    IDataProtectionProvider dataProtection,
    IOptions<SignInOptions> options) : ISessionTokenStore
{
    /// <summary>Every key's prefix, so the entries are recognisable in a shared cache.</summary>
    public const string KeyPrefix = "apiary-keeper:session-tokens:";

    private readonly IDataProtector _protector = dataProtection.CreateProtector("ApiaryCopilot.Api.SignIn.SessionTokens.v1");

    /// <inheritdoc />
    public async Task<SessionTokens?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        var protectedEntry = await cache.GetAsync(KeyPrefix + sessionId, cancellationToken);
        if (protectedEntry is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(_protector.Unprotect(protectedEntry), SessionTokensJsonContext.Default.SessionTokens);
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            // Protected under a key no longer in the ring, or altered: no session.
            return null;
        }
    }

    /// <inheritdoc />
    public Task SetAsync(string sessionId, SessionTokens tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var settings = options.Value;
        return cache.SetAsync(
            KeyPrefix + sessionId,
            _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(tokens, SessionTokensJsonContext.Default.SessionTokens)),
            new DistributedCacheEntryOptions
            {
                SlidingExpiration = settings.SessionIdleTimeout,
                AbsoluteExpiration = tokens.Started + settings.SessionMaxLifetime,
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string sessionId, CancellationToken cancellationToken) => cache.RemoveAsync(KeyPrefix + sessionId, cancellationToken);
}

[JsonSerializable(typeof(SessionTokens))]
internal sealed partial class SessionTokensJsonContext : JsonSerializerContext;
