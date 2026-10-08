using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;

namespace ApiaryCopilot.UnitTests.Infrastructure;

// The stored form of a lineage event's data: recorded once and read for ever, so it must read back unchanged and
// its spelling must not drift (ARCHITECTURE.md D-2).
public sealed class ColonyEventJsonTests
{
    private static readonly ColonyId Colony = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000c2"));
    private static readonly YardId Yard = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000a1"));
    private static readonly QueenId FirstQueen = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000e1"));

    public static TheoryData<ColonyEventData> EveryKindOfEvent() => new(Events());

    private static ColonyEventData[] Events() =>
    [
        new ColonyRegistered { Colony = Profile(), Queen = NewQueen() },
        new ColonyRegistered { Colony = Profile() },
        new SwarmCaptured { Colony = Profile(), Queen = NewQueen() with { Details = NewQueen().Details with { MarkColour = null, MarkYear = null } } },
        new ColonySplit { NewColonyId = Colony, NewColony = Profile(), Queen = NewQueen() },
        new ColonyMerged { IntoColonyId = Colony },
        new ColonyRequeened { Queen = NewQueen(), ReplacedQueenId = new QueenId(Guid.Parse("0199b2a4-0000-7000-8000-0000000000e0")) },
        new ColonyRequeened { Queen = NewQueen() },
        new ColonyMoved { FromYardId = Yard, ToYardId = new YardId(Guid.Parse("0199b2a4-0000-7000-8000-0000000000a2")) },
    ];

    [Theory] // regression risk: every kind of event reads back exactly as it was recorded
    [MemberData(nameof(EveryKindOfEvent))]
    public void Deserialize_ShouldReturnTheRecordedData_WhenGivenWhatSerializeWrote(ColonyEventData data)
    {
        var json = ColonyEventJson.Serialize(data);

        ColonyEventJson.Deserialize(json).Should().Be(data);
    }

    [Fact] // invariant: the stored spelling is a contract with every event already recorded
    public void Serialize_ShouldWriteTheStoredSpelling_WhenGivenARequeen()
    {
        var json = ColonyEventJson.Serialize(new ColonyRequeened { Queen = NewQueen(), ReplacedQueenId = null });

        json.Should().Be(
            "{\"type\":\"requeen\",\"queen\":{\"queenId\":\"0199b2a4-0000-7000-8000-0000000000e1\"," +
            "\"details\":{\"source\":\"Synthetic Queen Breeder\",\"markColour\":\"Green\",\"markYear\":2024,\"introducedOn\":\"2026-05-03\"}}," +
            "\"replacedQueenId\":null}");
    }

    [Theory] // invariant: each kind of event is stored under its own, fixed type name
    [InlineData(typeof(ColonyRegistered), "registered")]
    [InlineData(typeof(SwarmCaptured), "swarm-capture")]
    [InlineData(typeof(ColonySplit), "split")]
    [InlineData(typeof(ColonyMerged), "merge")]
    [InlineData(typeof(ColonyMoved), "move")]
    public void Serialize_ShouldNameTheKindOfEvent_WhenGivenEachKind(Type kind, string name)
    {
        var data = Events().First(d => d.GetType() == kind);

        ColonyEventJson.Serialize(data).Should().StartWith($"{{\"type\":\"{name}\",");
    }

    [Fact] // boundary: stored data of a kind this build does not know is refused, never read as something else
    public void Deserialize_ShouldThrow_WhenTheTypeIsUnknown()
    {
        var act = () => ColonyEventJson.Deserialize("{\"type\":\"teleport\",\"intoColonyId\":\"0199b2a4-0000-7000-8000-0000000000c2\"}");

        act.Should().Throw<Exception>();
    }

    private static ColonyProfile Profile() => new()
    {
        TagId = "A-02",
        HiveNumber = "TX-0412",
        YardId = Yard,
        HiveType = "Langstroth 10-frame",
        BoxCount = 2,
    };

    private static NewQueen NewQueen() => new()
    {
        QueenId = FirstQueen,
        Details = new QueenDetails
        {
            Source = "Synthetic Queen Breeder",
            MarkColour = QueenMarkColour.Green,
            MarkYear = 2024,
            IntroducedOn = new DateOnly(2026, 5, 3),
        },
    };
}
