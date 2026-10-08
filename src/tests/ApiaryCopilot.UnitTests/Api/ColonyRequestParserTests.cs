using ApiaryCopilot.Api.Colonies;
using ApiaryCopilot.Domain;

namespace ApiaryCopilot.UnitTests.Api;

public sealed class ColonyRequestParserTests
{
    private static readonly Guid Yard = Guid.Parse("0199b2a4-0000-7000-8000-0000000000a1");

    [Fact] // regression risk: a valid colony is parsed whole, text trimmed and the mark colour read case-insensitively
    public void ParseColony_ShouldReturnTheColony_WhenEveryFieldIsValid()
    {
        var parsed = ColonyRequestParser.ParseColony(Valid() with { TagId = "  A-01 ", Queen = ValidQueen() with { MarkColour = "GREEN" } });

        parsed.Errors.Should().BeEmpty();
        parsed.Value.Should().Be(new NewColony(
            new ColonyProfile { TagId = "A-01", HiveNumber = "TX-0412", YardId = new YardId(Yard), HiveType = "Langstroth 10-frame", BoxCount = 2 },
            new QueenDetails { Source = "Synthetic Queen Breeder", MarkColour = QueenMarkColour.Green, MarkYear = 2024, IntroducedOn = new DateOnly(2026, 5, 3) },
            new DateOnly(2026, 4, 12)));
    }

    [Fact] // boundary: a colony may be recorded with no queen, and a queen with no mark
    public void ParseColony_ShouldAcceptNoQueenAndAnUnmarkedQueen_WhenOptionalFieldsAreLeftOut()
    {
        ColonyRequestParser.ParseColony(Valid() with { Queen = null }).Value!.Queen.Should().BeNull();

        var unmarked = ColonyRequestParser.ParseColony(Valid() with { Queen = ValidQueen() with { MarkColour = " ", MarkYear = null } }).Value!.Queen!;

        unmarked.MarkColour.Should().BeNull();
        unmarked.MarkYear.Should().BeNull();
    }

    public static TheoryData<ColonyRequest, string> InvalidColonies() => new()
    {
        { Valid() with { TagId = null }, "tagId" },
        { Valid() with { TagId = "   " }, "tagId" },
        { Valid() with { TagId = new string('t', 65) }, "tagId" },
        { Valid() with { HiveNumber = null }, "hiveNumber" },
        { Valid() with { HiveNumber = new string('h', 65) }, "hiveNumber" },
        { Valid() with { YardId = null }, "yardId" },
        { Valid() with { YardId = Guid.Empty }, "yardId" },
        { Valid() with { HiveType = "" }, "hiveType" },
        { Valid() with { HiveType = new string('h', 101) }, "hiveType" },
        { Valid() with { BoxCount = null }, "boxCount" },
        { Valid() with { BoxCount = 0 }, "boxCount" },
        { Valid() with { BoxCount = 31 }, "boxCount" },
        { Valid() with { OccurredOn = null }, "occurredOn" },
        { Valid() with { Queen = ValidQueen() with { Source = null } }, "queen.source" },
        { Valid() with { Queen = ValidQueen() with { Source = new string('s', 201) } }, "queen.source" },
        { Valid() with { Queen = ValidQueen() with { MarkColour = "purple" } }, "queen.markColour" },
        { Valid() with { Queen = ValidQueen() with { MarkColour = "1" } }, "queen.markColour" },
        { Valid() with { Queen = ValidQueen() with { MarkYear = 1999 } }, "queen.markYear" },
        { Valid() with { Queen = ValidQueen() with { MarkYear = 2101 } }, "queen.markYear" },
        { Valid() with { Queen = ValidQueen() with { IntroducedOn = null } }, "queen.introducedOn" },
    };

    [Theory] // boundary: each invalid field is reported under its JSON path, and nothing is parsed
    [MemberData(nameof(InvalidColonies))]
    public void ParseColony_ShouldReportTheField_WhenItIsInvalid(ColonyRequest request, string field)
    {
        var parsed = ColonyRequestParser.ParseColony(request);

        parsed.Value.Should().BeNull();
        parsed.Errors.Keys.Should().Equal(field);
        parsed.Errors[field].Should().ContainSingle().Which.Should().StartWith("Validation:");
    }

    [Theory] // boundary: the longest text and the box-count and mark-year limits are accepted
    [InlineData(1, 2000)]
    [InlineData(30, 2100)]
    public void ParseColony_ShouldAcceptTheLimits_WhenFieldsAreAtTheirBounds(int boxCount, int markYear)
    {
        var request = Valid() with
        {
            TagId = new string('t', 64),
            HiveNumber = new string('h', 64),
            HiveType = new string('h', 100),
            BoxCount = boxCount,
            Queen = ValidQueen() with { Source = new string('s', 200), MarkYear = markYear },
        };

        ColonyRequestParser.ParseColony(request).Errors.Should().BeEmpty();
    }

    [Fact] // invariant: every invalid field is reported at once, not just the first
    public void ParseColony_ShouldReportEveryInvalidField_WhenSeveralAreInvalid()
    {
        var parsed = ColonyRequestParser.ParseColony(new ColonyRequest());

        parsed.Errors.Keys.Should().BeEquivalentTo("tagId", "hiveNumber", "yardId", "hiveType", "boxCount", "occurredOn");
    }

