using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace ApiaryCopilot.IntegrationTests;

// A throwaway MinIO for one fixture, as the media bucket (D-13): a container on the fixture's docker network, published
// on a free loopback port. The phone side of every test (this process) reaches it there, and so do the presigned URLs,
// which are signed for that address; an application container reaches it by name on the network. Its root credential is
// made up per run and is the server's bucket credential; nothing is stored. The same image compose.yaml runs, pinned by
// the same digest (DEPLOYMENT.md §1): a moved tag cannot bring unreviewed code onto CI's test network.
internal sealed class TestMediaBucket
{
    public const string Image = "pgsty/minio:RELEASE.2026-08-04T00-00-00Z@sha256:b6bfe7239bfc83fb90d31612d9704d86039dd714f7904b3f1ad68f211e602372";
    public const string BucketName = "media-it";
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(1);

    private readonly int _hostPort = FreePort();
    private bool _containerStarted;

    public TestMediaBucket(string name)
    {
        NetworkName = name;
        ContainerName = $"{name}-media";
        AccessKeyId = $"it{Guid.NewGuid():N}"[..20];
        SecretAccessKey = Guid.NewGuid().ToString("N");
    }

    /// <summary>The docker network it joins; the fixture's database created it.</summary>
    public string NetworkName { get; }

    /// <summary>The container's name, which is its host name on <see cref="NetworkName"/>.</summary>
    public string ContainerName { get; }

    /// <summary>The bucket credential's key id, made up per run.</summary>
    public string AccessKeyId { get; }

    /// <summary>The bucket credential's secret, made up per run.</summary>
    public string SecretAccessKey { get; }

    /// <summary>The endpoint from this machine: the tests, a server run from source, and every presigned URL.</summary>
    public string HostUrl => $"http://127.0.0.1:{_hostPort}";

    /// <summary>The endpoint from a container on <see cref="NetworkName"/>.</summary>
    public string NetworkUrl => $"http://{ContainerName}:9000";

    public async Task StartAsync()
    {
        await DockerAsync(
            $"run -d --rm --name {ContainerName} --network {NetworkName} -p 127.0.0.1:{_hostPort}:9000 " +
            $"-e MINIO_ROOT_USER={AccessKeyId} -e MINIO_ROOT_PASSWORD={SecretAccessKey} -e MINIO_BROWSER=off {Image} server /data");
        _containerStarted = true;
        await WaitUntilReadyAsync();
        using var client = Client();
        await client.PutBucketAsync(BucketName);
    }

    /// <summary>Writes an object straight to the bucket with the root credential, as nothing but the bucket's owner could.</summary>
    public async Task PutAsync(string key, byte[] body, string contentType)
    {
        using var client = Client();
        using var stream = new MemoryStream(body);
        await client.PutObjectAsync(new PutObjectRequest { BucketName = BucketName, Key = key, InputStream = stream, ContentType = contentType });
    }

    /// <summary>The object's bytes, read straight from the bucket, or <see langword="null"/> when there is none.</summary>
    public async Task<byte[]?> ReadAsync(string key)
    {
        using var client = Client();
        try
        {
            using var response = await client.GetObjectAsync(BucketName, key);
            using var copy = new MemoryStream();
            await response.ResponseStream.CopyToAsync(copy);
            return copy.ToArray();
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task StopAsync()
    {
        if (_containerStarted)
        {
            await DockerAsync($"rm -f {ContainerName}", throwOnFailure: false);
        }
    }

    private AmazonS3Client Client() => new(
        new BasicAWSCredentials(AccessKeyId, SecretAccessKey),
        new AmazonS3Config { ServiceURL = HostUrl, ForcePathStyle = true, AuthenticationRegion = "auto" });

    private async Task WaitUntilReadyAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + ReadyTimeout;
        while (true)
        {
            try
            {
                using var response = await http.GetAsync(new Uri($"{HostUrl}/minio/health/ready"));
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception e) when ((e is HttpRequestException or TaskCanceledException) && DateTime.UtcNow < deadline)
            {
                // Not listening yet.
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"MinIO was not ready within {ReadyTimeout}.");
            }

            await Task.Delay(500);
        }
    }

    private static async Task DockerAsync(string arguments, bool throwOnFailure = true)
    {
        using var process = Process.Start(new ProcessStartInfo("docker", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Could not start docker: the integration tier needs Docker for MinIO.");
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        // A first run pulls the image.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await process.WaitForExitAsync(timeout.Token);
        if (throwOnFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker {arguments.Split(' ')[0]} failed ({process.ExitCode}): {await stderr}");
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
