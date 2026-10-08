using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ApiaryCopilot.Domain;

namespace ApiaryCopilot.IntegrationTests;

// Inspections end to end: the real server (source or image) on a real PostgreSQL the fixture migrated and seeded with
// synthetic accounts. Deployment=None: the database is a loopback container.
[Trait("Deployment", "None")]
[Collection(SharedServer.Name)]
public sealed class InspectionEndpointTests(AppServerFixture server)
{
    private const string OtherAccountNote = "Synthetic: another account's inspection note";

    [Fact] // regression risk (UC-1, FR-7): an inspection is recorded, then read back whole by its location and in its colony's list
    public async Task RecordInspection_ShouldBeReadBackWhole_WhenRunEndToEnd()
    {
        var colony = await RegisterColonyAsync();

        using var recorded = await server.Client.PostAsJsonAsync($"/api/colonies/{colony}/inspections", new
        {
            inspectedOn = "2026-04-12",
            findings = new
            {
                queenSeen = true,
                eggsSeen = false,
                broodFrames = 6,
                broodPattern = "Solid",
                foodStores = "adequate",
                temperament = "calm",
                diseaseSigns = Array.Empty<string>(),
                actionsTaken = new List<string> { "fed", "added-box" },
            },
            notes = "  Synthetic: laying well, added a feeder.  ",
        });

        recorded.StatusCode.Should().Be(HttpStatusCode.Created);
        using var body = JsonDocument.Parse(await recorded.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();
        recorded.Headers.Location!.ToString().Should().Be($"/api/inspections/{id}");
        using var read = await GetJsonAsync($"/api/inspections/{id}");
        read.RootElement.GetProperty("colonyId").GetGuid().Should().Be(colony);
        read.RootElement.GetProperty("inspectedOn").GetString().Should().Be("2026-04-12");
        read.RootElement.GetProperty("notes").GetString().Should().Be("Synthetic: laying well, added a feeder.");
        read.RootElement.GetProperty("recordedAt").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        var findings = read.RootElement.GetProperty("findings");
        findings.GetProperty("queenSeen").GetBoolean().Should().BeTrue();
        findings.GetProperty("eggsSeen").GetBoolean().Should().BeFalse();
        findings.GetProperty("broodFrames").GetInt32().Should().Be(6);
        findings.GetProperty("broodPattern").GetString().Should().Be("solid");
        findings.GetProperty("foodStores").GetString().Should().Be("adequate");
        findings.GetProperty("temperament").GetString().Should().Be("calm");
        findings.GetProperty("diseaseSigns").GetArrayLength().Should().Be(0);
        findings.GetProperty("actionsTaken").EnumerateArray().Select(e => e.GetString()).Should().Equal("fed", "added-box");
        using var list = await GetJsonAsync($"/api/colonies/{colony}/inspections");
        list.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(id);
        list.RootElement.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact] // invariant (FR-7): a colony's inspections list newest inspection day first, across pages, whatever order they were recorded in
    public async Task ListInspections_ShouldReturnNewestFirstAcrossPages_WhenRecordedOutOfOrder()
    {
        var colony = await RegisterColonyAsync();
        var other = await RegisterColonyAsync();
        foreach (var day in new[] { "2026-04-05", "2026-04-19", "2026-04-12", "2026-04-26" })
        {
            await RecordAsync(colony, day);
        }

        await RecordAsync(other, "2026-05-01");

        using var first = await GetJsonAsync($"/api/colonies/{colony}/inspections?limit=3");
        var cursor = first.RootElement.GetProperty("nextCursor").GetString();
        cursor.Should().NotBeNullOrEmpty();
        using var second = await GetJsonAsync($"/api/colonies/{colony}/inspections?limit=3&cursor={cursor}");
        var days = first.RootElement.GetProperty("items").EnumerateArray().Concat(second.RootElement.GetProperty("items").EnumerateArray())
            .Select(i => i.GetProperty("inspectedOn").GetString());
        days.Should().Equal("2026-04-26", "2026-04-19", "2026-04-12", "2026-04-05");
        second.RootElement.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact] // boundary: the database holds every field at the largest the API accepts, and reads it back unchanged
    public async Task RecordInspection_ShouldStoreEveryField_WhenEachIsAtItsLimit()
    {
        var colony = await RegisterColonyAsync();
        var every = InspectionFields.All.ToDictionary(f => f.Name, f => f.Kind switch
        {
            InspectionFieldKind.Flag => (object)true,
            InspectionFieldKind.Count => f.Max,
            InspectionFieldKind.Choice => f.Options[^1],
            _ => f.Options.ToList(),
        });
        var notes = new string('ñ', InspectionDetails.NotesMaxLength);

        using var recorded = await server.Client.PostAsJsonAsync(
            $"/api/colonies/{colony}/inspections", new { inspectedOn = "2026-04-12", findings = every, notes });

        recorded.StatusCode.Should().Be(HttpStatusCode.Created);
        using var read = await GetJsonAsync(recorded.Headers.Location!.ToString());
        read.RootElement.GetProperty("notes").GetString().Should().Be(notes);
        var findings = read.RootElement.GetProperty("findings");
        findings.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(InspectionFields.All.Select(f => f.Name));
        findings.GetProperty("broodFrames").GetInt32().Should().Be(100);
        InspectionFields.All.Where(f => f.Kind == InspectionFieldKind.Choices).Should().NotBeEmpty().And.AllSatisfy(f =>
            findings.GetProperty(f.Name).EnumerateArray().Select(e => e.GetString()).Should().Equal(f.Options));
    }

    [Fact] // boundary: an invalid inspection is refused with the field named, and nothing is written
    public async Task RecordInspection_ShouldRefuseAndWriteNothing_WhenAFindingIsInvalid()
    {
        var colony = await RegisterColonyAsync();

        using var refused = await server.Client.PostAsJsonAsync(
            $"/api/colonies/{colony}/inspections",
            new { inspectedOn = "2026-04-12", findings = new { broodFrames = 101, bees = true }, notes = "x" });

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errors").GetProperty("findings.broodFrames")[0].GetString().Should().StartWith("Validation:");
        body.RootElement.GetProperty("errors").GetProperty("findings.bees")[0].GetString().Should().StartWith("Validation:");
        (await CountForAsync(colony)).Should().Be(0);
    }

    [Fact] // regression risk: PostgreSQL's jsonb reorders keys, so a read must return the findings in the catalogue's order
    public async Task GetInspection_ShouldReturnFindingsInTheCataloguesOrder_WhenStoredInJsonb()
    {
        var colony = await RegisterColonyAsync();
        using var recorded = await server.Client.PostAsJsonAsync($"/api/colonies/{colony}/inspections", new
        {
            inspectedOn = "2026-04-12",
            findings = new { actionsTaken = new List<string> { "fed" }, temperament = "calm", broodFrames = 6, eggsSeen = true, queenSeen = true },
        });
        recorded.StatusCode.Should().Be(HttpStatusCode.Created);
        using var body = JsonDocument.Parse(await recorded.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();
        var catalogue = new[] { "queenSeen", "eggsSeen", "broodFrames", "temperament", "actionsTaken" };

        FindingNames(body).Should().Equal(catalogue);
        using var read = await GetJsonAsync($"/api/inspections/{id}");
        FindingNames(read).Should().Equal(catalogue);
        using var list = await GetJsonAsync($"/api/colonies/{colony}/inspections");
        FindingNames(list.RootElement.GetProperty("items")[0]).Should().Equal(catalogue);
    }

    [Fact] // regression risk: a field the catalogue no longer holds is still returned, after the catalogue's fields
    public async Task GetInspection_ShouldReturnAFieldTheCatalogueNoLongerHoldsLast_WhenItWasRecordedEarlier()
    {
        var colony = await RegisterColonyAsync();
        var id = await server.Database.AddInspectionAsync(
            server.CurrentAccountId,
            new ColonyId(colony),
            new InspectionDetails
            {
                InspectedOn = new DateOnly(2026, 4, 12),
                Findings = new InspectionFindings
                {
                    Values = new Dictionary<string, JsonElement>
                    {
                        ["zRetired"] = JsonSerializer.SerializeToElement("old"),
                        ["temperament"] = JsonSerializer.SerializeToElement("calm"),
                        ["queenSeen"] = JsonSerializer.SerializeToElement(true),
                        ["aRetired"] = JsonSerializer.SerializeToElement(2),
                    },
                },
            });

        using var read = await GetJsonAsync($"/api/inspections/{id}");

        FindingNames(read).Should().Equal("queenSeen", "temperament", "aRetired", "zRetired");
        read.RootElement.GetProperty("findings").GetProperty("zRetired").GetString().Should().Be("old");
    }

    [Fact] // boundary: a day later than today (UTC) or before the floor is a 400 keyed inspectedOn and nothing is appended; today is recorded
    public async Task RecordInspection_ShouldRefuseAFutureDayAndADayBeforeTheFloor_WhenRunEndToEnd()
    {
        var colony = await RegisterColonyAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Two days ahead, not one, so a run across UTC midnight cannot turn the refused day into today.
        foreach (var day in new[] { today.AddDays(2), InspectionDetails.EarliestInspectedOn.AddDays(-1), DateOnly.MinValue })
        {
            using var refused = await server.Client.PostAsJsonAsync($"/api/colonies/{colony}/inspections", new { inspectedOn = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), notes = "x" });

            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("errors").GetProperty("inspectedOn")[0].GetString().Should().StartWith("Validation:");
        }

        (await CountForAsync(colony)).Should().Be(0);
        await RecordAsync(colony, InspectionDetails.EarliestInspectedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await RecordAsync(colony, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        (await CountForAsync(colony)).Should().Be(2);
    }

    [Fact] // regression: no colony-status gate; an inspection of a merged colony, the one that found it so, is still recorded
    public async Task RecordInspection_ShouldRecordTheInspection_WhenTheColonyIsMerged()
    {
        var weak = await RegisterColonyAsync();
        var kept = await RegisterColonyAsync();
        using var merge = await server.Client.PostAsJsonAsync($"/api/colonies/{weak}/merges", new { intoColonyId = kept, occurredOn = "2026-04-05" });
        merge.StatusCode.Should().Be(HttpStatusCode.OK);

        await RecordAsync(weak, "2026-04-12");

        (await CountForAsync(weak)).Should().Be(1);
    }

    [Fact] // invariant (adversarial): another account's colony is NotFound to record into or list, and nothing is written to it
    public async Task Inspections_ShouldNotReachAnotherAccountsColony_WhenAttempted()
    {
        var theirs = server.OtherAccountColonyId.Value;
        var before = await CountForAsync(theirs);

        using var record = await server.Client.PostAsJsonAsync($"/api/colonies/{theirs}/inspections", new { inspectedOn = "2026-04-12", notes = "Synthetic: not mine" });
        using var list = await server.Client.GetAsync($"/api/colonies/{theirs}/inspections");

        record.StatusCode.Should().Be(HttpStatusCode.NotFound);
        list.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CountForAsync(theirs)).Should().Be(before);
    }

    [Fact] // invariant (adversarial): another account's inspection is NotFound by its id, and its content never shown
    public async Task GetInspection_ShouldReturnNotFound_WhenTheInspectionBelongsToAnotherAccount()
    {
        var theirs = await server.Database.AddInspectionAsync(
            server.OtherAccountId,
            server.OtherAccountColonyId,
            new InspectionDetails
            {
                InspectedOn = new DateOnly(2026, 4, 12),
                Notes = OtherAccountNote,
                Findings = InspectionFields.Check(new Dictionary<string, JsonElement> { ["queenSeen"] = JsonSerializer.SerializeToElement(true) }).Findings!,
            });

        using var response = await server.Client.GetAsync($"/api/inspections/{theirs}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(OtherAccountNote);
    }

    [Theory] // invariant: no endpoint changes or deletes an inspection, and it is unchanged after trying
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task ChangingAnInspection_ShouldBeRefusedAndLeaveItUnchanged_WhenAttemptedOverHttp(string method)
    {
        var colony = await RegisterColonyAsync();
        var id = await RecordAsync(colony, "2026-04-12");
        var before = await GetRawAsync($"/api/inspections/{id}");
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/inspections/{id}")
        {
            Content = JsonContent.Create(new { notes = "Synthetic: changed" }),
        };

        using var response = await server.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        (await GetRawAsync($"/api/inspections/{id}")).Should().Be(before);
    }

    private async Task<Guid> RegisterColonyAsync()
    {
        using var yardCreated = await server.Client.PostAsJsonAsync("/api/yards", new
        {
            name = $"Synthetic Inspection Yard {Guid.NewGuid():N}",
            location = new { latitude = 30.2672, longitude = -97.7431 },
            state = "TX",
            county = "Travis",
            landowner = new { contact = "Synthetic Landowner, 555-0100", terms = "free" },
        });
        yardCreated.StatusCode.Should().Be(HttpStatusCode.Created);
        using var yard = JsonDocument.Parse(await yardCreated.Content.ReadAsStringAsync());
        using var colonyCreated = await server.Client.PostAsJsonAsync("/api/colonies", new
        {
            tagId = $"I-{Guid.NewGuid():N}"[..20],
            hiveNumber = "TX-0412",
            yardId = yard.RootElement.GetProperty("id").GetGuid(),
            hiveType = "Langstroth 10-frame",
            boxCount = 2,
            occurredOn = "2026-04-01",
        });
        colonyCreated.StatusCode.Should().Be(HttpStatusCode.Created);
        using var colony = JsonDocument.Parse(await colonyCreated.Content.ReadAsStringAsync());
        return colony.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> RecordAsync(Guid colony, string inspectedOn)
    {
        using var recorded = await server.Client.PostAsJsonAsync(
            $"/api/colonies/{colony}/inspections", new { inspectedOn, findings = new { queenSeen = true } });
        recorded.StatusCode.Should().Be(HttpStatusCode.Created);
        using var body = JsonDocument.Parse(await recorded.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private Task<long> CountForAsync(Guid colony) =>
        server.Database.CountAsync($"SELECT count(*) FROM \"Inspections\" WHERE \"ColonyId\" = '{colony}'");

    private async Task<string> GetRawAsync(string path)
    {
        using var response = await server.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    private static string[] FindingNames(JsonDocument inspection) => FindingNames(inspection.RootElement);

    private static string[] FindingNames(JsonElement inspection) =>
        inspection.GetProperty("findings").EnumerateObject().Select(p => p.Name).ToArray();

    private async Task<JsonDocument> GetJsonAsync(string path) => JsonDocument.Parse(await GetRawAsync(path));
}
