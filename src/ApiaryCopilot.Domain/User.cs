namespace ApiaryCopilot.Domain;

/// <summary>
/// A person who acts on one account's records: the apiarist or a helper (ARCHITECTURE.md D-8). A user belongs to
/// exactly one account and is filtered like every other entity; every row they write is stamped with their
/// <see cref="UserId"/> (<see cref="IRowStamped"/>). Sign-in and roles are the identity provider's (D-19); a signed-in
/// caller is this user when its token's subject is <see cref="ExternalSubject"/>.
/// </summary>
public sealed class User : IAccountOwned
{
    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 200;

    /// <summary>Maximum length of <see cref="ExternalSubject"/>: OpenID Connect's limit for <c>sub</c>.</summary>
    public const int ExternalSubjectMaxLength = 255;

    /// <summary>The user's id.</summary>
    public required UserId UserId { get; init; }

    /// <inheritdoc />
    public required AccountId AccountId { get; init; }

    /// <summary>The name shown for the user, beside the rows they wrote.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The user's Keycloak subject (<c>sub</c>), compared exactly: how a signed-in caller is resolved to this user. Set when
    /// the user is invited, to the id Keycloak gave the user it created, which is the <c>sub</c> its tokens carry;
    /// <see langword="null"/> for a user with no Keycloak user, who cannot act. Never the email. Unique across every
    /// account: one sign-in is never two users.
    /// </summary>
    public string? ExternalSubject { get; init; }
}
