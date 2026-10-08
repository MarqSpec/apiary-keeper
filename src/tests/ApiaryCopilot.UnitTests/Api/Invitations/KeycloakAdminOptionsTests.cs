using ApiaryCopilot.Api.Invitations;
using ApiaryCopilot.Api.SignIn;

namespace ApiaryCopilot.UnitTests.Api.Invitations;

// The API's Keycloak service account, set as KeycloakAdmin__* (DEPLOYMENT.md §3): configured whole or not at all, only
// beside sign-in (it calls the sign-in realm), each failure naming its setting and never its value. Its admin and token
// addresses are derived from the realm the API already signs in through, by the back channel when one is set.
public sealed class KeycloakAdminOptionsTests
{
    private const string Secret = "synthetic-unit-test-api-admin-secret";

    [Fact] // invariant: unset, invitations are off and nothing is wrong
    public void Validate_ShouldReportNothing_WhenNothingIsSet()
    {
        new KeycloakAdminOptions().Validate(SignIn()).Should().BeEmpty();
        new KeycloakAdminOptions().IsConfigured.Should().BeFalse();
    }

    [Fact] // invariant: a complete set beside sign-in is usable
    public void Validate_ShouldReportNothing_WhenEverySettingIsSet()
    {
        var options = Complete();

        options.Validate(SignIn()).Should().BeEmpty();
        options.IsConfigured.Should().BeTrue();
    }

    [Fact] // boundary: a secret or a role id without the client id is half a configuration, refused
    public void Validate_ShouldRequireTheClientId_WhenAnyOtherSettingIsSet()
    {
        var options = new KeycloakAdminOptions { ClientSecret = Secret };

        options.Validate(SignIn()).Should().ContainSingle().Which.Should().Contain("KeycloakAdmin__ClientId must be set");
    }

    [Fact] // invariant: the service account calls the sign-in realm, so it needs sign-in configured
    public void Validate_ShouldRequireSignIn_WhenTheServiceAccountIsSet()
    {
        Complete().Validate(new SignInOptions()).Should().ContainSingle().Which.Should().Contain("SignIn__Authority must be set");
    }

    [Theory] // boundary: every setting without a default is required once the client id is set
    [InlineData(nameof(KeycloakAdminOptions.ClientSecret), "KeycloakAdmin__ClientSecret")]
    [InlineData(nameof(KeycloakAdminOptions.HelperRoleId), "KeycloakAdmin__HelperRoleId")]
    [InlineData(nameof(KeycloakAdminOptions.GrowerRoleId), "KeycloakAdmin__GrowerRoleId")]
    [InlineData(nameof(KeycloakAdminOptions.CustomerRoleId), "KeycloakAdmin__CustomerRoleId")]
    public void Validate_ShouldNameTheSetting_WhenARequiredOneIsMissing(string property, string setting)
    {
        var options = Complete();
        typeof(KeycloakAdminOptions).GetProperty(property)!.SetValue(options, " ");

        options.Validate(SignIn()).Should().ContainSingle().Which.Should().StartWith(setting);
    }

