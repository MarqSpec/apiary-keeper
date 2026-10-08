using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ApiaryCopilot.Api.Invitations;
using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ApiaryCopilot.UnitTests.Api.Invitations;

// POST /api/invitations (UC-18, FR-74, D-22): an account's owner invites a helper or grower by email. Keycloak creates
// and invites the user through the API's service account (faked here), and the API records the user in the owner's
// account with the Keycloak user id as its ExternalSubject, so the invited user acts as that row from its first sign-in.
// Through the real host in memory (CONVENTIONS.md §8.1), the caller set on each request.
public sealed class InvitationEndpointsTests
{
    private const string AccountId = "11111111-1111-4111-8111-111111111111";
    private const string KeycloakUserId = "5b0b3a1e-6f4c-4d9e-8a7b-1c2d3e4f5a6b";
    private static readonly UserId InvitedUserId = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000aa"));

    private readonly IKeycloakAdmin _keycloak = A.Fake<IKeycloakAdmin>();
    private readonly IUserStore _users = A.Fake<IUserStore>();

    public InvitationEndpointsTests()
    {
        A.CallTo(() => _keycloak.IsConfigured).Returns(true);
        A.CallTo(() => _keycloak.InviteAsync(A<KeycloakInvitation>._, A<CancellationToken>._)).Returns(new InviteOutcome.Invited(KeycloakUserId));
        A.CallTo(() => _users.AddInvitedAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .ReturnsLazily((string name, string subject, CancellationToken _) =>
                new User { UserId = InvitedUserId, AccountId = new AccountId(Guid.Parse(AccountId)), Name = name, ExternalSubject = subject });
    }

    [Fact] // invariant (FR-74): the owner's invitation creates the user in the owner's account and returns it
    public async Task Invite_ShouldCreateTheUserInTheOwnersAccount_WhenTheOwnerInvitesAHelper()
    {
        using var factory = Factory(ApiFactory.SignedIn(AccountId, role: "owner"));

        using var response = await InviteAsync(factory, Body("helper"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("userId").GetGuid().Should().Be(InvitedUserId.Value);
        result.GetProperty("email").GetString().Should().Be("helper.synthetic@apiary-keeper.test");
        result.GetProperty("name").GetString().Should().Be("Synthetic Helper");
        result.GetProperty("role").GetString().Should().Be("helper");
        A.CallTo(() => _keycloak.InviteAsync(
                new KeycloakInvitation("helper.synthetic@apiary-keeper.test", "Synthetic Helper", MemberRole.Helper, Guid.Parse(AccountId)),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact] // invariant: the user's ExternalSubject is the Keycloak user id the invitation created, stored at once
    public async Task Invite_ShouldStoreTheKeycloakUserIdAsTheSubject_WhenKeycloakCreatedTheUser()
    {
        using var factory = Factory(ApiFactory.SignedIn(AccountId, role: "owner"));

        using var response = await InviteAsync(factory, Body("grower"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        A.CallTo(() => _users.AddInvitedAsync("Synthetic Helper", KeycloakUserId, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Theory] // invariant (D-22): only an owner invites; a helper, grower or customer is refused before Keycloak is asked
    [InlineData("helper")]
    [InlineData("grower")]
    [InlineData("customer")]
    public async Task Invite_ShouldBeForbidden_WhenTheCallerIsNotAnOwner(string role)
    {
        using var factory = Factory(ApiFactory.SignedIn(AccountId, role: role));

        using var response = await InviteAsync(factory, Body("helper"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await TitleOf(response)).Should().StartWith("NotAllowed:");
        AssertNothingInvited();
    }

    [Fact] // invariant: with no signed-in caller (sign-in off) the endpoint fails closed like every account endpoint
    public async Task Invite_ShouldBeUnavailable_WhenThereIsNoSignedInCaller()
    {
        using var factory = Factory(caller: null);

        using var response = await InviteAsync(factory, Body("helper"));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        AssertNothingInvited();
    }

    [Fact] // invariant: without the service account configured there is nothing to invite through: 503, nothing written
    public async Task Invite_ShouldBeUnavailable_WhenTheServiceAccountIsNotConfigured()
    {
        A.CallTo(() => _keycloak.IsConfigured).Returns(false);
        using var factory = Factory(ApiFactory.SignedIn(AccountId, role: "owner"));

        using var response = await InviteAsync(factory, Body("helper"));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await TitleOf(response)).Should().Contain("invitations are not set up on this server");
        AssertNothingInvited();
    }

    [Theory] // invariant (D-22): an owner or a customer is never invited; the field says why, and Keycloak is not asked
    [InlineData("owner")]
    [InlineData("customer")]
    public async Task Invite_ShouldBeAValidationProblem_WhenTheRoleIsNotHelperOrGrower(string role)
    {
        using var factory = Factory(ApiFactory.SignedIn(AccountId, role: "owner"));

        using var response = await InviteAsync(factory, Body(role));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("role", out _).Should().BeTrue();
        AssertNothingInvited();
    }

    [Fact] // invariant: an address Keycloak already has is a conflict, and no row is written
    public async Task Invite_ShouldBeAConflict_WhenKeycloakAlreadyHasTheAddress()
    {
        A.CallTo(() => _keycloak.InviteAsync(A<KeycloakInvitation>._, A<CancellationToken>._)).Returns(new InviteOutcome.AlreadyExists());
        using var factory = Factory(ApiFactory.SignedIn(AccountId, role: "owner"));

        using var response = await InviteAsync(factory, Body("helper"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleOf(response)).Should().StartWith("Conflict:");
        A.CallTo(_users).MustNotHaveHappened();
    }

    [Fact] // invariant: Keycloak failing is a 503 the owner can retry, and no row is written
    public async Task Invite_ShouldBeUnavailable_WhenKeycloakFails()
    {
        A.CallTo(() => _keycloak.InviteAsync(A<KeycloakInvitation>._, A<CancellationToken>._)).Returns(new InviteOutcome.Failed("invitation", "http_500"));
        using var factory = Factory(ApiFactory.SignedIn(AccountId, role: "owner"));

        using var response = await InviteAsync(factory, Body("helper"));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await TitleOf(response)).Should().StartWith("Unavailable:").And.NotContain("http_500");
        A.CallTo(_users).MustNotHaveHappened();
    }

    [Fact] // regression risk: a Keycloak user whose row could not be written is withdrawn, so no orphan can sign in
    public async Task Invite_ShouldWithdrawTheKeycloakUser_WhenTheRowCannotBeWritten()
    {
        A.CallTo(() => _users.AddInvitedAsync(A<string>._, A<string>._, A<CancellationToken>._)).Throws(new InvalidOperationException("synthetic: database down"));
        using var factory = Factory(ApiFactory.SignedIn(AccountId, role: "owner"));

        using var response = await InviteAsync(factory, Body("helper"));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        A.CallTo(() => _keycloak.WithdrawAsync(KeycloakUserId, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    private void AssertNothingInvited()
    {
        A.CallTo(() => _keycloak.InviteAsync(A<KeycloakInvitation>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(_users).MustNotHaveHappened();
    }

    private ApiFactory Factory(System.Security.Claims.ClaimsPrincipal? caller) =>
        new(caller: caller, services: s =>
        {
            s.AddSingleton(_keycloak);
            s.AddScoped(_ => _users);
        });

    private static object Body(string role) => new { email = "Helper.Synthetic@apiary-keeper.test", name = "Synthetic Helper", role };

    private static async Task<HttpResponseMessage> InviteAsync(ApiFactory factory, object body)
    {
        using var client = factory.CreateClient();
        return await client.PostAsJsonAsync("/api/invitations", body);
    }

    private static async Task<string> TitleOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;
}
