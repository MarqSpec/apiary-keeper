using System.Net;
using System.Text;
using System.Text.Json;
using ApiaryCopilot.Api.Invitations;
using ApiaryCopilot.Api.SignIn;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ApiaryCopilot.UnitTests.Api.Invitations;

// The API's calls to Keycloak's admin interface as its service account (D-22): a client-credentials token, then create
// the user with its account, give it its one member role by the configured id, and send Keycloak's invitation email.
// A user Keycloak already has is reported as such; any other failure after the user exists removes it again, so a
// half-made invitation never stays behind. No retry: creating a user is not idempotent. The HTTP handler is faked.
public sealed class KeycloakAdminClientTests : IDisposable
{
    private const string Realm = "https://keycloak.unit.test/realms/apiary-keeper";
    private const string Admin = "https://keycloak.unit.test/admin/realms/apiary-keeper/";
    private const string Secret = "synthetic-unit-test-api-admin-secret";
    private const string KeycloakUserId = "5b0b3a1e-6f4c-4d9e-8a7b-1c2d3e4f5a6b";
    private const string AccountId = "a91a7c0e-5747-4e00-8000-000000000019";
    private const string HelperRoleId = "0d6f3a52-7a1e-4c1f-9b0e-000000000002";

    private static readonly KeycloakInvitation Helper = new("helper.synthetic@apiary-keeper.test", "Synthetic Helper", MemberRole.Helper, Guid.Parse(AccountId));

    private readonly HttpMessageHandler _handler = A.Fake<HttpMessageHandler>();
    private readonly List<Sent> _sent = [];
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answers = new(StringComparer.Ordinal);
    private readonly HttpClient _http;
    private readonly KeycloakAdminClient _client;

    public KeycloakAdminClientTests()
    {
        _http = new HttpClient(_handler);
        A.CallTo(_handler).Where(call => call.Method.Name == "SendAsync").WithReturnType<Task<HttpResponseMessage>>()
            .ReturnsLazily(async call =>
            {
                var request = (HttpRequestMessage)call.Arguments[0]!;
                var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync();
                var key = $"{request.Method} {request.RequestUri!.GetLeftPart(UriPartial.Path)}";
                _sent.Add(new Sent(key, request.RequestUri!, body, request.Headers.Authorization?.ToString()));
                var (status, answer) = _answers.TryGetValue(key, out var found) ? found : (HttpStatusCode.NotFound, "{}");
                var response = new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
                if (status == HttpStatusCode.Created)
                {
                    response.Headers.Location = new Uri($"{Admin}users/{KeycloakUserId}");
                }

                return response;
            });
        var options = new KeycloakAdminOptions
        {
            ClientId = "apiary-keeper-api-admin",
            ClientSecret = Secret,
            HelperRoleId = HelperRoleId,
            GrowerRoleId = "0d6f3a52-7a1e-4c1f-9b0e-000000000003",
            CustomerRoleId = "0d6f3a52-7a1e-4c1f-9b0e-000000000004",
            InvitationLifetime = TimeSpan.FromDays(3),
        };
        var signIn = new SignInOptions { Authority = Realm, ClientId = "apiary-keeper-bff", ClientSecret = "x", Audience = "apiary-keeper-api" };
        _client = new KeycloakAdminClient(_http, Options.Create(options), Options.Create(signIn), NullLogger<KeycloakAdminClient>.Instance, TimeProvider.System);
    }

    private static string TokenKey => $"POST {Realm}/protocol/openid-connect/token";

    private static string CreateKey => $"POST {Admin}users";

    private static string RoleKey => $"POST {Admin}users/{KeycloakUserId}/role-mappings/realm";

    private static string EmailKey => $"PUT {Admin}users/{KeycloakUserId}/execute-actions-email";

    private static string DeleteKey => $"DELETE {Admin}users/{KeycloakUserId}";

    public void Dispose() => _http.Dispose();

