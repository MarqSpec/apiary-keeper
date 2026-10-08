using ApiaryCopilot.Infrastructure;

namespace ApiaryCopilot.UnitTests.Infrastructure;

// The media settings (DEPLOYMENT.md §3): a bucket is all four connection settings or none, endpoints are https unless a
// local bucket is flagged, and every limit is bounded. A failure names the setting, never its value.
public sealed class MediaOptionsTests
{
    private const string SyntheticSecret = "synthetic-secret-do-not-echo";

    private readonly MediaOptionsValidator _validator = new();

    [Fact] // boundary: no bucket at all is valid - the media endpoints then answer 503
    public void Validate_ShouldSucceedAndReportNoBucket_WhenNothingIsSet()
    {
        var options = new MediaOptions();

        _validator.Validate(null, options).Succeeded.Should().BeTrue();
        options.IsConfigured.Should().BeFalse();
    }

    [Fact] // control: a complete https bucket is valid and configured
    public void Validate_ShouldSucceedAndReportABucket_WhenAllFourConnectionSettingsAreSet()
    {
        var options = Configured();

        _validator.Validate(null, options).Succeeded.Should().BeTrue();
        options.IsConfigured.Should().BeTrue();
    }

    [Theory] // invariant: a half-configured bucket stops the host rather than running without one
    [InlineData(nameof(MediaOptions.ServiceUrl))]
    [InlineData(nameof(MediaOptions.Bucket))]
    [InlineData(nameof(MediaOptions.AccessKeyId))]
    [InlineData(nameof(MediaOptions.SecretAccessKey))]
    public void Validate_ShouldFail_WhenOneConnectionSettingIsMissing(string missing)
    {
        var options = Configured();
        typeof(MediaOptions).GetProperty(missing)!.SetValue(options, " ");

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        options.IsConfigured.Should().BeFalse();
    }

    [Theory] // boundary (§5, token custody): an endpoint is https, a bare origin, with no credentials in it
    [InlineData("http://bucket.example.test")]
    [InlineData("ftp://bucket.example.test")]
    [InlineData("bucket.example.test")]
    [InlineData("https://bucket.example.test/media")]
    [InlineData("https://bucket.example.test/?x=1")]
    [InlineData("https://user:pass@bucket.example.test")]
    public void Validate_ShouldFail_WhenTheServiceUrlIsNotABareHttpsOrigin(string url)
    {
        var options = Configured();
        options.ServiceUrl = url;

        _validator.Validate(null, options).Failed.Should().BeTrue();
    }

    [Theory] // boundary: the public endpoint is held to the same rule as the service endpoint
    [InlineData("http://127.0.0.1:9000")]
    [InlineData("https://bucket.example.test/media")]
    public void Validate_ShouldFail_WhenThePublicUrlIsNotABareHttpsOrigin(string url)
    {
        var options = Configured();
        options.PublicUrl = url;

        _validator.Validate(null, options).Failed.Should().BeTrue();
    }

    [Fact] // boundary: http is accepted only for a bucket explicitly flagged as local
    public void Validate_ShouldSucceed_WhenHttpEndpointsAreFlaggedAsLocal()
    {
        var options = Configured();
        options.ServiceUrl = "http://media:9000";
        options.PublicUrl = "http://127.0.0.1:9000";
        options.AllowHttp = true;

        _validator.Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Theory] // boundary (token custody): a URL lives at least a second and at most an hour
    [InlineData("00:00:00")]
    [InlineData("01:00:01")]
    [InlineData("-00:01:00")]
    public void Validate_ShouldFail_WhenAUrlLifetimeIsOutOfBounds(string lifetime)
    {
        var upload = Configured();
        upload.UploadUrlLifetime = TimeSpan.Parse(lifetime, System.Globalization.CultureInfo.InvariantCulture);
        var download = Configured();
        download.DownloadUrlLifetime = TimeSpan.Parse(lifetime, System.Globalization.CultureInfo.InvariantCulture);

        _validator.Validate(null, upload).Failed.Should().BeTrue();
        _validator.Validate(null, download).Failed.Should().BeTrue();
    }

