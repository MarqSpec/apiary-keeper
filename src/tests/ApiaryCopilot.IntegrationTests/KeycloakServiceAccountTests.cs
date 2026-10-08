using System.Net;
using System.Text.Json;

namespace ApiaryCopilot.IntegrationTests;

// The API's Keycloak service account against the seeded realm (ARCHITECTURE.md D-22, as decided): Keycloak
// itself lets it create a helper, grower or customer with an account_id and send the invitation, and refuses it owner,
// reading credentials, managing roles, clients or the realm, impersonating, and - on a member of the owners group -
// disabling, editing, resetting, emailing, deleting, reading or taking it out of the group. What it holds beyond that is
// pinned too, as accepted in D-22: editing, resetting and deleting other users, which Keycloak bundles into the `manage`
// it needs to create users, and adding or removing a member role on an owner, which the group's deny does not reach (the
// users' `map-roles` scope). A Keycloak that closes or widens either fails here, and the documents follow.
// Every call is the service account's own, straight to Keycloak's admin interface with its client-credentials token.
// Deployment=None: Keycloak and Mailpit are loopback containers. Runs alone (KeycloakAlone).
[Trait("Deployment", "None")]
[Collection(KeycloakAlone.Name)]
public sealed class KeycloakServiceAccountTests(ServiceAccountFixture fixture) : IClassFixture<ServiceAccountFixture>
{
    private const string SeedAccountId = "a91a7c0e-5747-4e00-8000-000000000019";
    private const string Owner = "synthetic-owner";

    private TestKeycloak Keycloak => fixture.Keycloak;

    [Theory] // invariant (D-22): the service account creates a member with its account, gives it its role and invites it
    [InlineData("helper")]
    [InlineData("grower")]
    [InlineData("customer")]
    public async Task ServiceAccount_ShouldCreateAndInviteAMember_WhenGivenAMemberRole(string role)
    {
        var email = NewAddress();

        var userId = await CreateAsync(email);
        using var mapped = await MapAsync(userId, role);
        using var invited = await Keycloak.AsServiceAccountAsync(HttpMethod.Put, $"users/{userId}/execute-actions-email?lifespan=3600", """["VERIFY_EMAIL","UPDATE_PASSWORD"]""");

        mapped.StatusCode.Should().Be(HttpStatusCode.NoContent);
        invited.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var user = await Keycloak.UserAsync(userId);
        user["attributes"]!["account_id"]!.AsArray().Select(a => a!.GetValue<string>()).Should().Equal(SeedAccountId);
        (await Keycloak.EffectiveRealmRolesAsync(userId)).Should().Contain(role).And.NotContain("owner");
        var message = await fixture.Mailbox.WaitForMessageToAsync(email);
        message.Text.Should().Contain("/login-actions/action-token?key=", "the invitation is Keycloak's own action link");
    }

