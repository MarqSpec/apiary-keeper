using System.Security.Cryptography.X509Certificates;
using ApiaryCopilot.Api.Accounts;
using ApiaryCopilot.Api.Problems;
using ApiaryCopilot.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ApiaryCopilot.Api.SignIn;

/// <summary>
/// The BFF in the API (ARCHITECTURE.md D-19 Option A): a browser signs in through Keycloak with the authorization code
/// and PKCE, as the confidential client, and holds only a cookie carrying a session id; an agent calls with a Keycloak
/// bearer token. A policy scheme picks the path per request, and both yield the same <see cref="ApiaryClaims"/> principal.
/// The session's refresh token is kept server-side (<see cref="ISessionTokenStore"/>) and renewed as the cookie is
/// validated (<see cref="SessionRefresher"/>); sign-out ends Keycloak's session over the back channel with it.
/// <para>
/// The apiary endpoints are authorized from that principal (<see cref="Accounts.AccountMemberAuthorization"/>);
/// there is no fallback policy, so a route that names no policy - <c>/health</c>, the PWA's files, sign-in itself - stays
/// anonymous.
/// </para>
/// </summary>
public static class SignInExtensions
{
    /// <summary>The policy scheme: the bearer path when the request has a bearer token, otherwise the cookie.</summary>
    public const string PolicyScheme = "ApiaryKeeper";

    /// <summary>The cookie session's scheme.</summary>
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;

    /// <summary>The OpenID Connect sign-in's scheme.</summary>
    public const string OpenIdConnectScheme = OpenIdConnectDefaults.AuthenticationScheme;

    /// <summary>The bearer path's scheme.</summary>
    public const string BearerScheme = JwtBearerDefaults.AuthenticationScheme;

    /// <summary>The Data Protection application name, so the key ring is this app's wherever it runs.</summary>
    public const string ApplicationName = "apiary-keeper";

    // Keycloak signs with RS256 by default; nothing else is accepted, so no algorithm can be swapped in.
    private static readonly string[] SigningAlgorithms = [SecurityAlgorithms.RsaSha256];

