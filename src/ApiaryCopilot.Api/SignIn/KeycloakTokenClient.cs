using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace ApiaryCopilot.Api.SignIn;

/// <summary>The outcome of presenting a refresh token to Keycloak.</summary>
public abstract record TokenRefresh
{
    private TokenRefresh()
    {
    }

    /// <summary>Keycloak issued new tokens: the ID token to validate, the rotated refresh token if it sent one, and the access token's lifetime.</summary>
    public sealed record Renewed(string IdToken, string? RefreshToken, TimeSpan AccessTokenLifetime) : TokenRefresh
    {
        /// <inheritdoc />
        public override string ToString() => $"Renewed {{ AccessTokenLifetime = {AccessTokenLifetime} }}";
    }

    /// <summary>
    /// Keycloak refused the refresh token - the user is disabled, the token was revoked, rotated away or expired, the
    /// session ended - or answered with something that is not a usable token response. The session cannot be renewed.
    /// </summary>
    public sealed record Refused(string Error) : TokenRefresh;

    /// <summary>Keycloak could not be asked: unreachable, too slow, or failing (5xx). The session may be renewed later.</summary>
    public sealed record Unavailable(string Reason) : TokenRefresh;
}

/// <summary>The BFF's back-channel calls to Keycloak's token and sign-out endpoints, as the realm's confidential client.</summary>
public interface IKeycloakTokens
{
    /// <summary>Presents a refresh token for new tokens (the refresh token grant).</summary>
    Task<TokenRefresh> RefreshAsync(string refreshToken, CancellationToken cancellationToken);

    /// <summary>Ends the Keycloak session a refresh token belongs to; whether Keycloak confirmed it.</summary>
    Task<bool> EndSessionAsync(string refreshToken, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IKeycloakTokens"/> over the OpenID Connect handler's own back channel, endpoints and client credentials. No
/// retry: with rotation on a refresh token is good once, so a retried request whose first answer was lost would present a
/// spent token and end the session; a failure is reported once and the caller decides. Each call is bounded by
/// <see cref="CallTimeout"/>. Nothing here logs or returns a token it was given.
/// </summary>
public sealed class KeycloakTokenClient(IOptionsMonitor<OpenIdConnectOptions> openIdConnect, TimeProvider clock) : IKeycloakTokens
{
    /// <summary>How long one call to Keycloak may take before it counts as unavailable.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public async Task<TokenRefresh> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);
        var options = openIdConnect.Get(SignInExtensions.OpenIdConnectScheme);
        using var timeout = new CancellationTokenSource(CallTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var configuration = await ConfigurationOf(options, linked.Token);
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = options.ClientId!,
                ["client_secret"] = options.ClientSecret!,
                ["refresh_token"] = refreshToken,
            });
            using var response = await options.Backchannel.PostAsync(new Uri(configuration.TokenEndpoint), form, linked.Token);
            if ((int)response.StatusCode >= 500)
            {
                return new TokenRefresh.Unavailable($"HTTP {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsStringAsync(linked.Token);
            return response.StatusCode == HttpStatusCode.OK ? Renewed(body) : new TokenRefresh.Refused(ErrorOf(body, response.StatusCode));
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidOperationException or IOException)
        {
            return new TokenRefresh.Unavailable(e.GetType().Name);
        }
    }

    /// <inheritdoc />
    public async Task<bool> EndSessionAsync(string refreshToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);
        var options = openIdConnect.Get(SignInExtensions.OpenIdConnectScheme);
        using var timeout = new CancellationTokenSource(CallTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var configuration = await ConfigurationOf(options, linked.Token);
            if (BackChannelLogoutEndpoint(configuration) is not { } endpoint)
            {
                return false;
            }

            // Keycloak's back-channel sign-out: the confidential client presents the session's refresh token.
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId!,
                ["client_secret"] = options.ClientSecret!,
                ["refresh_token"] = refreshToken,
            });
            using var response = await options.Backchannel.PostAsync(endpoint, form, linked.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Where the back-channel sign-out goes: Keycloak's logout endpoint beside its token endpoint. The discovery document's
    /// <c>end_session_endpoint</c> is a browser-facing URL on the front-end host, which the API may not reach when it talks to
    /// Keycloak over a private network (<c>SignIn__MetadataAddress</c>); the token endpoint is the back channel's own, and
    /// Keycloak serves <c>logout</c> next to <c>token</c>. Without a token endpoint of that shape, the discovery document's.
    /// </summary>
    public static Uri? BackChannelLogoutEndpoint(OpenIdConnectConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (Uri.TryCreate(configuration.TokenEndpoint, UriKind.Absolute, out var token)
            && token.AbsolutePath.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal)
            && token.Query.Length == 0)
        {
            return new Uri(token, "logout");
        }

        return Uri.TryCreate(configuration.EndSessionEndpoint, UriKind.Absolute, out var endSession) ? endSession : null;
    }

    private static async Task<OpenIdConnectConfiguration> ConfigurationOf(OpenIdConnectOptions options, CancellationToken cancellationToken) =>
        options.Configuration
        ?? await (options.ConfigurationManager ?? throw new InvalidOperationException("Sign-in has no Keycloak configuration."))
            .GetConfigurationAsync(cancellationToken);

    private static TokenRefresh Renewed(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var idToken = root.TryGetProperty("id_token", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            var refreshToken = root.TryGetProperty("refresh_token", out var refresh) && refresh.ValueKind == JsonValueKind.String ? refresh.GetString() : null;
            var expiresIn = root.TryGetProperty("expires_in", out var expires) && expires.ValueKind == JsonValueKind.Number && expires.TryGetInt32(out var seconds)
                ? seconds
                : 0;
            return string.IsNullOrEmpty(idToken) || expiresIn <= 0
                ? new TokenRefresh.Refused("malformed_response")
                : new TokenRefresh.Renewed(idToken, string.IsNullOrEmpty(refreshToken) ? null : refreshToken, TimeSpan.FromSeconds(expiresIn));
        }
        catch (JsonException)
        {
            return new TokenRefresh.Refused("malformed_response");
        }
    }

    // Only Keycloak's error code, never its description, so nothing user-specific reaches a log.
    private static string ErrorOf(string body, HttpStatusCode status)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                && error.GetString() is { Length: > 0 and <= 64 } code && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
                    ? code
                    : string.Create(CultureInfo.InvariantCulture, $"http_{(int)status}");
        }
        catch (JsonException)
        {
            return string.Create(CultureInfo.InvariantCulture, $"http_{(int)status}");
        }
    }
}