    [Theory] // boundary: a role id is the role's Keycloak id, a GUID; a role name is refused
    [InlineData("helper")]
    [InlineData("0d6f3a52-7a1e-4c1f-9b0e")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Validate_ShouldRefuseARoleId_WhenItIsNotAGuid(string value)
    {
        var options = Complete();
        options.GrowerRoleId = value;

        options.Validate(SignIn()).Should().ContainSingle().Which.Should().StartWith("KeycloakAdmin__GrowerRoleId must be");
    }

    [Theory] // boundary: an invitation lasts between an hour and thirty days
    [InlineData("00:59:59", false)]
    [InlineData("01:00:00", true)]
    [InlineData("30.00:00:00", true)]
    [InlineData("30.00:00:01", false)]
    public void Validate_ShouldBoundTheInvitationLifetime_WhenSet(string lifetime, bool valid)
    {
        var options = Complete();
        options.InvitationLifetime = TimeSpan.Parse(lifetime, System.Globalization.CultureInfo.InvariantCulture);

        var failures = options.Validate(SignIn()).ToList();

        if (valid)
        {
            failures.Should().BeEmpty();
        }
        else
        {
            failures.Should().ContainSingle().Which.Should().StartWith("KeycloakAdmin__InvitationLifetime");
        }
    }

    [Fact] // regression risk: a failure message must never carry the secret it is about
    public void Validate_ShouldNeverEchoTheSecret_WhenAnotherSettingIsWrong()
    {
        var options = Complete();
        options.HelperRoleId = "not-a-guid";

        options.Validate(SignIn()).Should().NotContain(f => f.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact] // invariant: an authority that is not a realm URL cannot yield an admin address, so it is refused at startup
    public void Validate_ShouldRefuse_WhenTheRealmUrlHasNoRealm()
    {
        Complete().Validate(SignIn("https://keycloak.unit.test/somewhere")).Should().ContainSingle()
            .Which.Should().Contain("/realms/<realm>");
    }

    [Fact] // invariant: the admin interface sits beside the realm, /admin/realms/<realm>, and the token endpoint in it
    public void EndpointsFor_ShouldDeriveTheAdminAndTokenAddresses_FromTheAuthority()
    {
        var endpoints = KeycloakAdminOptions.EndpointsFor(SignIn("https://keycloak.unit.test/realms/apiary-keeper/"));

        endpoints.Should().NotBeNull();
        endpoints!.Token.Should().Be(new Uri("https://keycloak.unit.test/realms/apiary-keeper/protocol/openid-connect/token"));
        endpoints.Admin.Should().Be(new Uri("https://keycloak.unit.test/admin/realms/apiary-keeper/"));
    }

    [Fact] // regression risk: a Keycloak under a relative path keeps it in front of /admin
    public void EndpointsFor_ShouldKeepTheRelativePath_WhenKeycloakIsServedUnderOne()
    {
        var endpoints = KeycloakAdminOptions.EndpointsFor(SignIn("https://keycloak.unit.test/auth/realms/apiary-keeper"));

        endpoints!.Admin.Should().Be(new Uri("https://keycloak.unit.test/auth/admin/realms/apiary-keeper/"));
    }

    [Fact] // invariant: over a private network the API reaches Keycloak by the back channel, as its token calls do
    public void EndpointsFor_ShouldUseTheBackChannel_WhenAMetadataAddressIsSet()
    {
        var signIn = SignIn();
        signIn.MetadataAddress = "http://keycloak.railway.internal:8080/realms/apiary-keeper/.well-known/openid-configuration";

        var endpoints = KeycloakAdminOptions.EndpointsFor(signIn);

        endpoints!.Token.Should().Be(new Uri("http://keycloak.railway.internal:8080/realms/apiary-keeper/protocol/openid-connect/token"));
        endpoints.Admin.Should().Be(new Uri("http://keycloak.railway.internal:8080/admin/realms/apiary-keeper/"));
    }

    [Theory] // invariant: each member role maps to its own configured id; there is no owner to map
    [InlineData(MemberRole.Helper, "0d6f3a52-7a1e-4c1f-9b0e-000000000002")]
    [InlineData(MemberRole.Grower, "0d6f3a52-7a1e-4c1f-9b0e-000000000003")]
    [InlineData(MemberRole.Customer, "0d6f3a52-7a1e-4c1f-9b0e-000000000004")]
    public void RoleIdOf_ShouldReturnTheRolesConfiguredId_WhenAsked(MemberRole role, string id) =>
        Complete().RoleIdOf(role).Should().Be(id);

    private static KeycloakAdminOptions Complete() => new()
    {
        ClientId = "apiary-keeper-api-admin",
        ClientSecret = Secret,
        HelperRoleId = "0d6f3a52-7a1e-4c1f-9b0e-000000000002",
        GrowerRoleId = "0d6f3a52-7a1e-4c1f-9b0e-000000000003",
        CustomerRoleId = "0d6f3a52-7a1e-4c1f-9b0e-000000000004",
    };

    private static SignInOptions SignIn(string authority = "https://keycloak.unit.test/realms/apiary-keeper") => new()
    {
        Authority = authority,
        ClientId = "apiary-keeper-bff",
        ClientSecret = "synthetic-unit-test-client-secret",
        Audience = "apiary-keeper-api",
    };
}
