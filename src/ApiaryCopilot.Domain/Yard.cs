namespace ApiaryCopilot.Domain;

/// <summary>
/// A place where the account keeps colonies, on land a host landowner provides (UC-9, FR-1, FR-2).
/// Scoped to its account like every entity (NFR-8).
/// </summary>
public sealed class Yard : IAccountOwned, IRowStamped
{
    /// <summary>The yard's id.</summary>
    public required YardId YardId { get; init; }

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

    /// <summary>Everything recorded about the yard; an update replaces it whole.</summary>
    public required YardDetails Details { get; set; }
}

/// <summary>What is recorded about a yard (FR-1) and its host landowner (FR-2).</summary>
public sealed record YardDetails
{
    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 200;

    /// <summary>Maximum length of <see cref="County"/>.</summary>
    public const int CountyMaxLength = 100;

    /// <summary>Maximum length of <see cref="AccessNotes"/> and <see cref="ForageNotes"/>.</summary>
    public const int NotesMaxLength = 2000;

    /// <summary>The name the family uses for the yard.</summary>
    public required string Name { get; init; }

    /// <summary>Where the yard is.</summary>
    public required GeoLocation Location { get; init; }

    /// <summary>The state the yard is in.</summary>
    public required UsState State { get; init; }

    /// <summary>The county the yard is in.</summary>
    public required string County { get; init; }

    /// <summary>How to get in: gates, codes, tracks, when to avoid.</summary>
    public string? AccessNotes { get; init; }

    /// <summary>What the bees forage on around the yard, and when.</summary>
    public string? ForageNotes { get; init; }

    /// <summary>The host farmer or landowner and the arrangement with them.</summary>
    public required Landowner Landowner { get; init; }
}

/// <summary>A GPS position in decimal degrees (WGS 84).</summary>
public sealed record GeoLocation
{
    /// <summary>Latitude, from -90 to 90.</summary>
    public required double Latitude { get; init; }

    /// <summary>Longitude, from -180 to 180.</summary>
    public required double Longitude { get; init; }
}

/// <summary>The host farmer or landowner of a yard (FR-2).</summary>
public sealed record Landowner
{
    /// <summary>Maximum length of <see cref="Contact"/> and <see cref="SprayContact"/>.</summary>
    public const int ContactMaxLength = 500;

    /// <summary>Maximum length of <see cref="NearbyCrops"/>.</summary>
    public const int NearbyCropsMaxLength = 1000;

    /// <summary>How to reach the landowner: name, phone, email.</summary>
    public required string Contact { get; init; }

    /// <summary>What the landowner gets for hosting the yard.</summary>
    public required LandownerTerms Terms { get; init; }

    /// <summary>Crops grown near the yard.</summary>
    public string? NearbyCrops { get; init; }

    /// <summary>Who to call about pesticide spraying near the yard.</summary>
    public string? SprayContact { get; init; }
}

/// <summary>What a landowner gets for hosting a yard (FR-2).</summary>
public enum LandownerTerms
{
    /// <summary>Paid in honey.</summary>
    Honey,

    /// <summary>Paid a fee.</summary>
    Fee,

    /// <summary>Nothing.</summary>
    Free,
}

/// <summary>The states a yard may be in: Texas at launch; New Mexico later (FR-1).</summary>
public enum UsState
{
    /// <summary>Texas.</summary>
    Texas,
}
