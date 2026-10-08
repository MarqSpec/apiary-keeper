using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace ApiaryCopilot.Infrastructure;

/// <summary>
/// The media bucket and the limits on the URLs issued for it (D-13), set as <c>Media__*</c> in the environment
/// (DEPLOYMENT.md §3). The four connection settings are set together or not at all; unset, there is no bucket and the
/// media endpoints answer 503. The credentials come only from the environment, never from shipped configuration.
/// </summary>
public sealed partial class MediaOptions
{
    /// <summary>The configuration section: <c>Media</c>.</summary>
    public const string SectionName = "Media";

    /// <summary>The largest object a single upload may carry: S3's limit for one PUT, 5 GiB.</summary>
    public const long MaxObjectBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>The shortest lifetime a URL may be given.</summary>
    public static readonly TimeSpan MinUrlLifetime = TimeSpan.FromSeconds(1);

    /// <summary>The longest lifetime a URL may be given: an hour, though a bucket would accept far longer.</summary>
    public static readonly TimeSpan MaxUrlLifetime = TimeSpan.FromHours(1);

    /// <summary>The S3 endpoint the API itself calls, e.g. <c>https://t3.storageapi.dev</c>.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>The S3 endpoint in the URLs handed to phones; unset, <see cref="ServiceUrl"/>.</summary>
    public string? PublicUrl { get; set; }

    /// <summary>The bucket's name for the S3 API.</summary>
    public string? Bucket { get; set; }

    /// <summary>The region URLs are signed for: <c>auto</c> on Railway.</summary>
    public string Region { get; set; } = "auto";

    /// <summary>The bucket credential's key id.</summary>
    public string? AccessKeyId { get; set; }

    /// <summary>The bucket credential's secret. Secret: never logged, echoed or returned.</summary>
    public string? SecretAccessKey { get; set; }

    /// <summary>Path-style URLs (<c>endpoint/bucket/key</c>) instead of virtual-hosted ones; MinIO needs them.</summary>
    public bool ForcePathStyle { get; set; }

    /// <summary>Allows <c>http</c> endpoints, for a local bucket only; off, both endpoints must be <c>https</c>.</summary>
    public bool AllowHttp { get; set; }

    /// <summary>How long an upload URL stays valid.</summary>
    public TimeSpan UploadUrlLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a download URL stays valid.</summary>
    public TimeSpan DownloadUrlLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>The image content types an upload may declare, space-separated.</summary>
    public string ImageContentTypes { get; set; } = "image/jpeg image/png image/webp image/heic image/heif";

    /// <summary>The video content types an upload may declare, space-separated.</summary>
    public string VideoContentTypes { get; set; } = "video/mp4 video/quicktime video/webm";

    /// <summary>The largest image an upload may declare, in bytes: 20 MiB by default.</summary>
    public long MaxImageBytes { get; set; } = 20L * 1024 * 1024;

    /// <summary>The largest video an upload may declare, in bytes: 200 MiB by default.</summary>
    public long MaxVideoBytes { get; set; } = 200L * 1024 * 1024;

    /// <summary>Whether a bucket is configured: the four connection settings are all set.</summary>
    public bool IsConfigured => ConnectionSettings().All(s => !string.IsNullOrWhiteSpace(s));

    /// <summary>
    /// The largest size an upload of <paramref name="contentType"/> may declare, or <see langword="null"/> when that
    /// content type is not an allowed image or video type. Matched exactly, in lower case.
    /// </summary>
    public long? MaxBytesFor(string contentType) =>
        Split(ImageContentTypes).Contains(contentType, StringComparer.Ordinal) ? MaxImageBytes
        : Split(VideoContentTypes).Contains(contentType, StringComparer.Ordinal) ? MaxVideoBytes
        : null;

    internal IEnumerable<string> Validate()
    {
        var set = ConnectionSettings().Count(s => !string.IsNullOrWhiteSpace(s));
        if (set is > 0 and < 4)
        {
            yield return "Media__ServiceUrl, Media__Bucket, Media__AccessKeyId and Media__SecretAccessKey are set together or not at all.";
        }

        if (!IsEndpoint(ServiceUrl, required: set > 0))
        {
            yield return $"Media__ServiceUrl must be an absolute https URL{HttpHint} with no path, query or user info.";
        }

        if (!IsEndpoint(PublicUrl, required: false))
        {
            yield return $"Media__PublicUrl must be an absolute https URL{HttpHint} with no path, query or user info, or unset.";
        }

        if (string.IsNullOrWhiteSpace(Region))
        {
            yield return "Media__Region must be set (auto on Railway).";
        }

        if (UploadUrlLifetime < MinUrlLifetime || UploadUrlLifetime > MaxUrlLifetime)
        {
            yield return "Media__UploadUrlLifetime must be between 00:00:01 and 01:00:00.";
        }

        if (DownloadUrlLifetime < MinUrlLifetime || DownloadUrlLifetime > MaxUrlLifetime)
        {
            yield return "Media__DownloadUrlLifetime must be between 00:00:01 and 01:00:00.";
        }

        if (MaxImageBytes is < 1 or > MaxObjectBytes)
        {
            yield return $"Media__MaxImageBytes must be between 1 and {MaxObjectBytes}.";
        }

        if (MaxVideoBytes is < 1 or > MaxObjectBytes)
        {
            yield return $"Media__MaxVideoBytes must be between 1 and {MaxObjectBytes}.";
        }

        if (!AreContentTypes(ImageContentTypes, "image"))
        {
            yield return "Media__ImageContentTypes must be one or more image/<subtype> content types, lower case, space-separated, none of them SVG, XML, HTML or a +suffix type.";
        }

        if (!AreContentTypes(VideoContentTypes, "video"))
        {
            yield return "Media__VideoContentTypes must be one or more video/<subtype> content types, lower case, space-separated, none of them SVG, XML, HTML or a +suffix type.";
        }
    }

    private string HttpHint => AllowHttp ? " (or http, Media__AllowHttp being on)" : string.Empty;

    private string?[] ConnectionSettings() => [ServiceUrl, Bucket, AccessKeyId, SecretAccessKey];

    private bool IsEndpoint(string? value, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return !required;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (AllowHttp && uri.Scheme == Uri.UriSchemeHttp))
            && uri.AbsolutePath == "/"
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0
            && uri.UserInfo.Length == 0;
    }

    private static bool AreContentTypes(string? value, string type)
    {
        var types = Split(value);
        return types.Length > 0 && types.All(t =>
            ContentTypePattern().IsMatch(t)
            && t.StartsWith(type + "/", StringComparison.Ordinal)
            && !ActiveContentSubtype().IsMatch(t[(type.Length + 1)..]));
    }

    private static string[] Split(string? value) =>
        (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex("^(image|video)/[a-z0-9][a-z0-9.+-]{0,62}$")]
    private static partial Regex ContentTypePattern();

    // A browser renders these as documents from the bucket's origin (SVG runs script); no photo or video is one.
    [GeneratedRegex("[+]|svg|xml|html")]
    private static partial Regex ActiveContentSubtype();
}

/// <summary>Validates <see cref="MediaOptions"/> at startup; a failure names the setting, never its value.</summary>
public sealed class MediaOptionsValidator : IValidateOptions<MediaOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, MediaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = options.Validate().ToList();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
