using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace ApiaryCopilot.IntegrationTests;

// The BFF in the API end to end against the seeded realm (ARCHITECTURE.md D-19 Option A): a browser signs in with the
// authorization code and PKCE through Keycloak's own login form and holds only a cookie; a write without the CSRF header
// is refused; sign-out is a POST that ends the session on the server; an agent's bearer token reaches the same endpoint
// with the same principal; a bearer for another audience, altered or forged is refused; the key ring is in PostgreSQL,
// encrypted. An expired Keycloak token needs the realm's five-minute lifetime to pass, so expiry is proven in the unit
// tier (SignInHostTests) with a key of the test's own. Deployment=None: Keycloak, PostgreSQL and MinIO are loopback
// containers. Runs alone (KeycloakAlone), after the server's tests.
[Trait("Deployment", "None")]
[Collection(KeycloakAlone.Name)]
public sealed class KeycloakSignInTests(SignInServerFixture fixture) : IClassFixture<SignInServerFixture>
{
    private const string Owner = "synthetic-owner";
    private const string SeedAccountId = "a91a7c0e-5747-4e00-8000-000000000019";
    private const string SessionCookie = "apiary-keeper-session";

    // Keycloak offers pushed authorization requests (RFC 9126), so the API sends the code flow's parameters - PKCE's
    // challenge among them - to Keycloak over the back channel with the client secret, and the browser carries only a
    // reference to them. PKCE itself is proven by Keycloak: this client requires S256 (KeycloakRealmTests) and refuses an
    // authorization request without it, so a completed sign-in used it.
    [Fact] // invariant: the sign-in is Keycloak's authorization code flow, as the BFF's confidential client, returning to the asked page
    public async Task SignIn_ShouldRunThePushedCodeFlow_WhenABrowserSignsIn()
    {
        using var browser = new TestBrowser(fixture.Address);

        using var callback = await browser.SignInAsync(Owner, fixture.Keycloak.Realm.PasswordOf(Owner), "/#/yards");

        var query = HttpUtility.ParseQueryString(browser.AuthorizationRequest!.Query);
        browser.AuthorizationRequest.GetLeftPart(UriPartial.Path).Should().Be($"{fixture.Keycloak.RealmUrl}/protocol/openid-connect/auth");
        query.AllKeys.Should().BeEquivalentTo(["client_id", "request_uri"], "every other parameter was pushed to Keycloak directly");
        query["client_id"].Should().Be(TestKeycloak.BffClientId);
        query["request_uri"].Should().StartWith("urn:ietf:params:oauth:request_uri:");
        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
        callback.Headers.Location!.OriginalString.Should().Be("/#/yards", "the browser goes back to the PWA screen it asked for");
    }

    [Fact] // invariant: after sign-in the browser holds one cookie for the API, a session id, and no token ever reached it
    public async Task SignIn_ShouldLeaveTheBrowserOnlyASessionCookie_WhenItCompletes()
    {
        using var browser = new TestBrowser(fixture.Address);

        using var callback = await browser.SignInAsync(Owner, fixture.Keycloak.Realm.PasswordOf(Owner));

        var cookie = browser.ApiCookies.Should().ContainSingle("the correlation and nonce cookies are gone once the callback is done").Subject;
        cookie.Name.Should().Be(SessionCookie);
        cookie.HttpOnly.Should().BeTrue();
        cookie.Path.Should().Be("/");
        cookie.Value.Length.Should().BeLessThan(400, "a session id, not the tokens or the claims");
        cookie.Value.Should().NotContain(SeedAccountId);
        browser.ApiResponses.Should().NotContain(r => r.Contains("eyJ", StringComparison.Ordinal), "no JWT in any header or body the API sent");
    }

    [Fact] // regression risk: the cookie is a session: the API answers with the signed-in user's sub, account and role
    public async Task GetSignedInUser_ShouldReturnTheSessionsPrincipal_WhenCalledWithTheCookie()
    {
        using var browser = await SignedInBrowserAsync();

        using var me = await browser.GetAsync(new Uri(fixture.Address, "/api/auth/me"));

        me.StatusCode.Should().Be(HttpStatusCode.OK);
        var user = await me.Content.ReadFromJsonAsync<JsonElement>();
        user.GetProperty("sub").GetString().Should().NotBeNullOrWhiteSpace();
        user.GetProperty("accountId").GetString().Should().Be(SeedAccountId);
        user.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal("owner");
        user.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("sub", "accountId", "roles");
    }