    [Fact] // regression risk: a requeen body is a queen at the root, so its errors are keyed without a prefix
    public void ParseQueen_ShouldReportFieldsAtTheRoot_WhenTheQueenIsInvalid()
    {
        var parsed = ColonyRequestParser.ParseQueen(new QueenRequest());

        parsed.Value.Should().BeNull();
        parsed.Errors.Keys.Should().BeEquivalentTo("source", "introducedOn");
    }

    [Fact] // regression risk: a valid requeen body is parsed whole
    public void ParseQueen_ShouldReturnTheQueen_WhenValid()
    {
        ColonyRequestParser.ParseQueen(ValidQueen()).Value!.IntroducedOn.Should().Be(new DateOnly(2026, 5, 3));
    }

    [Fact] // regression risk: a merge names the colony merged into and the day
    public void ParseMerge_ShouldReturnTheMerge_WhenValid()
    {
        var into = Guid.Parse("0199b2a4-0000-7000-8000-0000000000c2");

        var parsed = ColonyRequestParser.ParseMerge(new MergeRequest { IntoColonyId = into, OccurredOn = new DateOnly(2026, 4, 12) });

        parsed.Value.Should().Be(new MergeColony(new ColonyId(into), new DateOnly(2026, 4, 12)));
    }

    [Theory] // boundary: a merge needs a non-empty colony id and a day
    [InlineData(false, true, "intoColonyId")]
    [InlineData(true, false, "occurredOn")]
    public void ParseMerge_ShouldReportTheField_WhenItIsMissing(bool hasColony, bool hasDay, string field)
    {
        var parsed = ColonyRequestParser.ParseMerge(new MergeRequest
        {
            IntoColonyId = hasColony ? Guid.Parse("0199b2a4-0000-7000-8000-0000000000c2") : Guid.Empty,
            OccurredOn = hasDay ? new DateOnly(2026, 4, 12) : null,
        });

        parsed.Errors.Keys.Should().Equal(field);
    }

    [Fact] // regression risk: a move names the destination yard and the day
    public void ParseMove_ShouldReturnTheMove_WhenValid()
    {
        var parsed = ColonyRequestParser.ParseMove(new MoveRequest { YardId = Yard, OccurredOn = new DateOnly(2026, 4, 12) });

        parsed.Value.Should().Be(new MoveColony(new YardId(Yard), new DateOnly(2026, 4, 12)));
    }

    [Fact] // boundary: a move needs a yard and a day
    public void ParseMove_ShouldReportEachField_WhenTheyAreMissing()
    {
        ColonyRequestParser.ParseMove(new MoveRequest()).Errors.Keys.Should().BeEquivalentTo("yardId", "occurredOn");
    }

    [Theory] // invariant: every status, origin and mark colour has its wire spelling
    [InlineData(ColonyStatus.Active, "active")]
    [InlineData(ColonyStatus.DeadOut, "dead-out")]
    [InlineData(ColonyStatus.Merged, "merged")]
    [InlineData(ColonyStatus.Sold, "sold")]
    public void StatusCode_ShouldSpellTheStatus_WhenGivenEach(ColonyStatus status, string code)
    {
        ColonyRequestParser.StatusCode(status).Should().Be(code);
    }

    [Theory] // invariant: every origin has its wire spelling
    [InlineData(ColonyOrigin.Registered, "registered")]
    [InlineData(ColonyOrigin.SwarmCapture, "swarm-capture")]
    [InlineData(ColonyOrigin.Split, "split")]
    public void OriginCode_ShouldSpellTheOrigin_WhenGivenEach(ColonyOrigin origin, string code)
    {
        ColonyRequestParser.OriginCode(origin).Should().Be(code);
    }

    [Theory] // invariant: every mark colour has its wire spelling, and each parses back
    [InlineData(QueenMarkColour.White, "white")]
    [InlineData(QueenMarkColour.Yellow, "yellow")]
    [InlineData(QueenMarkColour.Red, "red")]
    [InlineData(QueenMarkColour.Green, "green")]
    [InlineData(QueenMarkColour.Blue, "blue")]
    public void MarkColourCode_ShouldSpellTheColour_WhenGivenEach(QueenMarkColour colour, string code)
    {
        ColonyRequestParser.MarkColourCode(colour).Should().Be(code);
        ColonyRequestParser.ParseQueen(ValidQueen() with { MarkColour = code }).Value!.MarkColour.Should().Be(colour);
    }

    private static ColonyRequest Valid() => new()
    {
        TagId = "A-01",
        HiveNumber = "TX-0412",
        YardId = Yard,
        HiveType = "Langstroth 10-frame",
        BoxCount = 2,
        OccurredOn = new DateOnly(2026, 4, 12),
        Queen = ValidQueen(),
    };

    private static QueenRequest ValidQueen() => new()
    {
        Source = "Synthetic Queen Breeder",
        MarkColour = "green",
        MarkYear = 2024,
        IntroducedOn = new DateOnly(2026, 5, 3),
    };
}
