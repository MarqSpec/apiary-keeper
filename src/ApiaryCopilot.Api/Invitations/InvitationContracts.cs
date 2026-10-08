using System.ComponentModel;
using System.Text.Json.Serialization;

namespace ApiaryCopilot.Api.Invitations;

/// <summary>An owner's invitation, as the caller writes it (FR-74). Every field is checked.</summary>
[Description("Someone the account's owner invites to help: their email address, their name and their role.")]
public sealed record InvitationRequest
{
    /// <summary>The address the invitation goes to.</summary>
    [Description("The address Keycloak sends the invitation to; it is also the user's sign-in name. Required; one address, at most 254 characters.")]
    public string? Email { get; init; }

    /// <summary>The name shown for the user.</summary>
    [Description("The name shown for the user beside the records they write. Required; at most 200 characters.")]
    public string? Name { get; init; }

    /// <summary>The one role the user gets.</summary>
    [Description("helper (field and sales work) or grower (their own placements). Required. An owner is created by the Keycloak administrator and a customer by the storefront, never by invitation.")]
    public string? Role { get; init; }
}

/// <summary>The invited user, recorded in the owner's account.</summary>
/// <param name="UserId">The user's id in this account.</param>
/// <param name="Email">The address the invitation went to.</param>
/// <param name="Name">The name shown for the user.</param>
/// <param name="Role">The role the user was given.</param>
[Description("The invited user: Keycloak has created them and emailed the invitation, and they act in this account from their first sign-in.")]
public sealed record InvitationResult(
    [property: Description("The user's id in this account.")] Guid UserId,
    [property: Description("The address the invitation went to.")] string Email,
    [property: Description("The name shown for the user.")] string Name,
    [property: Description("helper or grower.")] string Role);

/// <summary>An invitation that passed every check.</summary>
public sealed record ParsedInvitation(string Email, string Name, MemberRole Role);

/// <summary>The parse's outcome: the invitation, or the errors keyed by field.</summary>
public sealed record InvitationParseResult(ParsedInvitation? Invitation, IReadOnlyDictionary<string, string[]> Errors);

[JsonSerializable(typeof(InvitationRequest))]
[JsonSerializable(typeof(InvitationResult))]
[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
internal sealed partial class InvitationJsonContext : JsonSerializerContext;
