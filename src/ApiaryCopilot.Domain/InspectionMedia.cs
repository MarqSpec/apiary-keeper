namespace ApiaryCopilot.Domain;

/// <summary>
/// A photo or short video attached to an inspection, with when it was attached (UC-1, FR-8). The bytes live in the media
/// bucket under <see cref="ObjectKey"/>, uploaded by the phone straight to the bucket (D-13); this records that the
/// object belongs to the inspection. This record is attached once and never changed; the object's bytes are not: its
/// upload URL can be replayed until it expires, so whoever holds it may overwrite the object, with the same signed type
/// and length, for up to <c>Media__UploadUrlLifetime</c> after issue, attach included (INTERFACES.md F.3).
/// Scoped to its account like every entity (NFR-8).
/// </summary>
public sealed class InspectionMedia : IAccountOwned, IRowStamped
{
    /// <summary>Maximum length of <see cref="ContentType"/>.</summary>
    public const int ContentTypeMaxLength = 100;

    /// <summary>The media's id.</summary>
    public required MediaId MediaId { get; init; }

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

    /// <summary>The inspection it is attached to.</summary>
    public required InspectionId InspectionId { get; init; }

    /// <summary>Where the object lives in the bucket; one object is attached at most once.</summary>
    public required MediaObjectKey ObjectKey { get; init; }

    /// <summary>The object's content type, as the bucket stored it.</summary>
    public required string ContentType { get; init; }

    /// <summary>The object's size in bytes, as the bucket stored it.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>When the server attached it: the media's timestamp, not when it was captured.</summary>
    public required DateTimeOffset AttachedAt { get; init; }
}

/// <summary>An object found in the media bucket: what the bucket says it holds.</summary>
public sealed record StoredObject(string ContentType, long SizeBytes);
