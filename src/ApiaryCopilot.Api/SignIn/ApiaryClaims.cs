using System.Security.Claims;

namespace ApiaryCopilot.Api.SignIn;

/// <summary>
/// The claims principal both sign-in paths yield (ARCHITECTURE.md D-19 Option A): from the ID token the OpenID Connect
/// handler validated for a browser, and from the access token the bearer handler validated for an agent. It holds the
/// subject, the account and the application roles, and nothing else (maintainer, 2026-10-06), so an endpoint's
/// authorization is written once against it.
/// </summary>
public static class ApiaryClaims
{
    /// <summary>The identity's authentication type, the same on both paths.</summary>
    public const string AuthenticationType = "apiary-keeper";

    /// <summary>The Keycloak subject (<c>sub</c>), unchanged.</summary>
    public const string Subject = "sub";

    /// <summary>The user's one account (D-22), a lower-case GUID.</summary>
    public const string AccountId = "account_id";

    /// <summary>One claim per application role the user holds.</summary>
    public const string Role = "role";

    /// <summary>The roles the realm's mapper puts in both tokens (D-22).</summary>
    public const string TokenRoles = "roles";

    /// <summary>The application roles (D-22); any other role in a token is Keycloak's own and is dropped.</summary>
    public static readonly IReadOnlySet<string> ApplicationRoles =
        new HashSet<string>(["owner", "helper", "grower", "customer"], StringComparer.Ordinal);

    /// <summary>Keycloak's own token-type claim: <c>Bearer</c> on an access token, <c>ID</c> on an ID token.</summary>
    public const string TokenType = "typ";

    /// <summary>The <see cref="TokenType"/> of a Keycloak access token.</summary>
    public const string AccessTokenType = "Bearer";

    /// <summary>The <see cref="TokenType"/> of a Keycloak ID token.</summary>
    public const string IdTokenType = "ID";

    /// <summary>
    /// <see cref="FromToken"/> for a validated access token (the bearer path): <see langword="null"/> unless the token says it is
    /// one (<c>typ</c> <c>Bearer</c>), so a Keycloak ID token that happened to carry the API's audience is still refused.
    /// </summary>
    public static ClaimsPrincipal? FromAccessToken(IEnumerable<Claim> tokenClaims) => FromTokenOfType(tokenClaims, AccessTokenType);

    /// <summary>
    /// <see cref="FromToken"/> for a validated ID token (sign-in and its refresh): <see langword="null"/> unless the token says
    /// it is one (<c>typ</c> <c>ID</c>).
    /// </summary>
    public static ClaimsPrincipal? FromIdToken(IEnumerable<Claim> tokenClaims) => FromTokenOfType(tokenClaims, IdTokenType);

    /// <summary>
    /// Builds the principal from a validated token's claims, or returns <see langword="null"/> when the token has no
    /// subject or not exactly one well-formed account, so it is refused rather than turned into a partial principal.
    /// </summary>
    public static ClaimsPrincipal? FromToken(IEnumerable<Claim> tokenClaims)
    {
        ArgumentNullException.ThrowIfNull(tokenClaims);
        var claims = tokenClaims.ToList();
        var subjects = claims.Where(c => c.Type == Subject).Select(c => c.Value).ToList();
        var accounts = claims.Where(c => c.Type == AccountId).Select(c => c.Value).ToList();
        if (subjects.Count != 1 || string.IsNullOrWhiteSpace(subjects[0])
            || accounts.Count != 1 || !Guid.TryParse(accounts[0], out var account) || account == Guid.Empty)
        {
            return null;
        }

        var identity = new ClaimsIdentity(AuthenticationType, Subject, Role);
        identity.AddClaim(new Claim(Subject, subjects[0]));
        identity.AddClaim(new Claim(AccountId, account.ToString("D")));
        foreach (var role in claims.Where(c => c.Type == TokenRoles).Select(c => c.Value)
                     .Where(ApplicationRoles.Contains).Distinct(StringComparer.Ordinal))
        {
            identity.AddClaim(new Claim(Role, role));
        }

        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// The subject of a principal this class built and the request authenticated, or <see langword="null"/> for anyone else
    /// - an anonymous caller included - so nothing but a validated token's claim is ever taken as the caller.
    /// </summary>
    public static string? SubjectOf(ClaimsPrincipal? principal) =>
        IsOurs(principal) && principal!.FindFirst(Subject)?.Value is { Length: > 0 } subject ? subject : null;

    /// <summary>The account of a principal this class built and the request authenticated, or <see langword="null"/>.</summary>
    public static Guid? AccountOf(ClaimsPrincipal? principal) =>
        IsOurs(principal) && Guid.TryParse(principal!.FindFirst(AccountId)?.Value, out var account) && account != Guid.Empty ? account : null;

    private static bool IsOurs(ClaimsPrincipal? principal) =>
        principal?.Identity is { IsAuthenticated: true, AuthenticationType: AuthenticationType };

    private static ClaimsPrincipal? FromTokenOfType(IEnumerable<Claim> tokenClaims, string type)
    {
        ArgumentNullException.ThrowIfNull(tokenClaims);
        var claims = tokenClaims.ToList();
        var types = claims.Where(c => c.Type == TokenType).Select(c => c.Value).ToList();
        return types.Count == 1 && string.Equals(types[0], type, StringComparison.Ordinal) ? FromToken(claims) : null;
    }
}
