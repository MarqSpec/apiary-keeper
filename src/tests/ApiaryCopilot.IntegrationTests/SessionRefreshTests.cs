using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ApiaryCopilot.IntegrationTests;

// The renewal written in the API against the seeded realm (D-19): a browser session is renewed with its server-held
// refresh token as its access token nears expiry, so a role or account change made in Keycloak reaches the session at
// the next renewal and not before (#5: a refresh re-reads the roles and attributes), a user disabled in Keycloak
// is refused at the next renewal, concurrent requests at the renewal share one rotation, and sign-out ends Keycloak's own
// session over the back channel. Each test makes its own user through the admin API, so nothing it changes reaches
// another; the BFF client's access-token lifespan is cut to AccessTokenLifespan for this Keycloak only, so a renewal
// comes in seconds. Deployment=None: Keycloak, PostgreSQL and MinIO are loopback containers. Runs alone (KeycloakAlone).
[Trait("Deployment", "None")]
[Collection(KeycloakAlone.Name)]
public sealed class SessionRefreshTests(SignInServerFixture fixture) : IClassFixture<SignInServerFixture>, IAsyncLifetime
{
    // The API renews 30 s before the access token expires (SessionRefresher.RefreshMargin), so a session made now is due
    // AccessTokenLifespan - 30 s from now: long enough to see the claims held before it, short enough to wait for.
    private static readonly TimeSpan AccessTokenLifespan = TimeSpan.FromSeconds(50);
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(30);
    private const string SeedAccountId = "a91a7c0e-5747-4e00-8000-000000000019";
    private const string OtherAccountId = "c057e0a1-5747-4e00-8000-000000000071";

    public Task InitializeAsync() => fixture.Keycloak.SetAccessTokenLifespanAsync(TestKeycloak.BffClientId, AccessTokenLifespan);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact] // invariant (#5): a role or account change reaches the session at the next renewal, and not before
    public async Task Session_ShouldTakeANewRoleAndAccount_AtTheNextRenewal()
    {
        var user = await fixture.Keycloak.CreateUserAsync("helper", SeedAccountId);
        var (browser, due) = await SignInAsync(user);
        using var _ = browser;
        var before = await MeAsync(browser);

        await fixture.Keycloak.RemoveRealmRoleAsync(user.Id, "helper");
        await fixture.Keycloak.AddRealmRoleAsync(user.Id, "owner");
        await fixture.Keycloak.SetAccountAsync(user.Id, OtherAccountId);
        var notYet = await MeAsync(browser);
        await WaitUntilAsync(due);
        var renewed = await MeAsync(browser);

        Roles(before).Should().Equal("helper");
        Roles(notYet).Should().Equal(["helper"], "the session holds its claims until the renewal; nothing is looked up per request (D-22)");
        notYet.GetProperty("accountId").GetString().Should().Be(SeedAccountId);
        Roles(renewed).Should().Equal("owner");
        renewed.GetProperty("accountId").GetString().Should().Be(OtherAccountId);
        renewed.GetProperty("sub").GetString().Should().Be(user.Id);
    }

    [Fact] // invariant (D-19): a user disabled in Keycloak is refused at the next renewal, and the session is over
    public async Task Session_ShouldBeRefused_WhenTheUserIsDisabledAndTheRenewalComes()
    {
        var user = await fixture.Keycloak.CreateUserAsync("owner", SeedAccountId);
        var (browser, due) = await SignInAsync(user);
        using var _ = browser;

        await fixture.Keycloak.DisableUserAsync(user.Id);
        using var beforeRenewal = await browser.GetAsync(new Uri(fixture.Address, "/api/auth/me"));
        await WaitUntilAsync(due);
        using var atRenewal = await browser.GetAsync(new Uri(fixture.Address, "/api/auth/me"));
        using var afterwards = await browser.GetAsync(new Uri(fixture.Address, "/api/auth/me"));

        beforeRenewal.StatusCode.Should().Be(HttpStatusCode.OK, "until the renewal the session is what Keycloak signed");
        atRenewal.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        browser.ApiCookies.Should().BeEmpty("the session cookie is cleared");
        afterwards.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact] // regression risk (rotation on, reuse 0): concurrent requests at the renewal must not present one refresh token twice
    public async Task Session_ShouldSurviveTheRenewal_WhenRequestsArriveTogether()
    {
        var user = await fixture.Keycloak.CreateUserAsync("owner", SeedAccountId);
        var (browser, due) = await SignInAsync(user);
        using var _ = browser;

        await WaitUntilAsync(due);
        var together = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => browser.GetAsync(new Uri(fixture.Address, "/api/auth/me"))));
        using var after = await browser.GetAsync(new Uri(fixture.Address, "/api/auth/me"));

        together.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.OK);
        after.StatusCode.Should().Be(HttpStatusCode.OK, "a spent refresh token would have ended the session");
        foreach (var response in together)
        {
            response.Dispose();
        }
    }

    [Fact] // invariant: sign-out ends Keycloak's own session over the back channel, not only the API's
    public async Task SignOut_ShouldEndKeycloaksSession_WhenTheBrowserSignsOut()
    {
        var user = await fixture.Keycloak.CreateUserAsync("owner", SeedAccountId);
        var (browser, _) = await SignInAsync(user);
        using var signedIn = browser;
        var sessionsBefore = await fixture.Keycloak.SessionCountAsync(user.Id);

        using var signOut = new HttpRequestMessage(HttpMethod.Post, new Uri(fixture.Address, "/api/auth/logout"));
        signOut.Headers.Add("X-CSRF", "1");
        using var signedOut = await browser.SendAsync(signOut);

        sessionsBefore.Should().BePositive();
        signedOut.StatusCode.Should().Be(HttpStatusCode.OK);
        (await fixture.Keycloak.SessionCountAsync(user.Id)).Should().Be(0, "Keycloak's session ended without the browser visiting its sign-out page");
    }

    private async Task<(TestBrowser Browser, DateTimeOffset Due)> SignInAsync(CreatedUser user)
    {
        var browser = new TestBrowser(fixture.Address);
        try
        {
            using var callback = await browser.SignInAsync(user.Username, user.Password);
            callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
            // The server stamps the access token's expiry at the callback, which has answered by now: measured from after it,
            // plus a second, the wait is never short of the server's own due time.
            var signedIn = DateTimeOffset.UtcNow;
            return (browser, signedIn + AccessTokenLifespan - RefreshMargin + TimeSpan.FromSeconds(1));
        }
        catch
        {
            browser.Dispose();
            throw;
        }
    }

    // The renewal is due at a known instant: wait for it once, rather than retrying an assertion until it passes.
    private static async Task WaitUntilAsync(DateTimeOffset due)
    {
        var wait = due - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
        }
    }

    private async Task<JsonElement> MeAsync(TestBrowser browser)
    {
        using var me = await browser.GetAsync(new Uri(fixture.Address, "/api/auth/me"));
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        return await me.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<string?> Roles(JsonElement user) => user.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();
}
