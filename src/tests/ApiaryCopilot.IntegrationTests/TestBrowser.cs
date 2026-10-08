using System.Net;
using System.Text.RegularExpressions;

namespace ApiaryCopilot.IntegrationTests;

// A browser as far as sign-in needs one: a cookie jar and redirects followed by hand, so each hop can be checked. It
// fills in Keycloak's own login form, as a person would; nothing here uses a direct-access grant. Every response the API
// sent is kept, so a test can check that no token ever reached the browser. Like a browser, it treats http on loopback
// as a secure context, so it keeps and sends the Secure cookies Keycloak sets there; .NET's own cookie handling would not.
internal sealed partial class TestBrowser : IDisposable
{
    private readonly HttpClient _http;

    public TestBrowser(Uri api)
    {
        Api = api;
        _http = new HttpClient(new LoopbackCookies(Cookies))
        {
            // Bounded like the server fixture's client, with room for Keycloak's pages on a starved runner: a hang still
            // fails the test ().
            Timeout = AppServerFixture.RequestTimeout * 2,
        };
    }

    public Uri Api { get; }

    public CookieContainer Cookies { get; } = new();

    /// <summary>What the API sent the browser during sign-in: status, headers and body, one entry per response.</summary>
    public List<string> ApiResponses { get; } = [];

    /// <summary>The Keycloak authorization request the API redirected to.</summary>
    public Uri? AuthorizationRequest { get; private set; }

    /// <summary>The cookies the browser would send to the API's paths.</summary>
    public IReadOnlyList<Cookie> ApiCookies => Cookies.GetCookies(LoopbackCookies.AsSecureContext(new Uri(Api, "/api/auth/me"))).ToList();

    /// <summary>
    /// Signs in through the API: /api/auth/login, Keycloak's login form, then the callback. Returns the callback's
    /// response, the API's last word in the flow.
    /// </summary>
    public async Task<HttpResponseMessage> SignInAsync(string username, string password, string returnUrl = "/")
    {
        using var login = await GetAsync(new Uri(Api, $"/api/auth/login?returnUrl={Uri.EscapeDataString(returnUrl)}"));
        await KeepAsync(login);
        login.StatusCode.Should().Be(HttpStatusCode.Redirect, "the API sends the browser to Keycloak");
        AuthorizationRequest = login.Headers.Location!;

        // Keycloak may redirect within itself (to login-actions) before it shows the form, as it would for a browser.
        var page = await GetAsync(AuthorizationRequest);
        for (var hops = 0; page.StatusCode == HttpStatusCode.Found && hops < 5
             && page.Headers.Location is { } next && next.IsAbsoluteUri && next.Authority == AuthorizationRequest.Authority; hops++)
        {
            page.Dispose();
            page = await GetAsync(next);
        }

        using var form = page;
        form.StatusCode.Should().Be(HttpStatusCode.OK, $"Keycloak shows its login form (it answered {form.Headers.Location})");
        var html = await form.Content.ReadAsStringAsync();
        var action = WebUtility.HtmlDecode(LoginFormAction().Match(html).Groups["action"].Value);
        action.Should().NotBeNullOrEmpty("Keycloak's login form has an action");

        using var credentials = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = username, ["password"] = password });
        using var submitted = await _http.PostAsync(new Uri(action), credentials);
        submitted.StatusCode.Should().Be(HttpStatusCode.Found, "Keycloak returns the browser to the API with a code");
        var callback = submitted.Headers.Location!;
        callback.GetLeftPart(UriPartial.Path).Should().Be(new Uri(Api, "/api/auth/callback").ToString());

        var response = await GetAsync(callback);
        await KeepAsync(response);
        return response;
    }

    public Task<HttpResponseMessage> GetAsync(Uri uri) => _http.GetAsync(uri);

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => _http.SendAsync(request);

    public void Dispose() => _http.Dispose();

    private async Task KeepAsync(HttpResponseMessage response) =>
        ApiResponses.Add($"{(int)response.StatusCode} {response.Headers} {response.Content.Headers} {await response.Content.ReadAsStringAsync()}");

    // A cookie jar that keeps and sends cookies as a browser does on loopback, where http is a secure context.
    private sealed class LoopbackCookies(CookieContainer jar) : DelegatingHandler(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
    {
        public static Uri AsSecureContext(Uri uri) =>
            uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp ? new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps }.Uri : uri;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var context = AsSecureContext(request.RequestUri!);
            var cookies = jar.GetCookieHeader(context);
            if (cookies.Length > 0)
            {
                request.Headers.Add("Cookie", cookies);
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var setCookie in setCookies)
                {
                    jar.SetCookies(context, setCookie);
                }
            }

            return response;
        }
    }

    [GeneratedRegex("""<form[^>]*\bid="kc-form-login"[^>]*\baction="(?<action>[^"]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex LoginFormAction();
}
