using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ApiaryCopilot.Domain;

namespace ApiaryCopilot.IntegrationTests;

// Photos and videos end to end (FR-8, D-13): the real server (source or image) issuing presigned URLs for a real MinIO,
// and this process playing the phone - uploading and downloading straight to the bucket, never through the server.
// What the server signs is what the bucket enforces, so the refusals here are the bucket's own. Deployment=None: the
// database and the bucket are loopback containers.
[Trait("Deployment", "None")]
[Collection(SharedServer.Name)]
public sealed class MediaEndpointTests(AppServerFixture server)
{
    private static readonly byte[] Photo = [0xFF, 0xD8, 0xFF, 0xE0, .. "synthetic jpeg body"u8];

    // The phone: no base address, no credential, nothing shared with the client that calls the API.
    private static readonly HttpClient Phone = new() { Timeout = TimeSpan.FromSeconds(10) };

    [Fact] // regression risk (FR-8, D-13): a photo is uploaded straight to the bucket, attached with its timestamp, and downloaded back whole
    public async Task UploadAttachAndDownload_ShouldRoundTripThePhoto_WhenRunEndToEnd()
    {
        var inspection = await RecordInspectionAsync();

        using var issued = await RequestUploadAsync(inspection, "image/jpeg", Photo.Length);
        var key = issued.RootElement.GetProperty("objectKey").GetString()!;
        key.Should().StartWith($"accounts/{server.CurrentAccountId}/inspections/{inspection}/");
        using var put = await PutAsync(issued, Photo, "image/jpeg");
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        using var attached = await server.Client.PostAsJsonAsync($"/api/inspections/{inspection}/media", new { objectKey = key });

        attached.StatusCode.Should().Be(HttpStatusCode.Created);
        using var media = JsonDocument.Parse(await attached.Content.ReadAsStringAsync());
        var id = media.RootElement.GetProperty("id").GetGuid();
        attached.Headers.Location!.ToString().Should().Be($"/api/media/{id}");
        media.RootElement.GetProperty("contentType").GetString().Should().Be("image/jpeg");
        media.RootElement.GetProperty("sizeBytes").GetInt64().Should().Be(Photo.Length);
        media.RootElement.GetProperty("attachedAt").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        using var listed = await GetJsonAsync($"/api/inspections/{inspection}/media");
        listed.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(id);
        var download = listed.RootElement.GetProperty("items")[0].GetProperty("downloadUrl").GetString()!;
        new Uri(download).GetLeftPart(UriPartial.Authority).Should().Be(server.MediaBucket.HostUrl);
        (await Phone.GetByteArrayAsync(new Uri(download))).Should().Equal(Photo);
    }

