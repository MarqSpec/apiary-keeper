using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace ApiaryCopilot.Api.SignIn;

/// <summary>
/// The BFF's routes, under <c>/api/auth</c> so the PWA's service worker never answers for them (its navigation
/// fallback skips <c>/api/</c>, an installed worker included). Mapped only when sign-in is configured; otherwise
/// they do not exist (404).
/// </summary>
public static partial class SignInEndpoints
{
    /// <summary>The routes' root.</summary>
    public const string BasePath = "/api/auth";

    /// <summary>Where Keycloak returns the browser with the code; the OpenID Connect handler answers it.</summary>
    public const string CallbackPath = BasePath + "/callback";

    /// <summary>Registers the JSON contracts.</summary>
    public static IServiceCollection AddSignInContracts(this IServiceCollection services) =>
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, SignInJsonContext.Default));

    /// <summary>Maps sign-in, sign-out and the signed-in user; does nothing when sign-in is off.</summary>
    public static WebApplication MapSignIn(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Services.GetService<SignInExtensions.SignInRegistration>() is null)
        {
            return app;
        }

        var auth = app.MapGroup(BasePath).WithTags("Sign-in");

        auth.MapGet("/login", SignIn)
            .WithName("SignIn")
            .WithSummary("Sign in through Keycloak")
            .WithDescription(
                "Starts the browser's sign-in: redirects to Keycloak with the authorization code flow and PKCE. Keycloak returns " +
                "to /api/auth/callback, which sets the session cookie - a session id only - and redirects to returnUrl, a path " +
                "on this host (anything else becomes /).")
            .AllowAnonymous();

        auth.MapPost("/logout", SignOutAsync)
            .WithName("SignOut")
            .WithSummary("Sign out")
            .WithDescription(
                "Ends the browser's session on the server and clears its cookie, and ends the user's Keycloak session over the " +
                "back channel with the session's refresh token. With the session cookie it needs the X-CSRF: 1 header. Answers " +
                "with endSessionUrl, Keycloak's sign-out page for this client, which the browser may still visit to clear Keycloak's " +
                "own cookies (null if Keycloak could not be reached).")
            .AllowAnonymous();

        auth.MapGet("/me", GetSignedInUser)
            .WithName("GetSignedInUser")
            .WithSummary("The signed-in user")
            .WithDescription(
                "The caller's principal, the same whether it signed in by the session cookie or sent a bearer token: its sub, " +
                "its accountId and its roles. 401 when the caller is not signed in.")
            .RequireAuthorization(policy => policy.RequireAuthenticatedUser());

        return app;
    }

    private static ChallengeHttpResult SignIn(string? returnUrl) =>
        TypedResults.Challenge(
            new AuthenticationProperties { RedirectUri = LocalReturnUrl.Sanitize(returnUrl) },
            [SignInExtensions.OpenIdConnectScheme]);

    private static async Task<Ok<SignedOut>> SignOutAsync(
        HttpContext context,
        IOptionsMonitor<OpenIdConnectOptions> openIdConnect,
        ISessionTokenStore sessionTokens,
        IKeycloakTokens keycloak,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        // Keycloak's own session ends over the back channel with the session's refresh token, so the user is signed out
        // there too whether or not the browser visits the sign-out page.
        var properties = context.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Properties;
        if (properties is not null && SessionTokens.SessionIdOf(properties) is { } sessionId)
        {
            if (await sessionTokens.GetAsync(sessionId, cancellationToken) is { } tokens
                && !await keycloak.EndSessionAsync(tokens.RefreshToken, CancellationToken.None))
            {
                LogBackChannelSignOutFailed(loggers.CreateLogger(typeof(SignInEndpoints)));
            }

            await sessionTokens.RemoveAsync(sessionId, CancellationToken.None);
        }

        await context.SignOutAsync(SignInExtensions.CookieScheme);

        var options = openIdConnect.Get(SignInExtensions.OpenIdConnectScheme);
        string? endSessionUrl = null;
        try
        {
            var configuration = await options.ConfigurationManager!.GetConfigurationAsync(cancellationToken);
            if (!string.IsNullOrEmpty(configuration.EndSessionEndpoint))
            {
                endSessionUrl = QueryHelpers.AddQueryString(configuration.EndSessionEndpoint, new Dictionary<string, string?>
                {
                    ["client_id"] = options.ClientId,
                    ["post_logout_redirect_uri"] = $"{context.Request.Scheme}://{context.Request.Host}/",
                });
            }
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or IOException)
        {
            // The local session is already gone; only the link to Keycloak's own sign-out is missing.
            LogEndSessionUnavailable(loggers.CreateLogger(typeof(SignInEndpoints)), e.GetType().Name);
        }

        return TypedResults.Ok(new SignedOut(endSessionUrl));
    }

    private static Ok<SignedInUser> GetSignedInUser(ClaimsPrincipal user) =>
        TypedResults.Ok(new SignedInUser(
            user.FindFirstValue(ApiaryClaims.Subject)!,
            user.FindFirstValue(ApiaryClaims.AccountId)!,
            user.FindAll(ApiaryClaims.Role).Select(c => c.Value).Order(StringComparer.Ordinal).ToList()));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keycloak did not confirm the back-channel sign-out; its session ends at its own idle timeout.")]
    private static partial void LogBackChannelSignOutFailed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keycloak's sign-out page could not be found: {ErrorType}.")]
    private static partial void LogEndSessionUnavailable(ILogger logger, string errorType);
}

/// <summary>The signed-in user: the principal's subject, account and roles, nothing else.</summary>
public sealed record SignedInUser(string Sub, string AccountId, IReadOnlyList<string> Roles);

/// <summary>A sign-out's result: Keycloak's sign-out page for the browser to visit, or null.</summary>
public sealed record SignedOut(string? EndSessionUrl);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SignedInUser))]
[JsonSerializable(typeof(SignedOut))]
internal sealed partial class SignInJsonContext : JsonSerializerContext;
