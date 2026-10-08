using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ApiaryCopilot.Domain;

namespace ApiaryCopilot.IntegrationTests;

// A server with sign-in on and its own mailbox: Keycloak's invitations reach a Mailpit on the fixture's network, apart
// from every other server's tests.
public sealed class InvitationServerFixture : IAsyncLifetime, IDisposable
{
    private readonly AppServerFixture _app = new(withMailbox: true);

    internal AppServerFixture App => _app;

    public Task InitializeAsync() => _app.InitializeAsync();

    public Task DisposeAsync() => _app.DisposeAsync();

    public void Dispose() => _app.Dispose();
}

// The invitation end to end (UC-18, FR-74, ARCHITECTURE.md D-22): the realm's synthetic owner calls POST /api/invitations;
// the API, as its Keycloak service account, creates the user with the owner's account_id and the role and Keycloak emails
// the invitation (read here from Mailpit); the API records the user in the owner's account with the Keycloak user id as
// its ExternalSubject; and once the invitation is completed the user signs in and acts in the account as that row. Only
// an owner invites, and an address Keycloak already has is a conflict. Deployment=None: Keycloak, Mailpit, PostgreSQL and
// MinIO are loopback containers. Runs alone (KeycloakAlone).
[Trait("Deployment", "None")]
[Collection(KeycloakAlone.Name)]
public sealed class InvitationTests(InvitationServerFixture fixture) : IClassFixture<InvitationServerFixture>
{
    private AppServerFixture App => fixture.App;

    [Fact] // invariant (FR-74, D-22): the owner's invitation makes a Keycloak user in the owner's account, emailed, and its row
    public async Task Invite_ShouldCreateEmailAndRecordTheUser_WhenTheOwnerInvitesAHelper()
    {
        RequireInvitations();
        var email = NewAddress();

        using var response = await App.Client.PostAsJsonAsync("/api/invitations", new { email, name = "Synthetic Invited Helper", role = "helper" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("email").GetString().Should().Be(email);
        result.GetProperty("role").GetString().Should().Be("helper");
        var keycloakUserId = await App.Keycloak.UserIdOfAsync(email);
        var user = await App.Keycloak.UserAsync(keycloakUserId);
        user["attributes"]!["account_id"]!.AsArray().Select(a => a!.GetValue<string>()).Should().Equal(App.CurrentAccountId.ToString());
        (await App.Keycloak.EffectiveRealmRolesAsync(keycloakUserId)).Should().Contain("helper").And.NotContain("owner");
        var message = await App.Mailbox.WaitForMessageToAsync(email);
        message.Text.Should().Contain("/login-actions/action-token?key=");
        var userId = new UserId(result.GetProperty("userId").GetGuid());
        (await App.Database.CountAsync(
            $"SELECT COUNT(*) FROM \"Users\" WHERE \"UserId\" = '{userId}' AND \"AccountId\" = '{App.CurrentAccountId}' AND \"ExternalSubject\" = '{keycloakUserId}'"))
            .Should().Be(1, "the row's ExternalSubject is the Keycloak user id, stored at creation");
    }

    [Fact] // invariant: the invited user's first sign-in acts in the owner's account as the recorded row, no matching needed
    public async Task InvitedUser_ShouldActInTheAccount_WhenItFirstSignsIn()
    {
        RequireInvitations();
        var email = NewAddress();
        using var invited = await App.Client.PostAsJsonAsync("/api/invitations", new { email, name = "Synthetic Invited Grower Helper", role = "helper" });
        invited.StatusCode.Should().Be(HttpStatusCode.Created);
        var completed = await App.Keycloak.CompleteInvitationAsync(await App.Keycloak.UserIdOfAsync(email));

        using var client = App.ClientFor(completed);
        using var yards = await client.GetAsync("/api/yards");

        yards.StatusCode.Should().Be(HttpStatusCode.OK, "the token's sub is the row's ExternalSubject, so the user is a user of the account");
    }

    [Fact] // invariant: an address Keycloak already has is a conflict; no second user or row is made
    public async Task Invite_ShouldBeAConflict_WhenTheAddressIsAlreadyInvited()
    {
        RequireInvitations();
        var email = NewAddress();
        using var first = await App.Client.PostAsJsonAsync("/api/invitations", new { email, name = "Synthetic First", role = "grower" });
        var rows = await UserRowsAsync();

        using var second = await App.Client.PostAsJsonAsync("/api/invitations", new { email, name = "Synthetic Second", role = "helper" });

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await UserRowsAsync()).Should().Be(rows);
        (await App.Keycloak.EffectiveRealmRolesAsync(await App.Keycloak.UserIdOfAsync(email))).Should().Contain("grower").And.NotContain("helper");
    }

    [Fact] // invariant (D-22): only an owner invites - a helper of the account is refused, and nothing reaches Keycloak
    public async Task Invite_ShouldBeForbidden_WhenTheCallerIsAHelper()
    {
        var helper = await App.Keycloak.CreateUserAsync("helper", App.CurrentAccountId.ToString());
        await App.Database.AddUserAsync(App.CurrentAccountId, new UserId(Guid.NewGuid()), helper.Id);
        using var client = App.ClientFor(helper);
        var email = NewAddress();

        using var response = await client.PostAsJsonAsync("/api/invitations", new { email, name = "Synthetic Refused", role = "helper" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await App.Keycloak.UsersNamedAsync(email)).Should().Be(0, "nothing was made for the helper");
    }

    [Fact] // invariant (D-22): an owner is never invited; the API refuses it before Keycloak is asked
    public async Task Invite_ShouldBeAValidationProblem_WhenTheOwnerAsksForAnOwner()
    {
        RequireInvitations();
        var rows = await UserRowsAsync();

        using var response = await App.Client.PostAsJsonAsync("/api/invitations", new { email = NewAddress(), name = "Synthetic Owner", role = "owner" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("role")[0].GetString().Should().Contain("administrator");
        (await UserRowsAsync()).Should().Be(rows);
    }

    private static string NewAddress() => $"synthetic-it-{Guid.NewGuid():N}@apiary-keeper.test";

    private Task<long> UserRowsAsync() => App.Database.CountAsync("SELECT COUNT(*) FROM \"Users\"");

    // A container run gets only the settings its command passes with -e; without the service account's, the invitation
    // answers 503 by design, so say what the command is missing rather than fail on the status.
    private void RequireInvitations()
    {
        if (!App.InvitationsConfigured)
        {
            throw new InvalidOperationException(
                "APP_SERVER_COMMAND runs a container without the service account's settings: pass "
                + string.Join(' ', AppServerFixture.ServiceAccountSettingNames.Select(s => $"-e {s}"))
                + " (DEPLOYMENT.md §3).");
        }
    }
}
