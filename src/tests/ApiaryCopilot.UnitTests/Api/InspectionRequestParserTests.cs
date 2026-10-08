using System.Globalization;
using System.Text.Json;
using ApiaryCopilot.Api.Colonies;
using ApiaryCopilot.Api.Inspections;
using ApiaryCopilot.Domain;

namespace ApiaryCopilot.UnitTests.Api;

// The request rules around the field catalogue (whose own checks are InspectionFieldsTests): the day, the notes and
// the rule that an inspection says something.
public sealed class InspectionRequestParserTests
{
    private static readonly DateOnly Today = new(2026, 6, 1);

    private static ParseResult<InspectionDetails> Parse(InspectionRequest request) => InspectionRequestParser.Parse(request, Today);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static InspectionRequest Valid() => new()
    {
        InspectedOn = new DateOnly(2026, 4, 12),
        Findings = new Dictionary<string, JsonElement> { ["queenSeen"] = Json("true"), ["broodFrames"] = Json("6") },
        Notes = "Synthetic: laying well.",
    };

    [Fact] // regression risk: a valid inspection is parsed whole, the findings normalized and the notes trimmed
    public void Parse_ShouldReturnTheInspection_WhenEveryFieldIsValid()
    {
        var parsed = Parse(Valid() with { Notes = "  Synthetic: laying well.  " });

        parsed.Errors.Should().BeEmpty();
        parsed.Value!.InspectedOn.Should().Be(new DateOnly(2026, 4, 12));
        parsed.Value.Notes.Should().Be("Synthetic: laying well.");
        parsed.Value.Findings.Values.Keys.Should().Equal("queenSeen", "broodFrames");
    }

    [Fact] // boundary: findings alone, or notes alone, are an inspection
    public void Parse_ShouldAcceptFindingsWithoutNotesAndNotesWithoutFindings_WhenEitherIsGiven()
    {
        Parse(Valid() with { Notes = null }).Value!.Notes.Should().BeNull();

        var notesOnly = Parse(Valid() with { Findings = null }).Value!;

        notesOnly.Findings.Values.Should().BeEmpty();
        notesOnly.Notes.Should().Be("Synthetic: laying well.");
    }

    [Fact] // boundary: a false flag and an empty list are findings: they record that something was looked for
    public void Parse_ShouldAcceptAnInspectionWhoseOnlyFindingIsAbsence_WhenAFlagIsFalse()
    {
        var parsed = Parse(Valid() with
        {
            Findings = new Dictionary<string, JsonElement> { ["queenSeen"] = Json("false") },
            Notes = null,
        });

        parsed.Errors.Should().BeEmpty();
        parsed.Value!.Findings.Values["queenSeen"].GetBoolean().Should().BeFalse();
    }

    public static TheoryData<InspectionRequest> RequestsThatSayNothing() => new()
    {
        Valid() with { Findings = null, Notes = null },
        Valid() with { Findings = [], Notes = "   " },
        Valid() with { Findings = new Dictionary<string, JsonElement> { ["queenSeen"] = Json("null") }, Notes = null },
    };

    [Theory] // invariant: an inspection that says nothing is refused, however the nothing is spelled
    [MemberData(nameof(RequestsThatSayNothing))]
    public void Parse_ShouldReportFindings_WhenThereAreNeitherFindingsNorNotes(InspectionRequest request)
    {
        var parsed = Parse(request);

        parsed.Value.Should().BeNull();
        parsed.Errors["findings"].Single().Should().StartWith("Validation:");
    }

    [Fact] // boundary: blank notes are no notes
    public void Parse_ShouldStoreNoNotes_WhenTheNotesAreBlank()
    {
        Parse(Valid() with { Notes = "  " }).Value!.Notes.Should().BeNull();
    }

    [Fact] // boundary: notes at the limit are accepted, one more is refused
    public void Parse_ShouldRefuseNotes_WhenTheyExceedTheLimit()
    {
        Parse(Valid() with { Notes = new string('n', InspectionDetails.NotesMaxLength) }).Errors.Should().BeEmpty();

        var parsed = Parse(Valid() with { Notes = new string('n', InspectionDetails.NotesMaxLength + 1) });

        parsed.Errors["notes"].Single().Should().StartWith("Validation:");
    }

    [Fact] // boundary: the day is required
    public void Parse_ShouldReportTheDay_WhenItIsMissing()
    {
        var parsed = Parse(Valid() with { InspectedOn = null });

        parsed.Value.Should().BeNull();
        parsed.Errors["inspectedOn"].Single().Should().StartWith("Validation:");
    }

    [Fact] // invariant: a bad field in the findings is reported under its JSON path, with the other problems
    public void Parse_ShouldReportEveryProblem_WhenTheDayAndAFindingAreInvalid()
    {
        var parsed = Parse(Valid() with
        {
            InspectedOn = null,
            Findings = new Dictionary<string, JsonElement> { ["broodFrames"] = Json("-1") },
        });

        parsed.Value.Should().BeNull();
        parsed.Errors.Keys.Should().BeEquivalentTo(["inspectedOn", "findings.broodFrames"]);
    }

    [Fact] // boundary: today is accepted and tomorrow is refused, keyed by the request's field name
    public void Parse_ShouldRefuseTheDay_WhenItIsLaterThanToday()
    {
        Parse(Valid() with { InspectedOn = Today }).Errors.Should().BeEmpty();
        Parse(Valid() with { InspectedOn = Today.AddDays(-1) }).Errors.Should().BeEmpty();

        var parsed = Parse(Valid() with { InspectedOn = Today.AddDays(1) });

        parsed.Value.Should().BeNull();
        parsed.Errors["inspectedOn"].Single().Should().StartWith("Validation:").And.Contain("future");
    }

    [Fact] // boundary: the floor is accepted and the day before it is refused, so 0001-01-01 is refused
    public void Parse_ShouldRefuseTheDay_WhenItIsBeforeTheFloor()
    {
        Parse(Valid() with { InspectedOn = InspectionDetails.EarliestInspectedOn }).Errors.Should().BeEmpty();

        var dayBefore = Parse(Valid() with { InspectedOn = InspectionDetails.EarliestInspectedOn.AddDays(-1) });
        var minimum = Parse(Valid() with { InspectedOn = DateOnly.MinValue });

        dayBefore.Value.Should().BeNull();
        dayBefore.Errors["inspectedOn"].Single().Should().StartWith("Validation:").And.Contain(InspectionDetails.EarliestInspectedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        minimum.Errors.Keys.Should().Contain("inspectedOn");
    }

    [Fact] // invariant: the floor is 2000-01-01, the year a queen's mark year starts too
    public void EarliestInspectedOn_ShouldBeTheStartOfTheYear2000_WhenRead() =>
        InspectionDetails.EarliestInspectedOn.Should().Be(new DateOnly(2000, 1, 1));

    [Fact] // invariant: a refused day is reported with the other problems, not instead of them
    public void Parse_ShouldReportEveryProblem_WhenTheDayIsInTheFutureAndAFindingIsInvalid()
    {
        var parsed = Parse(Valid() with
        {
            InspectedOn = Today.AddDays(1),
            Findings = new Dictionary<string, JsonElement> { ["broodFrames"] = Json("-1") },
        });

        parsed.Errors.Keys.Should().BeEquivalentTo(["inspectedOn", "findings.broodFrames"]);
    }

    [Fact] // boundary: a null request is a programming error, not a validation failure
    public void Parse_ShouldThrow_WhenTheRequestIsNull()
    {
        var act = () => InspectionRequestParser.Parse(null!, Today);

        act.Should().Throw<ArgumentNullException>();
    }
}
