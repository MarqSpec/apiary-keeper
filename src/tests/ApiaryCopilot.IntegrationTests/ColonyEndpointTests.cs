using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Npgsql;

namespace ApiaryCopilot.IntegrationTests;

// Colonies, queens and their append-only lineage end to end: the real server (source or image) on a real PostgreSQL
// the fixture migrated and seeded with synthetic accounts. Deployment=None: the database is a loopback container.
[Trait("Deployment", "None")]
[Collection(SharedServer.Name)]
public sealed class ColonyEndpointTests(AppServerFixture server)
{
    [Fact] // regression risk (FR-6): a split runs end to end — the child exists, and both histories show the one split
    public async Task SplitColony_ShouldCreateTheChildAndRecordTheSplitInBothHistories_WhenRunEndToEnd()
    {
        var home = await CreateYardAsync();
        var away = await CreateYardAsync();
        var parent = await RegisterAsync(home, queen: Queen("Synthetic Queen Breeder", "2026-03-01"));

        using var split = await server.Client.PostAsJsonAsync(
            $"/api/colonies/{parent}/splits",
            Colony(away, "2026-04-12", Queen("Raised in the yard", "2026-04-12")) with { BoxCount = 1 });

        split.StatusCode.Should().Be(HttpStatusCode.Created);
        using var childBody = JsonDocument.Parse(await split.Content.ReadAsStringAsync());
        var childId = childBody.RootElement.GetProperty("id").GetGuid();
        split.Headers.Location!.ToString().Should().Be($"/api/colonies/{childId}");
        using var child = await GetJsonAsync($"/api/colonies/{childId}");
        child.RootElement.GetProperty("parentColonyId").GetGuid().Should().Be(parent);
        child.RootElement.GetProperty("origin").GetString().Should().Be("split");
        child.RootElement.GetProperty("status").GetString().Should().Be("active");
        child.RootElement.GetProperty("yardId").GetGuid().Should().Be(away);
        child.RootElement.GetProperty("boxCount").GetInt32().Should().Be(1);
        child.RootElement.GetProperty("queen").GetProperty("source").GetString().Should().Be("Raised in the yard");
        using var parentNow = await GetJsonAsync($"/api/colonies/{parent}");
        parentNow.RootElement.GetProperty("status").GetString().Should().Be("active");
        parentNow.RootElement.GetProperty("yardId").GetGuid().Should().Be(home);
        parentNow.RootElement.GetProperty("queen").GetProperty("source").GetString().Should().Be("Synthetic Queen Breeder");
        var parentHistory = await EventsAsync(parent);
        var childHistory = await EventsAsync(childId);
        parentHistory.Select(e => e.GetProperty("type").GetString()).Should().Equal("registered", "split");
        childHistory.Should().ContainSingle();
        childHistory[0].GetProperty("id").GetGuid().Should().Be(parentHistory[1].GetProperty("id").GetGuid());
        childHistory[0].GetProperty("colonyId").GetGuid().Should().Be(parent);
        childHistory[0].GetProperty("relatedColonyId").GetGuid().Should().Be(childId);
        childHistory[0].GetProperty("occurredOn").GetString().Should().Be("2026-04-12");
    }