    [Fact] // invariant (D-19 Option A): an agent's bearer token reaches the same endpoint with the same principal
    public async Task GetSignedInUser_ShouldReturnTheSamePrincipal_WhenAnAgentCallsWithABearerToken()
    {
        using var browser = await SignedInBrowserAsync();
        using var bySession = await browser.GetAsync(new Uri(fixture.Address, "/api/auth/me"));
        using var http = AppServerFixture.CreateClient(fixture.Address);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync());
        using var byBearer = await http.SendAsync(request);

        byBearer.StatusCode.Should().Be(HttpStatusCode.OK);
        (await byBearer.Content.ReadAsStringAsync()).Should().Be(await bySession.Content.ReadAsStringAsync());
        byBearer.Headers.Contains("Set-Cookie").Should().BeFalse("the bearer path starts no session");
    }

    [Fact] // invariant: a write with the session cookie and no CSRF header is refused; with it, the write goes through
    public async Task Write_ShouldNeedTheCsrfHeader_WhenItCarriesTheSessionCookie()
    {
        using var browser = await SignedInBrowserAsync();
        var before = await YardCountAsync();

        using var refused = await browser.SendAsync(YardWrite(csrf: false));
        var afterRefusal = await YardCountAsync();
        using var accepted = await browser.SendAsync(YardWrite(csrf: true));

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString().Should().StartWith("NotAllowed:");
        afterRefusal.Should().Be(before, "nothing was written");
        accepted.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact] // invariant: sign-out is a POST with the CSRF header, and it ends the session on the server, not just in the browser
    public async Task SignOut_ShouldEndTheSessionOnTheServer_WhenPostedWithTheCsrfHeader()
    {
        using var browser = await SignedInBrowserAsync();
        var session = browser.ApiCookies.Single(c => c.Name == SessionCookie).Value;

        using var byGet = await browser.GetAsync(new Uri(fixture.Address, "/api/auth/logout"));
        using var withoutHeader = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Post, new Uri(fixture.Address, "/api/auth/logout")));
        using var stillIn = await browser.GetAsync(new Uri(fixture.Address, "/api/auth/me"));
        var signOut = new HttpRequestMessage(HttpMethod.Post, new Uri(fixture.Address, "/api/auth/logout"));
        signOut.Headers.Add("X-CSRF", "1");
        using var signedOut = await browser.SendAsync(signOut);

        byGet.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        withoutHeader.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        stillIn.StatusCode.Should().Be(HttpStatusCode.OK, "a refused sign-out leaves the session alone");
        signedOut.StatusCode.Should().Be(HttpStatusCode.OK);
        browser.ApiCookies.Should().BeEmpty("the cookie is cleared");
        using var replay = AppServerFixture.CreateClient(fixture.Address);
        using var replayed = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        replayed.Headers.Add("Cookie", $"{SessionCookie}={session}");
        using var afterwards = await replay.SendAsync(replayed);
        afterwards.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the session is gone from the server, so a copy of the cookie is worthless");
    }

    [Fact] // regression risk: sign-out hands back Keycloak's own sign-out page for this client, which Keycloak accepts
    public async Task SignOut_ShouldReturnAnEndSessionUrlKeycloakAccepts_WhenSignedOut()
    {
        using var browser = await SignedInBrowserAsync();
        var signOut = new HttpRequestMessage(HttpMethod.Post, new Uri(fixture.Address, "/api/auth/logout"));
        signOut.Headers.Add("X-CSRF", "1");

        using var signedOut = await browser.SendAsync(signOut);
        var endSession = new Uri((await signedOut.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("endSessionUrl").GetString()!);
        using var page = await browser.GetAsync(endSession);

        endSession.GetLeftPart(UriPartial.Path).Should().Be($"{fixture.Keycloak.RealmUrl}/protocol/openid-connect/logout");
        HttpUtility.ParseQueryString(endSession.Query)["client_id"].Should().Be(TestKeycloak.BffClientId);
        ((int)page.StatusCode).Should().BeLessThan(400, "Keycloak accepts the client and the return address (its confirmation page, or the redirect)");
    }

    [Fact] // invariant: Keycloak's ID token - signed by the realm, but for another audience - is no bearer for the API
    public async Task GetSignedInUser_ShouldRefuseTheBearer_WhenItIsAnIdTokenForAnotherAudience()
    {
        using var tokens = await PasswordGrantAsync();

        using var response = await CallWithBearerAsync(tokens.RootElement.GetProperty("id_token").GetString()!);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact] // invariant: a Keycloak access token changed after signing is refused
    public async Task GetSignedInUser_ShouldRefuseTheBearer_WhenTheTokenWasAltered()
    {
        var token = await AccessTokenAsync();
        var parts = token.Split('.');
        var payload = Decode(parts[1]).Replace(SeedAccountId, "c057e0a1-5747-4e00-8000-000000000071", StringComparison.Ordinal);

        using var response = await CallWithBearerAsync($"{parts[0]}.{Encode(payload)}.{parts[2]}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact] // invariant: a token forged with another key under Keycloak's key id, or unsigned, is refused
    public async Task GetSignedInUser_ShouldRefuseTheBearer_WhenTheTokenIsForgedOrUnsigned()
    {
        var token = await AccessTokenAsync();
        var parts = token.Split('.');
        using var forger = RSA.Create(2048);
        var signedPart = $"{parts[0]}.{parts[1]}";
        var forged = $"{signedPart}.{Encode(forger.SignData(Encoding.ASCII.GetBytes(signedPart), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))}";
        var unsigned = $"{Encode("""{"alg":"none","typ":"JWT"}""")}.{parts[1]}.";

        using var forgedResponse = await CallWithBearerAsync(forged);
        using var unsignedResponse = await CallWithBearerAsync(unsigned);

        forgedResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unsignedResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact] // invariant (D-19): the Data Protection key ring is in PostgreSQL, every key encrypted with the certificate
    public async Task KeyRing_ShouldBeInPostgresEncrypted_WhenABrowserHasSignedIn()
    {
        using var browser = await SignedInBrowserAsync();

        var keys = await fixture.Database.CountAsync("SELECT count(*) FROM \"DataProtectionKeys\"");
        var inTheClear = await fixture.Database.CountAsync(
            "SELECT count(*) FROM \"DataProtectionKeys\" WHERE \"Xml\" NOT LIKE '%EncryptedData%' OR \"Xml\" LIKE '%<value>%'");

        keys.Should().BePositive();
        inTheClear.Should().Be(0);
    }

    private async Task<TestBrowser> SignedInBrowserAsync()
    {
        var browser = new TestBrowser(fixture.Address);
        try
        {
            using var callback = await browser.SignInAsync(Owner, fixture.Keycloak.Realm.PasswordOf(Owner));
            callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
            return browser;
        }
        catch
        {
            browser.Dispose();
            throw;
        }
    }

    // An agent's token for the same user, from the realm's test client (its direct-access grant exists in this dev realm only).
    private async Task<JsonDocument> PasswordGrantAsync()
    {
        using var response = await fixture.Keycloak.PasswordGrantAsync(Owner, fixture.Keycloak.Realm.PasswordOf(Owner));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<string> AccessTokenAsync()
    {
        using var tokens = await PasswordGrantAsync();
        return tokens.RootElement.GetProperty("access_token").GetString()!;
    }

    private async Task<HttpResponseMessage> CallWithBearerAsync(string token)
    {
        using var http = AppServerFixture.CreateClient(fixture.Address);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(request);
    }

    private HttpRequestMessage YardWrite(bool csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(fixture.Address, "/api/yards"))
        {
            Content = JsonContent.Create(new
            {
                name = $"Synthetic Sign-in Yard {Guid.NewGuid():N}",
                location = new { latitude = 30.2672, longitude = -97.7431 },
                state = "TX",
                county = "Travis",
                landowner = new { contact = "Synthetic Landowner, 555-0100", terms = "free" },
            }),
        };
        if (csrf)
        {
            request.Headers.Add("X-CSRF", "1");
        }

        return request;
    }

    private Task<long> YardCountAsync() =>
        fixture.Database.CountAsync($"SELECT count(*) FROM \"Yards\" WHERE \"AccountId\" = '{fixture.OwnerAccountId}'");

    private static string Decode(string part)
    {
        var padded = part.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private static string Encode(string text) => Encode(Encoding.UTF8.GetBytes(text));

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
