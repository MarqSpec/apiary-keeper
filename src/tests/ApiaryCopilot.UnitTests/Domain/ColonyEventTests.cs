using ApiaryCopilot.Domain;

namespace ApiaryCopilot.UnitTests.Domain;

public sealed class ColonyEventTests
{
    private static readonly ColonyId Subject = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000c1"));
    private static readonly ColonyId Other = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000c2"));
    private static readonly YardId Yard = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000a1"));

    public static TheoryData<ColonyEventData, Guid?> EventsAndTheColonyTheyRelateTo() => new()
    {
        { new ColonySplit { NewColonyId = Other, NewColony = Profile() }, Other.Value },
        { new ColonyMerged { IntoColonyId = Other }, Other.Value },
        { new ColonyRegistered { Colony = Profile() }, null },
        { new SwarmCaptured { Colony = Profile() }, null },
        { new ColonyMoved { FromYardId = Yard, ToYardId = new YardId(Guid.Parse("0199b2a4-0000-7000-8000-0000000000a2")) }, null },
    };

    [Theory] // invariant: the second colony a split or merge involves is recorded, so its history finds the event too
    [MemberData(nameof(EventsAndTheColonyTheyRelateTo))]
    public void Constructor_ShouldRecordTheRelatedColony_WhenTheEventInvolvesOne(ColonyEventData data, Guid? related)
    {
        var colonyEvent = new ColonyEvent(
            ColonyEventId.New(),
            new AccountId(Guid.Parse("11111111-1111-4111-8111-111111111111")),
            Subject,
            new DateOnly(2026, 4, 12),
            DateTimeOffset.UnixEpoch,
            data);

        colonyEvent.RelatedColonyId.Should().Be(related is { } value ? new ColonyId(value) : null);
        colonyEvent.Data.Should().BeSameAs(data);
    }

    private static ColonyProfile Profile() => new()
    {
        TagId = "A-01",
        HiveNumber = "TX-0412",
        YardId = Yard,
        HiveType = "Langstroth 10-frame",
        BoxCount = 2,
    };
}
