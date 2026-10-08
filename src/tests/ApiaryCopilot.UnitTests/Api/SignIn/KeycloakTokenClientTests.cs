using System.Net;
using System.Text;
using ApiaryCopilot.Api.SignIn;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace ApiaryCopilot.UnitTests.Api.SignIn;

// The BFF's back-channel calls to Keycloak as the confidential client: the refresh token grant and the sign-out, each
// read into an outcome the session can act on - renewed, refused (the session ends) or unavailable (try again) - with
// no retry, since a rotated refresh token is good once. The HTTP handler is faked; nothing leaves the process.
public sealed class KeycloakTokenClientTests : IDisposable
{
    private const string TokenEndpoint = "https://keycloak.unit.test/realms/apiary-keeper/protocol/openid-connect/token";
    private const string EndSessionEndpoint = "https://keycloak.unit.test/realms/apiary-keeper/protocol/openid-connect/logout";

    private readonly HttpMessageHandler _handler = A.Fake<HttpMessageHandler>();
    private readonly List<(Uri Uri, Dictionary<string, string> Form)> _sent = [];
    private readonly HttpClient _backchannel;
    private readonly KeycloakTokenClient _client;

    public KeycloakTokenClientTests()
    {
        _backchannel = new HttpClient(_handler);
        var options = new OpenIdConnectOptions
        {
            ClientId = "apiary-keeper-bff",
            ClientSecret = "synthetic-unit-test-client-secret",
            Backchannel = _backchannel,
            Configuration = new OpenIdConnectConfiguration { TokenEndpoint = TokenEndpoint, EndSessionEndpoint = EndSessionEndpoint },
        };
        var monitor = A.Fake<IOptionsMonitor<OpenIdConnectOptions>>();
        A.CallTo(() => monitor.Get(SignInExtensions.OpenIdConnectScheme)).Returns(options);
        _client = new KeycloakTokenClient(monitor, TimeProvider.System);
    }

    public void Dispose() => _backchannel.Dispose();