    /// <summary>
    /// How a refreshed ID token is validated - as the sign-in's is: the configured issuer, this client as the audience, a
    /// lifetime within the clock skew, signed, RS256 only. The caller supplies Keycloak's published signing keys.
    /// </summary>
    public static TokenValidationParameters IdTokenValidation(SignInOptions signIn)
    {
        ArgumentNullException.ThrowIfNull(signIn);
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = signIn.Issuer,
            ValidateAudience = true,
            ValidAudience = signIn.ClientId,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            ValidAlgorithms = SigningAlgorithms,
            ClockSkew = signIn.ClockSkew,
            IssuerValidator = PinnedIssuer(signIn.Issuer),
        };
    }

    // ValidIssuer alone does not pin: both handlers also accept the issuer their discovery document names. This validator
    // takes the configured authority and nothing else, so a metadata address pointing at another realm cannot widen it.
    private static IssuerValidator PinnedIssuer(string issuer) => (tokenIssuer, _, _) =>
        string.Equals(tokenIssuer, issuer, StringComparison.Ordinal)
            ? tokenIssuer
            : throw new SecurityTokenInvalidIssuerException("The token's issuer is not the configured authority.") { InvalidIssuer = tokenIssuer };

    /// <summary>
    /// Registers sign-in when <c>SignIn__Authority</c> is set, and nothing when it is not. Set, every
    /// <c>SignIn__</c> setting, the key ring certificate and the connection string are checked now, so a bad setting
    /// stops the host at startup naming the setting and never its value.
    /// </summary>
    /// <exception cref="OptionsValidationException">A setting is missing or malformed.</exception>
    public static IServiceCollection AddSignIn(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SignInOptions.SectionName);
        var signIn = section.Get<SignInOptions>() ?? new SignInOptions();
        var keyRing = configuration.GetSection(KeyRingOptions.SectionName).Get<KeyRingOptions>() ?? new KeyRingOptions();
        var failures = signIn.Validate().ToList();
        if (signIn.IsConfigured)
        {
            failures.AddRange(keyRing.Validate());
            if (string.IsNullOrWhiteSpace(configuration.GetConnectionString(InfrastructureServiceCollectionExtensions.ConnectionStringName)))
            {
                failures.Add($"ConnectionStrings__{InfrastructureServiceCollectionExtensions.ConnectionStringName} must be set when sign-in is: the Data Protection key ring is kept in PostgreSQL.");
            }
        }

        if (failures.Count > 0)
        {
            throw new OptionsValidationException(SignInOptions.SectionName, typeof(SignInOptions), failures);
        }

        services.AddOptions<SignInOptions>().Bind(section);
        if (!signIn.IsConfigured)
        {
            return services;
        }

        services.AddSingleton(new SignInRegistration(signIn.SessionCookieName));
        services.AddKeyRing(keyRing.LoadCertificate());
        services.AddDistributedMemoryCache();
        services.AddSingleton<ITicketStore, DistributedCacheTicketStore>();
        // The renewal: the refresh token held server-side beside the session, never in the cookie.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISessionTokenStore, DistributedCacheSessionTokenStore>();
        services.AddSingleton<IKeycloakTokens, KeycloakTokenClient>();
        services.AddSingleton<IIdTokenValidator, KeycloakIdTokenValidator>();
        services.AddSingleton<SessionRefresher>();

        var cookieSecurity = signIn.RequireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        services
            .AddAuthentication(o =>
            {
                o.DefaultScheme = PolicyScheme;
                o.DefaultChallengeScheme = PolicyScheme;
                o.DefaultSignInScheme = CookieScheme;
                o.DefaultSignOutScheme = CookieScheme;
            })
            .AddPolicyScheme(PolicyScheme, "Session cookie or bearer token", o => o.ForwardDefaultSelector = SelectScheme)
            .AddCookie(CookieScheme, o => ConfigureCookie(o, signIn, cookieSecurity))
            .AddOpenIdConnect(OpenIdConnectScheme, o => ConfigureOpenIdConnect(o, signIn, cookieSecurity))
            .AddJwtBearer(BearerScheme, o => ConfigureBearer(o, signIn));
        services.AddOptions<CookieAuthenticationOptions>(CookieScheme)
            .Configure<ITicketStore>((o, store) => o.SessionStore = store);
        services.AddAuthorization(o => o.AddAccountMemberPolicies());
        return services;
    }

    /// <summary>Authenticates each request, applies the CSRF check and authorizes the sign-in routes; does nothing when sign-in is off.</summary>
    public static WebApplication UseSignIn(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Services.GetService<SignInRegistration>() is not { } registration)
        {
            return app;
        }

        app.UseAuthentication();
        app.UseCsrfHeaderCheck(registration.SessionCookieName);
        app.UseAuthorization();
        return app;
    }

    /// <summary>The scheme a request is authenticated by: a bearer token's when it sends one, otherwise the session cookie's.</summary>
    public static string SelectScheme(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var authorization = context.Request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? BearerScheme : CookieScheme;
    }

    private static void AddKeyRing(this IServiceCollection services, X509Certificate2 certificate)
    {
        // The default key ring is unencrypted (D-19): every key is encrypted with the certificate before it is stored.
        services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .ProtectKeysWithCertificate(certificate)
            .UnprotectKeysWithAnyCertificate(certificate);
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(provider =>
            new ConfigureOptions<KeyManagementOptions>(o =>
                o.XmlRepository = new KeyRingRepository(provider.GetRequiredService<IDataProtectionKeyStore>())));
    }

    private static void ConfigureCookie(CookieAuthenticationOptions o, SignInOptions signIn, CookieSecurePolicy security)
    {
        o.Cookie.Name = signIn.SessionCookieName;
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = security;
        o.Cookie.Path = "/";
        o.ExpireTimeSpan = signIn.SessionIdleTimeout;
        o.SlidingExpiration = true;
        // An API: an unauthenticated or refused call is a status, never a redirect to a login page.
        o.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
        o.Events.OnSigningIn = context =>
        {
            SessionLifetime.Stamp(context.Properties, context.Options.TimeProvider!.GetUtcNow());
            return Task.CompletedTask;
        };
        o.Events.OnValidatePrincipal = ValidateSessionAsync;

        async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
        {
            var services = context.HttpContext.RequestServices;
            var sessionId = SessionTokens.SessionIdOf(context.Properties);
            if (SessionLifetime.HasEnded(context.Properties, context.Options.TimeProvider!.GetUtcNow(), signIn.SessionMaxLifetime))
            {
                if (sessionId is not null)
                {
                    await services.GetRequiredService<ISessionTokenStore>().RemoveAsync(sessionId, CancellationToken.None);
                }

                await EndAsync(context);
                return;
            }

            // A session with nothing to renew with is over: fail closed rather than serve claims nothing can refresh.
            if (sessionId is null)
            {
                await EndAsync(context);
                return;
            }

            var check = await services.GetRequiredService<SessionRefresher>().CheckAsync(sessionId, SessionTokens.GenerationOf(context.Properties));
            switch (check)
            {
                case SessionCheck.Keep:
                    break;
                case SessionCheck.Replace replace:
                    // The properties are the ticket's own, so the renewed ticket carries the new generation.
                    SessionTokens.Stamp(context.Properties, sessionId, replace.Generation);
                    context.ReplacePrincipal(replace.Principal);
                    context.ShouldRenew = true;
                    break;
                case SessionCheck.Ended:
                    await EndAsync(context);
                    break;
                case SessionCheck.Deny:
                    context.RejectPrincipal();
                    break;
                default:
                    throw new InvalidOperationException("Unhandled session check.");
            }
        }

        static async Task EndAsync(CookieValidatePrincipalContext context)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieScheme);
        }
    }

    private static void ConfigureOpenIdConnect(OpenIdConnectOptions o, SignInOptions signIn, CookieSecurePolicy security)
    {
        o.Authority = signIn.Issuer;
        if (MetadataAddressOf(signIn) is { } metadataAddress)
        {
            o.MetadataAddress = metadataAddress;
        }

        o.ClientId = signIn.ClientId;
        o.ClientSecret = signIn.ClientSecret;
        o.RequireHttpsMetadata = signIn.RequireHttps;
        o.ResponseType = OpenIdConnectResponseType.Code;
        o.UsePkce = true;
        // The code comes back on a top-level GET, so the correlation and nonce cookies can be SameSite=Lax.
        o.ResponseMode = OpenIdConnectResponseMode.Query;
        o.CallbackPath = SignInEndpoints.CallbackPath;
        o.SignInScheme = CookieScheme;
        o.Scope.Clear();
        o.Scope.Add(OpenIdConnectScope.OpenId);
        // No token in the ticket: the session holds the principal's claims; the refresh token alone is kept,
        // server-side in the session's tokens (OnTokenValidated, below), and no ID or access token at all.
        o.SaveTokens = false;
        o.GetClaimsFromUserInfoEndpoint = false;
        o.MapInboundClaims = false;
        o.DisableTelemetry = true;
        // The issuer is the configured authority, never whatever a discovery document fetched elsewhere names.
        o.TokenValidationParameters.ValidateIssuer = true;
        o.TokenValidationParameters.ValidIssuer = signIn.Issuer;
        o.TokenValidationParameters.IssuerValidator = PinnedIssuer(signIn.Issuer);
        o.TokenValidationParameters.ValidAlgorithms = SigningAlgorithms;
        o.TokenValidationParameters.ClockSkew = signIn.ClockSkew;
        foreach (var cookie in new[] { o.CorrelationCookie, o.NonceCookie })
        {
            cookie.SameSite = SameSiteMode.Lax;
            cookie.SecurePolicy = security;
            cookie.HttpOnly = true;
        }

        o.Events.OnTokenValidated = async context =>
        {
            if (ApiaryClaims.FromIdToken(context.Principal!.Claims) is not { } principal)
            {
                context.Fail("The ID token is not an ID token, or carries no subject or no single account.");
                return;
            }

            // The session cannot be renewed without a refresh token, so a sign-in that returned none fails closed.
            var response = context.TokenEndpointResponse;
            if (SessionTokens.FromSignIn(principal, response?.RefreshToken, response?.ExpiresIn, context.Options.TimeProvider!.GetUtcNow()) is not { } tokens)
            {
                context.Fail("Keycloak returned no refresh token or no access token lifetime.");
                return;
            }

            var sessionId = SessionTokens.NewSessionId();
            await context.HttpContext.RequestServices.GetRequiredService<ISessionTokenStore>().SetAsync(
                sessionId, tokens, context.HttpContext.RequestAborted);
            SessionTokens.Stamp(context.Properties!, sessionId, generation: 0);
            context.Principal = principal;
        };
        o.Events.OnRemoteFailure = async context =>
        {
            context.HandleResponse();
            await ProblemResults.NotAllowed("sign-in did not complete; start it again from the app.").ExecuteAsync(context.HttpContext);
        };
    }

    private static void ConfigureBearer(JwtBearerOptions o, SignInOptions signIn)
    {
        o.Authority = signIn.Issuer;
        if (MetadataAddressOf(signIn) is { } metadataAddress)
        {
            o.MetadataAddress = metadataAddress;
        }

        o.Audience = signIn.Audience;
        o.RequireHttpsMetadata = signIn.RequireHttps;
        o.MapInboundClaims = false;
        o.SaveToken = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = signIn.Issuer,
            ValidateAudience = true,
            ValidAudience = signIn.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            ValidAlgorithms = SigningAlgorithms,
            ClockSkew = signIn.ClockSkew,
            IssuerValidator = PinnedIssuer(signIn.Issuer),
        };
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                // An access token only (typ Bearer): a Keycloak ID token never passes for one, whatever its audience.
                if (ApiaryClaims.FromAccessToken(context.Principal!.Claims) is { } principal)
                {
                    context.Principal = principal;
                }
                else
                {
                    context.Fail("The token is not an access token, or carries no subject or no single account.");
                }

                return Task.CompletedTask;
            },
        };
    }

    // Unset, the handlers derive it from the authority; never an empty string, which they would try to fetch.
    private static string? MetadataAddressOf(SignInOptions signIn) =>
        string.IsNullOrWhiteSpace(signIn.MetadataAddress) ? null : signIn.MetadataAddress.Trim();

    /// <summary>Present only when sign-in is configured; carries what the middleware needs.</summary>
    internal sealed record SignInRegistration(string SessionCookieName);
}
