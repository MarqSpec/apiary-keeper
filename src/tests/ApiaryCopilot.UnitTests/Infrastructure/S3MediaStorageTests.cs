using System.Globalization;
using System.Web;
using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.Extensions.Options;

namespace ApiaryCopilot.UnitTests.Infrastructure;

// The wrapper around the AWS SDK (CONVENTIONS.md §3): pins what the code relies on in the URLs it signs -
// where they point, how long they live and what they bind. Signing is offline, so no bucket is reached; that the bucket
// enforces what is signed is the integration tier's (MinIO).
public sealed class S3MediaStorageTests
{
    private const string SyntheticKeyId = "synthetic-key-id";
    private const string SyntheticSecret = "synthetic-secret-never-in-a-url";

    private static readonly MediaObjectKey Key = MediaObjectKey.New(
        new AccountId(Guid.Parse("11111111-1111-4111-8111-111111111111")),
        new InspectionId(Guid.Parse("0199b2a4-0000-7000-8000-0000000000d1")));

    [Fact] // invariant (D-13): an upload URL points at the endpoint phones reach, never the one only the API reaches
    public void PresignUpload_ShouldPointAtThePublicEndpoint_WhenOneIsSet()
    {
        using var storage = Storage(o => o.PublicUrl = "https://media.example.test:8443");

        var url = storage.PresignUpload(Key, "image/jpeg", 1234).Url;

        url.Scheme.Should().Be("https");
        url.Host.Should().Be("media.example.test");
        url.Port.Should().Be(8443);
        url.AbsolutePath.Should().Be($"/synthetic-media/{Key.Value}");
    }

    [Fact] // regression risk: a local http bucket gets http URLs, or the phone's PUT never reaches it
    public void PresignUpload_ShouldUseHttp_WhenThePublicEndpointIsALocalHttpOne()
    {
        using var storage = Storage(o =>
        {
            o.AllowHttp = true;
            o.ServiceUrl = "http://media:9000";
            o.PublicUrl = "http://127.0.0.1:9000";
        });

        var url = storage.PresignUpload(Key, "image/jpeg", 1234).Url;

        url.GetLeftPart(UriPartial.Authority).Should().Be("http://127.0.0.1:9000");
    }

    [Fact] // invariant: with no public endpoint the URL points at the service endpoint
    public void PresignUpload_ShouldPointAtTheServiceEndpoint_WhenNoPublicOneIsSet()
    {
        using var storage = Storage();

        storage.PresignUpload(Key, "image/jpeg", 1234).Url.Host.Should().Be("bucket.example.test");
    }

    [Fact] // regression risk: without path style the bucket is the host's first label (Railway's virtual-hosted URLs)
    public void PresignUpload_ShouldPutTheBucketInTheHost_WhenPathStyleIsOff()
    {
        using var storage = Storage(o => o.ForcePathStyle = false);

        var url = storage.PresignUpload(Key, "image/jpeg", 1234).Url;

        url.Host.Should().Be("synthetic-media.bucket.example.test");
        url.AbsolutePath.Should().Be($"/{Key.Value}");
    }

    [Fact] // invariant (token custody): the content type and the length are signed, so the bucket refuses any other
    public void PresignUpload_ShouldSignTheContentTypeAndLength_WhenIssued()
    {
        using var storage = Storage();

        var query = Query(storage.PresignUpload(Key, "video/mp4", 52_428_800).Url);

        query["X-Amz-SignedHeaders"]!.Split(';').Should().BeEquivalentTo("content-length", "content-type", "host");
    }

    [Fact] // invariant (token custody): an upload URL lives exactly the configured lifetime, and says when it ends
    public void PresignUpload_ShouldExpireAfterTheUploadLifetime_WhenIssued()
    {
        using var storage = Storage(o => o.UploadUrlLifetime = TimeSpan.FromMinutes(7));
        var before = DateTimeOffset.UtcNow;

        var presigned = storage.PresignUpload(Key, "image/jpeg", 1234);

        long.Parse(Query(presigned.Url)["X-Amz-Expires"]!, CultureInfo.InvariantCulture).Should().BeInRange(419, 420);
        presigned.ExpiresAt.Should().BeCloseTo(before + TimeSpan.FromMinutes(7), TimeSpan.FromSeconds(5));
    }

    [Fact] // invariant (token custody): a download URL lives the download lifetime and binds nothing but the host
    public void PresignDownload_ShouldExpireAfterTheDownloadLifetime_WhenIssued()
    {
        using var storage = Storage(o => o.DownloadUrlLifetime = TimeSpan.FromMinutes(3));

        var presigned = storage.PresignDownload(Key);

        var query = Query(presigned.Url);
        long.Parse(query["X-Amz-Expires"]!, CultureInfo.InvariantCulture).Should().BeInRange(179, 180);
        query["X-Amz-SignedHeaders"].Should().Be("host");
        presigned.Url.AbsolutePath.Should().Be($"/synthetic-media/{Key.Value}");
    }

    [Fact] // invariant (token custody): a URL is signed with the configured key id and region, and never carries the secret
    public void PresignUpload_ShouldSignWithTheConfiguredCredentialAndNeverCarryTheSecret_WhenIssued()
    {
        using var storage = Storage();

        var url = storage.PresignUpload(Key, "image/jpeg", 1234).Url;

        Query(url)["X-Amz-Credential"].Should().MatchRegex($"^{SyntheticKeyId}/\\d{{8}}/auto/s3/aws4_request$");
        Query(url)["X-Amz-Signature"].Should().MatchRegex("^[0-9a-f]{64}$");
        url.ToString().Should().NotContain(SyntheticSecret);
    }

    [Fact] // boundary: with no bucket configured nothing is signed and nothing is read
    public async Task Members_ShouldThrowInvalidOperation_WhenNoBucketIsConfigured()
    {
        using var storage = new S3MediaStorage(Options.Create(new MediaOptions()), TimeProvider.System);

        storage.IsConfigured.Should().BeFalse();
        storage.Invoking(s => s.PresignUpload(Key, "image/jpeg", 1)).Should().Throw<InvalidOperationException>();
        storage.Invoking(s => s.PresignDownload(Key)).Should().Throw<InvalidOperationException>();
        await storage.Awaiting(s => s.FindAsync(Key, CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
    }

    private static S3MediaStorage Storage(Action<MediaOptions>? configure = null)
    {
        var options = new MediaOptions
        {
            ServiceUrl = "https://bucket.example.test",
            Bucket = "synthetic-media",
            AccessKeyId = SyntheticKeyId,
            SecretAccessKey = SyntheticSecret,
            ForcePathStyle = true,
        };
        configure?.Invoke(options);
        return new S3MediaStorage(Options.Create(options), TimeProvider.System);
    }

    private static System.Collections.Specialized.NameValueCollection Query(Uri url) => HttpUtility.ParseQueryString(url.Query);
}
