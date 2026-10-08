using ApiaryCopilot.Domain;

namespace ApiaryCopilot.Infrastructure;

/// <summary>The current account's users: an invited user's row, written as the invitation is made.</summary>
public interface IUserStore
{
    /// <summary>
    /// Records an invited user in the current account, with the Keycloak user id the invitation created as its
    /// <see cref="User.ExternalSubject"/>, so the user acts as this row from its first sign-in.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no current account; nothing is written.</exception>
    Task<User> AddInvitedAsync(string name, string externalSubject, CancellationToken cancellationToken);
}
