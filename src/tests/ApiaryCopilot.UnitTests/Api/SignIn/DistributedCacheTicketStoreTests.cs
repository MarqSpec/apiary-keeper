using System.Security.Claims;
using System.Text;
using ApiaryCopilot.Api.SignIn;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace ApiaryCopilot.UnitTests.Api.SignIn;

// The server-side session store behind IDistributedCache (D-19): the cookie carries only the key this store hands out,
// the session itself stays on the server, protected, and expires with the session.
public sealed class DistributedCacheTicketStoreTests
{
    private const string Subject = "4e97dd9a-9f79-4493-8185-795ac0034b7d";
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

    private readonly Dictionary<string, (byte[] Value, DistributedCacheEntryOptions Options)> _entries = new(StringComparer.Ordinal);
    private readonly IDistributedCache _cache = A.Fake<IDistributedCache>();
    private readonly TimeProvider _clock = A.Fake<TimeProvider>();
    private readonly SignInOptions _options = new();
    private readonly DistributedCacheTicketStore _store;

    public DistributedCacheTicketStoreTests()
    {
        A.CallTo(() => _clock.GetUtcNow()).Returns(Now);
        A.CallTo(() => _cache.SetAsync(A<string>._, A<byte[]>._, A<DistributedCacheEntryOptions>._, A<CancellationToken>._))
            .Invokes((string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken _) => _entries[key] = (value, options));
        A.CallTo(() => _cache.GetAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily((string key, CancellationToken _) => _entries.TryGetValue(key, out var entry) ? entry.Value : null);
        A.CallTo(() => _cache.RemoveAsync(A<string>._, A<CancellationToken>._))
            .Invokes((string key, CancellationToken _) => _entries.Remove(key));
        _store = new DistributedCacheTicketStore(_cache, new EphemeralDataProtectionProvider(), _clock, Options.Create(_options));
    }

    [Fact] // invariant: the key the cookie carries is random and long enough not to be guessed, and new every time
    public async Task StoreAsync_ShouldHandOutAnUnguessableKey_WhenASessionIsStored()
    {
        var first = await _store.StoreAsync(Ticket());
        var second = await _store.StoreAsync(Ticket());

        first.Should().NotBe(second);
        foreach (var key in new[] { first, second })
        {
            key.Should().StartWith(DistributedCacheTicketStore.KeyPrefix);
            key[DistributedCacheTicketStore.KeyPrefix.Length..].Should().MatchRegex("^[A-Za-z0-9_-]{43}$", "32 random bytes, base64url");
        }
    }

    [Fact] // regression risk: what is stored is what comes back - the principal and the session's own items
    public async Task RetrieveAsync_ShouldReturnTheStoredSession_WhenItsKeyIsGiven()
    {
        var ticket = Ticket();
        var key = await _store.StoreAsync(ticket);

        var read = await _store.RetrieveAsync(key);

        read.Should().NotBeNull();
        read!.Principal.FindFirst("sub")!.Value.Should().Be(Subject);
        read.Properties.Items[SessionLifetime.StartedKey].Should().Be(ticket.Properties.Items[SessionLifetime.StartedKey]);
    }

    [Fact] // invariant: the session is protected in the cache, so a cache moved out of process (Redis) holds no claims in the clear
    public async Task StoreAsync_ShouldNotWriteTheClaimsInTheClear_WhenASessionIsStored()
    {
        var key = await _store.StoreAsync(Ticket());

        Encoding.UTF8.GetString(_entries[key].Value).Should().NotContain(Subject);
    }

    [Fact] // invariant: the stored session expires with the session, at the cookie's expiry or the maximum lifetime
    public async Task StoreAsync_ShouldExpireTheEntryWithTheSession_WhenASessionIsStored()
    {
        var ticket = Ticket();
        ticket.Properties.ExpiresUtc = Now + _options.SessionIdleTimeout;

        var key = await _store.StoreAsync(ticket);

        _entries[key].Options.AbsoluteExpiration.Should().Be(Now + _options.SessionIdleTimeout);
    }

    [Fact] // regression risk: a sliding renewal replaces the session under the same key and moves its expiry
    public async Task RenewAsync_ShouldReplaceTheSessionAndItsExpiry_WhenRenewed()
    {
        var key = await _store.StoreAsync(Ticket());
        var renewed = Ticket(role: "helper");
        var later = Now + TimeSpan.FromMinutes(20);
        A.CallTo(() => _clock.GetUtcNow()).Returns(later);
        renewed.Properties.ExpiresUtc = later + _options.SessionIdleTimeout;

        await _store.RenewAsync(key, renewed);

        _entries[key].Options.AbsoluteExpiration.Should().Be(later + _options.SessionIdleTimeout);
        (await _store.RetrieveAsync(key))!.Principal.FindFirst("role")!.Value.Should().Be("helper");
    }

    [Fact] // boundary: an unknown key is no session
    public async Task RetrieveAsync_ShouldReturnNull_WhenTheKeyIsUnknown()
    {
        (await _store.RetrieveAsync(DistributedCacheTicketStore.KeyPrefix + "unknown")).Should().BeNull();
    }

    [Fact] // boundary: an entry that does not unprotect is no session, rather than an error for the caller
    public async Task RetrieveAsync_ShouldReturnNull_WhenTheEntryIsUnreadable()
    {
        var key = await _store.StoreAsync(Ticket());
        _entries[key] = ([1, 2, 3], _entries[key].Options);

        (await _store.RetrieveAsync(key)).Should().BeNull();
    }

    [Fact] // invariant: signing out removes the session from the server, so a copied cookie stops working
    public async Task RemoveAsync_ShouldRemoveTheSession_WhenRemoved()
    {
        var key = await _store.StoreAsync(Ticket());

        await _store.RemoveAsync(key);

        (await _store.RetrieveAsync(key)).Should().BeNull();
    }

    private static AuthenticationTicket Ticket(string role = "owner")
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", Subject), new Claim("account_id", "a91a7c0e-5747-4e00-8000-000000000019"), new Claim("role", role)],
            ApiaryClaims.AuthenticationType, ApiaryClaims.Subject, ApiaryClaims.Role));
        var properties = new AuthenticationProperties();
        SessionLifetime.Stamp(properties, Now);
        return new AuthenticationTicket(principal, properties, "Cookies");
    }
}
