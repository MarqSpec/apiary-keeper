using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ApiaryCopilot.UnitTests.Api;

// The inspection endpoints as a caller sees them, through the real host in memory with the store and the current
// account faked (CONVENTIONS.md §8.1).
public sealed class InspectionEndpointsTests : IDisposable
{
    private static readonly AccountId Current = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly ColonyId KnownColony = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000c1"));
    private static readonly InspectionId KnownId = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000d1"));
    private static readonly DateTimeOffset RecordedAt = new(2026, 4, 12, 18, 30, 0, TimeSpan.Zero);

    private readonly IInspectionStore _store = A.Fake<IInspectionStore>();
    private readonly ICurrentAccount _currentAccount = A.Fake<ICurrentAccount>();
    private readonly TimeProvider _clock = A.Fake<TimeProvider>();
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public InspectionEndpointsTests()
    {
        A.CallTo(() => _currentAccount.AccountId).Returns(Current);
        A.CallTo(() => _clock.GetUtcNow()).Returns(RecordedAt);
        _factory = new ApiFactory(caller: ApiFactory.SignedIn(), services: s => s
            .AddScoped(_ => _store)
            .AddSingleton(_currentAccount)
            .AddSingleton(_clock));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    public static TheoryData<string, string> EveryInspectionEndpoint() => new()
    {
        { "POST", $"/api/colonies/{KnownColony}/inspections" },
        { "GET", $"/api/colonies/{KnownColony}/inspections" },
        { "GET", $"/api/inspections/{KnownId}" },
    };

    [Theory] // invariant: with no signed-in account (sign-in off) every inspection endpoint fails closed, before the store is touched
    [MemberData(nameof(EveryInspectionEndpoint))]
    public async Task InspectionEndpoint_ShouldReturnUnavailableAndTouchNothing_WhenNoAccountIsCurrent(string method, string path)
    {
        A.CallTo(() => _currentAccount.AccountId).Returns(null);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { });
        }

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await TitleOf(response)).Should().StartWith("Unavailable:");
        A.CallTo(_store).MustNotHaveHappened();
    }

    [Theory] // invariant: a caller whose subject no user of its account holds is refused, before anything is touched
    [MemberData(nameof(EveryInspectionEndpoint))]
    public async Task InspectionEndpoint_ShouldBeRefusedAndTouchNothing_WhenTheSubjectIsNotAUserOfTheAccount(string method, string path)
    {
        A.CallTo(() => _factory.TenantDirectory.FindActingUserAsync(A<string>._, A<CancellationToken>._)).Returns(TenantLookup.SubjectUnknown);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { });
        }

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await TitleOf(response)).Should().StartWith("NotAllowed:").And.Contain("not a user of this account");
        A.CallTo(_store).MustNotHaveHappened();
    }

    [Fact] // invariant: an inspection is recorded once, so the surface is the three reads and appends and nothing more
    public void InspectionRoutes_ShouldOfferOnlyReadsAndAppends_WhenTheHostIsBuilt()
    {
        var routes = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.Contains("/inspections", StringComparison.Ordinal)
                && !e.RoutePattern.RawText.Contains("/media", StringComparison.Ordinal)) // an inspection's media: MediaEndpointsTests
            .Select(e => (Route: e.RoutePattern.RawText!, Methods: e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods))
            .ToList();

        routes.Should().HaveCount(3);
        routes.SelectMany(r => r.Methods).Distinct().Should().BeSubsetOf(["GET", "POST"]);
    }

    public static TheoryData<string, string, HttpStatusCode> AttemptsToChangeAnInspection() => new()
    {
        { "PUT", $"/api/inspections/{KnownId}", HttpStatusCode.MethodNotAllowed },
        { "PATCH", $"/api/inspections/{KnownId}", HttpStatusCode.MethodNotAllowed },
        { "DELETE", $"/api/inspections/{KnownId}", HttpStatusCode.MethodNotAllowed },
        { "POST", $"/api/inspections/{KnownId}", HttpStatusCode.MethodNotAllowed },
        { "PUT", $"/api/colonies/{KnownColony}/inspections", HttpStatusCode.MethodNotAllowed },
        { "DELETE", $"/api/colonies/{KnownColony}/inspections", HttpStatusCode.MethodNotAllowed },
        { "PUT", $"/api/colonies/{KnownColony}/inspections/{KnownId}", HttpStatusCode.NotFound },
        { "DELETE", $"/api/colonies/{KnownColony}/inspections/{KnownId}", HttpStatusCode.NotFound },
    };

    [Theory] // invariant: an attempt to change or delete an inspection is refused by the router; the store is never reached
    [MemberData(nameof(AttemptsToChangeAnInspection))]
    public async Task ChangingAnInspection_ShouldBeRefusedWithoutTouchingTheStore_WhenAttemptedOverHttp(string method, string path, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { notes = "changed" }) };

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(expected);
        A.CallTo(_store).MustNotHaveHappened();
    }

    [Fact] // regression risk: a recorded inspection answers 201 with its location, every field and no account
    public async Task RecordInspection_ShouldReturnCreatedWithTheInspection_WhenTheStoreRecordsIt()
    {
        A.CallTo(() => _store.AddAsync(KnownColony, A<InspectionDetails>._, A<CancellationToken>._))
            .ReturnsLazily((ColonyId colony, InspectionDetails details, CancellationToken _) => Stored(KnownId, colony, details));

        using var response = await _client.PostAsJsonAsync($"/api/colonies/{KnownColony}/inspections", new
        {
            inspectedOn = "2026-04-12",
            findings = new { queenSeen = true, eggsSeen = false, broodFrames = 6, broodPattern = "Solid", diseaseSigns = Array.Empty<string>(), actionsTaken = new List<string> { "fed", "added-box" } },
            notes = "  Synthetic: laying well.  ",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.ToString().Should().Be($"/api/inspections/{KnownId}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var inspection = body.RootElement;
        inspection.GetProperty("id").GetGuid().Should().Be(KnownId.Value);
        inspection.GetProperty("colonyId").GetGuid().Should().Be(KnownColony.Value);
        inspection.GetProperty("inspectedOn").GetString().Should().Be("2026-04-12");
        inspection.GetProperty("recordedAt").GetDateTimeOffset().Should().Be(RecordedAt);
        inspection.GetProperty("notes").GetString().Should().Be("Synthetic: laying well.");
        var findings = inspection.GetProperty("findings");
        findings.GetProperty("queenSeen").GetBoolean().Should().BeTrue();
        findings.GetProperty("eggsSeen").GetBoolean().Should().BeFalse();
        findings.GetProperty("broodFrames").GetInt32().Should().Be(6);
        findings.GetProperty("broodPattern").GetString().Should().Be("solid");
        findings.GetProperty("diseaseSigns").GetArrayLength().Should().Be(0);
        findings.GetProperty("actionsTaken").EnumerateArray().Select(e => e.GetString()).Should().Equal("fed", "added-box");
        inspection.TryGetProperty("accountId", out _).Should().BeFalse();
        A.CallTo(() => _store.AddAsync(
                KnownColony,
                A<InspectionDetails>.That.Matches(d => d.InspectedOn == new DateOnly(2026, 4, 12) && d.Notes == "Synthetic: laying well." && d.Findings.Values.Count == 6),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Theory] // boundary: an invalid inspection is a 400 keyed by field before the store is touched
    [InlineData("{\"findings\":{\"queenSeen\":true}}", "inspectedOn")]
    [InlineData("{\"inspectedOn\":\"2026-04-12\"}", "findings")]
    [InlineData("{\"inspectedOn\":\"2026-04-12\",\"findings\":{\"broodFrames\":-1}}", "findings.broodFrames")]
    [InlineData("{\"inspectedOn\":\"2026-04-12\",\"findings\":{\"bees\":true}}", "findings.bees")]
    public async Task RecordInspection_ShouldReturnValidationProblem_WhenTheRequestIsInvalid(string json, string field)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var response = await _client.PostAsync($"/api/colonies/{KnownColony}/inspections", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleOf(response)).Should().StartWith("Validation:");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errors").GetProperty(field)[0].GetString().Should().StartWith("Validation:");
        A.CallTo(_store).MustNotHaveHappened();
    }

    [Theory] // boundary: today is the server's UTC date, so the evening window (Texas still on the 12th, UTC on the 13th) accepts the 13th and the 14th is refused
    [InlineData("2026-04-12T23:59:59+00:00", "2026-04-12", HttpStatusCode.Created)]
    [InlineData("2026-04-12T23:59:59+00:00", "2026-04-13", HttpStatusCode.BadRequest)]
    [InlineData("2026-04-13T00:00:00+00:00", "2026-04-13", HttpStatusCode.Created)]
    [InlineData("2026-04-13T00:00:00+00:00", "2026-04-14", HttpStatusCode.BadRequest)]
    public async Task RecordInspection_ShouldRefuseOnlyADayLaterThanTodayUtc_WhenTheClockIsAtTheDayBoundary(string now, string day, HttpStatusCode expected)
    {
        A.CallTo(() => _clock.GetUtcNow()).Returns(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture));
        A.CallTo(() => _store.AddAsync(KnownColony, A<InspectionDetails>._, A<CancellationToken>._))
            .ReturnsLazily((ColonyId colony, InspectionDetails details, CancellationToken _) => Stored(KnownId, colony, details));

        using var response = await _client.PostAsJsonAsync($"/api/colonies/{KnownColony}/inspections", new { inspectedOn = day, notes = "x" });

        response.StatusCode.Should().Be(expected);
        if (expected == HttpStatusCode.BadRequest)
        {
            (await TitleOf(response)).Should().StartWith("Validation:");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("errors").GetProperty("inspectedOn")[0].GetString().Should().StartWith("Validation:");
            A.CallTo(_store).MustNotHaveHappened();
        }
    }

    [Theory] // boundary: a day before the floor is a 400 keyed inspectedOn and nothing is recorded; the floor itself is recorded
    [InlineData("0001-01-01", HttpStatusCode.BadRequest)]
    [InlineData("1999-12-31", HttpStatusCode.BadRequest)]
    [InlineData("2000-01-01", HttpStatusCode.Created)]
    public async Task RecordInspection_ShouldRefuseAFloorViolation_WhenTheDayIsBeforeTheFloor(string day, HttpStatusCode expected)
    {
        A.CallTo(() => _store.AddAsync(KnownColony, A<InspectionDetails>._, A<CancellationToken>._))
            .ReturnsLazily((ColonyId colony, InspectionDetails details, CancellationToken _) => Stored(KnownId, colony, details));

        using var response = await _client.PostAsJsonAsync($"/api/colonies/{KnownColony}/inspections", new { inspectedOn = day, notes = "x" });

        response.StatusCode.Should().Be(expected);
        if (expected == HttpStatusCode.BadRequest)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("errors").GetProperty("inspectedOn")[0].GetString().Should().StartWith("Validation:");
            A.CallTo(_store).MustNotHaveHappened();
        }
    }

    [Fact] // boundary: a colony the account does not have is NotFound, whoever's it is
    public async Task RecordInspection_ShouldReturnNotFound_WhenTheStoreHasNoSuchColony()
    {
        A.CallTo(() => _store.AddAsync(A<ColonyId>._, A<InspectionDetails>._, A<CancellationToken>._)).Returns((Inspection?)null);

        using var response = await _client.PostAsJsonAsync($"/api/colonies/{KnownColony}/inspections", new { inspectedOn = "2026-04-12", notes = "x" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await TitleOf(response)).Should().StartWith("NotFound:");
    }

    [Theory] // boundary: an empty id names no colony or inspection, and the store is never asked
    [InlineData("POST", "/api/colonies/00000000-0000-0000-0000-000000000000/inspections")]
    [InlineData("GET", "/api/colonies/00000000-0000-0000-0000-000000000000/inspections")]
    [InlineData("GET", "/api/inspections/00000000-0000-0000-0000-000000000000")]
    public async Task InspectionEndpoint_ShouldReturnNotFoundWithoutTheStore_WhenTheIdIsEmpty(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { inspectedOn = "2026-04-12", notes = "x" });
        }

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        A.CallTo(_store).MustNotHaveHappened();
    }

    [Fact] // invariant (FR-7): the list is bounded, newest first as the store returns it, and carries the next cursor
    public async Task ListInspections_ShouldReturnAPageAndTheNextCursor_WhenMoreInspectionsRemain()
    {
        A.CallTo(() => _store.ListAsync(KnownColony, 0, 3, A<CancellationToken>._)).Returns(
        [
            Stored(InspectionId.New(), KnownColony, Details(new DateOnly(2026, 4, 12))),
            Stored(InspectionId.New(), KnownColony, Details(new DateOnly(2026, 4, 5))),
            Stored(InspectionId.New(), KnownColony, Details(new DateOnly(2026, 3, 29))),
        ]);

        using var response = await _client.GetAsync($"/api/colonies/{KnownColony}/inspections?limit=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("inspectedOn").GetString())
            .Should().Equal("2026-04-12", "2026-04-05");
        body.RootElement.GetProperty("nextCursor").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact] // boundary: the last page has no cursor, and a colony with no inspections lists none
    public async Task ListInspections_ShouldReturnNoCursor_WhenThePageIsTheLast()
    {
        A.CallTo(() => _store.ListAsync(KnownColony, 0, 51, A<CancellationToken>._)).Returns([]);

        using var response = await _client.GetAsync($"/api/colonies/{KnownColony}/inspections");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
        body.RootElement.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory] // boundary: the list refuses a limit outside 1-100 or a cursor this API did not issue, before the store is touched
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("cursor=bm90LWEtbnVtYmVy")]
    public async Task ListInspections_ShouldReturnValidationProblem_WhenThePagingArgumentsAreInvalid(string query)
    {
        using var response = await _client.GetAsync($"/api/colonies/{KnownColony}/inspections?{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        A.CallTo(_store).MustNotHaveHappened();
    }

    [Fact] // boundary: another account's colony lists as NotFound
    public async Task ListInspections_ShouldReturnNotFound_WhenTheStoreHasNoSuchColony()
    {
        A.CallTo(() => _store.ListAsync(A<ColonyId>._, A<int>._, A<int>._, A<CancellationToken>._)).Returns((IReadOnlyList<Inspection>?)null);

        using var response = await _client.GetAsync($"/api/colonies/{KnownColony}/inspections");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await TitleOf(response)).Should().StartWith("NotFound:");
    }

    [Fact] // regression risk: a read returns the stored inspection whole
    public async Task GetInspection_ShouldReturnTheInspection_WhenTheStoreFindsIt()
    {
        A.CallTo(() => _store.FindAsync(KnownId, A<CancellationToken>._)).Returns(Stored(KnownId, KnownColony, Details(new DateOnly(2026, 4, 12))));

        using var response = await _client.GetAsync($"/api/inspections/{KnownId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("colonyId").GetGuid().Should().Be(KnownColony.Value);
        body.RootElement.GetProperty("findings").GetProperty("queenSeen").GetBoolean().Should().BeTrue();
    }

    [Fact] // boundary: another account's inspection, or none, is NotFound
    public async Task GetInspection_ShouldReturnNotFound_WhenTheStoreFindsNothing()
    {
        A.CallTo(() => _store.FindAsync(A<InspectionId>._, A<CancellationToken>._)).Returns((Inspection?)null);

        using var response = await _client.GetAsync($"/api/inspections/{KnownId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await TitleOf(response)).Should().StartWith("NotFound:");
    }

    private static InspectionDetails Details(DateOnly day) => new()
    {
        InspectedOn = day,
        Notes = "Synthetic note",
        Findings = InspectionFields.Check(new Dictionary<string, JsonElement> { ["queenSeen"] = JsonSerializer.SerializeToElement(true) }).Findings!,
    };

    private static Inspection Stored(InspectionId id, ColonyId colony, InspectionDetails details) => new()
    {
        InspectionId = id,
        AccountId = Current,
        ColonyId = colony,
        InspectedOn = details.InspectedOn,
        RecordedAt = RecordedAt,
        Findings = details.Findings,
        Notes = details.Notes,
    };

    private static async Task<string?> TitleOf(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("title").GetString();
    }
}
