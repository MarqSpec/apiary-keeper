namespace ApiaryCopilot.Domain;

/// <summary>
/// A colony's current state (FR-4): the read model projected from its lineage events (FR-6, ARCHITECTURE.md D-2).
/// Only <see cref="ColonyProjection"/> changes it; what happened to it is in <see cref="ColonyEvent"/>, never
/// overwritten. Scoped to its account like every entity (NFR-8).
/// </summary>
public sealed class Colony : IAccountOwned, IRowStamped
{
    /// <summary>The colony's id.</summary>
    public required ColonyId ColonyId { get; init; }

    /// <inheritdoc />
    public required AccountId AccountId { get; init; }

    /// <inheritdoc />
    public UserId CreatedBy { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; private set; }

    /// <inheritdoc />
    public UserId LastModifiedBy { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset LastModifiedAt { get; private set; }

    /// <summary>The id on the QR/NFC tag on the box; unique within the account.</summary>
    public required string TagId { get; init; }

    /// <summary>The hive's brand or ID number.</summary>
    public required string HiveNumber { get; init; }

    /// <summary>The kind of hive, as the family names it.</summary>
    public required string HiveType { get; init; }

    /// <summary>How many boxes the hive had when the colony was recorded.</summary>
    public required int BoxCount { get; init; }

    /// <summary>How the colony came to be recorded.</summary>
    public required ColonyOrigin Origin { get; init; }

    /// <summary>The colony it was split from, when <see cref="Origin"/> is <see cref="ColonyOrigin.Split"/>.</summary>
    public ColonyId? ParentColonyId { get; init; }

    /// <summary>The yard the colony is in now.</summary>
    public required YardId YardId { get; set; }

    /// <summary>Whether the colony is still kept.</summary>
    public required ColonyStatus Status { get; set; }

    /// <summary>The colony it was merged into, once <see cref="Status"/> is <see cref="ColonyStatus.Merged"/>.</summary>
    public ColonyId? MergedIntoColonyId { get; set; }

    /// <summary>How many events about this colony have been applied; two writers of one colony cannot both win.</summary>
    public required int Version { get; set; }
}

/// <summary>What is recorded about a colony when it comes into being: registered, captured or split off (FR-4).</summary>
public sealed record ColonyProfile
{
    /// <summary>Maximum length of <see cref="TagId"/> and <see cref="HiveNumber"/>.</summary>
    public const int IdMaxLength = 64;

    /// <summary>Maximum length of <see cref="HiveType"/>.</summary>
    public const int HiveTypeMaxLength = 100;

    /// <summary>The most boxes a hive may be recorded with; the fewest is one.</summary>
    public const int BoxCountMax = 30;

    /// <summary>The id on the QR/NFC tag on the box; unique within the account.</summary>
    public required string TagId { get; init; }

    /// <summary>The hive's brand or ID number.</summary>
    public required string HiveNumber { get; init; }

    /// <summary>The yard the colony is placed in.</summary>
    public required YardId YardId { get; init; }

    /// <summary>The kind of hive, as the family names it.</summary>
    public required string HiveType { get; init; }

    /// <summary>How many boxes the hive has.</summary>
    public required int BoxCount { get; init; }
}

/// <summary>Whether a colony is still kept (FR-4).</summary>
public enum ColonyStatus
{
    /// <summary>Kept and alive.</summary>
    Active,

    /// <summary>Died out.</summary>
    DeadOut,

    /// <summary>Combined into another colony.</summary>
    Merged,

    /// <summary>Sold.</summary>
    Sold,
}

/// <summary>How a colony came to be recorded (FR-6).</summary>
public enum ColonyOrigin
{
    /// <summary>An existing colony entered into the app.</summary>
    Registered,

    /// <summary>A captured swarm.</summary>
    SwarmCapture,

    /// <summary>Split from another colony.</summary>
    Split,
}
