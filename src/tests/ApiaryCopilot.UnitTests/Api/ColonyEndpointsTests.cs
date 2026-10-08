using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ApiaryCopilot.UnitTests.Api;

// The colony endpoints as a caller sees them, through the real host in memory with the store and the current
// account faked (CONVENTIONS.md §8.1).
public sealed class ColonyEndpointsTests : IDisposable
{
    private static readonly AccountId Current = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly ColonyId KnownId = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000c1"));
    private static readonly ColonyId ChildId = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000c2"));
    private static readonly YardId Yard = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000a1"));
    private static readonly YardId OtherYard = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000a2"));
    private static readonly QueenId QueenId = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000e1"));
    private static readonly Guid EventId = Guid.Parse("0199b2a4-0000-7000-8000-0000000000f1");

    private readonly IColonyStore _store = A.Fake<IColonyStore>();
    private readonly ICurrentAccount _currentAccount = A.Fake<ICurrentAccount>();
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ColonyEndpointsTests()
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

    public static TheoryData<string, string> EveryColonyEndpoint() => new()
    {
        { "POST", "/api/colonies" },
        { "POST", "/api/colonies/swarm-captures" },
        { "GET", "/api/colonies" },
        { "GET", $"/api/colonies/{KnownId}" },
        { "GET", $"/api/colonies/{KnownId}/events" },
        { "GET", $"/api/colonies/{KnownId}/queens" },
        { "POST", $"/api/colonies/{KnownId}/splits" },
        { "POST", $"/api/colonies/{KnownId}/merges" },
        { "POST", $"/api/colonies/{KnownId}/requeens" },
        { "POST", $"/api/colonies/{KnownId}/moves" },
    };

    [Theory] // invariant: with no signed-in account (sign-in off) every colony endpoint fails closed, before the store is touched
    [MemberData(nameof(EveryColonyEndpoint))]
    public async Task ColonyEndpoint_ShouldReturnUnavailableAndTouchNothing_WhenNoAccountIsCurrent(string method, string path)
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
    [MemberData(nameof(EveryColonyEndpoint))]
    public async Task ColonyEndpoint_ShouldBeRefusedAndTouchNothing_WhenTheSubjectIsNotAUserOfTheAccount(string method, string path)
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

    [Fact] // invariant (FR-6, D-2): no endpoint can change or delete a recorded event; the colony surface is reads and appends
    public void ColonyRoutes_ShouldOfferOnlyReadsAndAppends_WhenTheHostIsBuilt()
    {
        var routes = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/colonies", StringComparison.Ordinal)
                && !e.RoutePattern.RawText.Contains("/inspections", StringComparison.Ordinal)) // a colony's inspections are guarded by InspectionEndpointsTests
            .Select(e => (Route: e.RoutePattern.RawText!, Methods: e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods))
            .ToList();

