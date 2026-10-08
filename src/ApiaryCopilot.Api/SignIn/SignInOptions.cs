namespace ApiaryCopilot.Api.SignIn;

/// <summary>
/// Sign-in through Keycloak (D-19), set as <c>SignIn__*</c> in the environment (DEPLOYMENT.md §3). Unset, there is no
/// sign-in: no scheme, route or check is registered and the host behaves as before. Set, every setting is checked at
/// startup. The client secret comes only from the environment, never from shipped configuration.
/// </summary>
public sealed class SignInOptions
{
    /// <summary>The configuration section: <c>SignIn</c>.</summary>
    public const string SectionName = "SignIn";

    /// <summary>The shortest idle timeout a session may be given.</summary>
    public static readonly TimeSpan MinIdleTimeout = TimeSpan.FromMinutes(1);

    /// <summary>The longest idle timeout a session may be given.</summary>
    public static readonly TimeSpan MaxIdleTimeout = TimeSpan.FromHours(24);

    /// <summary>The longest a session may last, however active.</summary>
    public static readonly TimeSpan MaxSessionLifetime = TimeSpan.FromDays(7);

    /// <summary>The most clock skew a token's lifetime is allowed.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>The realm's URL, which is also the issuer the API checks, e.g. <c>https://…/realms/apiary-keeper</c>.</summary>
    public string? Authority { get; set; }

    /// <summary>
    /// Where the API fetches the realm's discovery document when it reaches Keycloak by another address than the
    /// browser does - a container on a private network - e.g. <c>http://keycloak:8080/realms/…/.well-known/openid-configuration</c>.
    /// Unset, it is the authority's own. The issuer checked stays <see cref="Authority"/>.
    /// </summary>
    public string? MetadataAddress { get; set; }

    /// <summary>The BFF's confidential client in the realm.</summary>
    public string? ClientId { get; set; }

    /// <summary>The BFF client's secret. Secret: never logged, echoed or returned.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>The audience an agent's bearer token must carry: the API's.</summary>
    public string? Audience { get; set; }

    /// <summary>
    /// On (the default), the authority must be <c>https</c> and every cookie is <c>Secure</c> with the session cookie
    /// <c>__Host-</c> prefixed. Off only for a local http Keycloak and the tests, never on staging or production.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>How long a browser session lasts without a request; each request slides it.</summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How long a browser session lasts at most, counted from sign-in.</summary>
    public TimeSpan SessionMaxLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>How far a token's lifetime may be off the API's clock.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether sign-in is configured: the authority is set.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority);

    /// <summary>The issuer a token must name: the authority without a trailing slash, as Keycloak writes it.</summary>
    public string Issuer => (Authority ?? string.Empty).Trim().TrimEnd('/');

    /// <summary>The session cookie's name; with https required it is <c>__Host-</c> prefixed, so a browser holds it to Secure, path / and this host.</summary>
    public string SessionCookieName => RequireHttps ? "__Host-apiary-keeper-session" : "apiary-keeper-session";

    /// <summary>The failures, each naming its setting and never its value; empty when the settings are usable.</summary>
    public IEnumerable<string> Validate()
    {
        if (!IsConfigured)
        {
            if (new[] { ClientId, ClientSecret, Audience }.Any(s => !string.IsNullOrWhiteSpace(s)))
            {
                yield return "SignIn__Authority must be set when any other SignIn__ setting is: sign-in is configured whole or not at all.";
            }

            yield break;
        }

        if (!IsUrl(Authority))
        {
            yield return RequireHttps
                ? "SignIn__Authority must be an absolute https URL with no query, fragment or user info."
                : "SignIn__Authority must be an absolute http or https URL with no query, fragment or user info.";
        }

        if (!string.IsNullOrWhiteSpace(MetadataAddress) && !IsUrl(MetadataAddress))
        {
            yield return RequireHttps
                ? "SignIn__MetadataAddress must be an absolute https URL with no query, fragment or user info, or unset."
                : "SignIn__MetadataAddress must be an absolute http or https URL with no query, fragment or user info, or unset.";
        }

        foreach (var (name, value) in new[] { ("ClientId", ClientId), ("ClientSecret", ClientSecret), ("Audience", Audience) })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield return $"SignIn__{name} must be set when SignIn__Authority is.";
            }
        }

        if (SessionIdleTimeout < MinIdleTimeout || SessionIdleTimeout > MaxIdleTimeout)
        {
            yield return "SignIn__SessionIdleTimeout must be between 00:01:00 and 1.00:00:00.";
        }
        else if (SessionMaxLifetime < SessionIdleTimeout || SessionMaxLifetime > MaxSessionLifetime)
        {
            yield return "SignIn__SessionMaxLifetime must be at least SignIn__SessionIdleTimeout and at most 7.00:00:00.";
        }

        if (ClockSkew < TimeSpan.Zero || ClockSkew > MaxClockSkew)
        {
            yield return "SignIn__ClockSkew must be between 00:00:00 and 00:05:00.";
        }
    }

    private bool IsUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (!RequireHttps && uri.Scheme == Uri.UriSchemeHttp))
        && uri.Query.Length == 0
        && uri.Fragment.Length == 0
        && uri.UserInfo.Length == 0;
}