    [Fact] // regression risk (FR-5, FR-6): a requeen, a move and a merge are each projected, and the queen history kept
    public async Task Lineage_ShouldProjectRequeenMoveAndMerge_WhenRecordedThroughTheServer()
    {
        var home = await CreateYardAsync();
        var away = await CreateYardAsync();
        var kept = await RegisterAsync(home, queen: Queen("Synthetic Queen Breeder", "2025-05-01"));
        var weak = await RegisterAsync(home);

        using var requeen = await server.Client.PostAsJsonAsync($"/api/colonies/{kept}/requeens", Queen("Raised in the yard", "2026-06-01") with { MarkColour = "blue" });
        using var move = await server.Client.PostAsJsonAsync($"/api/colonies/{kept}/moves", new { yardId = away, occurredOn = "2026-06-02" });
        using var merge = await server.Client.PostAsJsonAsync($"/api/colonies/{weak}/merges", new { intoColonyId = kept, occurredOn = "2026-06-03" });

        requeen.StatusCode.Should().Be(HttpStatusCode.OK);
        move.StatusCode.Should().Be(HttpStatusCode.OK);
        merge.StatusCode.Should().Be(HttpStatusCode.OK);
        using var keptNow = await GetJsonAsync($"/api/colonies/{kept}");
        keptNow.RootElement.GetProperty("yardId").GetGuid().Should().Be(away);
        keptNow.RootElement.GetProperty("queen").GetProperty("markColour").GetString().Should().Be("blue");
        using var weakNow = await GetJsonAsync($"/api/colonies/{weak}");
        weakNow.RootElement.GetProperty("status").GetString().Should().Be("merged");
        weakNow.RootElement.GetProperty("mergedIntoColonyId").GetGuid().Should().Be(kept);
        using var queens = await GetJsonAsync($"/api/colonies/{kept}/queens");
        var history = queens.RootElement.GetProperty("items").EnumerateArray().ToList();
        history.Select(q => q.GetProperty("source").GetString()).Should().Equal("Raised in the yard", "Synthetic Queen Breeder");
        history[1].GetProperty("replacedOn").GetString().Should().Be("2026-06-01");
        history[1].GetProperty("replacedByQueenId").GetGuid().Should().Be(history[0].GetProperty("id").GetGuid());
        (await EventsAsync(kept)).Select(e => e.GetProperty("type").GetString()).Should().Equal("registered", "requeen", "move", "merge");
        using var refused = await server.Client.PostAsJsonAsync($"/api/colonies/{weak}/moves", new { yardId = home, occurredOn = "2026-06-04" });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact] // boundary (FR-5, FR-6): a requeen dated in the future is refused on its date field, and nothing is appended
    public async Task RequeenColony_ShouldRefuseIntroducedOnAndAppendNothing_WhenTheDateIsInTheFuture()
    {
        var colony = await RegisterAsync(await CreateYardAsync(), queen: Queen("Synthetic Queen Breeder", "2026-03-01"));
        var future = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        using var requeen = await server.Client.PostAsJsonAsync($"/api/colonies/{colony}/requeens", Queen("Raised in the yard", future));

        requeen.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await requeen.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("title").GetString().Should().StartWith("Validation:");
        body.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).Should().Equal("introducedOn");
        (await EventsAsync(colony)).Select(e => e.GetProperty("type").GetString()).Should().Equal("registered");
        using var colonyNow = await GetJsonAsync($"/api/colonies/{colony}");
        colonyNow.RootElement.GetProperty("queen").GetProperty("source").GetString().Should().Be("Synthetic Queen Breeder");
    }

    [Fact] // invariant (FR-6, D-2): a backdated requeen is appended as given and answered with a warning the history never holds
    public async Task RequeenColony_ShouldAppendAsGivenAndWarn_WhenTheDateIsBeforeTheCurrentQueensIntroduction()
    {
        var colony = await RegisterAsync(await CreateYardAsync(), queen: Queen("Synthetic Queen Breeder", "2026-05-10"));

        using var requeen = await server.Client.PostAsJsonAsync($"/api/colonies/{colony}/requeens", Queen("Raised in the yard", "2026-03-01"));

        requeen.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await requeen.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("queen").GetProperty("introducedOn").GetString().Should().Be("2026-03-01");
        var warning = body.RootElement.GetProperty("warnings").GetProperty("introducedOn")[0].GetString();
        warning.Should().StartWith("Warning:").And.Contain("2026-03-01").And.Contain("2026-05-10");
        var appended = (await EventsAsync(colony))[1];
        appended.GetProperty("type").GetString().Should().Be("requeen");
        appended.GetProperty("occurredOn").GetString().Should().Be("2026-03-01");
        var stored = $"SELECT count(*) FROM \"ColonyEvents\" WHERE \"ColonyId\" = '{colony}'";
        (await server.Database.CountAsync(stored)).Should().Be(2, "the query must reach the colony's events before their absence of a flag means anything");
        (await server.Database.CountAsync($"{stored} AND \"Data\"::text ILIKE '%warn%'")).Should().Be(0);
        using var read = await GetJsonAsync($"/api/colonies/{colony}");
        read.RootElement.TryGetProperty("warnings", out _).Should().BeFalse();
    }

    [Fact] // boundary: a requeen dated after the current queen's introduction carries no warning
    public async Task RequeenColony_ShouldCarryNoWarnings_WhenTheDateIsAfterTheCurrentQueensIntroduction()
    {
        var colony = await RegisterAsync(await CreateYardAsync(), queen: Queen("Synthetic Queen Breeder", "2026-03-01"));

        using var requeen = await server.Client.PostAsJsonAsync($"/api/colonies/{colony}/requeens", Queen("Raised in the yard", "2026-05-10"));

        requeen.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await requeen.Content.ReadAsStringAsync());
        body.RootElement.TryGetProperty("warnings", out _).Should().BeFalse();
    }

    [Fact] // boundary: the database columns hold every field at the longest the API accepts
    public async Task RegisterColony_ShouldStoreEveryField_WhenEachIsAtItsMaximumLength()
    {
        var yard = await CreateYardAsync();
        var tag = $"{Guid.NewGuid():N}{new string('t', 32)}";
        var request = Colony(yard, "2026-04-01", Queen(new string('s', 200), "2026-04-01")) with
        {
            TagId = tag,
            HiveNumber = new string('ñ', 64),
            HiveType = new string('h', 100),
            BoxCount = 30,
        };

        using var created = await server.Client.PostAsJsonAsync("/api/colonies", request);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var read = await GetJsonAsync(created.Headers.Location!.ToString());
        read.RootElement.GetProperty("tagId").GetString().Should().Be(tag);
        read.RootElement.GetProperty("hiveNumber").GetString().Should().Be(request.HiveNumber);
        read.RootElement.GetProperty("hiveType").GetString().Should().Be(request.HiveType);
        read.RootElement.GetProperty("queen").GetProperty("source").GetString().Should().Be(new string('s', 200));
    }

    [Fact] // invariant: a tag id is unique within the account
    public async Task RegisterColony_ShouldRefuseTheTag_WhenAnotherColonyCarriesIt()
    {
        var yard = await CreateYardAsync();
        var tag = $"DUP-{Guid.NewGuid():N}"[..20];
        using var first = await server.Client.PostAsJsonAsync("/api/colonies", Colony(yard, "2026-04-01") with { TagId = tag });

        using var second = await server.Client.PostAsJsonAsync("/api/colonies/swarm-captures", Colony(yard, "2026-04-02") with { TagId = tag });

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errors").GetProperty("tagId")[0].GetString().Should().StartWith("Validation:");
    }

    [Fact] // regression (race): a registration whose tag another one takes after the pre-check is the tagId 400, not a 500
    public async Task RegisterColony_ShouldRefuseTheTag_WhenAnotherRegistrationOfItCommitsFirst()
    {
        var yard = await CreateYardAsync();
        var tag = $"RACE-{Guid.NewGuid():N}"[..20];
        await using var first = await server.Database.HoldColonyWriteAsync(
            server.CurrentAccountId,
            store => store.RegisterAsync(Profile(tag, yard), null, new DateOnly(2026, 4, 1), CancellationToken.None));
        first.Result.Outcome.Should().Be(ColonyChangeOutcome.Recorded);

        var second = server.Client.PostAsJsonAsync("/api/colonies", Colony(yard, "2026-04-01") with { TagId = tag });
        (await server.Database.WaitsOnALockAsync(second)).Should().BeTrue("the server's insert passed the pre-check and waits on the held tag");
        await first.CommitAsync();
        using var response = await second;

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errors").GetProperty("tagId")[0].GetString().Should().StartWith("Validation:");
        (await server.Database.CountAsync($"SELECT count(*) FROM \"Colonies\" WHERE \"TagId\" = '{tag}'")).Should().Be(1);
        (await server.Database.CountAsync($"SELECT count(*) FROM \"ColonyEvents\" WHERE \"Data\"->'colony'->>'tagId' = '{tag}'")).Should().Be(1);
    }

    [Fact] // regression (race): a merge into a colony that is merged meanwhile is the retry result, and nothing of it is kept
    public async Task MergeColony_ShouldAskForARetryAndKeepNothing_WhenTheTargetIsMergedMeanwhile()
    {
        var yard = await CreateYardAsync();
        var colony = await RegisterAsync(yard);
        var target = await RegisterAsync(yard);
        var beyond = await RegisterAsync(yard);
        await using var targetMerge = await server.Database.HoldColonyWriteAsync(
            server.CurrentAccountId,
            store => store.MergeAsync(new ColonyId(target), new ColonyId(beyond), new DateOnly(2026, 6, 3), CancellationToken.None));
        targetMerge.Result.Outcome.Should().Be(ColonyChangeOutcome.Recorded);

        var merge = server.Client.PostAsJsonAsync($"/api/colonies/{colony}/merges", new { intoColonyId = target, occurredOn = "2026-06-03" });
        (await server.Database.WaitsOnALockAsync(merge)).Should().BeTrue("the server's merge waits on the target's held change");
        await targetMerge.CommitAsync();
        using var response = await merge;

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await AssertMergeNotKeptAsync(response, colony);
        using var targetNow = await GetJsonAsync($"/api/colonies/{target}");
        targetNow.RootElement.GetProperty("mergedIntoColonyId").GetGuid().Should().Be(beyond);
    }

    [Fact] // regression (race): a colony that another is merged into meanwhile cannot itself be merged away on stale state
    public async Task MergeColony_ShouldAskForARetryAndKeepNothing_WhenAnotherColonyIsMergedIntoItMeanwhile()
    {
        var yard = await CreateYardAsync();
        var incoming = await RegisterAsync(yard);
        var colony = await RegisterAsync(yard);
        var beyond = await RegisterAsync(yard);
        await using var incomingMerge = await server.Database.HoldColonyWriteAsync(
            server.CurrentAccountId,
            store => store.MergeAsync(new ColonyId(incoming), new ColonyId(colony), new DateOnly(2026, 6, 3), CancellationToken.None));
        incomingMerge.Result.Outcome.Should().Be(ColonyChangeOutcome.Recorded);

        var merge = server.Client.PostAsJsonAsync($"/api/colonies/{colony}/merges", new { intoColonyId = beyond, occurredOn = "2026-06-03" });
        (await server.Database.WaitsOnALockAsync(merge)).Should().BeTrue("the server's merge waits on the held merge into its colony");
        await incomingMerge.CommitAsync();
        using var response = await merge;

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await AssertMergeNotKeptAsync(response, colony);
        using var incomingNow = await GetJsonAsync($"/api/colonies/{incoming}");
        incomingNow.RootElement.GetProperty("mergedIntoColonyId").GetGuid().Should().Be(colony);
    }

    [Fact] // invariant (FR-5): PostgreSQL lists queens by introduction day, so the backdated current queen is not first
    public async Task ListQueens_ShouldOrderByIntroductionDay_WhenTheNewQueenIsBackdated()
    {
        var colony = await RegisterAsync(await CreateYardAsync(), queen: Queen("Synthetic Queen Breeder", "2026-05-10"));
        using var requeen = await server.Client.PostAsJsonAsync($"/api/colonies/{colony}/requeens", Queen("Raised in the yard", "2026-03-01"));
        requeen.StatusCode.Should().Be(HttpStatusCode.OK);

        using var queens = await GetJsonAsync($"/api/colonies/{colony}/queens");

        var history = queens.RootElement.GetProperty("items").EnumerateArray().ToList();
        history.Select(q => q.GetProperty("source").GetString()).Should().Equal("Synthetic Queen Breeder", "Raised in the yard");
        history[0].GetProperty("replacedOn").GetString().Should().Be("2026-03-01");
        history[1].GetProperty("replacedOn").ValueKind.Should().Be(JsonValueKind.Null);
        using var read = await GetJsonAsync($"/api/colonies/{colony}");
        read.RootElement.GetProperty("queen").GetProperty("id").GetGuid().Should().Be(history[1].GetProperty("id").GetGuid());
    }

    public static TheoryData<string> StatementsThatRewriteHistory() => new()
    {
        "UPDATE \"ColonyEvents\" SET \"OccurredOn\" = DATE '2020-01-01'",
        "DELETE FROM \"ColonyEvents\"",
        "TRUNCATE \"ColonyEvents\" CASCADE",
    };

    [Theory] // invariant (FR-6, D-2): whatever reaches the database, recorded events cannot be changed or removed
    [MemberData(nameof(StatementsThatRewriteHistory))]
    public async Task ColonyEvents_ShouldRefuseTheStatement_WhenItWouldRewriteHistory(string sql)
    {
        await RegisterAsync(await CreateYardAsync());
        var before = await server.Database.CountAsync("SELECT count(*) FROM \"ColonyEvents\"");

        var act = () => server.Database.ExecuteAsync(sql);

        (await act.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
        (await server.Database.CountAsync("SELECT count(*) FROM \"ColonyEvents\"")).Should().Be(before);
        (await server.Database.CountAsync("SELECT count(*) FROM \"ColonyEvents\" WHERE \"OccurredOn\" = DATE '2020-01-01'")).Should().Be(0);
    }

    [Theory] // invariant (FR-6, D-2): no endpoint changes or deletes an event, and the history is unchanged after trying
    [InlineData("PUT", "/events", HttpStatusCode.MethodNotAllowed)]
    [InlineData("PATCH", "/events", HttpStatusCode.MethodNotAllowed)]
    [InlineData("DELETE", "/events", HttpStatusCode.MethodNotAllowed)]
    [InlineData("PUT", "/events/{event}", HttpStatusCode.NotFound)]
    [InlineData("DELETE", "/events/{event}", HttpStatusCode.NotFound)]
    [InlineData("DELETE", "", HttpStatusCode.MethodNotAllowed)]
    public async Task ChangingAnEvent_ShouldBeRefusedAndLeaveTheHistoryUnchanged_WhenAttemptedOverHttp(string method, string suffix, HttpStatusCode expected)
    {
        var colony = await RegisterAsync(await CreateYardAsync());
        var before = await EventsAsync(colony);
        var path = $"/api/colonies/{colony}{suffix.Replace("{event}", before[0].GetProperty("id").GetGuid().ToString(), StringComparison.Ordinal)}";
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(new { occurredOn = "2020-01-01" }) };

        using var response = await server.Client.SendAsync(request);

        response.StatusCode.Should().Be(expected);
        var after = await EventsAsync(colony);
        after.Select(e => e.GetRawText()).Should().Equal(before.Select(e => e.GetRawText()));
    }

    [Fact] // invariant (adversarial): another account's colony is NotFound, its history too, and never listed
    public async Task GetColony_ShouldReturnNotFound_WhenTheColonyBelongsToAnotherAccount()
    {
        foreach (var path in new[] { "", "/events", "/queens" })
        {
            using var response = await server.Client.GetAsync($"/api/colonies/{server.OtherAccountColonyId}{path}");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await response.Content.ReadAsStringAsync()).Should().NotContain(AppServerFixture.OtherAccountColonyTag);
        }

        (await ListedTagsAsync()).Should().NotContain(AppServerFixture.OtherAccountColonyTag);
    }

    [Fact] // invariant (adversarial): another account's colony cannot be split, moved, requeened or merged into, and stays as it was
    public async Task LineageChange_ShouldNotReachAnotherAccountsColony_WhenAttempted()
    {
        var yard = await CreateYardAsync();
        var ours = await RegisterAsync(yard);
        var theirs = server.OtherAccountColonyId;

        using var split = await server.Client.PostAsJsonAsync($"/api/colonies/{theirs}/splits", Colony(yard, "2026-04-12"));
        using var move = await server.Client.PostAsJsonAsync($"/api/colonies/{theirs}/moves", new { yardId = yard, occurredOn = "2026-04-12" });
        using var requeen = await server.Client.PostAsJsonAsync($"/api/colonies/{theirs}/requeens", Queen("Taken over", "2026-04-12"));
        using var mergeInto = await server.Client.PostAsJsonAsync($"/api/colonies/{ours}/merges", new { intoColonyId = theirs, occurredOn = "2026-04-12" });
        using var placeInTheirYard = await server.Client.PostAsJsonAsync("/api/colonies", Colony(server.OtherAccountYardId.Value, "2026-04-12"));

        split.StatusCode.Should().Be(HttpStatusCode.NotFound);
        move.StatusCode.Should().Be(HttpStatusCode.NotFound);
        requeen.StatusCode.Should().Be(HttpStatusCode.NotFound);
        mergeInto.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        placeInTheirYard.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var theirsNow = await server.Database.FindColonyAsync(server.OtherAccountId, theirs);
        theirsNow!.Colony.Version.Should().Be(1);
        theirsNow.Colony.YardId.Should().Be(server.OtherAccountYardId);
        theirsNow.Queen.Should().BeNull();
    }

    private async Task<Guid> CreateYardAsync()
    {
        using var created = await server.Client.PostAsJsonAsync("/api/yards", new
        {
            name = $"Synthetic Colony Yard {Guid.NewGuid():N}",
            location = new { latitude = 30.2672, longitude = -97.7431 },
            state = "TX",
            county = "Travis",
            landowner = new { contact = "Synthetic Landowner, 555-0100", terms = "free" },
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> RegisterAsync(Guid yard, QueenBody? queen = null)
    {
        using var created = await server.Client.PostAsJsonAsync("/api/colonies", Colony(yard, "2026-04-01", queen));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    // The lost merge answered "retry" and left the colony active, with no merge in its history.
    private async Task AssertMergeNotKeptAsync(HttpResponseMessage response, Guid colony)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("title").GetString().Should().StartWith("Unavailable:").And.Contain("retry");
        using var colonyNow = await GetJsonAsync($"/api/colonies/{colony}");
        colonyNow.RootElement.GetProperty("status").GetString().Should().Be("active");
        (await EventsAsync(colony)).Where(e => e.GetProperty("colonyId").GetGuid() == colony)
            .Select(e => e.GetProperty("type").GetString()).Should().Equal("registered");
    }

    private async Task<List<JsonElement>> EventsAsync(Guid colony)
    {
        using var history = await GetJsonAsync($"/api/colonies/{colony}/events?limit=100");
        return [.. history.RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone())];
    }

    private async Task<List<string?>> ListedTagsAsync()
    {
        var tags = new List<string?>();
        string? cursor = null;
        do
        {
            using var page = await GetJsonAsync(cursor is null ? "/api/colonies?limit=100" : $"/api/colonies?limit=100&cursor={cursor}");
            tags.AddRange(page.RootElement.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("tagId").GetString()));
            cursor = page.RootElement.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        return tags;
    }

    private async Task<JsonDocument> GetJsonAsync(string path)
    {
        using var response = await server.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static ColonyBody Colony(Guid yard, string occurredOn, QueenBody? queen = null) =>
        new($"T-{Guid.NewGuid():N}"[..20], "TX-0412", yard, "Langstroth 10-frame", 2, occurredOn, queen);

    // A colony as the store takes it, for a write held open beside the server's.
    private static ColonyProfile Profile(string tag, Guid yard) => new()
    {
        TagId = tag,
        HiveNumber = "TX-0412",
        YardId = new YardId(yard),
        HiveType = "Langstroth 10-frame",
        BoxCount = 2,
    };

    private static QueenBody Queen(string source, string introducedOn) => new(source, "green", 2024, introducedOn);

    // Serialized camelCase by the web defaults, as a caller writes it.
    private sealed record ColonyBody(string TagId, string HiveNumber, Guid YardId, string HiveType, int BoxCount, string OccurredOn, QueenBody? Queen);

    private sealed record QueenBody(string Source, string? MarkColour, int? MarkYear, string IntroducedOn);
}
