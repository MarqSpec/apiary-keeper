using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ApiaryCopilot.Domain;

namespace ApiaryCopilot.IntegrationTests;

// The yard endpoints end to end: the real server (source or image) on a real PostgreSQL the fixture migrated and
// seeded with synthetic accounts. Deployment=None: the database is a loopback container.
[Trait("Deployment", "None")]
[Collection(SharedServer.Name)]
public sealed class YardEndpointTests(AppServerFixture server)
{
    [Fact] // regression risk: a yard and its landowner survive the round trip through the server and PostgreSQL (FR-1, FR-2)
    public async Task CreateYard_ShouldBeReadBackUnchanged_WhenCreatedThroughTheServer()
    {
        var name = $"Mesquite Flat {Guid.NewGuid():N}";

        using var created = await server.Client.PostAsJsonAsync("/api/yards", Yard(name));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var read = await server.Client.GetAsync(created.Headers.Location);

        read.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        var yard = body.RootElement;
        yard.GetProperty("name").GetString().Should().Be(name);
        yard.GetProperty("location").GetProperty("latitude").GetDouble().Should().Be(30.2672);
        yard.GetProperty("location").GetProperty("longitude").GetDouble().Should().Be(-97.7431);
        yard.GetProperty("state").GetString().Should().Be("TX");
        yard.GetProperty("county").GetString().Should().Be("Travis");
        yard.GetProperty("accessNotes").GetString().Should().Be("North pasture gate.");
        yard.GetProperty("forageNotes").GetString().Should().Be("Huisache in February.");
        var landowner = yard.GetProperty("landowner");
        landowner.GetProperty("contact").GetString().Should().Be("Synthetic Landowner, 555-0100");
        landowner.GetProperty("terms").GetString().Should().Be("fee");
        landowner.GetProperty("nearbyCrops").GetString().Should().Be("Cotton to the east.");
        landowner.GetProperty("sprayContact").GetString().Should().Be("Synthetic Crop Duster, 555-0101");
    }

    [Fact] // boundary: the database columns hold every field at the longest the API accepts
    public async Task CreateYard_ShouldStoreEveryField_WhenEachIsAtItsMaximumLength()
    {
        var request = new
        {
            name = new string('ñ', 200),
            location = new { latitude = -90.0, longitude = 180.0 },
            state = "TX",
            county = new string('c', 100),
            accessNotes = new string('a', 2000),
            forageNotes = new string('f', 2000),
            landowner = new { contact = new string('l', 500), terms = "free", nearbyCrops = new string('n', 1000), sprayContact = new string('s', 500) },
        };

        using var created = await server.Client.PostAsJsonAsync("/api/yards", request);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var read = await server.Client.GetAsync(created.Headers.Location);
        using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("name").GetString().Should().Be(request.name);
        body.RootElement.GetProperty("accessNotes").GetString().Should().Be(request.accessNotes);
    }

    [Fact] // regression risk: an update replaces the stored yard, and a list shows it by name
    public async Task UpdateYard_ShouldReplaceTheStoredYard_WhenTheYardBelongsToTheAccount()
    {
        using var created = await server.Client.PostAsJsonAsync("/api/yards", Yard($"Before {Guid.NewGuid():N}"));
        var renamed = $"After {Guid.NewGuid():N}";

        using var updated = await server.Client.PutAsJsonAsync(created.Headers.Location, Yard(renamed, terms: "honey"));

        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        using var read = await server.Client.GetAsync(created.Headers.Location);
        using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("name").GetString().Should().Be(renamed);
        body.RootElement.GetProperty("landowner").GetProperty("terms").GetString().Should().Be("honey");
        (await ListedNamesAsync()).Should().Contain(renamed);
    }

    [Fact] // invariant: PostgreSQL holds the acting user's stamps; an update moves the last-modified ones only
    public async Task UpdateYard_ShouldMoveOnlyTheLastModifiedStamps_WhenTheYardIsChangedThroughTheServer()
    {
        using var created = await server.Client.PostAsJsonAsync("/api/yards", Yard($"Stamped {Guid.NewGuid():N}"));
        var yardId = new YardId(Guid.Parse(created.Headers.Location!.OriginalString.Split('/')[^1]));
        var actingUser = TestDatabase.ActingUserOf(server.CurrentAccountId);
        var asCreated = await server.Database.FindYardAsync(server.CurrentAccountId, yardId);

        using var updated = await server.Client.PutAsJsonAsync(created.Headers.Location, Yard($"Restamped {Guid.NewGuid():N}"));

        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        var asUpdated = await server.Database.FindYardAsync(server.CurrentAccountId, yardId);
        asCreated!.CreatedBy.Should().Be(actingUser);
        asCreated.LastModifiedAt.Should().Be(asCreated.CreatedAt);
        asUpdated!.CreatedBy.Should().Be(actingUser);
        asUpdated.CreatedAt.Should().Be(asCreated.CreatedAt);
        asUpdated.LastModifiedBy.Should().Be(actingUser);
        asUpdated.LastModifiedAt.Should().BeAfter(asCreated.LastModifiedAt);
    }

    [Fact] // invariant (adversarial): another account's yard is NotFound, never leaked
    public async Task GetYard_ShouldReturnNotFound_WhenTheYardBelongsToAnotherAccount()
    {
        using var response = await server.Client.GetAsync($"/api/yards/{server.OtherAccountYardId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(AppServerFixture.OtherAccountYardName);
    }

    [Fact] // invariant (adversarial): another account's yard is never listed
    public async Task ListYards_ShouldOmitAnotherAccountsYards_WhenTheyExist()
    {
        (await ListedNamesAsync()).Should().NotContain(AppServerFixture.OtherAccountYardName);
    }

    [Fact] // invariant (adversarial): another account's yard cannot be overwritten
    public async Task UpdateYard_ShouldReturnNotFoundAndChangeNothing_WhenTheYardBelongsToAnotherAccount()
    {
        using var response = await server.Client.PutAsJsonAsync($"/api/yards/{server.OtherAccountYardId}", Yard("Taken Over"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await server.ReadOtherAccountYardNameAsync()).Should().Be(AppServerFixture.OtherAccountYardName);
    }

    [Fact] // boundary: an invalid yard is a categorized validation problem from the real pipeline
    public async Task CreateYard_ShouldReturnValidationProblem_WhenTheStateIsNotTexas()
    {
        using var response = await server.Client.PostAsJsonAsync("/api/yards", Yard("Out of State") with { State = "NM" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errors").GetProperty("state")[0].GetString().Should().StartWith("Validation:");
    }

    private async Task<List<string?>> ListedNamesAsync()
    {
        var names = new List<string?>();
        string? cursor = null;
        do
        {
            using var response = await server.Client.GetAsync(cursor is null ? "/api/yards?limit=100" : $"/api/yards?limit=100&cursor={cursor}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            names.AddRange(body.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()));
            cursor = body.RootElement.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        return names;
    }

    private static YardBody Yard(string name, string terms = "fee") => new(
        name,
        new { latitude = 30.2672, longitude = -97.7431 },
        "TX",
        "Travis",
        "North pasture gate.",
        "Huisache in February.",
        new { contact = "Synthetic Landowner, 555-0100", terms, nearbyCrops = "Cotton to the east.", sprayContact = "Synthetic Crop Duster, 555-0101" });

    // Serialized camelCase by the web defaults, as a caller writes it.
    private sealed record YardBody(string Name, object Location, string State, string County, string AccessNotes, string ForageNotes, object Landowner);
}
