using ApiaryCopilot.Domain;

namespace ApiaryCopilot.Infrastructure;

/// <summary>
/// The media bucket (Interface F, D-13): short-lived presigned URLs for phones to upload and download objects directly,
/// and a read of what the bucket holds at a key. The bucket's credentials never leave this service; a URL grants one
/// operation on one key until it expires, and is never logged.
/// </summary>
public interface IMediaStorage
{
    /// <summary>Whether a bucket is configured (<see cref="MediaOptions.IsConfigured"/>); otherwise every other member throws.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// A URL to <c>PUT</c> exactly <paramref name="sizeBytes"/> bytes of <paramref name="contentType"/> to
    /// <paramref name="key"/>, valid for <see cref="MediaOptions.UploadUrlLifetime"/>. The content type and length are
    /// signed: the bucket refuses an upload that sends any other, or arrives after the URL expires.
    /// </summary>
    /// <exception cref="InvalidOperationException">No bucket is configured.</exception>
    PresignedUrl PresignUpload(MediaObjectKey key, string contentType, long sizeBytes);

    /// <summary>A URL to <c>GET</c> the object at <paramref name="key"/>, valid for <see cref="MediaOptions.DownloadUrlLifetime"/>.</summary>
    /// <exception cref="InvalidOperationException">No bucket is configured.</exception>
    PresignedUrl PresignDownload(MediaObjectKey key);

    /// <summary>What the bucket holds at <paramref name="key"/>, or <see langword="null"/> when it holds nothing there.</summary>
    /// <exception cref="InvalidOperationException">No bucket is configured.</exception>
    /// <exception cref="MediaStorageUnavailableException">The bucket did not answer, or refused the read.</exception>
    Task<StoredObject?> FindAsync(MediaObjectKey key, CancellationToken cancellationToken);
}

/// <summary>A presigned URL and the moment it stops working.</summary>
public sealed record PresignedUrl(Uri Url, DateTimeOffset ExpiresAt);

/// <summary>The media bucket could not be read; the caller retries later.</summary>
public sealed class MediaStorageUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public MediaStorageUnavailableException()
        : base("The media bucket did not answer.")
    {
    }

    /// <summary>Creates the exception with its message.</summary>
    public MediaStorageUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with its message and the failure behind it.</summary>
    public MediaStorageUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
