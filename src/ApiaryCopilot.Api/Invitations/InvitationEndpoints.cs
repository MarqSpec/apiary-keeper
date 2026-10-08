using ApiaryCopilot.Api.Accounts;
using ApiaryCopilot.Api.Problems;
using ApiaryCopilot.Api.SignIn;
using ApiaryCopilot.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ApiaryCopilot.Api.Invitations;

/// <summary>
/// An account's owner invites a helper or grower (UC-18, FR-74, D-22): Keycloak creates the user with the owner's
/// account and the role and emails its invitation, through the API's service account; the API records the user in the
/// owner's account with the Keycloak user id as its <c>ExternalSubject</c>, so the user acts as that row from its first
/// sign-in.
/// </summary>
public static class InvitationEndpoints
{
    /// <summary>The route the invitation lives at.</summary>
    public const string Path = "/api/invitations";

    /// <summary>
    /// Registers the invitation's JSON and the service account: <see cref="KeycloakAdminClient"/> when
    /// <c>KeycloakAdmin__ClientId</c> is set, every <c>KeycloakAdmin__</c> setting checked now so a bad one stops the host
    /// naming the setting and never its value; otherwise <see cref="UnconfiguredKeycloakAdmin"/>, and the endpoint answers 503.
    /// </summary>
    /// <exception cref="OptionsValidationException">A setting is missing or malformed.</exception>
    public static IServiceCollection AddInvitations(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, InvitationJsonContext.Default));
        var section = configuration.GetSection(KeycloakAdminOptions.SectionName);
        var admin = section.Get<KeycloakAdminOptions>() ?? new KeycloakAdminOptions();
        var signIn = configuration.GetSection(SignInOptions.SectionName).Get<SignInOptions>() ?? new SignInOptions();
        var failures = admin.Validate(signIn).ToList();
        if (failures.Count > 0)
        {
            throw new OptionsValidationException(KeycloakAdminOptions.SectionName, typeof(KeycloakAdminOptions), failures);
        }

        services.AddOptions<KeycloakAdminOptions>().Bind(section);
        if (!admin.IsConfigured)
        {
            services.AddSingleton<IKeycloakAdmin, UnconfiguredKeycloakAdmin>();
            return services;
        }

        services.AddHttpClient<IKeycloakAdmin, KeycloakAdminClient>();
        return services;
    }

    /// <summary>Maps the invitation, for a signed-in owner acting in their own account (<see cref="AccountMemberAuthorization.RequireAccountOwner"/>).</summary>
    public static RouteGroupBuilder MapInvitations(this IEndpointRouteBuilder endpoints)
    {
        var invitations = endpoints.MapGroup(Path)
            .WithTags("Invitations")
            .RequireAccountOwner();

        invitations.MapPost("/", InviteAsync)
            .WithName("InviteUser")
            .WithSummary("Invite a helper or grower")
            .WithDescription("The account's owner invites someone by email as a helper or a grower. Keycloak creates them in this account and emails the invitation, with which they verify the address and set a password; they act in this account from their first sign-in. An address Keycloak already has is a conflict. An owner is created by the Keycloak administrator and a customer by the storefront, never here.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        return invitations;
    }

    private static async Task<Results<Created<InvitationResult>, ValidationProblem, ProblemHttpResult>> InviteAsync(
        InvitationRequest request, IKeycloakAdmin keycloak, IUserStore users, ICurrentAccount currentAccount, CancellationToken cancellationToken)
    {
        if (!keycloak.IsConfigured)
        {
            return ProblemResults.Unavailable("invitations are not set up on this server; nothing was created.");
        }

        var parsed = InvitationRequestParser.Parse(request);
        if (parsed.Invitation is not { } invitation)
        {
            return ProblemResults.Validation(parsed.Errors.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal));
        }

        var account = currentAccount.AccountId
            ?? throw new InvalidOperationException("No current account: RequireCurrentAccountFilter runs first.");
        var outcome = await keycloak.InviteAsync(
            new KeycloakInvitation(invitation.Email, invitation.Name, invitation.Role, account.Value), cancellationToken);
        switch (outcome)
        {
            case InviteOutcome.AlreadyExists:
                return ProblemResults.Conflict("someone with that email address already has a sign-in; nothing was created.");
            case InviteOutcome.Failed:
                return ProblemResults.Unavailable("Keycloak could not create the invitation; nothing was created. Try again later.");
            case InviteOutcome.Invited invited:
                var user = await RecordAsync(invited.UserId);
                return TypedResults.Created((string?)null, new InvitationResult(user.UserId.Value, invitation.Email, user.Name, RoleName(invitation.Role)));
            default:
                throw new InvalidOperationException("Unhandled invitation outcome.");
        }

        async Task<Domain.User> RecordAsync(string keycloakUserId)
        {
            try
            {
                return await users.AddInvitedAsync(invitation.Name, keycloakUserId, cancellationToken);
            }
            catch
            {
                // A Keycloak user with no row could sign in and act as no one; remove it rather than leave it behind. The email is
                // already out, so its link then fails: accepted (INTERFACES.md D.1).
                await keycloak.WithdrawAsync(keycloakUserId, CancellationToken.None);
                throw;
            }
        }
    }

    private static string RoleName(MemberRole role) => role switch
    {
        MemberRole.Helper => "helper",
        MemberRole.Grower => "grower",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Only a helper or grower is invited."),
    };
}
