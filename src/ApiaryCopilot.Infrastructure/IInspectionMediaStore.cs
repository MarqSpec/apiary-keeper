using ApiaryCopilot.Domain;

namespace ApiaryCopilot.Infrastructure;

/// <summary>
/// The photos and videos attached to the current account's inspections (UC-1; FR-8). A media record is attached
/// once and never changed or removed: nothing here updates or deletes it (the object's bytes are the bucket's, and an
/// unexpired upload URL can still overwrite them - INTERFACES.md F.3). Every read and write is scoped to
/// <see cref="ICurrentAccount"/>; another account's inspection or media is never found, and a key issued to
/// another account or inspection is never attached.
/// </summary>
public interface IInspectionMediaStore
{
    /// <summary>Whether the account has an inspection with that id.</summary>
    Task<bool> InspectionExistsAsync(InspectionId inspectionId, CancellationToken cancellationToken);

    /// <summary>
    /// Attaches the object at <paramref name="key"/> to the account's inspection, stamped with the server's time,
    /// unless the inspection is not the account's, the key was not issued for it, or the key is already attached;
    /// then nothing is written.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no current account; nothing is written.</exception>
    Task<MediaAttachResult> AttachAsync(InspectionId inspectionId, MediaObjectKey key, StoredObject stored, CancellationToken cancellationToken);

    /// <summary>
    /// One page of the inspection's media in the order attached, or <see langword="null"/> when the account has
    /// no inspection with that id.
    /// </summary>
    Task<IReadOnlyList<InspectionMedia>?> ListAsync(InspectionId inspectionId, int skip, int take, CancellationToken cancellationToken);

    /// <summary>The media, or <see langword="null"/> when the account has none with that id.</summary>
    Task<InspectionMedia?> FindAsync(MediaId mediaId, CancellationToken cancellationToken);
}

/// <summary>How an attach ended.</summary>
public enum MediaAttachOutcome
{
    /// <summary>The media was attached.</summary>
    Attached,

    /// <summary>The account has no inspection with that id.</summary>
    NoSuchInspection,

    /// <summary>The key was issued to another account or for another inspection.</summary>
    KeyNotForThisInspection,

    /// <summary>The object is already attached.</summary>
    AlreadyAttached,
}

/// <summary>The outcome of an attach, with the media when it was attached.</summary>
public sealed record MediaAttachResult(MediaAttachOutcome Outcome, InspectionMedia? Media)
{
    /// <summary>The media was attached.</summary>
    public static MediaAttachResult Attached(InspectionMedia media) => new(MediaAttachOutcome.Attached, media);

    /// <summary>Nothing was attached, for <paramref name="outcome"/>.</summary>
    public static MediaAttachResult Refused(MediaAttachOutcome outcome) => new(outcome, null);
}