    [Fact] // regression risk: the grant is the refresh token grant, as the confidential client, with the session's token
    public async Task RefreshAsync_ShouldPostTheRefreshGrantAsTheConfidentialClient_WhenCalled()
    {
        Answers(HttpStatusCode.OK, """{"id_token":"synthetic.id.token","refresh_token":"synthetic-refresh-2","expires_in":300}""");

        await _client.RefreshAsync("synthetic-refresh-1", CancellationToken.None);

        var (uri, form) = _sent.Should().ContainSingle().Subject;
        uri.ToString().Should().Be(TokenEndpoint);
        form.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = "apiary-keeper-bff",
            ["client_secret"] = "synthetic-unit-test-client-secret",
            ["refresh_token"] = "synthetic-refresh-1",
        });
    }

    [Fact] // invariant: a 200 is read into the ID token, the rotated refresh token and the access token's lifetime
    public async Task RefreshAsync_ShouldReturnRenewed_WhenKeycloakIssuesTokens()
    {
        Answers(HttpStatusCode.OK, """{"access_token":"synthetic.access","id_token":"synthetic.id.token","refresh_token":"synthetic-refresh-2","expires_in":300,"token_type":"Bearer"}""");

        var result = await _client.RefreshAsync("synthetic-refresh-1", CancellationToken.None);

        result.Should().Be(new TokenRefresh.Renewed("synthetic.id.token", "synthetic-refresh-2", TimeSpan.FromMinutes(5)));
    }

    [Theory] // invariant: Keycloak's refusal (a disabled user, a revoked or spent token) is a refusal, with its error code only
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"User disabled"}""", "invalid_grant")]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"unauthorized_client"}""", "unauthorized_client")]
    [InlineData(HttpStatusCode.BadRequest, "not json", "http_400")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"a description with spaces, not a code"}""", "http_400")]
    public async Task RefreshAsync_ShouldReturnRefused_WhenKeycloakRefuses(HttpStatusCode status, string body, string error)
    {
        Answers(status, body);

        var result = await _client.RefreshAsync("synthetic-refresh-1", CancellationToken.None);

        result.Should().Be(new TokenRefresh.Refused(error));
    }

    [Theory] // boundary: a 200 that is not a usable token response is refused, never half-used
    [InlineData("""{"refresh_token":"synthetic-refresh-2","expires_in":300}""")]
    [InlineData("""{"id_token":"","refresh_token":"synthetic-refresh-2","expires_in":300}""")]
    [InlineData("""{"id_token":"synthetic.id.token","refresh_token":"synthetic-refresh-2"}""")]
    [InlineData("""{"id_token":"synthetic.id.token","expires_in":0}""")]
    [InlineData("""{"id_token":"synthetic.id.token","expires_in":"300"}""")]
    [InlineData("<html>not json</html>")]
    public async Task RefreshAsync_ShouldReturnRefused_WhenTheResponseIsMalformed(string body)
    {
        Answers(HttpStatusCode.OK, body);

        var result = await _client.RefreshAsync("synthetic-refresh-1", CancellationToken.None);

        result.Should().Be(new TokenRefresh.Refused("malformed_response"));
    }

    [Fact] // boundary: with rotation off a 200 has no refresh token, and that is not a failure
    public async Task RefreshAsync_ShouldReturnRenewedWithoutARefreshToken_WhenKeycloakSendsNone()
    {
        Answers(HttpStatusCode.OK, """{"id_token":"synthetic.id.token","expires_in":60}""");

        var result = await _client.RefreshAsync("synthetic-refresh-1", CancellationToken.None);

        result.Should().Be(new TokenRefresh.Renewed("synthetic.id.token", null, TimeSpan.FromMinutes(1)));
    }

    [Theory] // invariant: Keycloak failing is unavailable (try later), not a refusal that would end every session
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task RefreshAsync_ShouldReturnUnavailable_WhenKeycloakFails(HttpStatusCode status)
    {
        Answers(status, "{}");

        var result = await _client.RefreshAsync("synthetic-refresh-1", CancellationToken.None);

        result.Should().BeOfType<TokenRefresh.Unavailable>();
    }

    [Fact] // invariant: an unreachable Keycloak is unavailable, and the call is made once (no retry of a one-use token)
    public async Task RefreshAsync_ShouldReturnUnavailableAfterOneAttempt_WhenKeycloakCannotBeReached()
    {
        A.CallTo(_handler).Where(call => call.Method.Name == "SendAsync").WithReturnType<Task<HttpResponseMessage>>()
            .Throws(new HttpRequestException("synthetic: connection refused"));

        var result = await _client.RefreshAsync("synthetic-refresh-1", CancellationToken.None);

        result.Should().Be(new TokenRefresh.Unavailable(nameof(HttpRequestException)));
        A.CallTo(_handler).Where(call => call.Method.Name == "SendAsync").MustHaveHappenedOnceExactly();
    }

    [Fact] // invariant: neither outcome's text carries a token, so logging one leaks nothing
    public void ToString_ShouldNotCarryTheTokens_WhenAnOutcomeIsFormatted()
    {
        var renewed = new TokenRefresh.Renewed("synthetic.id.token", "synthetic-refresh-2", TimeSpan.FromMinutes(5));
        var tokens = new SessionTokens("synthetic-refresh-1", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0, "sub", "account", []);

        renewed.ToString().Should().NotContain("synthetic.id.token").And.NotContain("synthetic-refresh-2");
        tokens.ToString().Should().NotContain("synthetic-refresh-1");
    }

    [Fact] // regression risk: the back-channel sign-out posts the session's refresh token as the confidential client
    public async Task EndSessionAsync_ShouldPostTheRefreshTokenToTheEndSessionEndpoint_WhenCalled()
    {
        Answers(HttpStatusCode.NoContent, string.Empty);

        var ended = await _client.EndSessionAsync("synthetic-refresh-1", CancellationToken.None);

        ended.Should().BeTrue();
        var (uri, form) = _sent.Should().ContainSingle().Subject;
        uri.ToString().Should().Be(EndSessionEndpoint);
        form.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["client_id"] = "apiary-keeper-bff",
            ["client_secret"] = "synthetic-unit-test-client-secret",
            ["refresh_token"] = "synthetic-refresh-1",
        });
    }

    [Fact] // regression risk: the sign-out goes over the back channel, beside the token endpoint, not to the browser-facing host
    public void BackChannelLogoutEndpoint_ShouldBeBesideTheTokenEndpoint_WhenTheEndSessionEndpointIsOnTheFrontEndHost()
    {
        var configuration = new OpenIdConnectConfiguration
        {
            TokenEndpoint = "http://keycloak.railway.internal:8080/realms/apiary-keeper/protocol/openid-connect/token",
            EndSessionEndpoint = "https://keycloak.example.test/realms/apiary-keeper/protocol/openid-connect/logout",
        };

        KeycloakTokenClient.BackChannelLogoutEndpoint(configuration)!.ToString()
            .Should().Be("http://keycloak.railway.internal:8080/realms/apiary-keeper/protocol/openid-connect/logout");
    }

    [Theory] // boundary: a token endpoint not of Keycloak's shape falls back to the discovery document's end-session endpoint, or none
    [InlineData("https://idp.example.test/oauth2/token", "https://idp.example.test/logout", "https://idp.example.test/logout")]
    [InlineData("https://idp.example.test/realms/x/protocol/openid-connect/token?x=1", null, null)]
    [InlineData(null, null, null)]
    public void BackChannelLogoutEndpoint_ShouldFallBack_WhenTheTokenEndpointIsNotKeycloaks(string? token, string? endSession, string? expected)
    {
        var configuration = new OpenIdConnectConfiguration { TokenEndpoint = token, EndSessionEndpoint = endSession };

        KeycloakTokenClient.BackChannelLogoutEndpoint(configuration)?.ToString().Should().Be(expected);
        if (expected is null)
        {
            KeycloakTokenClient.BackChannelLogoutEndpoint(configuration).Should().BeNull();
        }
    }

    [Theory] // boundary: a refused or failed sign-out is reported, never thrown at the user signing out
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task EndSessionAsync_ShouldReturnFalse_WhenKeycloakDoesNotConfirm(HttpStatusCode status)
    {
        Answers(status, """{"error":"invalid_grant"}""");

        (await _client.EndSessionAsync("synthetic-refresh-1", CancellationToken.None)).Should().BeFalse();
    }

    [Fact] // boundary: an unreachable Keycloak at sign-out is reported, never thrown
    public async Task EndSessionAsync_ShouldReturnFalse_WhenKeycloakCannotBeReached()
    {
        A.CallTo(_handler).Where(call => call.Method.Name == "SendAsync").WithReturnType<Task<HttpResponseMessage>>()
            .Throws(new HttpRequestException("synthetic: connection refused"));

        (await _client.EndSessionAsync("synthetic-refresh-1", CancellationToken.None)).Should().BeFalse();
    }

    private void Answers(HttpStatusCode status, string body) =>
        A.CallTo(_handler).Where(call => call.Method.Name == "SendAsync").WithReturnType<Task<HttpResponseMessage>>()
            .ReturnsLazily(async call =>
            {
                var request = (HttpRequestMessage)call.Arguments[0]!;
                var content = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync();
                _sent.Add((request.RequestUri!, QueryHelpers.ParseQuery(content).ToDictionary(p => p.Key, p => p.Value.ToString())));
                return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            });
}
