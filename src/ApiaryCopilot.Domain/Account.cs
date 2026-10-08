namespace ApiaryCopilot.Domain;

/// <summary>
/// An apiary: the tenant, and the root every other record belongs to (ARCHITECTURE.md D-8). Its own key is its
/// <see cref="AccountId"/>, so it is filtered like every other entity and a caller sees only its own account.
/// </summary>
public sealed class Account : IAccountOwned
{
    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 200;

    /// <inheritdoc />
    public required AccountId AccountId { get; init; }

    /// <summary>The apiary's display name.</summary>
    public required string Name { get; init; }
}
