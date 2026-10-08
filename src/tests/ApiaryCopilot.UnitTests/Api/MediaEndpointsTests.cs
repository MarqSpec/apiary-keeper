using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ApiaryCopilot.UnitTests.Api;

// The media endpoints as a caller sees them, through the real host in memory with the store, the bucket and the current
// account faked (CONVENTIONS.md §8.1).
public sealed class MediaEndpointsTests : IDisposable
{
    private static readonly AccountId Current = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly AccountId Other = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));
    private static readonly InspectionId KnownInspection = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000d1"));
    private static readonly InspectionId OtherInspection = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000d2"));
    private static readonly MediaId KnownMedia = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000e1"));
    private static readonly DateTimeOffset AttachedAt = new(2026, 4, 12, 18, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ExpiresAt = new(2026, 4, 12, 18, 40, 0, TimeSpan.Zero);
    private static readonly Uri UploadUrl = new("https://media.example.test/synthetic-media/upload?X-Amz-Signature=synthetic");
    private static readonly Uri DownloadUrl = new("https://media.example.test/synthetic-media/download?X-Amz-Signature=synthetic");

    private readonly IInspectionMediaStore _store = A.Fake<IInspectionMediaStore>();
    private readonly IMediaStorage _storage = A.Fake<IMediaStorage>();
    private readonly ICurrentAccount _currentAccount = A.Fake<ICurrentAccount>();
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public MediaEndpointsTests()
    {
        A.CallTo(() => _currentAccount.AccountId).Returns(Current);
        A.CallTo(() => _storage.IsConfigured).Returns(true);
        A.CallTo(() => _storage.PresignUpload(A<MediaObjectKey>._, A<string>._, A<long>._)).Returns(new PresignedUrl(UploadUrl, ExpiresAt));
        A.CallTo(() => _storage.PresignDownload(A<MediaObjectKey>._)).Returns(new PresignedUrl(DownloadUrl, ExpiresAt));
        A.CallTo(() => _store.InspectionExistsAsync(KnownInspection, A<CancellationToken>._)).Returns(true);
        _factory = new ApiFactory(
            caller: ApiFactory.SignedIn(),
            settings: new Dictionary<string, string?> { ["Media:MaxImageBytes"] = "1000", ["Media:MaxVideoBytes"] = "5000" },
            services: s => s
                .AddScoped(_ => _store)
                .AddSingleton(_storage)
                .AddSingleton(_currentAccount));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    public static TheoryData<string, string> EveryMediaEndpoint() => new()
    {
        { "POST", $"/api/inspections/{KnownInspection}/media/uploads" },
        { "POST", $"/api/inspections/{KnownInspection}/media" },
        { "GET", $"/api/inspections/{KnownInspection}/media" },
        { "GET", $"/api/media/{KnownMedia}" },
    };

    [Theory] // invariant: with no signed-in account (sign-in off) every media endpoint fails closed, before the store or the bucket is touched
    [MemberData(nameof(EveryMediaEndpoint))]
    public async Task MediaEndpoint_ShouldReturnUnavailableAndTouchNothing_WhenNoAccountIsCurrent(string method, string path)
    {
        A.CallTo(() => _currentAccount.AccountId).Returns(null);

        using var response = await SendAsync(method, path);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await TitleOf(response)).Should().StartWith("Unavailable:");
        A.CallTo(_store).MustNotHaveHappened();
        A.CallTo(() => _storage.PresignUpload(A<MediaObjectKey>._, A<string>._, A<long>._)).MustNotHaveHappened();
        A.CallTo(() => _storage.FindAsync(A<MediaObjectKey>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Theory] // invariant: a caller whose subject no user of its account holds is refused, before anything is touched
    [MemberData(nameof(EveryMediaEndpoint))]
    public async Task MediaEndpoint_ShouldBeRefusedAndTouchNothing_WhenTheSubjectIsNotAUserOfTheAccount(string method, string path)
    {
        A.CallTo(() => _factory.TenantDirectory.FindActingUserAsync(A<string>._, A<CancellationToken>._)).Returns(TenantLookup.SubjectUnknown);

        using var response = await SendAsync(method, path);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await TitleOf(response)).Should().StartWith("NotAllowed:").And.Contain("not a user of this account");
        A.CallTo(_store).MustNotHaveHappened();
        A.CallTo(() => _storage.PresignUpload(A<MediaObjectKey>._, A<string>._, A<long>._)).MustNotHaveHappened();
        A.CallTo(() => _storage.FindAsync(A<MediaObjectKey>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Theory] // invariant: with no media bucket configured every media endpoint fails closed, before the store is touched
    [MemberData(nameof(EveryMediaEndpoint))]
    public async Task MediaEndpoint_ShouldReturnUnavailableAndTouchNothing_WhenNoBucketIsConfigured(string method, string path)
    {
        A.CallTo(() => _storage.IsConfigured).Returns(false);

        using var response = await SendAsync(method, path);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await TitleOf(response)).Should().StartWith("Unavailable:");
        A.CallTo(_store).MustNotHaveHappened();
    }

    [Fact] // invariant (D-13): media is attached once - the surface is the upload request, the attach and two reads, nothing more
    public void MediaRoutes_ShouldOfferOnlyReadsAndAppends_WhenTheHostIsBuilt()
    {
        var routes = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.Contains("/media", StringComparison.Ordinal))
            .Select(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)
            .ToList();

        routes.Should().HaveCount(4);
        routes.SelectMany(methods => methods).Distinct().Should().BeSubsetOf(["GET", "POST"]);
    }

    [Fact] // invariant (NFR-8, D-13): an upload URL is for a new key under the caller's account and this inspection, with the type and size signed
    public async Task RequestMediaUpload_ShouldReturnAPresignedUrlForAnAccountPrefixedKey_WhenTheRequestIsValid()
    {
        using var response = await _client.PostAsJsonAsync($"/api/inspections/{KnownInspection}/media/uploads", new { contentType = "Image/JPEG", sizeBytes = 1000 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = body.RootElement;
        var key = result.GetProperty("objectKey").GetString();
        key.Should().StartWith($"accounts/{Current}/inspections/{KnownInspection}/");
        result.GetProperty("uploadUrl").GetString().Should().Be(UploadUrl.ToString());
        result.GetProperty("method").GetString().Should().Be("PUT");
        result.GetProperty("headers").GetProperty("Content-Type").GetString().Should().Be("image/jpeg");
        result.GetProperty("expiresAt").GetDateTimeOffset().Should().Be(ExpiresAt);
        A.CallTo(() => _storage.PresignUpload(A<MediaObjectKey>.That.Matches(k => k.Value == key && k.BelongsTo(Current, KnownInspection)), "image/jpeg", 1000))
            .MustHaveHappenedOnceExactly();
    }

    [Theory] // boundary (adversarial): a type that is not an allowed image or video, or a size out of its limit, is a 400 and nothing is signed
    [InlineData("text/html", 10L, "contentType")]
    [InlineData("image/svg+xml", 10L, "contentType")]
    [InlineData("image/jpeg", 1001L, "sizeBytes")]
    [InlineData("image/jpeg", 0L, "sizeBytes")]
    [InlineData("video/mp4", 5001L, "sizeBytes")]
    public async Task RequestMediaUpload_ShouldReturnValidationProblemAndSignNothing_WhenTheTypeOrSizeIsNotAllowed(string contentType, long size, string field)
    {
        using var response = await _client.PostAsJsonAsync($"/api/inspections/{KnownInspection}/media/uploads", new { contentType, sizeBytes = size });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errors").GetProperty(field)[0].GetString().Should().StartWith("Validation:");
        A.CallTo(() => _storage.PresignUpload(A<MediaObjectKey>._, A<string>._, A<long>._)).MustNotHaveHappened();
    }

    [Fact] // invariant (adversarial, NFR-8): no URL is issued for an inspection the account does not have, whoever's it is
    public async Task RequestMediaUpload_ShouldReturnNotFoundAndSignNothing_WhenTheInspectionIsNotTheAccounts()
    {
        using var response = await _client.PostAsJsonAsync($"/api/inspections/{OtherInspection}/media/uploads", new { contentType = "image/jpeg", sizeBytes = 10 });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        A.CallTo(() => _storage.PresignUpload(A<MediaObjectKey>._, A<string>._, A<long>._)).MustNotHaveHappened();
    }

    [Fact] // regression risk (FR-8): an uploaded file is attached with what the bucket holds - not what the caller claimed - and answers 201
    public async Task AttachMedia_ShouldReturnCreatedWithTheMedia_WhenTheFileIsInTheBucket()
    {
        var key = MediaObjectKey.New(Current, KnownInspection);
        var stored = new StoredObject("image/jpeg", 900);
        A.CallTo(() => _storage.FindAsync(key, A<CancellationToken>._)).Returns(stored);
        A.CallTo(() => _store.AttachAsync(KnownInspection, key, stored, A<CancellationToken>._))
            .Returns(MediaAttachResult.Attached(Media(key, stored)));

        using var response = await _client.PostAsJsonAsync($"/api/inspections/{KnownInspection}/media", new { objectKey = key.Value });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.ToString().Should().Be($"/api/media/{KnownMedia}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var media = body.RootElement;
        media.GetProperty("id").GetGuid().Should().Be(KnownMedia.Value);
        media.GetProperty("inspectionId").GetGuid().Should().Be(KnownInspection.Value);
        media.GetProperty("contentType").GetString().Should().Be("image/jpeg");
        media.GetProperty("sizeBytes").GetInt64().Should().Be(900);
        media.GetProperty("attachedAt").GetDateTimeOffset().Should().Be(AttachedAt);
        media.GetProperty("downloadUrl").GetString().Should().Be(DownloadUrl.ToString());
        media.TryGetProperty("accountId", out _).Should().BeFalse();
        A.CallTo(() => _store.AttachAsync(KnownInspection, key, stored, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    public static TheoryData<string> KeysNotIssuedForThisInspection() => new()
    {
        MediaObjectKey.New(Other, KnownInspection).Value,
        MediaObjectKey.New(Other, OtherInspection).Value,
        MediaObjectKey.New(Current, OtherInspection).Value,
        $"accounts/{Current}/inspections/{KnownInspection}/../{MediaObjectKey.New(Other, OtherInspection).Value}",
        $"accounts/{Current}/inspections/{KnownInspection}/not-a-token",
        string.Empty,
    };

    [Theory] // invariant (adversarial, NFR-8): another account's key, or one not issued for this inspection, is refused before the bucket or the database is read
    [MemberData(nameof(KeysNotIssuedForThisInspection))]
    public async Task AttachMedia_ShouldRefuseTheKeyAndReadNothing_WhenItWasNotIssuedForThisInspection(string objectKey)
    {
        using var response = await _client.PostAsJsonAsync($"/api/inspections/{KnownInspection}/media", new { objectKey });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errors").GetProperty("objectKey")[0].GetString().Should().StartWith("Validation:");
        A.CallTo(() => _storage.FindAsync(A<MediaObjectKey>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(_store).MustNotHaveHappened();
    }

    [Fact] // boundary: a key nothing was uploaded to is refused, and nothing is attached
    public async Task AttachMedia_ShouldRefuseAndAttachNothing_WhenNothingWasUploaded()
    {
        var key = MediaObjectKey.New(Current, KnownInspection);
        A.CallTo(() => _storage.FindAsync(key, A<CancellationToken>._)).Returns((StoredObject?)null);

        using var response = await _client.PostAsJsonAsync($"/api/inspections/{KnownInspection}/media", new { objectKey = key.Value });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        A.CallTo(() => _store.AttachAsync(A<InspectionId>._, A<MediaObjectKey>._, A<StoredObject>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Theory] // invariant (defence in depth): an object the bucket holds with a type or size the upload could not have declared is never attached
    [InlineData("text/html", 10L)]
    [InlineData("image/jpeg", 1001L)]
    [InlineData("image/jpeg", 0L)]
    public async Task AttachMedia_ShouldRefuseAndAttachNothing_WhenTheStoredObjectIsNotAnAllowedTypeAndSize(string contentType, long size)
    {
        var key = MediaObjectKey.New(Current, KnownInspection);
        A.CallTo(() => _storage.FindAsync(key, A<CancellationToken>._)).Returns(new StoredObject(contentType, size));

        using var response = await _client.PostAsJsonAsync($"/api/inspections/{KnownInspection}/media", new { objectKey = key.Value });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        A.CallTo(() => _store.AttachAsync(A<InspectionId>._, A<MediaObjectKey>._, A<StoredObject>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact] // degradation (§5): a bucket that does not answer is a 503 to retry, and nothing is attached
    public async Task AttachMedia_ShouldReturnUnavailable_WhenTheBucketDoesNotAnswer()
    {
        var key = MediaObjectKey.New(Current, KnownInspection);
        A.CallTo(() => _storage.FindAsync(key, A<CancellationToken>._)).ThrowsAsync(new MediaStorageUnavailableException());

        using var response = await _client.PostAsJsonAsync($"/api/inspections/{KnownInspection}/media", new { objectKey = key.Value });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await TitleOf(response)).Should().StartWith("Unavailable:");
        A.CallTo(() => _store.AttachAsync(A<InspectionId>._, A<MediaObjectKey>._, A<StoredObject>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Theory] // invariant: the store's refusals map to the caller's results - attached once, and the account's inspection only
    [InlineData(MediaAttachOutcome.AlreadyAttached, HttpStatusCode.BadRequest)]
    [InlineData(MediaAttachOutcome.KeyNotForThisInspection, HttpStatusCode.BadRequest)]
    [InlineData(MediaAttachOutcome.NoSuchInspection, HttpStatusCode.NotFound)]
    public async Task AttachMedia_ShouldMapTheStoresRefusal_WhenTheStoreRefuses(MediaAttachOutcome outcome, HttpStatusCode expected)
    {
        var key = MediaObjectKey.New(Current, KnownInspection);
        A.CallTo(() => _storage.FindAsync(key, A<CancellationToken>._)).Returns(new StoredObject("image/jpeg", 10));
        A.CallTo(() => _store.AttachAsync(A<InspectionId>._, A<MediaObjectKey>._, A<StoredObject>._, A<CancellationToken>._)).Returns(MediaAttachResult.Refused(outcome));

        using var response = await _client.PostAsJsonAsync($"/api/inspections/{KnownInspection}/media", new { objectKey = key.Value });

        response.StatusCode.Should().Be(expected);
    }

    [Fact] // invariant (adversarial, NFR-8): another account's inspection is NotFound before the bucket is read
    public async Task AttachMedia_ShouldReturnNotFoundAndReadNothing_WhenTheInspectionIsNotTheAccounts()
    {
        var key = MediaObjectKey.New(Current, OtherInspection);

        using var response = await _client.PostAsJsonAsync($"/api/inspections/{OtherInspection}/media", new { objectKey = key.Value });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        A.CallTo(() => _storage.FindAsync(A<MediaObjectKey>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact] // invariant: an inspection's media list a page at a time, each with a fresh download URL
    public async Task ListMedia_ShouldReturnAPageWithDownloadUrls_WhenMoreMediaRemain()
    {
        var media = Enumerable.Range(0, 3).Select(_ => Media(MediaObjectKey.New(Current, KnownInspection), new StoredObject("video/mp4", 10))).ToList();
        A.CallTo(() => _store.ListAsync(KnownInspection, 0, 3, A<CancellationToken>._)).Returns(media);

        using var response = await _client.GetAsync($"/api/inspections/{KnownInspection}/media?limit=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(2);
        items.Should().AllSatisfy(i => i.GetProperty("downloadUrl").GetString().Should().Be(DownloadUrl.ToString()));
        body.RootElement.GetProperty("nextCursor").GetString().Should().NotBeNullOrEmpty();
        A.CallTo(() => _storage.PresignDownload(media[0].ObjectKey)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _storage.PresignDownload(media[2].ObjectKey)).MustNotHaveHappened();
    }

    [Fact] // invariant (adversarial, NFR-8): another account's inspection has no media to list
    public async Task ListMedia_ShouldReturnNotFound_WhenTheInspectionIsNotTheAccounts()
    {
        A.CallTo(() => _store.ListAsync(OtherInspection, A<int>._, A<int>._, A<CancellationToken>._)).Returns((IReadOnlyList<InspectionMedia>?)null);

        using var response = await _client.GetAsync($"/api/inspections/{OtherInspection}/media");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        A.CallTo(() => _storage.PresignDownload(A<MediaObjectKey>._)).MustNotHaveHappened();
    }

    [Fact] // invariant (adversarial, NFR-8): another account's media is NotFound, and no download URL is signed for it
    public async Task GetMedia_ShouldReturnNotFoundAndSignNothing_WhenTheMediaIsNotTheAccounts()
    {
        A.CallTo(() => _store.FindAsync(KnownMedia, A<CancellationToken>._)).Returns((InspectionMedia?)null);

        using var response = await _client.GetAsync($"/api/media/{KnownMedia}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await TitleOf(response)).Should().StartWith("NotFound:");
        A.CallTo(() => _storage.PresignDownload(A<MediaObjectKey>._)).MustNotHaveHappened();
    }

    [Fact] // regression risk: one media item reads back with a fresh download URL
    public async Task GetMedia_ShouldReturnTheMediaWithADownloadUrl_WhenItIsTheAccounts()
    {
        var key = MediaObjectKey.New(Current, KnownInspection);
        A.CallTo(() => _store.FindAsync(KnownMedia, A<CancellationToken>._)).Returns(Media(key, new StoredObject("image/png", 42)));

        using var response = await _client.GetAsync($"/api/media/{KnownMedia}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("downloadUrl").GetString().Should().Be(DownloadUrl.ToString());
        body.RootElement.GetProperty("downloadUrlExpiresAt").GetDateTimeOffset().Should().Be(ExpiresAt);
        A.CallTo(() => _storage.PresignDownload(key)).MustHaveHappenedOnceExactly();
    }

    private Task<HttpResponseMessage> SendAsync(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { contentType = "image/jpeg", sizeBytes = 10, objectKey = MediaObjectKey.New(Current, KnownInspection).Value });
        }

        return _client.SendAsync(request);
    }

    private static InspectionMedia Media(MediaObjectKey key, StoredObject stored) => new()
    {
        MediaId = KnownMedia,
        AccountId = Current,
        InspectionId = KnownInspection,
        ObjectKey = key,
        ContentType = stored.ContentType,
        SizeBytes = stored.SizeBytes,
        AttachedAt = AttachedAt,
    };

    private static async Task<string?> TitleOf(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("title").GetString();
    }
}
