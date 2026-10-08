using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace ApiaryCopilot.Api.SignIn;

/// <summary>
/// The server-side session store (D-19): sessions behind <see cref="IDistributedCache"/> - in memory today, one replica,
/// lost on a restart - so the cookie carries only the key this store hands out. Each session is protected with Data
/// Protection before it is cached, so moving the cache out of process (Redis) puts no claims in the clear, and it
/// expires with the session: at the cookie's expiry or the maximum lifetime, whichever is first.
/// </summary>
public sealed class DistributedCacheTicketStore(
    IDistributedCache cache,
    IDataProtectionProvider dataProtection,
    TimeProvider clock,
    IOptions<SignInOptions> options) : ITicketStore
{
    /// <summary>Every key's prefix, so the sessions are recognisable in a shared cache.</summary>
    public const string KeyPrefix = "apiary-keeper:session:";

    private readonly IDataProtector _protector = dataProtection.CreateProtector("ApiaryCopilot.Api.SignIn.Session.v1");

    /// <inheritdoc />
    public Task<string> StoreAsync(AuthenticationTicket ticket) => StoreCoreAsync(ticket, CancellationToken.None);

    /// <inheritdoc />
    public Task<string> StoreAsync(AuthenticationTicket ticket, CancellationToken cancellationToken) =>
        StoreCoreAsync(ticket, cancellationToken);

    /// <inheritdoc />
    public Task<string> StoreAsync(AuthenticationTicket ticket, HttpContext httpContext, CancellationToken cancellationToken) =>
        StoreCoreAsync(ticket, cancellationToken);

    /// <inheritdoc />
    public Task RenewAsync(string key, AuthenticationTicket ticket) => RenewAsync(key, ticket, CancellationToken.None);

    /// <inheritdoc />
    public Task RenewAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken) =>
        WriteAsync(key, ticket, cancellationToken);

    /// <inheritdoc />
    public Task RenewAsync(string key, AuthenticationTicket ticket, HttpContext httpContext, CancellationToken cancellationToken) =>
        WriteAsync(key, ticket, cancellationToken);

    /// <inheritdoc />
    public Task<AuthenticationTicket?> RetrieveAsync(string key) => RetrieveCoreAsync(key, CancellationToken.None);

    /// <inheritdoc />
    public Task<AuthenticationTicket?> RetrieveAsync(string key, CancellationToken cancellationToken) =>
        RetrieveCoreAsync(key, cancellationToken);

    /// <inheritdoc />
    public Task<AuthenticationTicket?> RetrieveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken) =>
        RetrieveCoreAsync(key, cancellationToken);

    /// <inheritdoc />
    public Task RemoveAsync(string key) => cache.RemoveAsync(key, CancellationToken.None);

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken) => cache.RemoveAsync(key, cancellationToken);

    /// <inheritdoc />
    public Task RemoveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken) => cache.RemoveAsync(key, cancellationToken);

    private async Task<string> StoreCoreAsync(AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        var key = KeyPrefix + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await WriteAsync(key, ticket, cancellationToken);
        return key;
    }

    private async Task<AuthenticationTicket?> RetrieveCoreAsync(string key, CancellationToken cancellationToken)
    {
        var protectedTicket = await cache.GetAsync(key, cancellationToken);
        if (protectedTicket is null)
        {
            return null;
        }

        try
        {
            return TicketSerializer.Default.Deserialize(_protector.Unprotect(protectedTicket));
        }
        catch (CryptographicException)
        {
            // Protected under a key no longer in the ring, or altered: no session.
            return null;
        }
    }

    private Task WriteAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var settings = options.Value;
        var expiry = SessionLifetime.EntryExpiry(ticket.Properties, clock.GetUtcNow(), settings.SessionIdleTimeout, settings.SessionMaxLifetime);
        return cache.SetAsync(
            key,
            _protector.Protect(TicketSerializer.Default.Serialize(ticket)),
            new DistributedCacheEntryOptions { AbsoluteExpiration = expiry },
            cancellationToken);
    }
}
