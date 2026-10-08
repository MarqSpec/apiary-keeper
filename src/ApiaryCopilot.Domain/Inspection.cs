namespace ApiaryCopilot.Domain;

/// <summary>
/// One inspection of a colony (UC-1, FR-7): what was seen on a day, as the structured findings of
/// <see cref="InspectionFields"/> and free-text notes. Recorded once and never changed. Scoped to its account
/// like every entity (NFR-8).
/// </summary>
public sealed class Inspection : IAccountOwned, IRowStamped
{
    /// <summary>The inspection's id.</summary>
    public required InspectionId InspectionId { get; init; }

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

    /// <summary>The colony inspected.</summary>
    public required ColonyId ColonyId { get; init; }

    /// <summary>The day the colony was inspected.</summary>
    public required DateOnly InspectedOn { get; init; }

    /// <summary>When the server recorded it.</summary>
    public required DateTimeOffset RecordedAt { get; init; }

    /// <summary>What was seen: the fields of <see cref="InspectionFields"/> the inspector filled in.</summary>
    public required InspectionFindings Findings { get; init; }

    /// <summary>Free-text notes, or <see langword="null"/> when there are none.</summary>
    public string? Notes { get; init; }
}

/// <summary>What is recorded about an inspection (FR-7); the colony it belongs to is given beside it.</summary>
public sealed record InspectionDetails
{
    /// <summary>Maximum length of <see cref="Notes"/>.</summary>
    public const int NotesMaxLength = 4000;

    /// <summary>
    /// The earliest day an inspection may be dated: no hive record predates it, and a queen's mark year starts there
    /// too. It exists to refuse a default or mistyped date such as 0001-01-01.
    /// </summary>
    public static readonly DateOnly EarliestInspectedOn = new(2000, 1, 1);

    /// <summary>The day the colony was inspected.</summary>
    public required DateOnly InspectedOn { get; init; }

    /// <summary>What was seen.</summary>
    public required InspectionFindings Findings { get; init; }

    /// <summary>Free-text notes, or <see langword="null"/> when there are none.</summary>
    public string? Notes { get; init; }
}