        routes.Should().HaveCount(10);
        routes.SelectMany(r => r.Methods).Distinct().Should().BeSubsetOf(["GET", "POST"]);
        routes.Where(r => r.Route.Contains("/events", StringComparison.Ordinal)).Should().ContainSingle()
            .Which.Methods.Should().Equal("GET");
    }

    public static TheoryData<string, string, HttpStatusCode> AttemptsToChangeAnEvent() => new()
    {
        { "PUT", $"/api/colonies/{KnownId}/events", HttpStatusCode.MethodNotAllowed },
        { "PATCH", $"/api/colonies/{KnownId}/events", HttpStatusCode.MethodNotAllowed },
        { "DELETE", $"/api/colonies/{KnownId}/events", HttpStatusCode.MethodNotAllowed },
        { "POST", $"/api/colonies/{KnownId}/events", HttpStatusCode.MethodNotAllowed },
        { "PUT", $"/api/colonies/{KnownId}/events/{EventId}", HttpStatusCode.NotFound },
        { "PATCH", $"/api/colonies/{KnownId}/events/{EventId}", HttpStatusCode.NotFound },
        { "DELETE", $"/api/colonies/{KnownId}/events/{EventId}", HttpStatusCode.NotFound },
        { "DELETE", $"/api/colonies/{KnownId}", HttpStatusCode.MethodNotAllowed },
        { "PUT", $"/api/colonies/{KnownId}", HttpStatusCode.MethodNotAllowed },
    };

    [Theory] // invariant (FR-6, D-2): an attempt to change or delete history is refused by the router; the store is never reached
    [MemberData(nameof(AttemptsToChangeAnEvent))]
    public async Task ChangingAnEvent_ShouldBeRefusedWithoutTouchingTheStore_WhenAttemptedOverHttp(string method, string path, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { occurredOn = "2020-01-01" }) };

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(expected);
        A.CallTo(_store).MustNotHaveHappened();
    }

    [Fact] // regression risk: a registered colony answers 201 with its location, its state and its queen
    public async Task RegisterColony_ShouldReturnCreatedWithTheColony_WhenTheStoreRecordsIt()
    {
        A.CallTo(() => _store.RegisterAsync(A<ColonyProfile>._, A<QueenDetails?>._, A<DateOnly>._, A<CancellationToken>._))
            .ReturnsLazily((ColonyProfile profile, QueenDetails? queen, DateOnly _, CancellationToken _) => ColonyChangeResult.Recorded(State(KnownId, profile, queen)));

        using var response = await _client.PostAsJsonAsync("/api/colonies", ValidColony());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.ToString().Should().Be($"/api/colonies/{KnownId}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var colony = body.RootElement;
        colony.GetProperty("id").GetGuid().Should().Be(KnownId.Value);
        colony.GetProperty("tagId").GetString().Should().Be("A-01");
        colony.GetProperty("hiveNumber").GetString().Should().Be("TX-0412");
        colony.GetProperty("yardId").GetGuid().Should().Be(Yard.Value);
        colony.GetProperty("hiveType").GetString().Should().Be("Langstroth 10-frame");
        colony.GetProperty("boxCount").GetInt32().Should().Be(2);
        colony.GetProperty("status").GetString().Should().Be("active");
        colony.GetProperty("origin").GetString().Should().Be("registered");
        colony.GetProperty("queen").GetProperty("markColour").GetString().Should().Be("green");
        colony.GetProperty("queen").GetProperty("introducedOn").GetString().Should().Be("2026-05-03");
        colony.TryGetProperty("accountId", out _).Should().BeFalse();
        colony.TryGetProperty("version", out _).Should().BeFalse();
        A.CallTo(() => _store.RegisterAsync(
                A<ColonyProfile>.That.Matches(p => p.TagId == "A-01" && p.YardId == Yard),
                A<QueenDetails?>.That.Matches(q => q!.MarkColour == QueenMarkColour.Green),
                new DateOnly(2026, 4, 12),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact] // regression risk: a captured swarm goes to its own store operation
    public async Task CaptureSwarm_ShouldRecordASwarmCapture_WhenTheRequestIsValid()
    {
        A.CallTo(() => _store.CaptureSwarmAsync(A<ColonyProfile>._, A<QueenDetails?>._, A<DateOnly>._, A<CancellationToken>._))
            .ReturnsLazily((ColonyProfile profile, QueenDetails? queen, DateOnly _, CancellationToken _) => ColonyChangeResult.Recorded(State(KnownId, profile, queen, ColonyOrigin.SwarmCapture)));

        using var response = await _client.PostAsJsonAsync("/api/colonies/swarm-captures", ValidColony());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("origin").GetString().Should().Be("swarm-capture");
        A.CallTo(() => _store.RegisterAsync(A<ColonyProfile>._, A<QueenDetails?>._, A<DateOnly>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact] // regression risk: a split answers 201 with the new colony, located at its own path
    public async Task SplitColony_ShouldReturnCreatedWithTheNewColony_WhenTheStoreRecordsIt()
    {
        A.CallTo(() => _store.SplitAsync(KnownId, A<ColonyProfile>._, A<QueenDetails?>._, A<DateOnly>._, A<CancellationToken>._))
            .ReturnsLazily((ColonyId parent, ColonyProfile profile, QueenDetails? queen, DateOnly _, CancellationToken _) =>
                ColonyChangeResult.Recorded(State(ChildId, profile, queen, ColonyOrigin.Split, parent)));

        using var response = await _client.PostAsJsonAsync($"/api/colonies/{KnownId}/splits", ValidColony() with { TagId = "A-02" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.ToString().Should().Be($"/api/colonies/{ChildId}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("parentColonyId").GetGuid().Should().Be(KnownId.Value);
        body.RootElement.GetProperty("origin").GetString().Should().Be("split");
    }

    [Fact] // regression risk: a merge answers 200 with the merged colony
    public async Task MergeColony_ShouldReturnTheMergedColony_WhenTheStoreRecordsIt()
    {
        var merged = State(KnownId, Profile(), null);
        merged.Colony.Status = ColonyStatus.Merged;
        merged.Colony.MergedIntoColonyId = ChildId;
        A.CallTo(() => _store.MergeAsync(KnownId, ChildId, new DateOnly(2026, 4, 12), A<CancellationToken>._)).Returns(ColonyChangeResult.Recorded(merged));

        using var response = await _client.PostAsJsonAsync($"/api/colonies/{KnownId}/merges", new { intoColonyId = ChildId.Value, occurredOn = "2026-04-12" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetString().Should().Be("merged");
        body.RootElement.GetProperty("mergedIntoColonyId").GetGuid().Should().Be(ChildId.Value);
    }

    [Fact] // regression risk: a requeen passes the queen to the store and answers with the colony's new queen
    public async Task RequeenColony_ShouldReturnTheColonyWithItsNewQueen_WhenTheStoreRecordsIt()
    {
        A.CallTo(() => _store.RequeenAsync(KnownId, A<QueenDetails>._, A<CancellationToken>._))
            .ReturnsLazily((ColonyId id, QueenDetails queen, CancellationToken _) => ColonyChangeResult.Recorded(State(id, Profile(), queen)));

        using var response = await _client.PostAsJsonAsync($"/api/colonies/{KnownId}/requeens", ValidQueen() with { Source = "Raised in the yard" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("queen").GetProperty("source").GetString().Should().Be("Raised in the yard");
    }

    [Fact] // regression risk: a backdated requeen is answered 200 with a warning keyed by its date field, naming both days
    public async Task RequeenColony_ShouldCarryAWarningNamingBothDays_WhenTheStoreRecordsItWithOne()
    {
        A.CallTo(() => _store.RequeenAsync(KnownId, A<QueenDetails>._, A<CancellationToken>._))
            .ReturnsLazily((ColonyId id, QueenDetails queen, CancellationToken _) => ColonyChangeResult.Recorded(
                State(id, Profile(), queen), new RequeenBeforeCurrentQueen(new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 12))));

        using var response = await _client.PostAsJsonAsync($"/api/colonies/{KnownId}/requeens", ValidQueen() with { IntroducedOn = "2026-03-01" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("queen").GetProperty("introducedOn").GetString().Should().Be("2026-03-01");
        var warnings = body.RootElement.GetProperty("warnings");
        warnings.EnumerateObject().Select(p => p.Name).Should().Equal("introducedOn");
        warnings.GetProperty("introducedOn").GetArrayLength().Should().Be(1);
        warnings.GetProperty("introducedOn")[0].GetString().Should().StartWith("Warning: introducedOn 2026-03-01 is before 2026-04-12,");
    }

    [Fact] // boundary: an ordinary requeen carries no warnings at all, not an empty set
    public async Task RequeenColony_ShouldCarryNoWarnings_WhenTheStoreRecordsItWithoutOne()
    {
        A.CallTo(() => _store.RequeenAsync(KnownId, A<QueenDetails>._, A<CancellationToken>._))
            .ReturnsLazily((ColonyId id, QueenDetails queen, CancellationToken _) => ColonyChangeResult.Recorded(State(id, Profile(), queen)));

        using var response = await _client.PostAsJsonAsync($"/api/colonies/{KnownId}/requeens", ValidQueen());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.TryGetProperty("warnings", out _).Should().BeFalse();
    }

    [Fact] // boundary: a requeen the store refuses as future-dated is a Validation 400 keyed by the requeen's date field
    public async Task RequeenColony_ShouldReturnValidationProblemKeyedByIntroducedOn_WhenTheStoreRefusesAFutureDay()
    {
        A.CallTo(() => _store.RequeenAsync(KnownId, A<QueenDetails>._, A<CancellationToken>._))
            .Returns(ColonyChangeResult.Refused(ColonyRefusal.IntroducedInFuture));

        using var response = await _client.PostAsJsonAsync($"/api/colonies/{KnownId}/requeens", ValidQueen());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("title").GetString().Should().StartWith("Validation:");
        body.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().Equal("introducedOn");
        body.RootElement.GetProperty("errors").GetProperty("introducedOn")[0].GetString().Should().StartWith("Validation:").And.Contain("future");
    }

    [Fact] // regression risk: a move passes the yard and day to the store and answers with the colony where it is now
    public async Task MoveColony_ShouldReturnTheMovedColony_WhenTheStoreRecordsIt()
    {
        A.CallTo(() => _store.MoveAsync(KnownId, OtherYard, new DateOnly(2026, 4, 12), A<CancellationToken>._))
            .Returns(ColonyChangeResult.Recorded(State(KnownId, Profile() with { YardId = OtherYard }, null)));

        using var response = await _client.PostAsJsonAsync($"/api/colonies/{KnownId}/moves", new { yardId = OtherYard.Value, occurredOn = "2026-04-12" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("yardId").GetGuid().Should().Be(OtherYard.Value);
    }

    public static TheoryData<string, object> EveryLineageChange() => new()
    {
        { $"/api/colonies/{KnownId}/splits", ValidColony() },
        { $"/api/colonies/{KnownId}/merges", new { intoColonyId = ChildId.Value, occurredOn = "2026-04-12" } },
        { $"/api/colonies/{KnownId}/requeens", ValidQueen() },
        { $"/api/colonies/{KnownId}/moves", new { yardId = OtherYard.Value, occurredOn = "2026-04-12" } },
    };

    [Theory] // boundary: a change to a colony the account does not have is NotFound (§4.3)
    [MemberData(nameof(EveryLineageChange))]
    public async Task LineageChange_ShouldReturnNotFoundProblem_WhenTheStoreFindsNoSuchColony(string path, object body)
    {
        StoreAnswers(ColonyChangeResult.NotFound);

        using var response = await _client.PostAsJsonAsync(path, body);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await TitleOf(response)).Should().StartWith("NotFound:");
    }

    [Theory] // boundary: a writer that lost a race is told to retry, as an Unavailable result, not a 500
    [MemberData(nameof(EveryLineageChange))]
    public async Task LineageChange_ShouldReturnUnavailableProblem_WhenTheColonyChangedMeanwhile(string path, object body)
    {
        StoreAnswers(ColonyChangeResult.Conflict);

        using var response = await _client.PostAsJsonAsync(path, body);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await TitleOf(response)).Should().StartWith("Unavailable:").And.Contain("retry");
    }

    [Theory] // boundary: each rule the store refuses on is a validation result naming the field at fault
    [InlineData(ColonyRefusal.TagIdTaken, "tagId")]
    [InlineData(ColonyRefusal.NoSuchYard, "yardId")]
    [InlineData(ColonyRefusal.AlreadyInThatYard, "yardId")]
    [InlineData(ColonyRefusal.NotActive, "id")]
    [InlineData(ColonyRefusal.NoSuchTargetColony, "intoColonyId")]
    [InlineData(ColonyRefusal.TargetNotActive, "intoColonyId")]
    [InlineData(ColonyRefusal.MergeIntoItself, "intoColonyId")]
    [InlineData(ColonyRefusal.IntroducedInFuture, "introducedOn")]
    public async Task RegisterColony_ShouldReturnValidationProblemNamingTheField_WhenTheStoreRefuses(ColonyRefusal refusal, string field)
    {
        A.CallTo(() => _store.RegisterAsync(A<ColonyProfile>._, A<QueenDetails?>._, A<DateOnly>._, A<CancellationToken>._))
            .Returns(ColonyChangeResult.Refused(refusal));

        using var response = await _client.PostAsJsonAsync("/api/colonies", ValidColony());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("title").GetString().Should().StartWith("Validation:");
        body.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().Equal(field);
        body.RootElement.GetProperty("errors").GetProperty(field)[0].GetString().Should().StartWith("Validation:");
    }

    public static TheoryData<string, object> InvalidBodies() => new()
    {
        { "/api/colonies", ValidColony() with { BoxCount = 0 } },
        { "/api/colonies/swarm-captures", ValidColony() with { TagId = "" } },
        { $"/api/colonies/{KnownId}/splits", ValidColony() with { YardId = null } },
        { $"/api/colonies/{KnownId}/merges", new { occurredOn = "2026-04-12" } },
        { $"/api/colonies/{KnownId}/requeens", ValidQueen() with { MarkColour = "purple" } },
        { $"/api/colonies/{KnownId}/moves", new { yardId = OtherYard.Value } },
    };

    [Theory] // boundary: an invalid request is a categorized validation result, and nothing is written
    [MemberData(nameof(InvalidBodies))]
    public async Task ColonyWrite_ShouldReturnValidationProblem_WhenTheRequestIsInvalid(string path, object body)
    {
        using var response = await _client.PostAsJsonAsync(path, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await TitleOf(response)).Should().StartWith("Validation:");
        A.CallTo(_store).MustNotHaveHappened();
    }

    [Theory] // boundary: the empty id names no colony, and is NotFound rather than an exception
    [InlineData("GET", "")]
    [InlineData("GET", "/events")]
    [InlineData("GET", "/queens")]
    [InlineData("POST", "/splits")]
    [InlineData("POST", "/merges")]
    [InlineData("POST", "/requeens")]
    [InlineData("POST", "/moves")]
    public async Task ColonyEndpoint_ShouldReturnNotFoundProblem_WhenTheIdIsEmpty(string method, string suffix)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/colonies/{Guid.Empty}{suffix}");
        if (method == "POST")
        {
            request.Content = JsonContent.Create<object>(suffix switch
            {
                "/merges" => new { intoColonyId = ChildId.Value, occurredOn = "2026-04-12" },
                "/moves" => new { yardId = OtherYard.Value, occurredOn = "2026-04-12" },
                "/requeens" => ValidQueen(),
                _ => ValidColony(),
            });
        }

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await TitleOf(response)).Should().StartWith("NotFound:");
        A.CallTo(_store).MustNotHaveHappened();
    }

    [Fact] // boundary: an unknown colony, or another account's, is NotFound
    public async Task GetColony_ShouldReturnNotFoundProblem_WhenTheStoreFindsNoSuchColony()
    {
        A.CallTo(() => _store.FindAsync(KnownId, A<CancellationToken>._)).Returns((ColonyState?)null);

        using var response = await _client.GetAsync($"/api/colonies/{KnownId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await TitleOf(response)).Should().StartWith("NotFound:");
    }

    [Fact] // regression risk: a read returns the stored colony
    public async Task GetColony_ShouldReturnTheColony_WhenTheStoreFindsIt()
    {
        A.CallTo(() => _store.FindAsync(KnownId, A<CancellationToken>._)).Returns(State(KnownId, Profile(), null));

        using var response = await _client.GetAsync($"/api/colonies/{KnownId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("tagId").GetString().Should().Be("A-01");
        body.RootElement.GetProperty("queen").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact] // invariant: the colony list is bounded, ordered as the store returns it, and carries the next cursor
    public async Task ListColonies_ShouldReturnAPageAndTheNextCursor_WhenMoreColoniesRemain()
    {
        A.CallTo(() => _store.ListAsync(0, 3, A<CancellationToken>._))
            .Returns([State(ColonyId.New(), Profile() with { TagId = "A" }, null), State(ColonyId.New(), Profile() with { TagId = "B" }, null), State(ColonyId.New(), Profile() with { TagId = "C" }, null)]);

        using var response = await _client.GetAsync("/api/colonies?limit=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("tagId").GetString()).Should().Equal("A", "B");
        body.RootElement.GetProperty("nextCursor").GetString().Should().NotBeNullOrEmpty();
    }

    [Theory] // boundary: every list refuses a limit outside 1-100 before the store is touched
    [InlineData("/api/colonies?limit=101")]
    [InlineData("/api/colonies/0199b2a4-0000-7000-8000-0000000000c1/events?limit=0")]
    [InlineData("/api/colonies/0199b2a4-0000-7000-8000-0000000000c1/queens?cursor=bm90LWEtbnVtYmVy")]
    public async Task ColonyList_ShouldReturnValidationProblem_WhenThePagingArgumentsAreInvalid(string path)
    {
        using var response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        A.CallTo(_store).MustNotHaveHappened();
    }

    [Fact] // regression risk: the history lists each event with what it names, oldest first, a page at a time
    public async Task ListColonyEvents_ShouldReturnEachEventWithWhatItNames_WhenTheColonyHasHistory()
    {
        var recordedAt = new DateTimeOffset(2026, 4, 12, 18, 30, 0, TimeSpan.Zero);
        A.CallTo(() => _store.ListEventsAsync(KnownId, 0, 51, A<CancellationToken>._)).Returns(
        [
            new ColonyEvent(new ColonyEventId(EventId), Current, KnownId, new DateOnly(2026, 4, 1), recordedAt, new ColonyRegistered { Colony = Profile(), Queen = new NewQueen { QueenId = QueenId, Details = Queen() } }),
            new ColonyEvent(ColonyEventId.New(), Current, KnownId, new DateOnly(2026, 4, 12), recordedAt, new ColonySplit { NewColonyId = ChildId, NewColony = Profile() with { YardId = OtherYard } }),
            new ColonyEvent(ColonyEventId.New(), Current, KnownId, new DateOnly(2026, 4, 13), recordedAt, new ColonyMoved { FromYardId = Yard, ToYardId = OtherYard }),
            new ColonyEvent(ColonyEventId.New(), Current, KnownId, new DateOnly(2026, 4, 14), recordedAt, new ColonyRequeened { Queen = new NewQueen { QueenId = QueenId.New(), Details = Queen() }, ReplacedQueenId = QueenId }),
            new ColonyEvent(ColonyEventId.New(), Current, KnownId, new DateOnly(2026, 4, 15), recordedAt, new ColonyMerged { IntoColonyId = ChildId }),
            new ColonyEvent(ColonyEventId.New(), Current, ChildId, new DateOnly(2026, 4, 16), recordedAt, new SwarmCaptured { Colony = Profile() }),
        ]);

        using var response = await _client.GetAsync($"/api/colonies/{KnownId}/events");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = body.RootElement.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("type").GetString()).Should().Equal("registered", "split", "move", "requeen", "merge", "swarm-capture");
        items[0].GetProperty("id").GetGuid().Should().Be(EventId);
        items[0].GetProperty("queenId").GetGuid().Should().Be(QueenId.Value);
        items[0].GetProperty("yardId").GetGuid().Should().Be(Yard.Value);
        items[0].GetProperty("occurredOn").GetString().Should().Be("2026-04-01");
        items[1].GetProperty("relatedColonyId").GetGuid().Should().Be(ChildId.Value);
        items[1].GetProperty("yardId").GetGuid().Should().Be(OtherYard.Value);
        items[2].GetProperty("fromYardId").GetGuid().Should().Be(Yard.Value);
        items[2].GetProperty("yardId").GetGuid().Should().Be(OtherYard.Value);
        items[3].GetProperty("replacedQueenId").GetGuid().Should().Be(QueenId.Value);
        items[4].GetProperty("relatedColonyId").GetGuid().Should().Be(ChildId.Value);
        items[4].GetProperty("yardId").ValueKind.Should().Be(JsonValueKind.Null);
        body.RootElement.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact] // regression risk: the queen history lists each queen with when she was replaced
    public async Task ListQueens_ShouldReturnTheReplacementHistory_WhenTheColonyHasQueens()
    {
        var replaced = new Queen { QueenId = QueenId, AccountId = Current, ColonyId = KnownId, Details = Queen(), ReplacedOn = new DateOnly(2026, 6, 1), ReplacedByQueenId = QueenId.New() };
        A.CallTo(() => _store.ListQueensAsync(KnownId, 0, 51, A<CancellationToken>._)).Returns([replaced]);

        using var response = await _client.GetAsync($"/api/colonies/{KnownId}/queens");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var queen = body.RootElement.GetProperty("items")[0];
        queen.GetProperty("id").GetGuid().Should().Be(QueenId.Value);
        queen.GetProperty("replacedOn").GetString().Should().Be("2026-06-01");
        queen.GetProperty("replacedByQueenId").GetGuid().Should().Be(replaced.ReplacedByQueenId!.Value.Value);
    }

    [Theory] // boundary: the history of a colony the account does not have is NotFound, not an empty page
    [InlineData("events")]
    [InlineData("queens")]
    public async Task ColonyHistory_ShouldReturnNotFoundProblem_WhenTheStoreFindsNoSuchColony(string history)
    {
        A.CallTo(() => _store.ListEventsAsync(KnownId, A<int>._, A<int>._, A<CancellationToken>._)).Returns((IReadOnlyList<ColonyEvent>?)null);
        A.CallTo(() => _store.ListQueensAsync(KnownId, A<int>._, A<int>._, A<CancellationToken>._)).Returns((IReadOnlyList<Queen>?)null);

        using var response = await _client.GetAsync($"/api/colonies/{KnownId}/{history}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await TitleOf(response)).Should().StartWith("NotFound:");
    }

    private void StoreAnswers(ColonyChangeResult result)
    {
        A.CallTo(() => _store.SplitAsync(A<ColonyId>._, A<ColonyProfile>._, A<QueenDetails?>._, A<DateOnly>._, A<CancellationToken>._)).Returns(result);
        A.CallTo(() => _store.MergeAsync(A<ColonyId>._, A<ColonyId>._, A<DateOnly>._, A<CancellationToken>._)).Returns(result);
        A.CallTo(() => _store.RequeenAsync(A<ColonyId>._, A<QueenDetails>.Ignored, A<CancellationToken>._)).Returns(result);
        A.CallTo(() => _store.MoveAsync(A<ColonyId>._, A<YardId>._, A<DateOnly>._, A<CancellationToken>._)).Returns(result);
    }

    private static ColonyState State(ColonyId id, ColonyProfile profile, QueenDetails? queen, ColonyOrigin origin = ColonyOrigin.Registered, ColonyId? parent = null) => new(
        new Colony
        {
            ColonyId = id,
            AccountId = Current,
            TagId = profile.TagId,
            HiveNumber = profile.HiveNumber,
            HiveType = profile.HiveType,
            BoxCount = profile.BoxCount,
            Origin = origin,
            ParentColonyId = parent,
            YardId = profile.YardId,
            Status = ColonyStatus.Active,
            Version = 1,
        },
        queen is null ? null : new Queen { QueenId = QueenId, AccountId = Current, ColonyId = id, Details = queen });

    private static ColonyProfile Profile() => new()
    {
        TagId = "A-01",
        HiveNumber = "TX-0412",
        YardId = Yard,
        HiveType = "Langstroth 10-frame",
        BoxCount = 2,
    };

    private static QueenDetails Queen() => new()
    {
        Source = "Synthetic Queen Breeder",
        MarkColour = QueenMarkColour.Green,
        MarkYear = 2024,
        IntroducedOn = new DateOnly(2026, 5, 3),
    };

    private static ColonyBody ValidColony() => new("A-01", "TX-0412", Yard.Value, "Langstroth 10-frame", 2, "2026-04-12", ValidQueen());

    private static QueenBody ValidQueen() => new("Synthetic Queen Breeder", "green", 2024, "2026-05-03");

    private static async Task<string?> TitleOf(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("title").GetString();
    }

    // The wire shape as a caller writes it, independent of the API's own request types.
    private sealed record ColonyBody(string? TagId, string? HiveNumber, Guid? YardId, string? HiveType, int? BoxCount, string? OccurredOn, QueenBody? Queen);

    private sealed record QueenBody(string? Source, string? MarkColour, int? MarkYear, string? IntroducedOn);
}
