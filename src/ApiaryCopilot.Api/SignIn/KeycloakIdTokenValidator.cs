using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ApiaryCopilot.Api.SignIn;

/// <summary>Validates the ID token a refresh returns and builds the session's new principal from it.</summary>
public interface IIdTokenValidator
{
    /// <summary>The principal, or <see langword="null"/> when the token is not a valid ID token of this realm for this client.</summary>
    Task<ClaimsPrincipal?> ValidateAsync(string idToken, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IIdTokenValidator"/> with the sign-in's own rules (<see cref="SignInExtensions.IdTokenValidation"/>): Keycloak's
/// published keys, the configured issuer, this client as the audience, a lifetime, RS256 only, and <c>typ</c> <c>ID</c>.
/// </summary>
public sealed class KeycloakIdTokenValidator(IOptionsMonitor<OpenIdConnectOptions> openIdConnect, IOptions<SignInOptions> signIn) : IIdTokenValidator
{
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    /// <inheritdoc />
    public async Task<ClaimsPrincipal?> ValidateAsync(string idToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(idToken);
        var options = openIdConnect.Get(SignInExtensions.OpenIdConnectScheme);
        OpenIdConnectConfiguration configuration;
        try
        {
            configuration = options.Configuration
                ?? await (options.ConfigurationManager ?? throw new InvalidOperationException("Sign-in has no Keycloak configuration."))
                    .GetConfigurationAsync(cancellationToken);
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or IOException)
        {
            return null;
        }

        var parameters = SignInExtensions.IdTokenValidation(signIn.Value);
        parameters.IssuerSigningKeys = configuration.SigningKeys;
        var result = await _handler.ValidateTokenAsync(idToken, parameters);
        return result.IsValid ? ApiaryClaims.FromIdToken(result.ClaimsIdentity.Claims) : null;
    }
}
