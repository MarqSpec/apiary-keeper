using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ApiaryCopilot.Domain;
using Microsoft.Extensions.Options;

namespace ApiaryCopilot.Infrastructure;

// The one place the AWS SDK is called (CONVENTIONS.md §3). Two clients: one signs URLs for the endpoint phones
// reach (PublicUrl), one reads the bucket from the endpoint the API reaches (ServiceUrl); signing needs no network. The
// credentials are the configured pair only, never the SDK's default chain, and the SDK writes no log of its own here.
internal sealed class S3MediaStorage : IMediaStorage, IDisposable
{
    // Each read is one HEAD: a short per-attempt timeout, and the SDK's standard retry mode - transient faults only,
    // exponential backoff with jitter, and a retry quota that stops retrying while the bucket keeps failing (§5).
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(5);
    private const int MaxRetries = 2;

    private readonly MediaOptions _options;
    private readonly TimeProvider _clock;
    private readonly AmazonS3Client? _service;
    private readonly AmazonS3Client? _signer;
    private readonly Protocol _publicProtocol;

    public S3MediaStorage(IOptions<MediaOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;
        if (!_options.IsConfigured)
        {
            return;
        }

        var credentials = new BasicAWSCredentials(_options.AccessKeyId, _options.SecretAccessKey);
        var publicUrl = string.IsNullOrWhiteSpace(_options.PublicUrl) ? _options.ServiceUrl! : _options.PublicUrl;
        _service = new AmazonS3Client(credentials, Config(_options.ServiceUrl!));
        _signer = new AmazonS3Client(credentials, Config(publicUrl));
        _publicProtocol = new Uri(publicUrl).Scheme == Uri.UriSchemeHttp ? Protocol.HTTP : Protocol.HTTPS;
    }

    public bool IsConfigured => _options.IsConfigured;

    public PresignedUrl PresignUpload(MediaObjectKey key, string contentType, long sizeBytes)
    {
        ArgumentNullException.ThrowIfNull(key);
        var expiresAt = _clock.GetUtcNow() + _options.UploadUrlLifetime;
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key.Value,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = expiresAt.UtcDateTime,
            Protocol = _publicProtocol,
        };
        // Signed with the URL, so the bucket refuses a body of any other length.
        request.Headers.ContentLength = sizeBytes;
        return new PresignedUrl(new Uri(Signer().GetPreSignedURL(request)), expiresAt);
    }

    public PresignedUrl PresignDownload(MediaObjectKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var expiresAt = _clock.GetUtcNow() + _options.DownloadUrlLifetime;
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key.Value,
            Verb = HttpVerb.GET,
            Expires = expiresAt.UtcDateTime,
            Protocol = _publicProtocol,
        };
        return new PresignedUrl(new Uri(Signer().GetPreSignedURL(request)), expiresAt);
    }

    public async Task<StoredObject?> FindAsync(MediaObjectKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var service = _service ?? throw NotConfigured();
        try
        {
            var metadata = await service.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = _options.Bucket, Key = key.Value },
                cancellationToken);
            return new StoredObject(metadata.Headers.ContentType ?? string.Empty, metadata.ContentLength);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception e) when (e is AmazonServiceException or AmazonClientException or HttpRequestException
            || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // The message names no key, URL or credential; the inner exception's may, so callers log only its type.
            throw new MediaStorageUnavailableException("The media bucket did not answer the read.", e);
        }
    }

    public void Dispose()
    {
        _service?.Dispose();
        _signer?.Dispose();
    }

    private AmazonS3Client Signer() => _signer ?? throw NotConfigured();

    private static InvalidOperationException NotConfigured() =>
        new("No media bucket is configured: set the Media__ settings (DEPLOYMENT.md §3).");

    private AmazonS3Config Config(string endpoint) => new()
    {
        ServiceURL = endpoint,
        ForcePathStyle = _options.ForcePathStyle,
        AuthenticationRegion = _options.Region,
        Timeout = AttemptTimeout,
        RetryMode = RequestRetryMode.Standard,
        MaxErrorRetry = MaxRetries,
    };
}
