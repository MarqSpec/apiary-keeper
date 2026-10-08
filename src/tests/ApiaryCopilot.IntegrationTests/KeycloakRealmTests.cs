using System.Net;
using System.Text;
using System.Text.Json;

namespace ApiaryCopilot.IntegrationTests;

// The seeded synthetic realm compose.yaml runs (ARCHITECTURE.md D-19, D-22), imported into a throwaway Keycloak
// (TestKeycloak): every synthetic user's token carries its role and account_id in the ID token and the access token, a
// wrong password and the password grant through any client but the test client are refused, and a used refresh token is
// not accepted again. Deployment=None: Keycloak is a loopback container. Runs alone (KeycloakAlone).
[Trait("Deployment", "None")]
[Collection(KeycloakAlone.Name)]
public sealed class KeycloakRealmTests(TestKeycloak keycloak) : IClassFixture<TestKeycloak>
{
    // The staging seed's synthetic account (DEPLOYMENT.md §3), so later work matches the seed.
    private const string SeedAccountId = "a91a7c0e-5747-4e00-8000-000000000019";
    private const string CustomerAccountId = "c057e0a1-5747-4e00-8000-000000000071";
    private static readonly string[] AppRoles = ["owner", "helper", "grower", "customer"];

    [Theory]
    [InlineData("synthetic-owner", "owner", SeedAccountId)]
    [InlineData("synthetic-helper", "helper", SeedAccountId)]
    [InlineData("synthetic-grower", "grower", SeedAccountId)]
    [InlineData("synthetic-customer", "customer", CustomerAccountId)]
    public async Task PasswordGrant_ShouldCarryRoleAndAccountIdInBothTokens_WhenSyntheticUserSignsIn(
        string username, string role, string accountId)
    {
        using var response = await keycloak.PasswordGrantAsync(username, keycloak.Realm.PasswordOf(username));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var tokenName in new[] { "id_token", "access_token" })
        {
            using var claims = Claims(body.RootElement.GetProperty(tokenName).GetString()!);
            claims.RootElement.GetProperty("account_id").GetString().Should().Be(accountId, $"the {tokenName} carries the account");
            var roles = claims.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();
            roles.Should().Contain(role, $"the {tokenName} carries the user's role");
            roles.Intersect(AppRoles).Should().ContainSingle($"the {tokenName} carries no other application role");
        }
    }

    [Fact]
    public async Task PasswordGrant_ShouldBeRefused_WhenPasswordIsWrong()
    {
        using var response = await keycloak.PasswordGrantAsync("synthetic-owner", keycloak.Realm.PasswordOf("synthetic-owner") + "-wrong");

        // RFC 6749 §5.2: a bad resource-owner credential is invalid_grant, a 400.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetString().Should().Be("invalid_grant");
        body.RootElement.TryGetProperty("access_token", out _).Should().BeFalse();
    }

    // The direct-access grant is the test client's alone: the browser client signs in only by code with PKCE, and
    // Keycloak's built-in clients, which it creates in every realm (admin-cli with the grant on), must not offer it.
    [Theory]
    [InlineData(TestKeycloak.BrowserClientId)]
    [InlineData("admin-cli")]
    [InlineData("account")]
    [InlineData("account-console")]
    [InlineData("security-admin-console")]
    [InlineData("broker")]
    [InlineData("realm-management")]
    public async Task PasswordGrant_ShouldBeRefused_WhenClientIsNotTheTestClient(string clientId)
    {
        using var response = await keycloak.TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = clientId,
            ["username"] = "synthetic-owner",
            ["password"] = keycloak.Realm.PasswordOf("synthetic-owner"),
            ["scope"] = "openid",
        });

        response.IsSuccessStatusCode.Should().BeFalse();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetString().Should().Be("unauthorized_client");
        body.RootElement.TryGetProperty("access_token", out _).Should().BeFalse();
    }

    // The BFF's client is confidential, and holding its secret is still no way to trade a password for tokens.
    [Fact]
    public async Task PasswordGrant_ShouldBeRefused_WhenTheBffClientAsksWithItsSecret()
    {
        using var response = await keycloak.TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = TestKeycloak.BffClientId,
            ["client_secret"] = keycloak.Realm.ClientSecretOf(TestKeycloak.BffClientId),
            ["username"] = "synthetic-owner",
            ["password"] = keycloak.Realm.PasswordOf("synthetic-owner"),
            ["scope"] = "openid",
        });

        response.IsSuccessStatusCode.Should().BeFalse();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetString().Should().Be("unauthorized_client");
    }

    // The bearer path checks the audience, so the access token names the API; the ID token stays the client's own.
    [Fact]
    public async Task PasswordGrant_ShouldPutTheApiAudienceInTheAccessTokenOnly_WhenSignedIn()
    {
        using var response = await keycloak.PasswordGrantAsync("synthetic-owner", keycloak.Realm.PasswordOf("synthetic-owner"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using (var access = Claims(body.RootElement.GetProperty("access_token").GetString()!))
        {
            Audiences(access.RootElement).Should().Contain(TestKeycloak.ApiAudience);
        }

        using var id = Claims(body.RootElement.GetProperty("id_token").GetString()!);
        Audiences(id.RootElement).Should().Equal(TestKeycloak.TestClientId);
    }

    [Fact]
    public void RealmFile_ShouldHoldTheBffClientAsDecided_WhenCommitted()
    {
        var bff = keycloak.Realm.Client(TestKeycloak.BffClientId);

        // D-19: a confidential client, the authorization code with PKCE S256 and nothing else.
        bff.GetProperty("publicClient").GetBoolean().Should().BeFalse();
        bff.GetProperty("standardFlowEnabled").GetBoolean().Should().BeTrue();
        bff.GetProperty("implicitFlowEnabled").GetBoolean().Should().BeFalse();
        bff.GetProperty("directAccessGrantsEnabled").GetBoolean().Should().BeFalse();
        bff.GetProperty("serviceAccountsEnabled").GetBoolean().Should().BeFalse();
        bff.GetProperty("attributes").GetProperty("pkce.code.challenge.method").GetString().Should().Be("S256");
        bff.GetProperty("secret").GetString().Should().StartWith("synthetic-dev-realm-only-", "it is a synthetic test value for this realm alone");
        // Loopback only, any port (RFC 8252 §7.3), and only the API's callback.
        bff.GetProperty("redirectUris").EnumerateArray().Select(u => u.GetString())
            .Should().BeEquivalentTo("http://127.0.0.1/api/auth/callback", "http://localhost/api/auth/callback");
        // The tokens carry only the basic scope's claims beside the mappers: no profile, no email.
        bff.GetProperty("defaultClientScopes").EnumerateArray().Select(s => s.GetString()).Should().Equal("basic");
        foreach (var clientId in new[] { TestKeycloak.BffClientId, TestKeycloak.TestClientId })
        {
            var audience = keycloak.Realm.Client(clientId).GetProperty("protocolMappers").EnumerateArray()
                .Single(m => m.GetProperty("protocolMapper").GetString() == "oidc-audience-mapper").GetProperty("config");
            audience.GetProperty("included.custom.audience").GetString().Should().Be(TestKeycloak.ApiAudience);
            audience.GetProperty("access.token.claim").GetString().Should().Be("true");
            audience.GetProperty("id.token.claim").GetString().Should().Be("false");
        }
    }

    // D-22 as decided: the service account's client has the client-credentials grant and nothing else, owners
    // hold owner only through the owners group (a direct mapping would escape the group's exclusion), and the grant is
    // written in the realm's fine-grained admin permissions.
    [Fact]
    public void RealmFile_ShouldHoldTheServiceAccountAndTheOwnersGroupAsDecided_WhenCommitted()
    {
        var realm = keycloak.Realm.Root;
        var client = keycloak.Realm.Client(TestKeycloak.ServiceAccountClientId);

        realm.GetProperty("adminPermissionsEnabled").GetBoolean().Should().BeTrue();
        client.GetProperty("publicClient").GetBoolean().Should().BeFalse();
        client.GetProperty("serviceAccountsEnabled").GetBoolean().Should().BeTrue();
        client.GetProperty("standardFlowEnabled").GetBoolean().Should().BeFalse();
        client.GetProperty("implicitFlowEnabled").GetBoolean().Should().BeFalse();
        client.GetProperty("directAccessGrantsEnabled").GetBoolean().Should().BeFalse();
        client.GetProperty("secret").GetString().Should().StartWith("synthetic-dev-realm-only-", "it is a synthetic test value for this realm alone");
        var owners = realm.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("name").GetString() == TestKeycloak.OwnersGroup);
        owners.GetProperty("realmRoles").EnumerateArray().Select(r => r.GetString()).Should().Equal("owner");
        realm.GetProperty("users").EnumerateArray()
            .Where(u => u.TryGetProperty("realmRoles", out var roles) && roles.EnumerateArray().Any(r => r.GetString() == "owner"))
            .Should().BeEmpty("no user holds owner directly, where the owners group's exclusion would not see it");
        keycloak.Realm.User("synthetic-owner").GetProperty("groups").EnumerateArray().Select(g => g.GetString()).Should().Equal("/owners");
    }

    [Fact]
    public async Task RefreshGrant_ShouldRefuseTheUsedRefreshToken_WhenItWasRotated()
    {
        using var signIn = await keycloak.PasswordGrantAsync("synthetic-helper", keycloak.Realm.PasswordOf("synthetic-helper"));
        signIn.StatusCode.Should().Be(HttpStatusCode.OK);
        using var first = JsonDocument.Parse(await signIn.Content.ReadAsStringAsync());
        var firstRefresh = first.RootElement.GetProperty("refresh_token").GetString()!;

        using var refreshed = await keycloak.RefreshGrantAsync(firstRefresh);
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
        using var second = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync());
        second.RootElement.GetProperty("refresh_token").GetString().Should().NotBe(firstRefresh);
        using (var claims = Claims(second.RootElement.GetProperty("access_token").GetString()!))
        {
            claims.RootElement.GetProperty("account_id").GetString().Should().Be(SeedAccountId, "a refreshed token still carries the account");
        }

        using var reused = await keycloak.RefreshGrantAsync(firstRefresh);

        reused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var refusal = JsonDocument.Parse(await reused.Content.ReadAsStringAsync());
        refusal.RootElement.GetProperty("error").GetString().Should().Be("invalid_grant");
    }

    [Fact]
    public void RealmFile_ShouldHoldTheDecidedSettings_WhenCommitted()
    {
        var realm = keycloak.Realm.Root;

        // D-22, a separate change answer 5: the emailed reset link lasts 30 minutes.
        realm.GetProperty("actionTokenGeneratedByUserLifespan").GetInt32().Should().Be(1800);
        realm.GetProperty("revokeRefreshToken").GetBoolean().Should().BeTrue();
        realm.GetProperty("refreshTokenMaxReuse").GetInt32().Should().Be(0);
        realm.GetProperty("registrationAllowed").GetBoolean().Should().BeFalse();
        // MFA is not enforced locally: Keycloak's own browser flow (OTP only for a user who set it up), and no OTP set-up
        // required of new users. A required action would also refuse the password grant in the tests above.
        realm.TryGetProperty("browserFlow", out _).Should().BeFalse();
        if (realm.TryGetProperty("requiredActions", out var actions))
        {
            actions.EnumerateArray()
                .Where(a => a.GetProperty("defaultAction").GetBoolean())
                .Select(a => a.GetProperty("alias").GetString())
                .Should().NotContain("CONFIGURE_TOTP");
        }

        var google = realm.GetProperty("identityProviders").EnumerateArray().Single(p => p.GetProperty("alias").GetString() == "google");
        var config = google.GetProperty("config");
        foreach (var endpoint in new[] { "authorizationUrl", "tokenUrl", "userInfoUrl", "jwksUrl", "issuer" })
        {
            new Uri(config.GetProperty(endpoint).GetString()!).Host.Should().EndWith(".invalid", $"the stub's {endpoint} points nowhere");
        }

        var clients = realm.GetProperty("clients").EnumerateArray().ToList();
        var browser = clients.Single(c => c.GetProperty("clientId").GetString() == TestKeycloak.BrowserClientId);
        browser.GetProperty("publicClient").GetBoolean().Should().BeTrue();
        browser.GetProperty("attributes").GetProperty("pkce.code.challenge.method").GetString().Should().Be("S256");
        browser.GetProperty("redirectUris").EnumerateArray()
            .Select(u => new Uri(u.GetString()!.TrimEnd('*')).Host)
            .Should().OnlyContain(host => host == "localhost" || host == "127.0.0.1");
        clients.Where(c => c.TryGetProperty("directAccessGrantsEnabled", out var on) && on.GetBoolean())
            .Select(c => c.GetProperty("clientId").GetString())
            .Should().Equal(TestKeycloak.TestClientId);
    }

    private static IEnumerable<string?> Audiences(JsonElement claims) =>
        claims.GetProperty("aud") is { ValueKind: JsonValueKind.Array } many
            ? many.EnumerateArray().Select(a => a.GetString())
            : [claims.GetProperty("aud").GetString()];

    private static JsonDocument Claims(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
    }
}
