using ApiaryCopilot.Api.Colonies;
using ApiaryCopilot.Infrastructure;

namespace ApiaryCopilot.Api.Media;

/// <summary>An upload request as parsed: a content type the server allows, in its one lower-case spelling, and a size within its limit.</summary>
public sealed record MediaUpload(string ContentType, long SizeBytes);

/// <summary>
/// Parses a <see cref="MediaUploadRequest"/> against the configured limits (<see cref="MediaOptions"/>); never throws.
/// The content type is trimmed and lower-cased, then must be exactly one of the allowed image or video types - no
/// parameters - and the size must be 1 byte to that type's limit. Every invalid field is reported, keyed by its JSON
/// name, with a message that starts <c>Validation:</c>.
/// </summary>
public static class MediaRequestParser
{
    /// <summary>Parses the request.</summary>
    public static ParseResult<MediaUpload> Parse(MediaUploadRequest request, MediaOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var contentType = request.ContentType?.Trim().ToLowerInvariant();
        var maxBytes = string.IsNullOrEmpty(contentType) ? null : options.MaxBytesFor(contentType);
        if (maxBytes is null)
        {
            errors["contentType"] = [$"Validation: contentType is required, and must be one of the image types ({options.ImageContentTypes}) or video types ({options.VideoContentTypes})."];
        }

        if (request.SizeBytes is not { } size || size < 1)
        {
            errors["sizeBytes"] = ["Validation: sizeBytes is required, the file's size in bytes, at least 1."];
        }
        else if (maxBytes is { } max && size > max)
        {
            errors["sizeBytes"] = [$"Validation: sizeBytes must be at most {max} for {contentType}."];
        }

        return errors.Count > 0
            ? new ParseResult<MediaUpload>(null, errors)
            : new ParseResult<MediaUpload>(new MediaUpload(contentType!, request.SizeBytes!.Value), errors);
    }
}
