using System.Security.Claims;
using ApiaryCopilot.Api.SignIn;
using Microsoft.Extensions.Logging.Abstractions;

namespace ApiaryCopilot.UnitTests.Api.SignIn;

// The renewal written in the API (D-19): a session is renewed with its server-held refresh token shortly before its access
// token expires; a renewal Keycloak refuses ends it; Keycloak being unreachable never extends the claims past the access
// token's life; one renewal per session at a time, since a rotated refresh token is good once.
public sealed class SessionRefresherTests
{
    private const string SessionId = "synthetic-session-id";
    private const string Subject = "4e97dd9a-9f79-4493-8185-795ac0034b7d";
    private const string AccountId = "a91a7c0e-5747-4e00-8000-000000000019";
    private const string OtherAccountId = "c057e0a1-5747-4e00-8000-000000000071";
    private static readonly DateTimeOffset SignedIn = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Expires = SignedIn + TimeSpan.FromMinutes(5);

    private readonly ISessionTokenStore _store = A.Fake<ISessionTokenStore>();
    private readonly IKeycloakTokens _keycloak = A.Fake<IKeycloakTokens>();
    private readonly IIdTokenValidator _idTokens = A.Fake<IIdTokenValidator>();
    private readonly TimeProvider _clock = A.Fake<TimeProvider>();
    private readonly SessionRefresher _refresher;
    private SessionTokens? _stored;
    private DateTimeOffset _now = SignedIn;

    public SessionRefresherTests()
    {
        _stored = new SessionTokens("synthetic-refresh-1", Expires, SignedIn, 0, Subject, AccountId, ["helper"]);
        A.CallTo(() => _clock.GetUtcNow()).ReturnsLazily(() => _now);
        A.CallTo(() => _store.GetAsync(SessionId, A<CancellationToken>._)).ReturnsLazily(() => _stored);
        A.CallTo(() => _store.SetAsync(SessionId, A<SessionTokens>._, A<CancellationToken>._))
            .Invokes((string _, SessionTokens tokens, CancellationToken _) => _stored = tokens);
        A.CallTo(() => _store.RemoveAsync(SessionId, A<CancellationToken>._)).Invokes(() => _stored = null);
        _refresher = new SessionRefresher(_store, _keycloak, _idTokens, _clock, NullLogger<SessionRefresher>.Instance);
    }

    [Fact] // invariant: before the margin nothing is asked of Keycloak and the ticket's principal stands
    public async Task CheckAsync_ShouldKeepThePrincipalWithoutAskingKeycloak_WhenTheRenewalIsNotDue()
    {
        _now = Expires - SessionRefresher.RefreshMargin - TimeSpan.FromTicks(1);

        var check = await _refresher.CheckAsync(SessionId, 0);

        check.Should().BeOfType<SessionCheck.Keep>();
        A.CallTo(_keycloak).MustNotHaveHappened();
    }

