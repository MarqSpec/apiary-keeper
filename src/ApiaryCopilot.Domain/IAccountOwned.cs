namespace ApiaryCopilot.Domain;

/// <summary>
/// A record that belongs to exactly one account. Every entity implements it, and the persistence layer
/// filters every query to the caller's account by it (NFR-8, ARCHITECTURE.md D-8).
/// </summary>
public interface IAccountOwned
{
    /// <summary>The account the record belongs to.</summary>
    AccountId AccountId { get; }
}
