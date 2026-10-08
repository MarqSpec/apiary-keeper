using ApiaryCopilot.Api.Media;
using ApiaryCopilot.Infrastructure;

namespace ApiaryCopilot.UnitTests.Api;

// What an upload may declare (FR-8): an allowed image or video type and a size within that kind's configured limit.
public sealed class MediaRequestParserTests
{
    private readonly MediaOptions _options = new() { MaxImageBytes = 1000, MaxVideoBytes = 5000 };

    [Theory] // invariant: an allowed type within its kind's limit parses, in its lower-case spelling
    [InlineData("image/jpeg", 1000L, "image/jpeg")]
    [InlineData(" IMAGE/PNG ", 1L, "image/png")]
    [InlineData("video/mp4", 5000L, "video/mp4")]
    [InlineData("video/quicktime", 1001L, "video/quicktime")]
    public void Parse_ShouldReturnTheUpload_WhenTheTypeIsAllowedAndTheSizeWithinItsLimit(string contentType, long size, string expected)
    {
        var parsed = MediaRequestParser.Parse(new MediaUploadRequest { ContentType = contentType, SizeBytes = size }, _options);

        parsed.Errors.Should().BeEmpty();
        parsed.Value.Should().Be(new MediaUpload(expected, size));
    }

    [Theory] // boundary (adversarial): anything but an allowed image or video type is refused - HTML and SVG above all
    [InlineData(null)]
    [InlineData("")]
    [InlineData("text/html")]
    [InlineData("image/svg+xml")]
    [InlineData("application/octet-stream")]
    [InlineData("image/jpeg; charset=utf-8")]
    [InlineData("image/*")]
    [InlineData("video/x-msvideo")]
    public void Parse_ShouldRefuseTheContentType_WhenItIsNotAnAllowedImageOrVideoType(string? contentType)
    {
        var parsed = MediaRequestParser.Parse(new MediaUploadRequest { ContentType = contentType, SizeBytes = 10 }, _options);

        parsed.Value.Should().BeNull();
        parsed.Errors.Keys.Should().Equal("contentType");
        parsed.Errors["contentType"][0].Should().StartWith("Validation:");
    }

    [Theory] // boundary: a size is at least a byte and at most its kind's limit - an image's, not a video's
    [InlineData("image/jpeg", null)]
    [InlineData("image/jpeg", 0L)]
    [InlineData("image/jpeg", -1L)]
    [InlineData("image/jpeg", 1001L)]
    [InlineData("video/mp4", 5001L)]
    public void Parse_ShouldRefuseTheSize_WhenItIsMissingOrOutsideTheKindsLimit(string contentType, long? size)
    {
        var parsed = MediaRequestParser.Parse(new MediaUploadRequest { ContentType = contentType, SizeBytes = size }, _options);

        parsed.Value.Should().BeNull();
        parsed.Errors.Keys.Should().Equal("sizeBytes");
        parsed.Errors["sizeBytes"][0].Should().StartWith("Validation:");
    }
}
