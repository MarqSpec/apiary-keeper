using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using ApiaryCopilot.Api.SignIn;
using ApiaryCopilot.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ApiaryCopilot.UnitTests.Api.SignIn;

// The real API in memory with sign-in configured. Keycloak's metadata and signing key are supplied statically, so
// nothing is fetched; the key ring's store is an in-memory list; tokens are signed here with a key made per test.
// /test/sign-in signs a principal into the cookie session as the OpenID Connect callback does, without Keycloak, and
// Keycloak's back channel (refresh and sign-out) is faked.
internal sealed class SignInHost : IDisposable
{
    public const string Authority = "https://keycloak.unit.test/realms/apiary-keeper";
    public const string ClientId = "apiary-keeper-bff";
    public const string Audience = "apiary-keeper-api";
    public const string AuthorizationEndpoint = Authority + "/protocol/openid-connect/auth";
    public const string EndSessionEndpoint = Authority + "/protocol/openid-connect/logout";
    public const string TestSignInPath = "/test/sign-in";
    public const string Subject = "4e97dd9a-9f79-4493-8185-795ac0034b7d";
    public const string AccountId = "a91a7c0e-5747-4e00-8000-000000000019";
    public const string KeyId = "unit-test-signing-key";

    private readonly RSA _signingKey = RSA.Create(2048);

