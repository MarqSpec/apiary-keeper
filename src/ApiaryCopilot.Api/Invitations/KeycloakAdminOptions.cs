using System.Globalization;
using ApiaryCopilot.Api.SignIn;

namespace ApiaryCopilot.Api.Invitations;

/// <summary>The roles an invitation can give (D-22). There is no owner: an owner is created by the Keycloak administrator.</summary>
public enum MemberRole
{
    /// <summary>Field and sales work in an account.</summary>
    Helper,

    /// <summary>A grower's read-only portal of their own placements (UC-12).</summary>
    Grower,

    /// <summary>A storefront customer (FR-38): no role in any apiary account.</summary>
    Customer,
}

/// <summary>Where the service account calls Keycloak: its realm's token endpoint and the realm's admin interface.</summary>
/// <param name="Token">The realm's token endpoint.</param>
/// <param name="Admin">The realm's admin interface, ending in a slash, so a relative path resolves inside it.</param>
public sealed record KeycloakAdminEndpoints(Uri Token, Uri Admin);

/// <summary>
/// The API's Keycloak service account (D-22), set as <c>KeycloakAdmin__*</c> (DEPLOYMENT.md §3). Unset, invitations are
/// off. Set, it needs sign-in (it calls the sign-in realm, by the back channel when <c>SignIn__MetadataAddress</c> is
/// set), and every setting is checked at startup, each failure naming its setting and never its value. The secret comes
/// only from the environment. The role ids are configured because the service account may not read roles.
/// </summary>
public sealed class KeycloakAdminOptions
{
    /// <summary>The configuration section: <c>KeycloakAdmin</c>.</summary>
    public const string SectionName = "KeycloakAdmin";

    /// <summary>The shortest an invitation may last.</summary>
    public static readonly TimeSpan MinInvitationLifetime = TimeSpan.FromHours(1);

    /// <summary>The longest an invitation may last.</summary>
    public static readonly TimeSpan MaxInvitationLifetime = TimeSpan.FromDays(30);

    private const string DiscoverySuffix = "/.well-known/openid-configuration";
    private const string RealmsSegment = "/realms/";

    /// <summary>The service account's client in the realm: <c>apiary-keeper-api-admin</c> locally.</summary>
    public string? ClientId { get; set; }

    /// <summary>That client's secret. Secret: never logged, echoed or returned.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>The Keycloak id of the realm role <c>helper</c>.</summary>
    public string? HelperRoleId { get; set; }

    /// <summary>The Keycloak id of the realm role <c>grower</c>.</summary>
    public string? GrowerRoleId { get; set; }

    /// <summary>The Keycloak id of the realm role <c>customer</c>.</summary>
    public string? CustomerRoleId { get; set; }

    /// <summary>How long the emailed invitation's link stays valid.</summary>
    public TimeSpan InvitationLifetime { get; set; } = TimeSpan.FromDays(3);

    /// <summary>Whether the service account is configured: its client id is set.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);

    /// <summary>The configured Keycloak id of <paramref name="role"/>.</summary>
    public string RoleIdOf(MemberRole role) => role switch
    {
        MemberRole.Helper => HelperRoleId!.Trim(),
        MemberRole.Grower => GrowerRoleId!.Trim(),
        MemberRole.Customer => CustomerRoleId!.Trim(),
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Not a member role."),
    };

    /// <summary>
    /// The realm's token endpoint and admin interface, from the realm the API signs in through: the back channel's
    /// discovery address when one is set, otherwise the authority. <see langword="null"/> when that is not a realm URL.
    /// </summary>
    public static KeycloakAdminEndpoints? EndpointsFor(SignInOptions signIn)
    {
        ArgumentNullException.ThrowIfNull(signIn);
        var realmUrl = string.IsNullOrWhiteSpace(signIn.MetadataAddress)
            ? signIn.Issuer
            : signIn.MetadataAddress.Trim().TrimEnd('/');
        if (realmUrl.EndsWith(DiscoverySuffix, StringComparison.Ordinal))
        {
            realmUrl = realmUrl[..^DiscoverySuffix.Length];
        }

        if (!Uri.TryCreate(realmUrl.TrimEnd('/'), UriKind.Absolute, out var realm) || realm.Query.Length > 0 || realm.Fragment.Length > 0)
        {
            return null;
        }

        var path = realm.AbsolutePath;
        var at = path.LastIndexOf(RealmsSegment, StringComparison.Ordinal);
        var name = at < 0 ? string.Empty : path[(at + RealmsSegment.Length)..];
        if (name.Length == 0 || name.Contains('/', StringComparison.Ordinal))
        {
            return null;
        }

        var root = realm.GetLeftPart(UriPartial.Authority) + path[..at];
        return new KeycloakAdminEndpoints(
            new Uri($"{root}{RealmsSegment}{name}/protocol/openid-connect/token"),
            new Uri($"{root}/admin{RealmsSegment}{name}/"));
    }

    /// <summary>The failures, each naming its setting and never its value; empty when the settings are usable.</summary>
    public IEnumerable<string> Validate(SignInOptions signIn)
    {
        ArgumentNullException.ThrowIfNull(signIn);
        if (!IsConfigured)
        {
            if (new[] { ClientSecret, HelperRoleId, GrowerRoleId, CustomerRoleId }.Any(s => !string.IsNullOrWhiteSpace(s)))
            {
                yield return "KeycloakAdmin__ClientId must be set when any other KeycloakAdmin__ setting is: the service account is configured whole or not at all.";
            }

            yield break;
        }

        if (!signIn.IsConfigured)
        {
            yield return "SignIn__Authority must be set when KeycloakAdmin__ClientId is: the service account calls the realm the API signs in through.";
        }
        else if (EndpointsFor(signIn) is null)
        {
            yield return "SignIn__Authority (or SignIn__MetadataAddress) must be a Keycloak realm URL ending in /realms/<realm> when KeycloakAdmin__ClientId is set.";
        }

        if (string.IsNullOrWhiteSpace(ClientSecret))
        {
            yield return "KeycloakAdmin__ClientSecret must be set when KeycloakAdmin__ClientId is.";
        }

        foreach (var (name, value) in new[] { ("HelperRoleId", HelperRoleId), ("GrowerRoleId", GrowerRoleId), ("CustomerRoleId", CustomerRoleId) })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield return $"KeycloakAdmin__{name} must be set when KeycloakAdmin__ClientId is.";
            }
            else if (!Guid.TryParseExact(value.Trim(), "D", out var id) || id == Guid.Empty)
            {
                yield return $"KeycloakAdmin__{name} must be the role's Keycloak id, a non-empty GUID.";
            }
        }

        if (InvitationLifetime < MinInvitationLifetime || InvitationLifetime > MaxInvitationLifetime)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"KeycloakAdmin__InvitationLifetime must be between {MinInvitationLifetime:c} and {MaxInvitationLifetime:c}.");
        }
    }
}
