using System.Text;
using ApiaryCopilot.Api.SignIn;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace ApiaryCopilot.UnitTests.Api.SignIn;

// The refresh token is held server-side only, protected before it is cached, and lives no longer than its session: the
// idle timeout since it was last used, and never past the maximum session length from sign-in.
public sealed class DistributedCacheSessionTokenStoreTests
{
    private const string SessionId = "synthetic-session-id";
    private const string RefreshToken = "synthetic-refresh-token-6f1c";
    private static readonly DateTimeOffset SignedIn = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private readonly Dictionary<string, (byte[] Value, DistributedCacheEntryOptions Options)> _entries = new(StringComparer.Ordinal);
    private readonly IDistributedCache _cache = A.Fake<IDistributedCache>();
    private readonly SignInOptions _options = new() { SessionIdleTimeout = TimeSpan.FromMinutes(30), SessionMaxLifetime = TimeSpan.FromHours(12) };
    private readonly DistributedCacheSessionTokenStore _store;

    public DistributedCacheSessionTokenStoreTests()
    {
        A.CallTo(() => _cache.SetAsync(A<string>._, A<byte[]>._, A<DistributedCacheEntryOptions>._, A<CancellationToken>._))
            .Invokes((string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken _) => _entries[key] = (value, options));
        A.CallTo(() => _cache.GetAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily((string key, CancellationToken _) => _entries.TryGetValue(key, out var entry) ? entry.Value : null);
        A.CallTo(() => _cache.RemoveAsync(A<string>._, A<CancellationToken>._))
            .Invokes((string key, CancellationToken _) => _entries.Remove(key));
        _store = new DistributedCacheSessionTokenStore(_cache, new EphemeralDataProtectionProvider(), Options.Create(_options));
    }

    [Fact] // regression risk: what is stored is read back whole
    public async Task GetAsync_ShouldReturnWhatWasSet_WhenTheSessionHasTokens()
    {
        var tokens = Tokens();
        await _store.SetAsync(SessionId, tokens, CancellationToken.None);

        var read = await _store.GetAsync(SessionId, CancellationToken.None);

        read.Should().BeEquivalentTo(tokens);
    }

    [Fact] // invariant: the refresh token and the claims are protected before they reach the cache
    public async Task SetAsync_ShouldProtectTheEntry_WhenItIsCached()
    {
        await _store.SetAsync(SessionId, Tokens(), CancellationToken.None);

        var (value, _) = _entries.Should().ContainSingle().Subject.Value;
        var text = Encoding.UTF8.GetString(value);
        text.Should().NotContain(RefreshToken).And.NotContain("a91a7c0e").And.NotContain("owner");
        _entries.Keys.Single().Should().Be(DistributedCacheSessionTokenStore.KeyPrefix + SessionId);
    }

    [Fact] // invariant: the entry lives no longer than its session - idle timeout, and the maximum length from sign-in
    public async Task SetAsync_ShouldExpireWithTheSession_WhenItIsCached()
    {
        await _store.SetAsync(SessionId, Tokens(), CancellationToken.None);

        var (_, options) = _entries.Single().Value;
        options.SlidingExpiration.Should().Be(TimeSpan.FromMinutes(30));
        options.AbsoluteExpiration.Should().Be(SignedIn + TimeSpan.FromHours(12));
        options.AbsoluteExpirationRelativeToNow.Should().BeNull();
    }

    [Fact] // boundary: an entry altered in the cache, or protected under a key no longer held, is no session
    public async Task GetAsync_ShouldReturnNull_WhenTheEntryCannotBeUnprotected()
    {
        await _store.SetAsync(SessionId, Tokens(), CancellationToken.None);
        var key = _entries.Keys.Single();
        var (value, options) = _entries[key];
        value[^1] ^= 0x5a;
        _entries[key] = (value, options);

        (await _store.GetAsync(SessionId, CancellationToken.None)).Should().BeNull();
    }

    [Fact] // boundary: a removed or never-set session has no tokens
    public async Task GetAsync_ShouldReturnNull_WhenTheSessionWasRemoved()
    {
        await _store.SetAsync(SessionId, Tokens(), CancellationToken.None);

        await _store.RemoveAsync(SessionId, CancellationToken.None);

        (await _store.GetAsync(SessionId, CancellationToken.None)).Should().BeNull();
        (await _store.GetAsync("never-set", CancellationToken.None)).Should().BeNull();
    }

    private static SessionTokens Tokens() =>
        new(RefreshToken, SignedIn + TimeSpan.FromMinutes(5), SignedIn, 2, "4e97dd9a-9f79-4493-8185-795ac0034b7d", "a91a7c0e-5747-4e00-8000-000000000019", ["owner"]);
}