    public SignInHost(
        bool requireHttps = false,
        IReadOnlyDictionary<string, string?>? overrides = null,
        List<(string? Name, string Xml)>? keyRing = null,
        string? certificate = null,
        TimeProvider? clock = null,
        string bearerMetadataIssuer = Authority)
    {
        KeyRing = keyRing ?? [];
        // Keycloak's back channel, faked: by default it renews a session with an ID token for the claims the session
        // started with, and confirms a sign-out. A test replaces either answer.
        A.CallTo(() => Keycloak.RefreshAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() => new TokenRefresh.Renewed(IdToken(), $"synthetic-refresh-{Guid.NewGuid():N}", TimeSpan.FromMinutes(5)));
        A.CallTo(() => Keycloak.EndSessionAsync(A<string>._, A<CancellationToken>._)).Returns(true);
        var settings = new Dictionary<string, string?>
        {
            ["SignIn:Authority"] = Authority,
            ["SignIn:ClientId"] = ClientId,
            ["SignIn:ClientSecret"] = "synthetic-unit-test-client-secret",
            ["SignIn:Audience"] = Audience,
            ["SignIn:RequireHttps"] = requireHttps ? "true" : "false",
            ["DataProtection:Certificate"] = certificate ?? SyntheticCertificate.Pkcs12Base64(),
            ["ConnectionStrings:ApiaryCopilot"] = "Host=localhost;Database=apiary_copilot_unit;Username=synthetic",
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        Factory = new ApiFactory(settings: settings, services: s =>
        {
            var store = A.Fake<IDataProtectionKeyStore>();
            A.CallTo(() => store.GetAll()).ReturnsLazily(() => KeyRing.Select(k => k.Xml).ToList());
            A.CallTo(() => store.Add(A<string?>._, A<string>.That.IsNotNull())).Invokes((string? name, string xml) => KeyRing.Add((name, xml)));
            s.AddSingleton(store);
            if (clock is not null)
            {
                s.AddSingleton(clock);
            }

            s.AddSingleton(Keycloak);
            s.Configure<OpenIdConnectOptions>(SignInExtensions.OpenIdConnectScheme, o =>
            {
                var configuration = new OpenIdConnectConfiguration
                {
                    Issuer = Authority,
                    AuthorizationEndpoint = AuthorizationEndpoint,
                    TokenEndpoint = Authority + "/protocol/openid-connect/token",
                    EndSessionEndpoint = EndSessionEndpoint,
                };
                configuration.SigningKeys.Add(new RsaSecurityKey(_signingKey) { KeyId = KeyId });
                o.Configuration = configuration;
            });
            s.Configure<JwtBearerOptions>(SignInExtensions.BearerScheme, o =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = bearerMetadataIssuer };
                configuration.SigningKeys.Add(new RsaSecurityKey(_signingKey) { KeyId = KeyId });
                o.Configuration = configuration;
            });
            s.AddSingleton<IStartupFilter, TestSignInStartupFilter>();
        });
    }

    public ApiFactory Factory { get; }

    /// <summary>Keycloak's back channel as the host sees it: the refresh grant and the back-channel sign-out.</summary>
    public IKeycloakTokens Keycloak { get; } = A.Fake<IKeycloakTokens>();

    /// <summary>The key ring's rows, as the store holds them.</summary>
    public List<(string? Name, string Xml)> KeyRing { get; }

    public HttpClient CreateClient() =>
        Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    /// <summary>Signs a session in for the claims and returns the session cookie's name=value.</summary>
    public static async Task<string> SignInSessionAsync(HttpClient client, params Claim[] tokenClaims)
    {
        using var response = await client.PostAsync(
            $"{TestSignInPath}?claims={Uri.EscapeDataString(string.Join(';', tokenClaims.Select(c => $"{c.Type}={c.Value}")))}", null);
        response.EnsureSuccessStatusCode();
        return SessionCookie(response) ?? throw new InvalidOperationException("No session cookie was set.");
    }

    /// <summary>The session cookie's name=value from a response's Set-Cookie, or null.</summary>
    public static string? SessionCookie(HttpResponseMessage response) =>
        SetCookies(response).Select(c => c.Split(';')[0])
            .FirstOrDefault(c => c.StartsWith("apiary-keeper-session=", StringComparison.Ordinal) || c.StartsWith("__Host-apiary-keeper-session=", StringComparison.Ordinal));

    public static IEnumerable<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];

    public static Claim[] OwnerClaims() =>
        [new Claim("sub", Subject), new Claim("account_id", AccountId), new Claim("roles", "owner")];

    /// <summary>A token signed with this host's key, or with <paramref name="signingKey"/>.</summary>
    public string Token(
        string? audience = Audience,
        string issuer = Authority,
        DateTime? expires = null,
        DateTime? notBefore = null,
        Dictionary<string, object>? claims = null,
        SigningCredentials? signingKey = null)
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = notBefore ?? now.AddMinutes(-1),
            NotBefore = notBefore ?? now.AddMinutes(-1),
            Expires = expires ?? now.AddMinutes(5),
            Claims = claims ?? new Dictionary<string, object>
            {
                ["sub"] = Subject,
                ["account_id"] = AccountId,
                ["roles"] = new[] { "owner", "offline_access" },
                ["email"] = "synthetic-owner@apiary-keeper.test",
                ["typ"] = "Bearer",
            },
            SigningCredentials = signingKey ?? new SigningCredentials(new RsaSecurityKey(_signingKey) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

    /// <summary>
    /// An ID token for the BFF's client, as a renewal returns it, for the given role and account; dated by the real clock,
    /// which is what its validation reads.
    /// </summary>
    public string IdToken(string role = "owner", string accountId = AccountId, string subject = Subject) =>
        Token(
            audience: ClientId,
            claims: new Dictionary<string, object> { ["sub"] = subject, ["account_id"] = accountId, ["roles"] = new[] { role }, ["typ"] = "ID" });

    public static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);

    /// <summary>This host's own signing key - the one Keycloak's metadata publishes - under another algorithm.</summary>
    public SigningCredentials OwnKeyWith(string algorithm) =>
        new(new RsaSecurityKey(_signingKey) { KeyId = KeyId }, algorithm);

    public void Dispose()
    {
        Factory.Dispose();
        _signingKey.Dispose();
    }

    // Signs the claims given as type=value pairs into the cookie session through the same principal builder the
    // OpenID Connect callback uses, with a synthetic refresh token held as the callback holds Keycloak's.
    private sealed class TestSignInStartupFilter : IStartupFilter
    {
        public const string RefreshToken = "synthetic-refresh-token-at-sign-in";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Map(TestSignInPath, branch => branch.Run(async context =>
            {
                var claims = context.Request.Query["claims"].ToString().Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(pair => pair.Split('=', 2))
                    .Select(pair => new Claim(pair[0], pair[1]));
                var principal = ApiaryClaims.FromToken(claims) ?? throw new InvalidOperationException("Not a valid principal.");
                var now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
                var sessionId = SessionTokens.NewSessionId();
                await context.RequestServices.GetRequiredService<ISessionTokenStore>().SetAsync(
                    sessionId, SessionTokens.Begin(principal, RefreshToken, now + TimeSpan.FromMinutes(5), now), CancellationToken.None);
                var properties = new AuthenticationProperties();
                // ?unstamped=true signs in a session that names no tokens, as one from before the renewal would.
                if (context.Request.Query["unstamped"] != "true")
                {
                    SessionTokens.Stamp(properties, sessionId, generation: 0);
                }

                await context.SignInAsync(SignInExtensions.CookieScheme, principal, properties);
            }));
            next(app);
        };
    }
}
