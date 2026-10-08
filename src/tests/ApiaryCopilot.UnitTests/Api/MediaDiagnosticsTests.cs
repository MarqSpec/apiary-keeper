using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ApiaryCopilot.UnitTests.Api;

// Token custody in the diagnostic stream (CONVENTIONS.md §7): the real host under Production, every log level
// on, the real S3 wrapper signing with a synthetic credential (signing is offline) and reading from an endpoint nothing
// listens on; the store and the account faked. Nothing it writes may carry a URL's signature, an object key, the
// bucket's endpoints or its credential.
public sealed partial class MediaDiagnosticsTests : IDisposable
{
    private const string SyntheticKeyId = "SYNTHETICKEYID0042";
    private const string SyntheticSecret = "synthetic-secret-planted-0042"; // gitleaks:allow - synthetic, planted to prove the log scan finds it
    private const string PublicHost = "media-public.example.test";
    private const string ServiceEndpoint = "http://127.0.0.1:1";

    private static readonly AccountId Current = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly InspectionId KnownInspection = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000d1"));
    private static readonly MediaId KnownMedia = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000e1"));

    private readonly CapturingLoggerProvider _logs = new();
    private readonly IInspectionMediaStore _store = A.Fake<IInspectionMediaStore>();
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public MediaDiagnosticsTests()
    {
        var currentAccount = A.Fake<ICurrentAccount>();
        A.CallTo(() => currentAccount.AccountId).Returns(Current);
        A.CallTo(() => _store.InspectionExistsAsync(KnownInspection, A<CancellationToken>._)).Returns(true);
        _factory = new ApiFactory(
            caller: ApiFactory.SignedIn(),
            settings: new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
                ["Media:ServiceUrl"] = ServiceEndpoint,
                ["Media:PublicUrl"] = $"https://{PublicHost}",
                ["Media:Bucket"] = "synthetic-media",
                ["Media:AccessKeyId"] = SyntheticKeyId,
                ["Media:SecretAccessKey"] = SyntheticSecret,
                ["Media:ForcePathStyle"] = "true",
                ["Media:AllowHttp"] = "true",
            },
            services: s => s
                .AddScoped(_ => _store)
                .AddSingleton(currentAccount)
                .AddSingleton<ILoggerProvider>(_logs));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact] // invariant (token custody, §7): issuing, attaching and reading media writes no URL, key, endpoint or credential to the logs
    public async Task MediaEndpoints_ShouldLogNoUrlKeyEndpointOrCredential_WhenDrivenEndToEnd()
    {
        using var upload = await _client.PostAsJsonAsync($"/api/inspections/{KnownInspection}/media/uploads", new { contentType = "image/jpeg", sizeBytes = 10 });
        upload.StatusCode.Should().Be(HttpStatusCode.OK);
        using var issued = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
        var key = issued.RootElement.GetProperty("objectKey").GetString()!;
        var signature = System.Web.HttpUtility.ParseQueryString(new Uri(issued.RootElement.GetProperty("uploadUrl").GetString()!).Query)["X-Amz-Signature"]!;
        A.CallTo(() => _store.FindAsync(KnownMedia, A<CancellationToken>._)).Returns(new InspectionMedia
        {
            MediaId = KnownMedia,
            AccountId = Current,
            InspectionId = KnownInspection,
            ObjectKey = MediaObjectKey.Parse(key)!,
            ContentType = "image/jpeg",
            SizeBytes = 10,
            AttachedAt = DateTimeOffset.UnixEpoch,
        });

        using var attach = await _client.PostAsJsonAsync($"/api/inspections/{KnownInspection}/media", new { objectKey = key });
        using var read = await _client.GetAsync($"/api/media/{KnownMedia}");

        attach.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "the bucket endpoint has nothing listening, so the read fails and is logged");
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        _logs.Lines.Should().Contain(l => l.Contains("did not answer a read", StringComparison.Ordinal), "the failure path ran and logged");
        Scan(_logs.Lines, signature, key[(key.LastIndexOf('/') + 1)..]).Should().BeEmpty();
    }

    [Fact] // red control (§7): the scan reports exactly a line that plants a URL's parts
    public void Scan_ShouldReportExactlyThePlantedLine_WhenOneLeaks()
    {
        var logger = _factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ApiaryCopilot.UnitTests.Planted");
        var planted = $"https://{PublicHost}/synthetic-media/k?X-Amz-Credential={SyntheticKeyId}";

        Planted(logger, planted);

        Scan(_logs.Lines, "unused-signature", "unused-token").Should().ContainSingle().Which.Should().Contain(planted);
    }

    private static List<string> Scan(IEnumerable<string> lines, string signature, string token) =>
        [.. lines.Where(l => new[] { "X-Amz-", signature, token, SyntheticKeyId, SyntheticSecret, PublicHost, "127.0.0.1:1" }
            .Any(secret => l.Contains(secret, StringComparison.OrdinalIgnoreCase)))];

    [LoggerMessage(EventId = 9001, Level = LogLevel.Warning, Message = "Planted {Url}")]
    private static partial void Planted(ILogger logger, string url);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IEnumerable<string> Lines => _lines;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _lines);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue($"{category} [{eventId.Id}] {formatter(state, exception)} {exception}");
        }
    }
}