    [Fact] // invariant: the token is the service account's own, by the client-credentials grant and nothing else
    public async Task InviteAsync_ShouldAskForAClientCredentialsToken_WhenCalled()
    {
        AllSucceed();

        await _client.InviteAsync(Helper, CancellationToken.None);

        var token = _sent.First();
        token.Key.Should().Be(TokenKey);
        QueryHelpers.ParseQuery(token.Body).ToDictionary(p => p.Key, p => p.Value.ToString()).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "apiary-keeper-api-admin",
            ["client_secret"] = Secret,
        });
        _sent.Skip(1).Should().OnlyContain(s => s.Authorization == "Bearer synthetic.admin.token", "every admin call carries that token");
    }

    [Fact] // invariant (D-22): the user is created enabled, by email, unverified, with its account and nothing else
    public async Task InviteAsync_ShouldCreateTheUserWithItsAccount_WhenCalled()
    {
        AllSucceed();

        await _client.InviteAsync(Helper, CancellationToken.None);

        using var user = JsonDocument.Parse(_sent.Single(s => s.Key == CreateKey).Body);
        var root = user.RootElement;
        root.GetProperty("username").GetString().Should().Be(Helper.Email);
        root.GetProperty("email").GetString().Should().Be(Helper.Email);
        root.GetProperty("firstName").GetString().Should().Be(Helper.Name);
        root.GetProperty("enabled").GetBoolean().Should().BeTrue();
        root.GetProperty("emailVerified").GetBoolean().Should().BeFalse("the invitation verifies it");
        root.GetProperty("attributes").GetProperty("account_id").EnumerateArray().Select(a => a.GetString()).Should().Equal(AccountId);
        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["username", "email", "firstName", "enabled", "emailVerified", "attributes"], "no role, group or credential is set at creation");
    }

    [Fact] // invariant (D-22): the one role is given by its configured id, never by name alone and never owner
    public async Task InviteAsync_ShouldGiveTheConfiguredRole_WhenTheUserIsCreated()
    {
        AllSucceed();

        await _client.InviteAsync(Helper, CancellationToken.None);

        using var roles = JsonDocument.Parse(_sent.Single(s => s.Key == RoleKey).Body);
        var role = roles.RootElement.EnumerateArray().Should().ContainSingle().Subject;
        role.GetProperty("id").GetString().Should().Be(HelperRoleId);
        role.GetProperty("name").GetString().Should().Be("helper");
    }

    [Fact] // invariant: the invitation is Keycloak's own email, verifying the address and setting a password, for the configured lifetime
    public async Task InviteAsync_ShouldSendKeycloaksInvitation_WhenTheRoleIsGiven()
    {
        AllSucceed();

        var outcome = await _client.InviteAsync(Helper, CancellationToken.None);

        var email = _sent.Single(s => s.Key == EmailKey);
        QueryHelpers.ParseQuery(email.Uri.Query)["lifespan"].ToString().Should().Be("259200");
        JsonSerializer.Deserialize<string[]>(email.Body).Should().Equal("VERIFY_EMAIL", "UPDATE_PASSWORD");
        outcome.Should().Be(new InviteOutcome.Invited(KeycloakUserId));
        _sent.Select(s => s.Key).Should().Equal(TokenKey, CreateKey, RoleKey, EmailKey);
    }

    [Fact] // invariant: an address Keycloak already has is reported as taken, and nothing is removed
    public async Task InviteAsync_ShouldReportAlreadyExists_WhenKeycloakHasTheUser()
    {
        AllSucceed();
        _answers[CreateKey] = (HttpStatusCode.Conflict, """{"errorMessage":"User exists with same username"}""");

        var outcome = await _client.InviteAsync(Helper, CancellationToken.None);

        outcome.Should().Be(new InviteOutcome.AlreadyExists());
        _sent.Select(s => s.Key).Should().Equal(TokenKey, CreateKey);
    }

    [Theory] // invariant: a failure after the user exists removes it again, so no half-made invitation stays behind
    [InlineData("role", HttpStatusCode.Forbidden)]
    [InlineData("role", HttpStatusCode.InternalServerError)]
    [InlineData("invitation", HttpStatusCode.InternalServerError)]
    [InlineData("invitation", HttpStatusCode.BadRequest)]
    public async Task InviteAsync_ShouldRemoveTheUser_WhenALaterStepFails(string step, HttpStatusCode status)
    {
        AllSucceed();
        _answers[step == "role" ? RoleKey : EmailKey] = (status, "{}");

        var outcome = await _client.InviteAsync(Helper, CancellationToken.None);

        outcome.Should().BeOfType<InviteOutcome.Failed>().Which.Step.Should().Be(step);
        _sent.Last().Key.Should().Be(DeleteKey);
    }

    [Theory] // boundary: no token, or no user created, is a failure with nothing to undo
    [InlineData(true, HttpStatusCode.Unauthorized, "token")]
    [InlineData(true, HttpStatusCode.ServiceUnavailable, "token")]
    [InlineData(false, HttpStatusCode.Forbidden, "create")]
    [InlineData(false, HttpStatusCode.InternalServerError, "create")]
    public async Task InviteAsync_ShouldFailWithoutUndoing_WhenNoUserWasCreated(bool tokenFails, HttpStatusCode status, string step)
    {
        AllSucceed();
        _answers[tokenFails ? TokenKey : CreateKey] = (status, "{}");

        var outcome = await _client.InviteAsync(Helper, CancellationToken.None);

        outcome.Should().Be(new InviteOutcome.Failed(step, $"http_{(int)status}"));
        _sent.Should().NotContain(s => s.Key.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Theory] // boundary: a 200 without a usable token is a failure, never a call made without one
    [InlineData("{}")]
    [InlineData("""{"access_token":""}""")]
    [InlineData("""{"access_token":42}""")]
    [InlineData("not json")]
    public async Task InviteAsync_ShouldFail_WhenTheTokenResponseIsMalformed(string body)
    {
        AllSucceed();
        _answers[TokenKey] = (HttpStatusCode.OK, body);

        var outcome = await _client.InviteAsync(Helper, CancellationToken.None);

        outcome.Should().Be(new InviteOutcome.Failed("token", "malformed_response"));
        _sent.Should().ContainSingle();
    }

    [Fact] // invariant: Keycloak not answering is a failure the caller can report, not an exception
    public async Task InviteAsync_ShouldFail_WhenKeycloakCannotBeReached()
    {
        A.CallTo(_handler).Where(call => call.Method.Name == "SendAsync").WithReturnType<Task<HttpResponseMessage>>()
            .Throws(new HttpRequestException("synthetic: connection refused"));

        var outcome = await _client.InviteAsync(Helper, CancellationToken.None);

        outcome.Should().Be(new InviteOutcome.Failed("token", nameof(HttpRequestException)));
    }

    [Fact] // regression risk: an owner can never be asked for - the type has no such member to map
    public void MemberRole_ShouldHaveNoOwner_WhenEnumerated() =>
        Enum.GetNames<MemberRole>().Should().BeEquivalentTo("Helper", "Grower", "Customer");

    [Fact] // invariant: withdrawing removes the user Keycloak made, as the service account
    public async Task WithdrawAsync_ShouldDeleteTheUser_WhenAsked()
    {
        AllSucceed();

        var removed = await _client.WithdrawAsync(KeycloakUserId, CancellationToken.None);

        removed.Should().BeTrue();
        _sent.Select(s => s.Key).Should().Equal(TokenKey, DeleteKey);
    }

    [Fact] // regression risk: the result never carries the token or the secret into a log or a response
    public async Task InviteAsync_ShouldNotRevealTheTokenOrSecret_WhenItFails()
    {
        AllSucceed();
        _answers[CreateKey] = (HttpStatusCode.BadRequest, """{"errorMessage":"synthetic.admin.token synthetic-unit-test-api-admin-secret"}""");

        var outcome = await _client.InviteAsync(Helper, CancellationToken.None);

        outcome.ToString().Should().NotContain("synthetic.admin.token").And.NotContain(Secret);
    }

    private void AllSucceed()
    {
        _answers[TokenKey] = (HttpStatusCode.OK, """{"access_token":"synthetic.admin.token","expires_in":300,"token_type":"Bearer"}""");
        _answers[CreateKey] = (HttpStatusCode.Created, string.Empty);
        _answers[RoleKey] = (HttpStatusCode.NoContent, string.Empty);
        _answers[EmailKey] = (HttpStatusCode.NoContent, string.Empty);
        _answers[DeleteKey] = (HttpStatusCode.NoContent, string.Empty);
    }

    private sealed record Sent(string Key, Uri Uri, string Body, string? Authorization);
}