    [Fact] // boundary: at the margin the renewal is due
    public async Task CheckAsync_ShouldRenew_WhenTheMarginIsReached()
    {
        _now = Expires - SessionRefresher.RefreshMargin;
        RenewsTo("synthetic-refresh-2", "owner", AccountId);

        var check = await _refresher.CheckAsync(SessionId, 0);

        check.Should().BeOfType<SessionCheck.Replace>();
        A.CallTo(() => _keycloak.RefreshAsync("synthetic-refresh-1", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact] // invariant (#5): a role or account change reaches the session at the renewal, from the new ID token
    public async Task CheckAsync_ShouldReplaceThePrincipalWithTheRenewedClaims_WhenRenewed()
    {
        _now = Expires;
        RenewsTo("synthetic-refresh-2", "owner", OtherAccountId);

        var check = await _refresher.CheckAsync(SessionId, 0);

        var replace = check.Should().BeOfType<SessionCheck.Replace>().Subject;
        replace.Generation.Should().Be(1);
        replace.Principal.FindFirstValue(ApiaryClaims.AccountId).Should().Be(OtherAccountId);
        replace.Principal.FindAll(ApiaryClaims.Role).Select(c => c.Value).Should().Equal("owner");
    }

    [Fact] // invariant: rotation - the new refresh token replaces the spent one, with the new access token's expiry
    public async Task CheckAsync_ShouldKeepTheRotatedRefreshTokenAndTheNewExpiry_WhenRenewed()
    {
        _now = Expires;
        RenewsTo("synthetic-refresh-2", "helper", AccountId, TimeSpan.FromMinutes(5));

        await _refresher.CheckAsync(SessionId, 0);

        _stored.Should().NotBeNull();
        _stored!.RefreshToken.Should().Be("synthetic-refresh-2");
        _stored.AccessTokenExpires.Should().Be(Expires + TimeSpan.FromMinutes(5));
        _stored.Generation.Should().Be(1);
        _stored.Started.Should().Be(SignedIn, "the maximum session length still counts from sign-in");
        _stored.Roles.Should().Equal("helper");
    }

    [Fact] // boundary: with rotation off Keycloak sends no new refresh token, and the current one is kept
    public async Task CheckAsync_ShouldKeepTheCurrentRefreshToken_WhenKeycloakSendsNoNewOne()
    {
        _now = Expires;
        RenewsTo(null, "helper", AccountId);

        await _refresher.CheckAsync(SessionId, 0);

        _stored!.RefreshToken.Should().Be("synthetic-refresh-1");
    }

    [Theory] // invariant: a disabled user, a revoked, rotated-away or expired refresh token, or an ended Keycloak session ends the session
    [InlineData("invalid_grant")]
    [InlineData("unauthorized_client")]
    [InlineData("malformed_response")]
    public async Task CheckAsync_ShouldEndTheSessionAndForgetItsTokens_WhenKeycloakRefusesTheRenewal(string error)
    {
        _now = Expires;
        A.CallTo(() => _keycloak.RefreshAsync(A<string>._, A<CancellationToken>._)).Returns(new TokenRefresh.Refused(error));

        var check = await _refresher.CheckAsync(SessionId, 0);

        check.Should().BeOfType<SessionCheck.Ended>();
        _stored.Should().BeNull();
    }

    [Fact] // invariant: a renewed ID token that does not validate ends the session rather than keeping the old claims
    public async Task CheckAsync_ShouldEndTheSession_WhenTheRenewedIdTokenIsNotValid()
    {
        _now = Expires;
        A.CallTo(() => _keycloak.RefreshAsync(A<string>._, A<CancellationToken>._))
            .Returns(new TokenRefresh.Renewed("synthetic.id.token", "synthetic-refresh-2", TimeSpan.FromMinutes(5)));
        A.CallTo(() => _idTokens.ValidateAsync(A<string>._, A<CancellationToken>._)).Returns(Task.FromResult<ClaimsPrincipal?>(null));

        var check = await _refresher.CheckAsync(SessionId, 0);

        check.Should().BeOfType<SessionCheck.Ended>();
        _stored.Should().BeNull();
    }

    [Fact] // invariant: a renewal can never turn the session into another user
    public async Task CheckAsync_ShouldEndTheSession_WhenTheRenewedIdTokenNamesAnotherSubject()
    {
        _now = Expires;
        RenewsTo("synthetic-refresh-2", "owner", AccountId, subject: "another-synthetic-subject");

        var check = await _refresher.CheckAsync(SessionId, 0);

        check.Should().BeOfType<SessionCheck.Ended>();
        _stored.Should().BeNull();
    }

    [Fact] // boundary: Keycloak unreachable inside the margin - the claims are still within the access token's life
    public async Task CheckAsync_ShouldKeepTheSession_WhenKeycloakIsUnavailableBeforeTheAccessTokenExpires()
    {
        _now = Expires - TimeSpan.FromSeconds(1);
        A.CallTo(() => _keycloak.RefreshAsync(A<string>._, A<CancellationToken>._)).Returns(new TokenRefresh.Unavailable("HTTP 503"));

        var check = await _refresher.CheckAsync(SessionId, 0);

        check.Should().BeOfType<SessionCheck.Keep>();
        _stored!.RefreshToken.Should().Be("synthetic-refresh-1");
    }

    [Fact] // invariant: Keycloak unreachable never serves claims past the access token's life, and does not end the session
    public async Task CheckAsync_ShouldDenyTheRequestButKeepTheSession_WhenKeycloakIsUnavailableAfterTheAccessTokenExpired()
    {
        _now = Expires;
        A.CallTo(() => _keycloak.RefreshAsync(A<string>._, A<CancellationToken>._)).Returns(new TokenRefresh.Unavailable("TaskCanceledException"));

        var check = await _refresher.CheckAsync(SessionId, 0);

        check.Should().BeOfType<SessionCheck.Deny>();
        _stored.Should().NotBeNull("the next request tries again");
    }

    [Fact] // invariant: a session with no tokens - signed out, expired, or protected under a lost key - is over
    public async Task CheckAsync_ShouldEndTheSession_WhenItHoldsNoTokens()
    {
        _stored = null;

        var check = await _refresher.CheckAsync(SessionId, 0);

        check.Should().BeOfType<SessionCheck.Ended>();
        A.CallTo(_keycloak).MustNotHaveHappened();
    }

    [Fact] // invariant: a request carrying an older ticket adopts the session's newer principal instead of its stale claims
    public async Task CheckAsync_ShouldReplaceAnOlderTicketsPrincipal_WhenTheSessionWasRenewedElsewhere()
    {
        _stored = _stored! with { Generation = 3, Roles = ["owner"] };

        var check = await _refresher.CheckAsync(SessionId, 2);

        var replace = check.Should().BeOfType<SessionCheck.Replace>().Subject;
        replace.Generation.Should().Be(3);
        replace.Principal.IsInRole("owner").Should().BeTrue();
        replace.Principal.IsInRole("helper").Should().BeFalse();
        A.CallTo(_keycloak).MustNotHaveHappened();
    }

    [Fact] // regression risk (rotation): concurrent requests at the renewal present the refresh token once, and all adopt its result
    public async Task CheckAsync_ShouldRenewOnceAndShareTheResult_WhenRequestsArriveTogether()
    {
        _now = Expires;
        var release = new TaskCompletionSource<TokenRefresh>(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => _keycloak.RefreshAsync(A<string>._, A<CancellationToken>._)).Returns(release.Task);
        A.CallTo(() => _idTokens.ValidateAsync(A<string>._, A<CancellationToken>._)).Returns(Principal(Subject, AccountId, "owner"));

        var first = _refresher.CheckAsync(SessionId, 0);
        var second = _refresher.CheckAsync(SessionId, 0);
        await Task.Delay(50);
        release.SetResult(new TokenRefresh.Renewed("synthetic.id.token", "synthetic-refresh-2", TimeSpan.FromMinutes(5)));
        var checks = await Task.WhenAll(first, second);

        A.CallTo(() => _keycloak.RefreshAsync(A<string>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        checks.Should().AllSatisfy(c => c.Should().BeOfType<SessionCheck.Replace>().Which.Generation.Should().Be(1));
    }

    private void RenewsTo(string? refreshToken, string role, string accountId, TimeSpan? lifetime = null, string subject = Subject)
    {
        A.CallTo(() => _keycloak.RefreshAsync(A<string>._, A<CancellationToken>._))
            .Returns(new TokenRefresh.Renewed("synthetic.id.token", refreshToken, lifetime ?? TimeSpan.FromMinutes(5)));
        A.CallTo(() => _idTokens.ValidateAsync("synthetic.id.token", A<CancellationToken>._)).Returns(Principal(subject, accountId, role));
    }

    private static ClaimsPrincipal? Principal(string subject, string accountId, string role) =>
        ApiaryClaims.FromToken([new Claim("sub", subject), new Claim("account_id", accountId), new Claim("roles", role)]);
}
