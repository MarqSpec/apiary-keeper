using ApiaryCopilot.Domain;

namespace ApiaryCopilot.Infrastructure;

/// <summary>
/// The user the current caller is: the one every row written is stamped with. The host supplies it from the
/// signed-in caller: the user whose <c>ExternalSubject</c> is the token's subject (ARCHITECTURE.md D-19) - never
/// from a request argument (CONVENTIONS.md §4.2).
/// </summary>
public interface ICurrentUser
{
    /// <summary>The acting user, or <see langword="null"/> when there is none; then nothing can be written.</summary>
    UserId? UserId { get; }
}
