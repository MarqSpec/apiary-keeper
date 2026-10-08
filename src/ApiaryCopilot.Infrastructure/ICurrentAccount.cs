using ApiaryCopilot.Domain;

namespace ApiaryCopilot.Infrastructure;

/// <summary>
/// The account the current caller acts for. The host supplies it from the signed-in caller's token (its
/// <c>account_id</c>, ARCHITECTURE.md D-19, D-22) - never from a request argument
/// (CONVENTIONS.md §4.2).
/// </summary>
public interface ICurrentAccount
{
    /// <summary>The caller's account, or <see langword="null"/> when there is none; then every query returns nothing.</summary>
    AccountId? AccountId { get; }
}
