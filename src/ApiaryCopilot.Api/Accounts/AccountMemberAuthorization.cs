using ApiaryCopilot.Api.SignIn;
using Microsoft.AspNetCore.Authorization;

namespace ApiaryCopilot.Api.Accounts;

/// <summary>
/// Who may use the apiary endpoints (yards, colonies, inspections, media), from the principal alone (D-19, D-22, NFR-6):
/// a signed-in user of an account who holds <c>owner</c> or <c>helper</c> - the field work of UC-1, UC-3 and UC-9
/// (REQUIREMENTS.md section 2). A <c>grower</c> (UC-12, a read-only portal of their own placements, not built) and a <c>customer</c>
/// (no role in an apiary account) are refused (403); a caller not signed in, 401.
/// </summary>
public static class AccountMemberAuthorization
{
    /// <summary>The policy's name.</summary>
    public const string FieldWork = "apiary-field-work";

    /// <summary>The roles the policy admits.</summary>
    public static readonly IReadOnlyList<string> FieldWorkRoles = ["owner", "helper"];

    /// <summary>The owner-only policy's name: what only an account's owner may do, such as inviting people (FR-74).</summary>
    public const string AccountOwner = "apiary-account-owner";

    /// <summary>The role <see cref="AccountOwner"/> admits.</summary>
    public const string OwnerRole = "owner";

    /// <summary>Adds the policies.</summary>
    public static AuthorizationOptions AddAccountMemberPolicies(this AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AddPolicy(FieldWork, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(ApiaryClaims.AccountId)
            .RequireClaim(ApiaryClaims.Subject)
            .RequireRole(FieldWorkRoles));
        options.AddPolicy(AccountOwner, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(ApiaryClaims.AccountId)
            .RequireClaim(ApiaryClaims.Subject)
            .RequireRole(OwnerRole));
        return options;
    }

    /// <summary>
    /// As <see cref="RequireAccountMember"/>, for an account's owner only: with sign-in on, the <see cref="AccountOwner"/>
    /// policy (401 or 403); then the account (503 with no sign-in), the acting user (403) and the owner role again on the
    /// principal (403), so the check holds whichever way the principal was set.
    /// </summary>
    public static RouteGroupBuilder RequireAccountOwner(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (((IEndpointRouteBuilder)group).ServiceProvider.GetService<SignInExtensions.SignInRegistration>() is not null)
        {
            group.RequireAuthorization(AccountOwner).ProducesProblem(StatusCodes.Status401Unauthorized);
        }

        return group
            .AddEndpointFilter<RequireCurrentAccountFilter>()
            .AddEndpointFilter<RequireActingUserFilter>()
            .AddEndpointFilter<RequireOwnerFilter>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// Authorizes the group for account members and resolves the acting user before each handler: with sign-in on, the
    /// <see cref="FieldWork"/> policy (401 or 403), then the account (503 with no sign-in) and the acting user (403). With
    /// sign-in off there is no caller to authorize, and the account check answers 503.
    /// </summary>
    public static RouteGroupBuilder RequireAccountMember(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (((IEndpointRouteBuilder)group).ServiceProvider.GetService<SignInExtensions.SignInRegistration>() is not null)
        {
            group.RequireAuthorization(FieldWork).ProducesProblem(StatusCodes.Status401Unauthorized);
        }

        return group
            .AddEndpointFilter<RequireCurrentAccountFilter>()
            .AddEndpointFilter<RequireActingUserFilter>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }
}
