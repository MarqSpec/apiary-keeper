using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApiaryCopilot.Api.SignIn;
using ApiaryCopilot.Infrastructure;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ApiaryCopilot.UnitTests.Api.SignIn;

// The BFF in the API through the real host in memory (CONVENTIONS.md §8.1): the OpenID Connect challenge, the
// cookie session and its renewal, the bearer path, the CSRF check, sign-out, the key ring, and the apiary
// endpoints authorized from the principal. Keycloak itself is the integration tier's (KeycloakSignInTests).
public sealed class SignInHostTests
{
    private const string WriteWithInvalidBody = "{}";

    [Theory] // invariant: with sign-in off (staging and production today) none of its routes exists
    [InlineData("GET", "/api/auth/login")]
    [InlineData("GET", "/api/auth/me")]
    [InlineData("POST", "/api/auth/logout")]
    [InlineData("GET", "/api/auth/callback")]
    public async Task SignInRoutes_ShouldNotExist_WhenSignInIsNotConfigured(string method, string path)
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact] // invariant: with sign-in off no caller has an account, so an apiary endpoint fails closed (503), whatever cookie it carries
    public async Task Write_ShouldBeUnavailable_WhenSignInIsNotConfigured()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Write("apiary-keeper-session=anything", csrf: false));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString().Should().Contain("sign-in is not set up");
    }

    [Fact] // regression risk: sign-in starts the authorization code flow with PKCE S256 as the BFF client, asking for openid only
    public async Task SignIn_ShouldRedirectToKeycloakWithCodeAndPkce_WhenCalled()
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/auth/login?returnUrl=%2F%23%2Fyards%2F7");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!;
        location.GetLeftPart(UriPartial.Path).Should().Be(SignInHost.AuthorizationEndpoint);
        var query = QueryHelpers.ParseQuery(location.Query);
        query["client_id"].ToString().Should().Be(SignInHost.ClientId);
        query["response_type"].ToString().Should().Be("code");
        (query.TryGetValue("response_mode", out var mode) ? mode.ToString() : "query")
            .Should().Be("query", "the code comes back on a top-level GET, the code flow's default, never a cross-site form post");
        query["scope"].ToString().Should().Be("openid");
        query["code_challenge_method"].ToString().Should().Be("S256");
        query["code_challenge"].ToString().Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        query["redirect_uri"].ToString().Should().Be("http://localhost/api/auth/callback");
        query.Should().ContainKey("state").And.ContainKey("nonce");
        query.Should().NotContainKey("client_secret");
        location.ToString().Should().NotContain("synthetic-unit-test-client-secret");
    }

    [Theory] // invariant: the browser is returned only to a path on this host, never to another site
    [InlineData("%2F%23%2Fyards%2F7", "/#/yards/7")]
    [InlineData("https%3A%2F%2Fevil.example%2F", "/")]
    [InlineData("%2F%2Fevil.example", "/")]
    public async Task SignIn_ShouldCarryOnlyALocalReturnUrl_WhenAReturnUrlIsGiven(string returnUrl, string expected)
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"/api/auth/login?returnUrl={returnUrl}");

        var state = QueryHelpers.ParseQuery(response.Headers.Location!.Query)["state"].ToString();
        var options = host.Factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(SignInExtensions.OpenIdConnectScheme);
        options.StateDataFormat.Unprotect(state)!.RedirectUri.Should().Be(expected);
    }

    [Fact] // invariant: the sign-in's correlation and nonce cookies are HttpOnly and SameSite=Lax
    public async Task SignIn_ShouldSetHttpOnlyLaxCorrelationCookies_WhenCalled()
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/auth/login");

        var cookies = SignInHost.SetCookies(response).ToList();
        cookies.Should().HaveCount(2).And.OnlyContain(c =>
            c.Contains("httponly", StringComparison.OrdinalIgnoreCase) && c.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] // regression risk: behind the proxy the callback URL and the cookies follow the client's https scheme and host
    public async Task SignIn_ShouldUseTheForwardedSchemeAndHost_WhenThePeerIsTheProxy()
    {
        using var host = new SignInHost(requireHttps: true);
        var server = host.Factory.Server;

        var context = await server.SendAsync(c => Behind(c, "172.18.0.5", "/api/auth/login"));

        var location = new Uri(context.Response.Headers.Location.ToString());
        QueryHelpers.ParseQuery(location.Query)["redirect_uri"].ToString().Should().Be("https://apiary-keeper.example.test/api/auth/callback");
        context.Response.Headers.SetCookie.Should().OnlyContain(c => c!.Contains("secure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] // invariant: a peer that is not the proxy cannot choose the callback URL with forwarded headers
    public async Task SignIn_ShouldIgnoreForwardedHeaders_WhenThePeerIsPublic()
    {
        using var host = new SignInHost();
        var server = host.Factory.Server;

        var context = await server.SendAsync(c => Behind(c, "198.51.100.9", "/api/auth/login"));

        var location = new Uri(context.Response.Headers.Location.ToString());
        QueryHelpers.ParseQuery(location.Query)["redirect_uri"].ToString().Should().Be("http://app.internal/api/auth/callback");
    }

    [Fact] // regression risk: both handlers fetch the discovery document where configured, and still check the authority as the issuer
    public void Handlers_ShouldUseTheMetadataAddressAndTheAuthoritysIssuer_WhenAMetadataAddressIsConfigured()
    {
        const string metadataAddress = "http://apiary-it-kc-5000:8080/realms/apiary-keeper/.well-known/openid-configuration";
        using var host = new SignInHost(overrides: new Dictionary<string, string?> { ["SignIn:MetadataAddress"] = metadataAddress });
        using var client = host.CreateClient();

        var openIdConnect = host.Factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(SignInExtensions.OpenIdConnectScheme);
        var bearer = host.Factory.Services.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>>().Get(SignInExtensions.BearerScheme);

        openIdConnect.MetadataAddress.Should().Be(metadataAddress);
        bearer.MetadataAddress.Should().Be(metadataAddress);
        openIdConnect.Authority.Should().Be(SignInHost.Authority);
        bearer.TokenValidationParameters.ValidIssuer.Should().Be(SignInHost.Authority);
        bearer.TokenValidationParameters.ValidAudience.Should().Be(SignInHost.Audience);
    }

    [Fact] // invariant: both handlers pin the issuer to the configured authority, never to what a discovery document says
    public void Handlers_ShouldPinTheIssuerToTheAuthority_WhenConfigured()
    {
        using var host = new SignInHost(overrides: new Dictionary<string, string?> { ["SignIn:Authority"] = SignInHost.Authority + "/" });
        using var client = host.CreateClient();

        var openIdConnect = host.Factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(SignInExtensions.OpenIdConnectScheme);
        var bearer = host.Factory.Services.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>>().Get(SignInExtensions.BearerScheme);

        openIdConnect.TokenValidationParameters.ValidateIssuer.Should().BeTrue();
        openIdConnect.TokenValidationParameters.ValidIssuer.Should().Be(SignInHost.Authority);
        bearer.TokenValidationParameters.ValidateIssuer.Should().BeTrue();
        bearer.TokenValidationParameters.ValidIssuer.Should().Be(SignInHost.Authority);
    }

    [Fact] // invariant: the session holds the principal and no token - neither handler saves one, nor asks userinfo for more claims
    public void Handlers_ShouldKeepNoToken_WhenConfigured()
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();

        var openIdConnect = host.Factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(SignInExtensions.OpenIdConnectScheme);
        var bearer = host.Factory.Services.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>>().Get(SignInExtensions.BearerScheme);

        openIdConnect.SaveTokens.Should().BeFalse();
        openIdConnect.GetClaimsFromUserInfoEndpoint.Should().BeFalse();
        openIdConnect.Scope.Should().Equal("openid");
        bearer.SaveToken.Should().BeFalse();
    }

    [Fact] // invariant: an API call that is not signed in is a 401, never a redirect to a login page
    public async Task GetSignedInUser_ShouldBeUnauthorized_WhenNotSignedIn()
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Location.Should().BeNull();
    }

    [Fact] // invariant: the cookie carries a session id only - small, opaque, HttpOnly, SameSite=Lax, path / - and no claim
    public async Task SessionCookie_ShouldCarryOnlyAnOpaqueSessionId_WhenSignedIn()
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();

        using var response = await client.PostAsync($"{SignInHost.TestSignInPath}?claims={Uri.EscapeDataString("sub=" + SignInHost.Subject + ";account_id=" + SignInHost.AccountId + ";roles=owner")}", null);

        var setCookie = SignInHost.SetCookies(response).Single(c => c.StartsWith("apiary-keeper-session=", StringComparison.Ordinal));
        setCookie.Should().ContainEquivalentOf("httponly").And.ContainEquivalentOf("samesite=lax").And.ContainEquivalentOf("path=/");
        var value = setCookie.Split(';')[0]["apiary-keeper-session=".Length..];
        value.Length.Should().BeLessThan(400, "a session id, not a serialized principal");
        value.Should().NotContain(SignInHost.Subject).And.NotContain(SignInHost.AccountId).And.NotContain("owner");
    }

    [Fact] // invariant: with https required the cookie is __Host- prefixed and Secure even over plain http (never SameAsRequest)
    public async Task SessionCookie_ShouldBeSecureAndHostPrefixed_WhenHttpsIsRequired()
    {
        using var host = new SignInHost(requireHttps: true);
        using var client = host.CreateClient();

        using var response = await client.PostAsync($"{SignInHost.TestSignInPath}?claims={Uri.EscapeDataString("sub=" + SignInHost.Subject + ";account_id=" + SignInHost.AccountId + ";roles=owner")}", null);

        client.BaseAddress!.Scheme.Should().Be("http", "the request itself is plain http");
        var setCookie = SignInHost.SetCookies(response).Should().ContainSingle(c => c.StartsWith("__Host-apiary-keeper-session=", StringComparison.Ordinal)).Subject;
        setCookie.Should().ContainEquivalentOf("secure").And.ContainEquivalentOf("httponly")
            .And.ContainEquivalentOf("samesite=lax").And.ContainEquivalentOf("path=/");
        setCookie.Should().NotContainEquivalentOf("domain=");
    }

    [Fact] // regression risk: the cookie session yields the principal: sub, account and roles
    public async Task GetSignedInUser_ShouldReturnTheSessionsPrincipal_WhenTheSessionCookieIsSent()
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();
        var cookie = await SignInHost.SignInSessionAsync(client, SignInHost.OwnerClaims());

        using var me = await client.SendAsync(Get("/api/auth/me", cookie));

        me.StatusCode.Should().Be(HttpStatusCode.OK);
        (await me.Content.ReadAsStringAsync()).Should().Be(
            $$"""{"sub":"{{SignInHost.Subject}}","accountId":"{{SignInHost.AccountId}}","roles":["owner"]}""");
    }

    [Fact] // invariant (D-19 Option A): an agent's bearer token yields the same principal as the cookie session
    public async Task GetSignedInUser_ShouldReturnTheSamePrincipal_WhenABearerTokenIsSentForTheSameUser()
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();
        var cookie = await SignInHost.SignInSessionAsync(client, SignInHost.OwnerClaims());
        using var bySession = await client.SendAsync(Get("/api/auth/me", cookie));

        using var request = Get("/api/auth/me");
        request.Headers.Authorization = SignInHost.Bearer(host.Token());
        using var byBearer = await client.SendAsync(request);

        byBearer.StatusCode.Should().Be(HttpStatusCode.OK);
        (await byBearer.Content.ReadAsStringAsync()).Should().Be(await bySession.Content.ReadAsStringAsync());
        SignInHost.SetCookies(byBearer).Should().BeEmpty("the bearer path never starts a session");
    }

    public static TheoryData<string> RefusedTokenKinds => new(RefusedTokens.Keys);

    private static readonly Dictionary<string, Func<SignInHost, string>> RefusedTokens = new()
    {
        { "another audience", h => h.Token(audience: "apiary-keeper-bff") },
        { "no audience", h => h.Token(audience: null) },
        { "another issuer", h => h.Token(issuer: "https://keycloak.unit.test/realms/other") },
        { "expired beyond the skew", h => h.Token(notBefore: DateTime.UtcNow.AddMinutes(-10), expires: DateTime.UtcNow.AddSeconds(-31)) },
        { "not valid yet", h => h.Token(notBefore: DateTime.UtcNow.AddMinutes(2), expires: DateTime.UtcNow.AddMinutes(10)) },
        { "signed by another key", h => h.Token(signingKey: new SigningCredentials(new RsaSecurityKey(RSA.Create(2048)) { KeyId = SignInHost.KeyId }, SecurityAlgorithms.RsaSha256)) },
        { "signed with Keycloak's own key under RS512", h => h.Token(signingKey: h.OwnKeyWith(SecurityAlgorithms.RsaSha512)) },
        { "signed with Keycloak's own key under PS256", h => h.Token(signingKey: h.OwnKeyWith(SecurityAlgorithms.RsaSsaPssSha256)) },
        { "signed with a shared secret", h => h.Token(signingKey: new SigningCredentials(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = SignInHost.KeyId }, SecurityAlgorithms.HmacSha256)) },
        { "unsigned", h => Unsigned(h.Token()) },
        { "altered after signing", h => Altered(h.Token()) },
        { "no account", h => h.Token(claims: new Dictionary<string, object> { ["sub"] = SignInHost.Subject, ["roles"] = new[] { "owner" }, ["typ"] = "Bearer" }) },
        { "no subject", h => h.Token(claims: new Dictionary<string, object> { ["account_id"] = SignInHost.AccountId, ["typ"] = "Bearer" }) },
        { "an ID token, though for the API's audience", h => h.Token(claims: new Dictionary<string, object> { ["sub"] = SignInHost.Subject, ["account_id"] = SignInHost.AccountId, ["roles"] = new[] { "owner" }, ["typ"] = "ID" }) },
        { "no token type", h => h.Token(claims: new Dictionary<string, object> { ["sub"] = SignInHost.Subject, ["account_id"] = SignInHost.AccountId, ["roles"] = new[] { "owner" } }) },
    };

    [Theory] // invariant: a bearer token is accepted only when signed by Keycloak's key, for this audience, from this issuer, in date
    [MemberData(nameof(RefusedTokenKinds))]
    public async Task GetSignedInUser_ShouldRefuseTheBearer_WhenTheTokenIsNotValid(string why)
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();
        using var request = Get("/api/auth/me");
        request.Headers.Authorization = SignInHost.Bearer(RefusedTokens[why](host));

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, why);
    }

    [Fact] // boundary: a token just inside the allowed clock skew is still accepted
    public async Task GetSignedInUser_ShouldAcceptTheBearer_WhenItExpiredWithinTheSkew()
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();
        using var request = Get("/api/auth/me");
        request.Headers.Authorization = SignInHost.Bearer(host.Token(notBefore: DateTime.UtcNow.AddMinutes(-10), expires: DateTime.UtcNow.AddSeconds(-20)));

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact] // invariant: a request with a bearer token is decided by the token, whatever session cookie it also carries
    public async Task GetSignedInUser_ShouldBeDecidedByTheBearer_WhenBothABearerAndASessionAreSent()
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();
        var cookie = await SignInHost.SignInSessionAsync(client, SignInHost.OwnerClaims());
        using var request = Get("/api/auth/me", cookie);
        request.Headers.Authorization = SignInHost.Bearer(host.Token(audience: "someone-else"));

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact] // invariant: a write with the session cookie and no X-CSRF header is refused before it reaches the endpoint
    public async Task Write_ShouldBeRefused_WhenItCarriesTheSessionCookieWithoutTheCsrfHeader()
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();
        var cookie = await SignInHost.SignInSessionAsync(client, SignInHost.OwnerClaims());

        using var response = await client.SendAsync(Write(cookie, csrf: false));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString().Should().StartWith("NotAllowed:");
    }

    [Fact] // boundary: the same write with the header reaches the endpoint
    public async Task Write_ShouldReachTheEndpoint_WhenItCarriesTheSessionCookieAndTheCsrfHeader()
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();
        var cookie = await SignInHost.SignInSessionAsync(client, SignInHost.OwnerClaims());

        using var response = await client.SendAsync(Write(cookie, csrf: true));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact] // invariant: a write that carries the session cookie needs the header even when it also carries a valid bearer
    public async Task Write_ShouldBeRefused_WhenItCarriesTheSessionCookieAndAValidBearerButNoCsrfHeader()
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();
        var cookie = await SignInHost.SignInSessionAsync(client, SignInHost.OwnerClaims());
        using var request = Write(cookie, csrf: false);
        request.Headers.Authorization = SignInHost.Bearer(host.Token());

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact] // boundary: a bearer write needs no CSRF header (no browser sends the token on its own)
    public async Task Write_ShouldReachTheEndpoint_WhenItCarriesABearerAndNoCsrfHeader()
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();
        using var request = Write(cookie: null, csrf: false);
        request.Headers.Authorization = SignInHost.Bearer(host.Token());

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory] // invariant: with sign-in on, an apiary endpoint refuses a caller with no or a failed credential (401), before its parser
    [InlineData(null)]
    [InlineData("not-a-token")]
    public async Task Write_ShouldBeUnauthorized_WhenItCarriesNoValidCredential(string? bearer)
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();
        using var request = Write(cookie: null, csrf: false);
        if (bearer is not null)
        {
            request.Headers.Authorization = SignInHost.Bearer(bearer);
        }

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // One endpoint per apiary group: yards, colonies, inspections and media.
    public static TheoryData<string, string> OneEndpointPerGroup() => new()
    {
        { "POST", "/api/yards" },
        { "POST", "/api/colonies" },
        { "GET", "/api/inspections/0199b2a4-0000-7000-8000-000000000001" },
        { "GET", "/api/inspections/0199b2a4-0000-7000-8000-000000000001/media" },
    };

    [Theory] // invariant: every apiary group needs a signed-in caller: 401, never a redirect, and nothing reached
    [MemberData(nameof(OneEndpointPerGroup))]
    public async Task ApiaryEndpoint_ShouldBeUnauthorized_WhenNotSignedIn(string method, string path)
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Location.Should().BeNull();
        A.CallTo(host.Factory.TenantDirectory).MustNotHaveHappened();
    }

    public static TheoryData<string, string, string> RefusedRolesPerGroup()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var role in new[] { "grower", "customer", "none" })
        {
            foreach (var (method, path) in new[] { ("POST", "/api/yards"), ("POST", "/api/colonies"), ("GET", "/api/inspections/0199b2a4-0000-7000-8000-000000000001"), ("GET", "/api/inspections/0199b2a4-0000-7000-8000-000000000001/media") })
            {
                data.Add(role, method, path);
            }
        }

        return data;
    }

    [Theory] // invariant (NFR-6, D-22): a grower, a customer or a user with no application role is refused every apiary group (403)
    [MemberData(nameof(RefusedRolesPerGroup))]
    public async Task ApiaryEndpoint_ShouldBeForbidden_WhenTheCallersRoleIsNotOwnerOrHelper(string role, string method, string path)
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();
        var claims = role == "none"
            ? new Dictionary<string, object> { ["sub"] = SignInHost.Subject, ["account_id"] = SignInHost.AccountId, ["typ"] = "Bearer" }
            : new Dictionary<string, object> { ["sub"] = SignInHost.Subject, ["account_id"] = SignInHost.AccountId, ["roles"] = new[] { role }, ["typ"] = "Bearer" };
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = SignInHost.Bearer(host.Token(claims: claims));

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
        A.CallTo(host.Factory.TenantDirectory).MustNotHaveHappened();
    }

    [Theory] // invariant (D-22, FR-74): only an owner invites; any other role is refused by the policy, before anything is looked up
    [InlineData("helper")]
    [InlineData("grower")]
    [InlineData("customer")]
    public async Task Invite_ShouldBeForbiddenByThePolicy_WhenTheCallerIsNotAnOwner(string role)
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/invitations");
        request.Headers.Authorization = SignInHost.Bearer(host.Token(claims: new Dictionary<string, object>
        {
            ["sub"] = SignInHost.Subject,
            ["account_id"] = SignInHost.AccountId,
            ["roles"] = new[] { role },
            ["typ"] = "Bearer",
        }));

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
        A.CallTo(host.Factory.TenantDirectory).MustNotHaveHappened();
    }

    [Theory] // invariant: an owner or a helper, by either path, reaches the endpoint (the yard parser's 400 proves it got there)
    [InlineData("owner", false)]
    [InlineData("helper", false)]
    [InlineData("owner", true)]
    [InlineData("helper", true)]
    public async Task Write_ShouldReachTheEndpoint_WhenTheCallerIsAnOwnerOrAHelper(string role, bool bySession)
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();
        HttpRequestMessage request;
        if (bySession)
        {
            var cookie = await SignInHost.SignInSessionAsync(client, new Claim("sub", SignInHost.Subject), new Claim("account_id", SignInHost.AccountId), new Claim("roles", role));
            request = Write(cookie, csrf: true);
        }
        else
        {
            request = Write(cookie: null, csrf: false);
            request.Headers.Authorization = SignInHost.Bearer(host.Token(claims: new Dictionary<string, object>
            {
                ["sub"] = SignInHost.Subject,
                ["account_id"] = SignInHost.AccountId,
                ["roles"] = new[] { role },
                ["typ"] = "Bearer",
            }));
        }

        using var response = await client.SendAsync(request);
        request.Dispose();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        A.CallTo(() => host.Factory.TenantDirectory.FindActingUserAsync(SignInHost.Subject, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Theory] // invariant: a signed-in owner whose subject no user of the account holds - or whose account has no row - is refused
    [InlineData(TenantStatus.SubjectUnknown, "you are not a user of this account")]
    [InlineData(TenantStatus.AccountMissing, "your account is not set up on this server")]
    public async Task Write_ShouldBeForbidden_WhenTheTokenDoesNotResolveToAUserOfTheAccount(TenantStatus status, string message)
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();
        A.CallTo(() => host.Factory.TenantDirectory.FindActingUserAsync(A<string>._, A<CancellationToken>._)).Returns(new TenantLookup(status, null));
        using var request = Write(cookie: null, csrf: false);
        request.Headers.Authorization = SignInHost.Bearer(host.Token());

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString().Should().Contain(message);
    }

    [Fact] // invariant: sign-out with the session cookie needs the CSRF header too, and leaves the session alone without it
    public async Task SignOut_ShouldBeRefusedAndKeepTheSession_WhenTheCsrfHeaderIsMissing()
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();
        var cookie = await SignInHost.SignInSessionAsync(client, SignInHost.OwnerClaims());

        using var refused = await client.SendAsync(Post("/api/auth/logout", cookie, csrf: false));
        using var me = await client.SendAsync(Get("/api/auth/me", cookie));

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        me.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact] // invariant: sign-out ends the session on the server, so the same cookie sent again is no longer signed in
    public async Task SignOut_ShouldEndTheSessionOnTheServer_WhenCalledWithTheCsrfHeader()
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();
        var cookie = await SignInHost.SignInSessionAsync(client, SignInHost.OwnerClaims());

        using var signOut = await client.SendAsync(Post("/api/auth/logout", cookie, csrf: true));
        using var replayed = await client.SendAsync(Get("/api/auth/me", cookie));

        signOut.StatusCode.Should().Be(HttpStatusCode.OK);
        SignInHost.SetCookies(signOut).Should().Contain(c => c.StartsWith("apiary-keeper-session=;", StringComparison.Ordinal) && c.Contains("expires=Thu, 01 Jan 1970", StringComparison.Ordinal));
        replayed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact] // regression risk: sign-out returns Keycloak's sign-out page for this client, back to this host
    public async Task SignOut_ShouldReturnKeycloaksEndSessionUrl_WhenCalled()
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();

        using var response = await client.PostAsync("/api/auth/logout", null);

        var url = new Uri((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("endSessionUrl").GetString()!);
        url.GetLeftPart(UriPartial.Path).Should().Be(SignInHost.EndSessionEndpoint);
        var query = QueryHelpers.ParseQuery(url.Query);
        query["client_id"].ToString().Should().Be(SignInHost.ClientId);
        query["post_logout_redirect_uri"].ToString().Should().Be("http://localhost/");
        query.Should().NotContainKey("id_token_hint", "no token is kept in the session");
    }

    [Fact] // invariant: sign-out is a POST; a GET (a link or an image on another site) cannot sign anyone out
    public async Task SignOut_ShouldNotBeAllowed_WhenCalledWithGet()
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/auth/logout");

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact] // invariant: a discovery document naming another issuer does not make that issuer's tokens acceptable
    public async Task GetSignedInUser_ShouldRefuseTheBearer_WhenTheMetadataAndTheTokenNameAnotherIssuer()
    {
        const string elsewhere = "https://keycloak.unit.test/realms/elsewhere";
        using var host = new SignInHost(bearerMetadataIssuer: elsewhere);
        using var client = host.CreateClient();
        using var fromElsewhere = Get("/api/auth/me");
        fromElsewhere.Headers.Authorization = SignInHost.Bearer(host.Token(issuer: elsewhere));
        using var fromTheAuthority = Get("/api/auth/me");
        fromTheAuthority.Headers.Authorization = SignInHost.Bearer(host.Token());

        using var refused = await client.SendAsync(fromElsewhere);
        using var accepted = await client.SendAsync(fromTheAuthority);

        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the issuer is the configured authority, whatever the metadata says");
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact] // invariant: the OpenID Connect handler takes an RS256 ID token only, from the authority alone, as the bearer path and the renewal do
    public void OpenIdConnect_ShouldAllowOnlyRs256FromTheAuthority_WhenConfigured()
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();

        var parameters = host.Factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(SignInExtensions.OpenIdConnectScheme).TokenValidationParameters;

        parameters.ValidAlgorithms.Should().Equal(SecurityAlgorithms.RsaSha256);
        parameters.IssuerValidator.Should().NotBeNull("the issuer is checked against the authority alone, never the metadata's");
        var accept = () => parameters.IssuerValidator!(SignInHost.Authority, null!, parameters);
        var refuse = () => parameters.IssuerValidator!("https://keycloak.unit.test/realms/elsewhere", null!, parameters);
        accept.Should().NotThrow().Which.Should().Be(SignInHost.Authority);
        refuse.Should().Throw<SecurityTokenInvalidIssuerException>();
    }

    [Fact] // invariant: before the access token is due nothing is asked of Keycloak
    public async Task Session_ShouldNotRenew_WhenTheAccessTokenIsNotDue()
    {
        var clock = new SettableClock();
        using var host = new SignInHost(clock: clock);
        using var client = host.CreateClient();
        var cookie = await SignInHost.SignInSessionAsync(client, SignInHost.OwnerClaims());

        clock.Advance(TimeSpan.FromMinutes(4));
        using var me = await client.SendAsync(Get("/api/auth/me", cookie));

        me.StatusCode.Should().Be(HttpStatusCode.OK);
        A.CallTo(() => host.Keycloak.RefreshAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact] // invariant (#5): at the renewal the session takes the new ID token's roles and account
    public async Task Session_ShouldTakeTheRenewedRolesAndAccount_WhenTheAccessTokenIsDue()
    {
        const string otherAccount = "c057e0a1-5747-4e00-8000-000000000071";
        var clock = new SettableClock();
        using var host = new SignInHost(clock: clock);
        using var client = host.CreateClient();
        var cookie = await SignInHost.SignInSessionAsync(client, SignInHost.OwnerClaims());
        A.CallTo(() => host.Keycloak.RefreshAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() => new TokenRefresh.Renewed(host.IdToken("helper", otherAccount), "synthetic-refresh-2", TimeSpan.FromMinutes(5)));

        clock.Advance(TimeSpan.FromMinutes(5));
        using var renewed = await client.SendAsync(Get("/api/auth/me", cookie));
        using var after = await client.SendAsync(Get("/api/auth/me", SignInHost.SessionCookie(renewed) ?? cookie));

        renewed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await renewed.Content.ReadAsStringAsync()).Should().Be(
            $$"""{"sub":"{{SignInHost.Subject}}","accountId":"{{otherAccount}}","roles":["helper"]}""");
        (await after.Content.ReadAsStringAsync()).Should().Contain(otherAccount).And.Contain("helper");
        A.CallTo(() => host.Keycloak.RefreshAsync("synthetic-refresh-token-at-sign-in", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => host.Keycloak.RefreshAsync(A<string>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact] // invariant: a renewal Keycloak refuses (a disabled user) ends the session: 401, the cookie cleared, the session gone
    public async Task Session_ShouldEnd_WhenKeycloakRefusesTheRenewal()
    {
        var clock = new SettableClock();
        using var host = new SignInHost(clock: clock);
        using var client = host.CreateClient();
        var cookie = await SignInHost.SignInSessionAsync(client, SignInHost.OwnerClaims());
        A.CallTo(() => host.Keycloak.RefreshAsync(A<string>._, A<CancellationToken>._)).Returns(new TokenRefresh.Refused("invalid_grant"));

        clock.Advance(TimeSpan.FromMinutes(5));
        using var refused = await client.SendAsync(Get("/api/auth/me", cookie));
        using var replayed = await client.SendAsync(Get("/api/auth/me", cookie));

        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        SignInHost.SetCookies(refused).Should().Contain(c => c.StartsWith("apiary-keeper-session=;", StringComparison.Ordinal));
        replayed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        A.CallTo(() => host.Keycloak.RefreshAsync(A<string>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact] // invariant: Keycloak unreachable past the access token's life refuses the request but keeps the session for later
    public async Task Session_ShouldRefuseTheRequestButSurvive_WhenKeycloakIsUnavailableAfterExpiry()
    {
        var clock = new SettableClock();
        using var host = new SignInHost(clock: clock);
        using var client = host.CreateClient();
        var cookie = await SignInHost.SignInSessionAsync(client, SignInHost.OwnerClaims());
        A.CallTo(() => host.Keycloak.RefreshAsync(A<string>._, A<CancellationToken>._))
            .Returns(new TokenRefresh.Unavailable("HttpRequestException")).Once()
            .Then.ReturnsLazily(() => new TokenRefresh.Renewed(host.IdToken(), "synthetic-refresh-2", TimeSpan.FromMinutes(5)));

        clock.Advance(TimeSpan.FromMinutes(5));
        using var whileDown = await client.SendAsync(Get("/api/auth/me", cookie));
        using var onceBack = await client.SendAsync(Get("/api/auth/me", cookie));

        whileDown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        SignInHost.SetCookies(whileDown).Should().NotContain(c => c.StartsWith("apiary-keeper-session=;", StringComparison.Ordinal));
        onceBack.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact] // invariant: sign-out ends Keycloak's session over the back channel with the session's current refresh token
    public async Task SignOut_ShouldEndKeycloaksSessionWithTheCurrentRefreshToken_WhenCalled()
    {
        var clock = new SettableClock();
        using var host = new SignInHost(clock: clock);
        using var client = host.CreateClient();
        var cookie = await SignInHost.SignInSessionAsync(client, SignInHost.OwnerClaims());
        A.CallTo(() => host.Keycloak.RefreshAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() => new TokenRefresh.Renewed(host.IdToken(), "synthetic-refresh-rotated", TimeSpan.FromMinutes(5)));
        clock.Advance(TimeSpan.FromMinutes(5));
        using var renewed = await client.SendAsync(Get("/api/auth/me", cookie));
        cookie = SignInHost.SessionCookie(renewed) ?? cookie;

        using var signOut = await client.SendAsync(Post("/api/auth/logout", cookie, csrf: true));

        signOut.StatusCode.Should().Be(HttpStatusCode.OK);
        A.CallTo(() => host.Keycloak.EndSessionAsync("synthetic-refresh-rotated", A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => host.Keycloak.EndSessionAsync(A<string>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact] // boundary: sign-out still ends the local session when Keycloak does not confirm its own
    public async Task SignOut_ShouldStillEndTheLocalSession_WhenKeycloakDoesNotConfirm()
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();
        var cookie = await SignInHost.SignInSessionAsync(client, SignInHost.OwnerClaims());
        A.CallTo(() => host.Keycloak.EndSessionAsync(A<string>._, A<CancellationToken>._)).Returns(false);

        using var signOut = await client.SendAsync(Post("/api/auth/logout", cookie, csrf: true));
        using var replayed = await client.SendAsync(Get("/api/auth/me", cookie));

        signOut.StatusCode.Should().Be(HttpStatusCode.OK);
        replayed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact] // boundary: a sign-out with no session (an agent's bearer, or nobody) asks nothing of Keycloak
    public async Task SignOut_ShouldNotCallKeycloak_WhenThereIsNoSession()
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        request.Headers.Authorization = SignInHost.Bearer(host.Token());

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        A.CallTo(() => host.Keycloak.EndSessionAsync(A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact] // invariant: a session cookie whose ticket names no server-side tokens (one from before the renewal) is not signed in
    public async Task Session_ShouldEnd_WhenItsTicketNamesNoTokens()
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();
        using var signIn = await client.PostAsync(
            $"{SignInHost.TestSignInPath}?unstamped=true&claims={Uri.EscapeDataString("sub=" + SignInHost.Subject + ";account_id=" + SignInHost.AccountId + ";roles=owner")}", null);
        var cookie = SignInHost.SessionCookie(signIn)!;

        using var me = await client.SendAsync(Get("/api/auth/me", cookie));

        me.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        SignInHost.SetCookies(me).Should().Contain(c => c.StartsWith("apiary-keeper-session=;", StringComparison.Ordinal));
    }

    [Fact] // invariant: a session ends after its idle timeout with no request
    public async Task Session_ShouldEnd_WhenIdleLongerThanTheIdleTimeout()
    {
        var clock = new SettableClock();
        using var host = new SignInHost(clock: clock, overrides: Lifetimes("01:00:00", "02:00:00"));
        using var client = host.CreateClient();
        var cookie = await SignInHost.SignInSessionAsync(client, SignInHost.OwnerClaims());

        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        using var me = await client.SendAsync(Get("/api/auth/me", cookie));

        me.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact] // invariant: an active session still ends at its maximum lifetime, however recently it slid
    public async Task Session_ShouldEndAtItsMaximumLifetime_WhenKeptActive()
    {
        var clock = new SettableClock();
        using var host = new SignInHost(clock: clock, overrides: Lifetimes("01:00:00", "02:00:00"));
        using var client = host.CreateClient();
        var cookie = await SignInHost.SignInSessionAsync(client, SignInHost.OwnerClaims());

        foreach (var minutes in new[] { 50, 50 })
        {
            clock.Advance(TimeSpan.FromMinutes(minutes));
            using var active = await client.SendAsync(Get("/api/auth/me", cookie));
            active.StatusCode.Should().Be(HttpStatusCode.OK, "the session slides while it is used");
            cookie = SignInHost.SessionCookie(active) ?? cookie;
        }

        clock.Advance(TimeSpan.FromMinutes(20));
        using var ended = await client.SendAsync(Get("/api/auth/me", cookie));

        ended.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact] // invariant (D-19): the key ring goes to PostgreSQL's store encrypted with the certificate, never in the clear
    public async Task KeyRing_ShouldBeStoredEncryptedWithTheCertificate_WhenAKeyIsCreated()
    {
        using var host = new SignInHost();
        using var client = host.CreateClient();

        await SignInHost.SignInSessionAsync(client, SignInHost.OwnerClaims());

        host.KeyRing.Should().NotBeEmpty();
        host.KeyRing.Should().OnlyContain(k =>
            k.Xml.Contains("<encryptedSecret", StringComparison.Ordinal)
            && k.Xml.Contains("EncryptedData", StringComparison.Ordinal)
            && !k.Xml.Contains("<value>", StringComparison.Ordinal));
    }

    [Fact] // regression risk: another instance with the same store and certificate reads what this one protected (a restart)
    public async Task KeyRing_ShouldUnprotectAcrossInstances_WhenTheyShareTheStoreAndTheCertificate()
    {
        var certificate = SyntheticCertificate.Pkcs12Base64();
        var keyRing = new List<(string? Name, string Xml)>();
        string protectedText;
        using (var first = new SignInHost(keyRing: keyRing, certificate: certificate))
        {
            protectedText = Protector(first).Protect("synthetic payload");
        }

        using var second = new SignInHost(keyRing: keyRing, certificate: certificate);

        Protector(second).Unprotect(protectedText).Should().Be("synthetic payload");
    }

    [Fact] // invariant: without the certificate the stored key ring is unreadable, so a copy of the table alone is no use
    public async Task KeyRing_ShouldNotUnprotect_WhenAnotherCertificateIsConfigured()
    {
        var keyRing = new List<(string? Name, string Xml)>();
        string protectedText;
        using (var first = new SignInHost(keyRing: keyRing))
        {
            protectedText = Protector(first).Protect("synthetic payload");
        }

        using var second = new SignInHost(keyRing: keyRing, certificate: SyntheticCertificate.Pkcs12Base64());

        var act = () => Protector(second).Unprotect(protectedText);
        act.Should().Throw<CryptographicException>();
        await Task.CompletedTask;
    }

    [Theory] // invariant: sign-in configured without what it needs stops the host at startup, naming the setting
    [InlineData("DataProtection:Certificate", "DataProtection__Certificate")]
    [InlineData("ConnectionStrings:ApiaryCopilot", "ConnectionStrings__ApiaryCopilot")]
    [InlineData("SignIn:Audience", "SignIn__Audience")]
    public void Host_ShouldFailToStartNamingTheSetting_WhenARequiredSettingIsMissing(string setting, string named)
    {
        using var host = new SignInHost(overrides: new Dictionary<string, string?> { [setting] = "" });

        var act = () => host.CreateClient();

        act.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain(named);
    }

    private static IDataProtector Protector(SignInHost host)
    {
        using var client = host.CreateClient();
        return host.Factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("unit-test");
    }

    private static Dictionary<string, string?> Lifetimes(string idle, string max) => new()
    {
        ["SignIn:SessionIdleTimeout"] = idle,
        ["SignIn:SessionMaxLifetime"] = max,
    };

    private static HttpRequestMessage Get(string path, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return request;
    }

    private static HttpRequestMessage Post(string path, string? cookie, bool csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        if (csrf)
        {
            request.Headers.Add(CsrfHeaderCheck.HeaderName, CsrfHeaderCheck.HeaderValue);
        }

        return request;
    }

    // A yard write with a body the parser refuses (400): reaching that 400 shows nothing stopped the write before it.
    private static HttpRequestMessage Write(string? cookie, bool csrf)
    {
        var request = Post("/api/yards", cookie, csrf);
        request.Content = new StringContent(WriteWithInvalidBody, Encoding.UTF8, "application/json");
        return request;
    }

    private static void Behind(HttpContext context, string peer, string path)
    {
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Method = "GET";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("app.internal");
        context.Request.Path = path;
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.7";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Headers["X-Forwarded-Host"] = "apiary-keeper.example.test";
    }

    private static string Unsigned(string jwt)
    {
        var parts = jwt.Split('.');
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        return $"{header}.{parts[1]}.";
    }

    private static string Altered(string jwt)
    {
        var parts = jwt.Split('.');
        var payload = Base64UrlEncoder.Decode(parts[1]).Replace(SignInHost.AccountId, "c057e0a1-5747-4e00-8000-000000000071", StringComparison.Ordinal);
        return $"{parts[0]}.{Base64UrlEncoder.Encode(payload)}.{parts[2]}";
    }

    private sealed class SettableClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