    [Fact] // invariant (token custody): the bucket refuses an upload whose content type is not the one signed, so HTML never lands
    public async Task Upload_ShouldBeRefusedByTheBucket_WhenTheContentTypeIsNotTheSignedOne()
    {
        var inspection = await RecordInspectionAsync();
        using var issued = await RequestUploadAsync(inspection, "image/jpeg", Photo.Length);

        using var put = await PutAsync(issued, Photo, "text/html");

        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await server.MediaBucket.ReadAsync(issued.RootElement.GetProperty("objectKey").GetString()!)).Should().BeNull();
    }

    [Fact] // invariant (token custody): the bucket refuses an upload whose length is not the one signed, so the size limit holds
    public async Task Upload_ShouldBeRefusedByTheBucket_WhenTheBodyIsLongerThanSigned()
    {
        var inspection = await RecordInspectionAsync();
        using var issued = await RequestUploadAsync(inspection, "image/jpeg", Photo.Length);

        using var put = await PutAsync(issued, [.. Photo, 0x00], "image/jpeg");

        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await server.MediaBucket.ReadAsync(issued.RootElement.GetProperty("objectKey").GetString()!)).Should().BeNull();
    }

    [Fact] // boundary (token custody): the server refuses to sign an upload of a type that is not an image or video
    public async Task RequestMediaUpload_ShouldReturnValidationProblem_WhenTheContentTypeIsNotAnImageOrVideo()
    {
        var inspection = await RecordInspectionAsync();

        using var response = await server.Client.PostAsJsonAsync(
            $"/api/inspections/{inspection}/media/uploads", new { contentType = "text/html", sizeBytes = Photo.Length });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errors").TryGetProperty("contentType", out _).Should().BeTrue();
    }

    [Fact] // invariant (token custody): an upload or download URL the server issued is refused by the bucket once it has expired
    public async Task PresignedUrl_ShouldBeRefusedByTheBucket_WhenItHasExpired()
    {
        var inspection = await RecordInspectionAsync();
        using var unused = await RequestUploadAsync(inspection, "image/jpeg", Photo.Length);
        var downloadUrl = await AttachAsync(inspection, Photo);

        await Task.Delay(AppServerFixture.MediaUrlLifetime + TimeSpan.FromSeconds(2));
        using var put = await PutAsync(unused, Photo, "image/jpeg");
        using var get = await Phone.GetAsync(downloadUrl);

        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        get.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await server.MediaBucket.ReadAsync(unused.RootElement.GetProperty("objectKey").GetString()!)).Should().BeNull();
    }

    [Fact] // invariant (adversarial, NFR-8): another account's object, though it is in the bucket, cannot be attached by this one
    public async Task AttachMedia_ShouldRefuseAnotherAccountsKey_WhenItsObjectIsInTheBucket()
    {
        var inspection = await RecordInspectionAsync();
        var theirs = MediaObjectKey.New(server.OtherAccountId, server.OtherAccountInspectionId);
        await server.MediaBucket.PutAsync(theirs.Value, Photo, "image/jpeg");

        using var onMine = await server.Client.PostAsJsonAsync($"/api/inspections/{inspection}/media", new { objectKey = theirs.Value });
        using var onTheirs = await server.Client.PostAsJsonAsync($"/api/inspections/{server.OtherAccountInspectionId}/media", new { objectKey = theirs.Value });

        onMine.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        onTheirs.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await server.Database.CountAsync(
            $"SELECT count(*) FROM \"InspectionMedia\" WHERE \"ObjectKey\" = '{theirs.Value}'")).Should().Be(0);
    }

    [Fact] // invariant (adversarial, NFR-8): a URL issued for this account's key cannot be bent to write another account's key
    public async Task Upload_ShouldBeRefusedByTheBucket_WhenTheUrlIsAlteredToAnotherAccountsKey()
    {
        var inspection = await RecordInspectionAsync();
        using var issued = await RequestUploadAsync(inspection, "image/jpeg", Photo.Length);
        var mine = issued.RootElement.GetProperty("objectKey").GetString()!;
        var theirs = MediaObjectKey.New(server.OtherAccountId, server.OtherAccountInspectionId).Value;
        var altered = new Uri(issued.RootElement.GetProperty("uploadUrl").GetString()!.Replace(mine, theirs, StringComparison.Ordinal));

        using var content = new ByteArrayContent(Photo);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var put = await Phone.PutAsync(altered, content);

        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await server.MediaBucket.ReadAsync(theirs)).Should().BeNull();
    }

    [Fact] // invariant (adversarial, NFR-8): no upload URL is issued for another account's inspection
    public async Task RequestMediaUpload_ShouldReturnNotFound_WhenTheInspectionIsAnotherAccounts()
    {
        using var response = await server.Client.PostAsJsonAsync(
            $"/api/inspections/{server.OtherAccountInspectionId}/media/uploads", new { contentType = "image/jpeg", sizeBytes = Photo.Length });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact] // invariant (adversarial, NFR-8): another account's media is NotFound, and no download URL for it is issued
    public async Task GetMedia_ShouldReturnNotFound_WhenTheMediaIsAnotherAccounts()
    {
        var theirs = MediaObjectKey.New(server.OtherAccountId, server.OtherAccountInspectionId);
        await server.MediaBucket.PutAsync(theirs.Value, Photo, "image/jpeg");
        var media = await server.Database.AttachMediaAsync(
            server.OtherAccountId, server.OtherAccountInspectionId, theirs, new StoredObject("image/jpeg", Photo.Length));

        using var one = await server.Client.GetAsync($"/api/media/{media}");
        using var list = await server.Client.GetAsync($"/api/inspections/{server.OtherAccountInspectionId}/media");

        one.StatusCode.Should().Be(HttpStatusCode.NotFound);
        list.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await one.Content.ReadAsStringAsync()).Should().NotContain(theirs.Value);
    }

    [Fact] // boundary: a key nothing has been uploaded to cannot be attached
    public async Task AttachMedia_ShouldReturnValidationProblem_WhenNothingWasUploaded()
    {
        var inspection = await RecordInspectionAsync();
        using var issued = await RequestUploadAsync(inspection, "image/jpeg", Photo.Length);

        using var attached = await server.Client.PostAsJsonAsync(
            $"/api/inspections/{inspection}/media", new { objectKey = issued.RootElement.GetProperty("objectKey").GetString() });

        attached.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact] // invariant: an object is attached once - the second attach is refused and the first record stands alone (the unique key)
    public async Task AttachMedia_ShouldRefuseTheSecondAttach_WhenTheObjectIsAlreadyAttached()
    {
        var inspection = await RecordInspectionAsync();
        using var issued = await RequestUploadAsync(inspection, "image/jpeg", Photo.Length);
        using var put = await PutAsync(issued, Photo, "image/jpeg");
        var key = issued.RootElement.GetProperty("objectKey").GetString()!;
        using var first = await server.Client.PostAsJsonAsync($"/api/inspections/{inspection}/media", new { objectKey = key });

        using var second = await server.Client.PostAsJsonAsync($"/api/inspections/{inspection}/media", new { objectKey = key });

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await server.Database.CountAsync($"SELECT count(*) FROM \"InspectionMedia\" WHERE \"ObjectKey\" = '{key}'")).Should().Be(1);
    }

    private async Task<Uri> AttachAsync(Guid inspection, byte[] body)
    {
        using var issued = await RequestUploadAsync(inspection, "image/jpeg", body.Length);
        using var put = await PutAsync(issued, body, "image/jpeg");
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        using var attached = await server.Client.PostAsJsonAsync(
            $"/api/inspections/{inspection}/media", new { objectKey = issued.RootElement.GetProperty("objectKey").GetString() });
        attached.StatusCode.Should().Be(HttpStatusCode.Created);
        using var media = JsonDocument.Parse(await attached.Content.ReadAsStringAsync());
        return new Uri(media.RootElement.GetProperty("downloadUrl").GetString()!);
    }

    private async Task<JsonDocument> RequestUploadAsync(Guid inspection, string contentType, long sizeBytes)
    {
        using var response = await server.Client.PostAsJsonAsync($"/api/inspections/{inspection}/media/uploads", new { contentType, sizeBytes });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    // The phone's PUT: the URL as issued, the body and the content type given.
    private static async Task<HttpResponseMessage> PutAsync(JsonDocument issued, byte[] body, string contentType)
    {
        issued.RootElement.GetProperty("method").GetString().Should().Be("PUT");
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return await Phone.PutAsync(new Uri(issued.RootElement.GetProperty("uploadUrl").GetString()!), content);
    }

    private async Task<Guid> RecordInspectionAsync()
    {
        using var yardCreated = await server.Client.PostAsJsonAsync("/api/yards", new
        {
            name = $"Synthetic Media Yard {Guid.NewGuid():N}",
            location = new { latitude = 30.2672, longitude = -97.7431 },
            state = "TX",
            county = "Travis",
            landowner = new { contact = "Synthetic Landowner, 555-0100", terms = "free" },
        });
        yardCreated.StatusCode.Should().Be(HttpStatusCode.Created);
        using var yard = JsonDocument.Parse(await yardCreated.Content.ReadAsStringAsync());
        using var colonyCreated = await server.Client.PostAsJsonAsync("/api/colonies", new
        {
            tagId = $"M-{Guid.NewGuid():N}"[..20],
            hiveNumber = "TX-0415",
            yardId = yard.RootElement.GetProperty("id").GetGuid(),
            hiveType = "Langstroth 10-frame",
            boxCount = 2,
            occurredOn = "2026-04-01",
        });
        colonyCreated.StatusCode.Should().Be(HttpStatusCode.Created);
        using var colony = JsonDocument.Parse(await colonyCreated.Content.ReadAsStringAsync());
        using var recorded = await server.Client.PostAsJsonAsync(
            $"/api/colonies/{colony.RootElement.GetProperty("id").GetGuid()}/inspections",
            new { inspectedOn = "2026-04-12", notes = "Synthetic: photographed the brood frames." });
        recorded.StatusCode.Should().Be(HttpStatusCode.Created);
        using var inspection = JsonDocument.Parse(await recorded.Content.ReadAsStringAsync());
        return inspection.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<JsonDocument> GetJsonAsync(string path)
    {
        using var response = await server.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
