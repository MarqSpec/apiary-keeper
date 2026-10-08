using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApiaryCopilot.Api.Media;

/// <summary>What the caller means to upload. Every field is checked (FR-8).</summary>
[Description("A photo or video the caller means to upload to an inspection.")]
public sealed record MediaUploadRequest
{
    /// <summary>The content type.</summary>
    [Description("The file's content type, e.g. image/jpeg or video/mp4: one of the image or video types the server allows. Required.")]
    public string? ContentType { get; init; }

    /// <summary>The size in bytes.</summary>
    [Description("The file's exact size in bytes; the upload must send exactly this many. Required; at least 1, at most the server's limit for the type.")]
    public long? SizeBytes { get; init; }
}

/// <summary>Where and how to upload the file.</summary>
[Description("A short-lived URL to upload one file straight to the media bucket, and the key to attach it by.")]
public sealed record MediaUploadResult(
    [property: Description("The object's key: pass it to attach the file once uploaded.")] string ObjectKey,
    [property: Description("The presigned URL to send the file to. It is a credential: keep it only until the upload is done.")] Uri UploadUrl,
    [property: Description("The HTTP method to send it with: PUT.")] string Method,
    [property: Description("Headers the upload must send exactly as given; the body must be exactly sizeBytes long.")] IReadOnlyDictionary<string, string> Headers,
    [property: Description("When the URL stops working; an upload started later is refused.")] DateTimeOffset ExpiresAt);

/// <summary>An uploaded file to attach.</summary>
[Description("An uploaded file to attach to the inspection.")]
public sealed record AttachMediaRequest
{
    /// <summary>The object key.</summary>
    [Description("The objectKey the upload request returned for this inspection. Required.")]
    public string? ObjectKey { get; init; }
}

/// <summary>An attached photo or video as the API returns it.</summary>
[Description("A photo or video attached to an inspection.")]
public sealed record MediaResult(
    [property: Description("The media's id.")] Guid Id,
    [property: Description("The inspection it is attached to.")] Guid InspectionId,
    [property: Description("Its content type, as the bucket stored it.")] string ContentType,
    [property: Description("Its size in bytes, as the bucket stored it.")] long SizeBytes,
    [property: Description("When the server attached it.")] DateTimeOffset AttachedAt,
    [property: Description("A presigned URL to download it. It is a credential: do not store or share it.")] Uri DownloadUrl,
    [property: Description("When the download URL stops working; read the media again for a fresh one.")] DateTimeOffset DownloadUrlExpiresAt);

/// <summary>One page of an inspection's media.</summary>
[Description("One page of an inspection's photos and videos, in the order attached.")]
public sealed record MediaPage(
    [property: Description("The media on this page.")] IReadOnlyList<MediaResult> Items,
    [property: Description("Pass as cursor to get the next page; null on the last page.")] string? NextCursor);

/// <summary>Source-generated JSON for the media contracts (camelCase, web defaults).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(MediaUploadRequest))]
[JsonSerializable(typeof(MediaUploadResult))]
[JsonSerializable(typeof(AttachMediaRequest))]
[JsonSerializable(typeof(MediaResult))]
[JsonSerializable(typeof(MediaPage))]
public sealed partial class MediaJsonContext : JsonSerializerContext;
