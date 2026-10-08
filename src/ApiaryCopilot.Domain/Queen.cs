namespace ApiaryCopilot.Domain;

/// <summary>
/// A queen that has headed a colony (FR-5). A colony's queens, oldest to newest, are its replacement history; the one
/// not yet replaced is its current queen. Projected from lineage events by <see cref="ColonyProjection"/> (D-2).
/// </summary>
public sealed class Queen : IAccountOwned, IRowStamped
{
    /// <summary>The queen's id.</summary>
    public required QueenId QueenId { get; init; }

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

    /// <summary>The colony she heads, or headed.</summary>
    public required ColonyId ColonyId { get; init; }

    /// <summary>What was recorded about her when she was introduced.</summary>
    public required QueenDetails Details { get; init; }

    /// <summary>The day she was replaced; <see langword="null"/> while she is the colony's current queen.</summary>
    public DateOnly? ReplacedOn { get; set; }

    /// <summary>The queen who replaced her.</summary>
    public QueenId? ReplacedByQueenId { get; set; }
}

/// <summary>What is recorded about a queen when she is introduced (FR-5).</summary>
public sealed record QueenDetails
{
    /// <summary>Maximum length of <see cref="Source"/>.</summary>
    public const int SourceMaxLength = 200;

    /// <summary>The earliest mark year accepted.</summary>
    public const int MarkYearMin = 2000;

    /// <summary>The latest mark year accepted.</summary>
    public const int MarkYearMax = 2100;

    /// <summary>Where she came from: a breeder, raised in the yard, a swarm.</summary>
    public required string Source { get; init; }

    /// <summary>The colour of her mark, or <see langword="null"/> when she is unmarked.</summary>
    public QueenMarkColour? MarkColour { get; init; }

    /// <summary>The year her mark stands for, or <see langword="null"/> when not known.</summary>
    public int? MarkYear { get; init; }

    /// <summary>The day she was introduced to the colony.</summary>
    public required DateOnly IntroducedOn { get; init; }
}

/// <summary>The colours of the international queen-marking code (FR-5).</summary>
public enum QueenMarkColour
{
    /// <summary>White: years ending 1 or 6.</summary>
    White,

    /// <summary>Yellow: years ending 2 or 7.</summary>
    Yellow,

    /// <summary>Red: years ending 3 or 8.</summary>
    Red,

    /// <summary>Green: years ending 4 or 9.</summary>
    Green,

    /// <summary>Blue: years ending 5 or 0.</summary>
    Blue,
}
