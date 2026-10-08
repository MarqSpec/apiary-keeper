using System.Security.Claims;
using ApiaryCopilot.Api.SignIn;
using Microsoft.AspNetCore.Authentication;

namespace ApiaryCopilot.UnitTests.Api.SignIn;

// A session starts only with something to renew it with, and its ticket points at its server-side state by an id that
// is not the cookie's key; the state holds the same principal the tokens give, and nothing else.
public sealed class SessionTokensTests
{
    private const string Subject = "4e97dd9a-9f79-4493-8185-795ac0034b7d";
    private const string AccountId = "a91a7c0e-5747-4e00-8000-000000000019";
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    [Fact] // regression risk: Keycloak's sign-in response starts the session with its refresh token and the access token's expiry
    public void FromSignIn_ShouldHoldTheRefreshTokenAndExpiry_WhenKeycloakReturnsBoth()
    {
        var tokens = SessionTokens.FromSignIn(Owner(), "synthetic-refresh-1", "300", Now);

        tokens.Should().NotBeNull();
        tokens!.RefreshToken.Should().Be("synthetic-refresh-1");
        tokens.AccessTokenExpires.Should().Be(Now + TimeSpan.FromMinutes(5));
        tokens.Started.Should().Be(Now);
        tokens.Generation.Should().Be(0);
        (tokens.Subject, tokens.AccountId).Should().Be((Subject, AccountId));
        tokens.Roles.Should().Equal("owner");
    }

    [Theory] // invariant: a sign-in that returned nothing to renew with fails closed rather than starting a session that cannot be renewed
    [InlineData(null, "300")]
    [InlineData("", "300")]
    [InlineData("synthetic-refresh-1", null)]
    [InlineData("synthetic-refresh-1", "0")]
    [InlineData("synthetic-refresh-1", "-300")]
    [InlineData("synthetic-refresh-1", "five minutes")]
    public void FromSignIn_ShouldReturnNull_WhenTheResponseCannotRenewTheSession(string? refreshToken, string? expiresIn)
    {
        SessionTokens.FromSignIn(Owner(), refreshToken, expiresIn, Now).Should().BeNull();
    }

    [Fact] // regression risk: a ticket names its session's state and generation, and reads them back
    public void Stamp_ShouldBeReadBack_WhenATicketIsStamped()
    {
        var properties = new AuthenticationProperties();

        SessionTokens.Stamp(properties, "synthetic-session-id", 4);

        SessionTokens.SessionIdOf(properties).Should().Be("synthetic-session-id");
        SessionTokens.GenerationOf(properties).Should().Be(4);
    }

    [Fact] // boundary: a ticket from before the renewal existed names no state, so it fails closed
    public void SessionIdOf_ShouldBeNull_WhenTheTicketWasNeverStamped()
    {
        var properties = new AuthenticationProperties();

        SessionTokens.SessionIdOf(properties).Should().BeNull();
        SessionTokens.GenerationOf(properties).Should().Be(-1);
    }

    [Fact] // invariant: the session id is random, long and new each time; it is not the cookie's key
    public void NewSessionId_ShouldBeUnguessable_WhenMade()
    {
        var ids = Enumerable.Range(0, 100).Select(_ => SessionTokens.NewSessionId()).ToList();

        ids.Should().OnlyHaveUniqueItems();
        ids.Should().OnlyContain(id => id.Length >= 43);
    }

    [Fact] // invariant: the state's principal is the one the tokens give: sub, account and the application roles
    public void ToPrincipal_ShouldYieldTheTokensPrincipal_WhenRead()
    {
        var tokens = new SessionTokens("synthetic-refresh-1", Now, Now, 1, Subject, AccountId, ["helper", "grower"]);

        var principal = tokens.ToPrincipal();

        principal.Claims.Select(c => (c.Type, c.Value)).Should().BeEquivalentTo(
            [("sub", Subject), ("account_id", AccountId), ("role", "helper"), ("role", "grower")]);
        principal.Identity!.AuthenticationType.Should().Be(ApiaryClaims.AuthenticationType);
    }

    private static ClaimsPrincipal Owner() =>
        ApiaryClaims.FromToken([new Claim("sub", Subject), new Claim("account_id", AccountId), new Claim("roles", "owner")])!;
}