    [Theory] // control for the bounds above: their edges are accepted
    [InlineData("00:00:01")]
    [InlineData("01:00:00")]
    public void Validate_ShouldSucceed_WhenAUrlLifetimeIsAtABound(string lifetime)
    {
        var options = Configured();
        options.UploadUrlLifetime = TimeSpan.Parse(lifetime, System.Globalization.CultureInfo.InvariantCulture);
        options.DownloadUrlLifetime = options.UploadUrlLifetime;

        _validator.Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Theory] // boundary: a size limit is at least a byte and at most what one PUT may carry
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(MediaOptions.MaxObjectBytes + 1)]
    public void Validate_ShouldFail_WhenASizeLimitIsOutOfBounds(long bytes)
    {
        var image = Configured();
        image.MaxImageBytes = bytes;
        var video = Configured();
        video.MaxVideoBytes = bytes;

        _validator.Validate(null, image).Failed.Should().BeTrue();
        _validator.Validate(null, video).Failed.Should().BeTrue();
    }

    [Theory] // boundary (adversarial): only image types may be listed as images and only video types as video
    [InlineData("image/jpeg text/html", "video/mp4")]
    [InlineData("image/jpeg", "video/mp4 image/svg+xml text/html")]
    [InlineData("image/jpeg video/mp4", "video/mp4")]
    [InlineData("", "video/mp4")]
    [InlineData("image/jpeg", "")]
    [InlineData("image/*", "video/mp4")]
    [InlineData("IMAGE/JPEG", "video/mp4")]
    public void Validate_ShouldFail_WhenAContentTypeListHoldsAnythingElse(string images, string videos)
    {
        var options = Configured();
        options.ImageContentTypes = images;
        options.VideoContentTypes = videos;

        _validator.Validate(null, options).Failed.Should().BeTrue();
    }

    [Theory] // boundary (adversarial, token custody): a type a browser renders as a document - SVG, any +suffix - is never signed
    [InlineData("image/jpeg image/svg+xml", "video/mp4")]
    [InlineData("image/svg", "video/mp4")]
    [InlineData("image/x-svg", "video/mp4")]
    [InlineData("image/vnd.example+xml", "video/mp4")]
    [InlineData("image/xml", "video/mp4")]
    [InlineData("image/x-html", "video/mp4")]
    [InlineData("image/jpeg", "video/mp4 video/vnd.example+xml")]
    [InlineData("image/jpeg", "video/vnd.example+zip")]
    public void Validate_ShouldFail_WhenAContentTypeListHoldsAnActiveContentType(string images, string videos)
    {
        var options = Configured();
        options.ImageContentTypes = images;
        options.VideoContentTypes = videos;

        var result = _validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().ContainSingle().Which.Should().Contain("ContentTypes");
    }

    [Fact] // control for the refusal above: real phone image and video types, dots and hyphens included, are accepted
    public void Validate_ShouldSucceed_WhenTheContentTypesAreMediaTypesWithDotsAndHyphens()
    {
        var options = Configured();
        options.ImageContentTypes = "image/jpeg image/png image/webp image/heic image/heif image/avif image/heic-sequence image/vnd.ms-photo";
        options.VideoContentTypes = "video/mp4 video/quicktime video/webm video/x-matroska video/3gpp";

        _validator.Validate(null, options).Succeeded.Should().BeTrue();
    }

    [Fact] // invariant (§6): a failure names the setting, never a value - the secret above all
    public void Validate_ShouldNotEchoAnyValue_WhenItFails()
    {
        var options = Configured();
        options.ServiceUrl = "http://synthetic-endpoint.example.test";
        options.Bucket = null;

        var failures = string.Join(" ", _validator.Validate(null, options).Failures!);

        failures.Should().Contain("Media__ServiceUrl");
        failures.Should().NotContain(SyntheticSecret).And.NotContain("synthetic-endpoint").And.NotContain("synthetic-key-id");
    }

    [Theory] // invariant: an allowed type maps to its kind's limit; anything else - HTML above all - has none
    [InlineData("image/jpeg", 20L * 1024 * 1024)]
    [InlineData("image/heic", 20L * 1024 * 1024)]
    [InlineData("video/mp4", 200L * 1024 * 1024)]
    [InlineData("video/quicktime", 200L * 1024 * 1024)]
    [InlineData("text/html", null)]
    [InlineData("image/svg+xml", null)]
    [InlineData("application/octet-stream", null)]
    [InlineData("IMAGE/JPEG", null)]
    [InlineData("image/jpeg; charset=utf-8", null)]
    [InlineData("", null)]
    public void MaxBytesFor_ShouldReturnTheKindsLimitOrNone_WhenGivenAContentType(string contentType, long? expected)
    {
        new MediaOptions().MaxBytesFor(contentType).Should().Be(expected);
    }

    private static MediaOptions Configured() => new()
    {
        ServiceUrl = "https://bucket.example.test",
        Bucket = "synthetic-media",
        AccessKeyId = "synthetic-key-id",
        SecretAccessKey = SyntheticSecret,
    };
}
