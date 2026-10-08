using System.Net.Mail;
using ApiaryCopilot.Domain;

namespace ApiaryCopilot.Api.Invitations;

/// <summary>
/// Checks an owner's invitation (FR-74, D-22): one email address, lower-cased as Keycloak keeps it; a name a user row can
/// hold; and <c>helper</c> or <c>grower</c>. An owner is created by the Keycloak administrator and a customer by the
/// storefront (FR-38), so neither is accepted, each refusal saying so. Every field's error is reported at once.
/// </summary>
public static class InvitationRequestParser
{
    /// <summary>The longest address SMTP carries (RFC 5321's path limit less its brackets).</summary>
    public const int EmailMaxLength = 254;

    /// <summary>Parses the request.</summary>
    public static InvitationParseResult Parse(InvitationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var email = request.Email?.Trim().ToLowerInvariant();
        if (!IsOneAddress(email))
        {
            errors["email"] = ["Must be one email address, at most 254 characters."];
        }

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > User.NameMaxLength || name.Any(char.IsControl))
        {
            errors["name"] = ["Required; at most 200 characters, with no control characters."];
        }

        MemberRole? role = request.Role switch
        {
            "helper" => MemberRole.Helper,
            "grower" => MemberRole.Grower,
            _ => null,
        };
        if (role is null)
        {
            errors["role"] = [request.Role switch
            {
                "owner" => "An owner is created by the Keycloak administrator, never by invitation (D-22); invite a helper or grower.",
                "customer" => "A customer account is created by the storefront (FR-38), not by invitation; invite a helper or grower.",
                _ => "Must be helper or grower.",
            }];
        }

        return errors.Count == 0
            ? new InvitationParseResult(new ParsedInvitation(email!, name!, role!.Value), errors)
            : new InvitationParseResult(null, errors);
    }

    private static bool IsOneAddress(string? email) =>
        !string.IsNullOrEmpty(email)
        && email.Length <= EmailMaxLength
        && !email.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
        && MailAddress.TryCreate(email, out var address)
        && string.Equals(address.Address, email, StringComparison.Ordinal)
        && address.DisplayName.Length == 0
        && address.Host.Length > 0
        && email.Count(c => c == '@') == 1;
}
