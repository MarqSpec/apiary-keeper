using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ApiaryCopilot.UnitTests.Api;

// The yard endpoints as a caller sees them, through the real host in memory with the store and the current
// account faked (CONVENTIONS.md §8.1).
public sealed class YardEndpointsTests : IDisposable
{
    private static readonly AccountId Current = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly YardId KnownId = new(Guid.Parse("0199b2a4-0000-7000-8000-000000000001"));

    private readonly IYardStore _store = A.Fake<IYardStore>();
    private readonly ICurrentAccount _currentAccount = A.Fake<ICurrentAccount>();
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public YardEndpointsTests()
    {
        A.CallTo(() => _currentAccount.AccountId).Returns(Current);
        _factory = new ApiFactory(caller: ApiFactory.SignedIn(), services: s => s
            .AddScoped(_ => _store)
            .AddSingleton(_currentAccount));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    public static TheoryData<string, string> EveryYardEndpoint() => new()
    {
        { "POST", "/api/yards" },
        { "GET", "/api/yards" },
        { "GET", $"/api/yards/{KnownId}" },
        { "PUT", $"/api/yards/{KnownId}" },
    };

    [Theory] // invariant: with no signed-in account (sign-in off) every yard endpoint fails closed, before the store is touched
    [MemberData(nameof(EveryYardEndpoint))]
    public async Task YardEndpoint_ShouldReturnUnavailableAndTouchNothing_WhenNoAccountIsCurrent(string method, string path)
    {
        A.CallTo(() => _currentAccount.AccountId).Returns(null);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(ValidBody());
        }

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await TitleOf(response)).Should().StartWith("Unavailable:");
        A.CallTo(_store).MustNotHaveHappened();
    }

    [Theory] // invariant: a caller whose subject no user of its account holds is refused, before anything is touched
    [MemberData(nameof(EveryYardEndpoint))]
    public async Task YardEndpoint_ShouldBeRefusedAndTouchNothing_WhenTheSubjectIsNotAUserOfTheAccount(string method, string path)
    {
        A.CallTo(() => _factory.TenantDirectory.FindActingUserAsync(A<string>._, A<CancellationToken>._)).Returns(TenantLookup.SubjectUnknown);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(ValidBody());
        }

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await TitleOf(response)).Should().StartWith("NotAllowed:").And.Contain("not a user of this account");
        A.CallTo(_store).MustNotHaveHappened();
    }

    [Fact] // regression risk: a created yard answers 201 with its location and what was stored
    public async Task CreateYard_ShouldReturnCreatedWithTheYard_WhenTheRequestIsValid()
    {
        A.CallTo(() => _store.AddAsync(A<YardDetails>._, A<CancellationToken>._))
            .ReturnsLazily((YardDetails details, CancellationToken _) => new Yard { YardId = KnownId, AccountId = Current, Details = details });

        using var response = await _client.PostAsJsonAsync("/api/yards", ValidBody());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.ToString().Should().Be($"/api/yards/{KnownId}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("id").GetGuid().Should().Be(KnownId.Value);
        body.RootElement.GetProperty("state").GetString().Should().Be("TX");
        body.RootElement.GetProperty("landowner").GetProperty("terms").GetString().Should().Be("honey");
        body.RootElement.TryGetProperty("accountId", out _).Should().BeFalse();
    }

    [Fact] // boundary: an invalid request is a categorized validation result naming the field, and nothing is written
    public async Task CreateYard_ShouldReturnValidationProblem_WhenTheRequestIsInvalid()
    {
        using var response = await _client.PostAsJsonAsync("/api/yards", ValidBody() with { State = "NM" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("title").GetString().Should().StartWith("Validation:");
        body.RootElement.GetProperty("errors").GetProperty("state")[0].GetString().Should().StartWith("Validation:");
        A.CallTo(_store).MustNotHaveHappened();
    }

    [Fact] // boundary: an unknown yard, or another account's, is NotFound (§4.3)
    public async Task GetYard_ShouldReturnNotFoundProblem_WhenTheStoreFindsNoSuchYard()
    {
        A.CallTo(() => _store.FindAsync(KnownId, A<CancellationToken>._)).Returns((Yard?)null);

        using var response = await _client.GetAsync($"/api/yards/{KnownId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await TitleOf(response)).Should().StartWith("NotFound:");
    }

    [Fact] // boundary: the empty id names no yard, and is NotFound rather than an exception
    public async Task GetYard_ShouldReturnNotFoundProblem_WhenTheIdIsEmpty()
    {
        using var response = await _client.GetAsync($"/api/yards/{Guid.Empty}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await TitleOf(response)).Should().StartWith("NotFound:");
        A.CallTo(_store).MustNotHaveHappened();
    }

    [Fact] // regression risk: a read returns the stored yard
    public async Task GetYard_ShouldReturnTheYard_WhenTheStoreFindsIt()
    {
        A.CallTo(() => _store.FindAsync(KnownId, A<CancellationToken>._)).Returns(StoredYard("Mesquite Flat"));

        using var response = await _client.GetAsync($"/api/yards/{KnownId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("name").GetString().Should().Be("Mesquite Flat");
    }

    [Fact] // regression risk: an update returns the yard as stored
    public async Task UpdateYard_ShouldReturnTheUpdatedYard_WhenTheStoreUpdatesIt()
    {
        A.CallTo(() => _store.UpdateAsync(KnownId, A<YardDetails>._, A<CancellationToken>._))
            .ReturnsLazily((YardId id, YardDetails details, CancellationToken _) => new Yard { YardId = id, AccountId = Current, Details = details });

        using var response = await _client.PutAsJsonAsync($"/api/yards/{KnownId}", ValidBody() with { Name = "Renamed" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("name").GetString().Should().Be("Renamed");
    }

    [Fact] // boundary: updating an unknown yard, or another account's, is NotFound
    public async Task UpdateYard_ShouldReturnNotFoundProblem_WhenTheStoreFindsNoSuchYard()
    {
        A.CallTo(() => _store.UpdateAsync(KnownId, A<YardDetails>._, A<CancellationToken>._)).Returns((Yard?)null);

        using var response = await _client.PutAsJsonAsync($"/api/yards/{KnownId}", ValidBody());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await TitleOf(response)).Should().StartWith("NotFound:");
    }

    [Fact] // boundary: an invalid update is refused before the store is touched
    public async Task UpdateYard_ShouldReturnValidationProblem_WhenTheRequestIsInvalid()
    {
        using var response = await _client.PutAsJsonAsync($"/api/yards/{KnownId}", ValidBody() with { Name = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleOf(response)).Should().StartWith("Validation:");
        A.CallTo(_store).MustNotHaveHappened();
    }

    [Fact] // invariant: the list is bounded; a full page carries the cursor of the next
    public async Task ListYards_ShouldReturnAPageAndTheNextCursor_WhenMoreYardsRemain()
    {
        A.CallTo(() => _store.ListAsync(0, 3, A<CancellationToken>._))
            .Returns([StoredYard("A"), StoredYard("B"), StoredYard("C")]);

        using var response = await _client.GetAsync("/api/yards?limit=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString())
            .Should().Equal("A", "B");
        body.RootElement.GetProperty("nextCursor").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact] // invariant: the next cursor continues exactly where the previous page ended
    public async Task ListYards_ShouldContinueFromTheCursor_WhenACursorIsGiven()
    {
        A.CallTo(() => _store.ListAsync(A<int>._, A<int>._, A<CancellationToken>._)).Returns([StoredYard("A"), StoredYard("B"), StoredYard("C")]);
        using var first = await _client.GetAsync("/api/yards?limit=2");
        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var cursor = firstBody.RootElement.GetProperty("nextCursor").GetString();

        using var second = await _client.GetAsync($"/api/yards?limit=2&cursor={cursor}");

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        A.CallTo(() => _store.ListAsync(2, 3, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact] // boundary: the last page has no next cursor, and the default limit is 50
    public async Task ListYards_ShouldReturnNoNextCursor_WhenNoMoreYardsRemain()
    {
        A.CallTo(() => _store.ListAsync(0, 51, A<CancellationToken>._)).Returns([StoredYard("A")]);

        using var response = await _client.GetAsync("/api/yards");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("items").GetArrayLength().Should().Be(1);
        body.RootElement.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory] // boundary: limit outside 1-100, or a cursor the API did not issue, is a validation result
    [InlineData("limit=0", "limit")]
    [InlineData("limit=101", "limit")]
    [InlineData("cursor=bm90LWEtbnVtYmVy", "cursor")]
    public async Task ListYards_ShouldReturnValidationProblem_WhenThePagingArgumentsAreInvalid(string query, string field)
    {
        using var response = await _client.GetAsync($"/api/yards?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue();
        A.CallTo(_store).MustNotHaveHappened();
    }

    [Fact] // invariant: an unexpected failure is a bare 500 problem, carrying no exception detail (§4.3)
    public async Task GetYard_ShouldReturnBare500Problem_WhenTheStoreFailsUnexpectedly()
    {
        A.CallTo(() => _store.FindAsync(A<YardId>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("synthetic internal detail"));

        using var response = await _client.GetAsync($"/api/yards/{KnownId}");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadAsStringAsync()).Should().NotContain("synthetic internal detail");
    }

    private static Yard StoredYard(string name) => new()
    {
        YardId = YardId.New(),
        AccountId = Current,
        Details = new YardDetails
        {
            Name = name,
            Location = new GeoLocation { Latitude = 30.2672, Longitude = -97.7431 },
            State = UsState.Texas,
            County = "Travis",
            Landowner = new Landowner { Contact = "Synthetic Landowner, 555-0100", Terms = LandownerTerms.Free },
        },
    };

    private static YardRequestBody ValidBody() => new(
        "Mesquite Flat",
        new LocationBody(30.2672, -97.7431),
        "TX",
        "Travis",
        "Gate code from the landowner.",
        "Huisache in February.",
        new LandownerBody("Synthetic Landowner, 555-0100", "honey", "Cotton to the east.", "Synthetic Crop Duster, 555-0101"));

    private static async Task<string?> TitleOf(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("title").GetString();
    }

    // The wire shape as a caller writes it, independent of the API's own request types.
    private sealed record YardRequestBody(
        string? Name, LocationBody? Location, string? State, string? County, string? AccessNotes, string? ForageNotes, LandownerBody? Landowner);

    private sealed record LocationBody(double? Latitude, double? Longitude);

    private sealed record LandownerBody(string? Contact, string? Terms, string? NearbyCrops, string? SprayContact);
}