    [Fact] // invariant (D-22): owner is never given - not by mapping the role, nor by naming the group or role at creation
    public async Task ServiceAccount_ShouldBeRefusedOwner_WhenAskedByAnyRoute()
    {
        var userId = await CreateAsync(NewAddress());
        var ownersGroupId = Keycloak.Realm.GroupIdOf(TestKeycloak.OwnersGroup);

        using var byRole = await MapAsync(userId, "owner");
        using var byGroup = await Keycloak.AsServiceAccountAsync(HttpMethod.Put, $"users/{userId}/groups/{ownersGroupId}");
        using var createdInGroup = await Keycloak.AsServiceAccountAsync(HttpMethod.Post, "users",
            $$"""{"username":"{{NewAddress()}}","enabled":true,"groups":["/owners"]}""");
        using var createdWithRole = await Keycloak.AsServiceAccountAsync(HttpMethod.Post, "users",
            $$"""{"username":"{{NewAddress()}}","enabled":true,"realmRoles":["owner"]}""");

        byRole.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        byGroup.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        createdInGroup.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Keycloak.EffectiveRealmRolesAsync(userId)).Should().NotContain("owner");
        // The admin interface's create ignores realmRoles: the user is made, but not an owner.
        createdWithRole.StatusCode.Should().Be(HttpStatusCode.Created);
        (await Keycloak.EffectiveRealmRolesAsync(createdWithRole.Headers.Location!.Segments[^1])).Should().NotContain("owner");
    }

    [Fact] // invariant (D-22): no credential is ever read - a member's it created or an owner's
    public async Task ServiceAccount_ShouldBeRefused_WhenItReadsCredentials()
    {
        var memberId = await CreateAsync(NewAddress());
        var ownerId = await Keycloak.UserIdOfAsync(Owner);

        using var member = await Keycloak.AsServiceAccountAsync(HttpMethod.Get, $"users/{memberId}/credentials");
        using var owner = await Keycloak.AsServiceAccountAsync(HttpMethod.Get, $"users/{ownerId}/credentials");

        member.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        owner.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact] // invariant (D-22): roles, clients and the realm's settings are the administrator's alone
    public async Task ServiceAccount_ShouldBeRefused_WhenItManagesRolesClientsOrTheRealm()
    {
        var helperRoleId = Keycloak.Realm.RoleIdOf("helper");
        var calls = new (HttpMethod Method, string Path, string? Json)[]
        {
            (HttpMethod.Post, "roles", """{"name":"synthetic-it-role"}"""),
            (HttpMethod.Put, "roles/helper", """{"name":"helper","description":"changed"}"""),
            (HttpMethod.Delete, "roles/grower", null),
            (HttpMethod.Put, $"roles-by-id/{helperRoleId}", """{"name":"helper","composite":true}"""),
            (HttpMethod.Post, "clients", """{"clientId":"synthetic-it-client"}"""),
            (HttpMethod.Get, "clients", null),
            (HttpMethod.Post, "groups", """{"name":"synthetic-it-group"}"""),
            (HttpMethod.Put, string.Empty, """{"registrationAllowed":true}"""),
            (HttpMethod.Get, string.Empty, null),
        };

        foreach (var (method, path, json) in calls)
        {
            using var response = await Keycloak.AsServiceAccountAsync(method, path, json);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} /{path}");
        }
    }

    [Fact] // invariant (D-22): the service account cannot act as anyone
    public async Task ServiceAccount_ShouldBeRefused_WhenItImpersonates()
    {
        var memberId = await CreateAsync(NewAddress());
        var ownerId = await Keycloak.UserIdOfAsync(Owner);

        using var member = await Keycloak.AsServiceAccountAsync(HttpMethod.Post, $"users/{memberId}/impersonation");
        using var owner = await Keycloak.AsServiceAccountAsync(HttpMethod.Post, $"users/{ownerId}/impersonation");

        member.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        owner.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact] // invariant (D-22): an owner in the owners group is not disabled, edited, reset, emailed, deleted, read or ungrouped
    public async Task ServiceAccount_ShouldBeRefused_WhenItDisablesEditsResetsDeletesReadsOrUngroupsAnOwnerInTheOwnersGroup()
    {
        var ownerId = await Keycloak.UserIdOfAsync(Owner);
        var calls = new (HttpMethod Method, string Path, string? Json)[]
        {
            (HttpMethod.Put, $"users/{ownerId}", """{"enabled":false}"""),
            (HttpMethod.Put, $"users/{ownerId}", """{"email":"synthetic-it-taken-over@apiary-keeper.test"}"""),
            (HttpMethod.Put, $"users/{ownerId}/reset-password", """{"type":"password","value":"synthetic-it-reset","temporary":true}"""),
            (HttpMethod.Put, $"users/{ownerId}/execute-actions-email", """["UPDATE_PASSWORD"]"""),
            (HttpMethod.Delete, $"users/{ownerId}", null),
            (HttpMethod.Delete, $"users/{ownerId}/groups/{Keycloak.Realm.GroupIdOf(TestKeycloak.OwnersGroup)}", null),
            (HttpMethod.Get, $"users/{ownerId}", null),
        };

        foreach (var (method, path, json) in calls)
        {
            using var response = await Keycloak.AsServiceAccountAsync(method, path, json);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} /{path} {json}");
        }

        var owner = await Keycloak.UserAsync(ownerId);
        owner["enabled"]!.GetValue<bool>().Should().BeTrue();
        owner["email"]!.GetValue<string>().Should().NotContain("taken-over");
        (await Keycloak.GroupsOfAsync(ownerId)).Should().Contain(TestKeycloak.OwnersGroup);
        (await Keycloak.EffectiveRealmRolesAsync(ownerId)).Should().Contain("owner");
    }

    [Fact] // invariant (D-22): the service account disables a member it may reach
    public async Task ServiceAccount_ShouldDisableAMember_WhenAsked()
    {
        var userId = await CreateAsync(NewAddress());

        using var disabled = await Keycloak.AsServiceAccountAsync(HttpMethod.Put, $"users/{userId}", """{"enabled":false}""");

        disabled.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Keycloak.UserAsync(userId))["enabled"]!.GetValue<bool>().Should().BeFalse();
    }

    // Accepted by the maintainer (D-22, "Accepted residual risk"): Keycloak's `manage`, which creating needs,
    // also edits, resets and deletes any user outside the owners group. Pinned so that a Keycloak upgrade changing it is noticed.
    [Fact] // invariant (D-22, accepted): beyond the set, the service account edits, resets and deletes a member
    public async Task ServiceAccount_ShouldHoldTheAcceptedWiderPermissions_OnAMemberOutsideTheOwnersGroup()
    {
        var userId = await CreateAsync(NewAddress());
        using var mapped = await MapAsync(userId, "helper");

        using var edited = await Keycloak.AsServiceAccountAsync(HttpMethod.Put, $"users/{userId}", JsonSerializer.Serialize(new { email = NewAddress() }));
        using var reset = await Keycloak.AsServiceAccountAsync(HttpMethod.Put, $"users/{userId}/reset-password", """{"type":"password","value":"synthetic-it-reset","temporary":true}""");
        using var deleted = await Keycloak.AsServiceAccountAsync(HttpMethod.Delete, $"users/{userId}");

        edited.StatusCode.Should().Be(HttpStatusCode.NoContent);
        reset.StatusCode.Should().Be(HttpStatusCode.NoContent);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // Accepted by the maintainer (D-22): "the owner role will have all those permissions anyway". The group's deny
    // covers `manage`, not the users' `map-roles`, so a member role can be added to or removed from an owner; `owner` itself,
    // held through the group, cannot be taken away. Pinned so that a Keycloak change closing or widening it is noticed.
    [Fact] // invariant (D-22, accepted): on an owner, a member role can be added and removed, and owner stays
    public async Task ServiceAccount_ShouldAddAndRemoveAMemberRoleButNotOwner_OnAnOwnerInTheOwnersGroup()
    {
        var ownerId = await Keycloak.UserIdOfAsync(Owner);
        var customer = JsonSerializer.Serialize(new[] { new { id = Keycloak.Realm.RoleIdOf("customer"), name = "customer" } });
        var owner = JsonSerializer.Serialize(new[] { new { id = Keycloak.Realm.RoleIdOf("owner"), name = "owner" } });

        using var added = await Keycloak.AsServiceAccountAsync(HttpMethod.Post, $"users/{ownerId}/role-mappings/realm", customer);
        var withCustomer = await Keycloak.EffectiveRealmRolesAsync(ownerId);
        using var removed = await Keycloak.AsServiceAccountAsync(HttpMethod.Delete, $"users/{ownerId}/role-mappings/realm", customer);
        using var ownerRemoved = await Keycloak.AsServiceAccountAsync(HttpMethod.Delete, $"users/{ownerId}/role-mappings/realm", owner);

        added.StatusCode.Should().Be(HttpStatusCode.NoContent, "adding a member role to an owner is accepted (D-22)");
        withCustomer.Should().Contain("customer").And.Contain("owner");
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent, "removing a member role from an owner is accepted (D-22)");
        ownerRemoved.StatusCode.Should().Be(HttpStatusCode.Forbidden, "owner is mapped only by the administrator");
        var after = await Keycloak.EffectiveRealmRolesAsync(ownerId);
        after.Should().Contain("owner").And.NotContain("customer");
        (await Keycloak.GroupsOfAsync(ownerId)).Should().Contain(TestKeycloak.OwnersGroup);
    }

    private static string NewAddress() => $"synthetic-it-{Guid.NewGuid():N}@apiary-keeper.test";

    private async Task<string> CreateAsync(string email)
    {
        using var created = await Keycloak.AsServiceAccountAsync(HttpMethod.Post, "users", JsonSerializer.Serialize(new
        {
            username = email,
            email,
            firstName = "Synthetic",
            enabled = true,
            emailVerified = false,
            attributes = new Dictionary<string, string[]> { ["account_id"] = [SeedAccountId] },
        }));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        return created.Headers.Location!.Segments[^1];
    }

    private Task<HttpResponseMessage> MapAsync(string userId, string role) =>
        Keycloak.AsServiceAccountAsync(HttpMethod.Post, $"users/{userId}/role-mappings/realm",
            JsonSerializer.Serialize(new[] { new { id = Keycloak.Realm.RoleIdOf(role), name = role } }));
}
