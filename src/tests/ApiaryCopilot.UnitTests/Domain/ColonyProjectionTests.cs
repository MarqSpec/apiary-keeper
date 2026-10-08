using ApiaryCopilot.Domain;

namespace ApiaryCopilot.UnitTests.Domain;

// Each lineage event's projection into a colony's current state (FR-4, FR-5, FR-6, ARCHITECTURE.md D-2).
public sealed class ColonyProjectionTests
{
    private static readonly AccountId Account = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly YardId HomeYard = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000a1"));
    private static readonly YardId OtherYard = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000a2"));
    private static readonly ColonyId ParentId = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000c1"));
    private static readonly ColonyId ChildId = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000c2"));
    private static readonly QueenId FirstQueenId = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000e1"));
    private static readonly QueenId SecondQueenId = new(Guid.Parse("0199b2a4-0000-7000-8000-0000000000e2"));
    private static readonly DateOnly SplitDay = new(2026, 4, 12);

    [Fact] // regression risk: a registered colony is active, carries every recorded field and heads with its queen
    public void Apply_ShouldCreateAnActiveColonyWithItsQueen_WhenAColonyIsRegistered()
    {
        var result = ColonyProjection.Apply(Event(ParentId, new ColonyRegistered { Colony = Profile("A-01"), Queen = Queen(FirstQueenId) }), null);

        var colony = result.Colony.Colony;
        colony.ColonyId.Should().Be(ParentId);
        colony.AccountId.Should().Be(Account);
        colony.TagId.Should().Be("A-01");
        colony.HiveNumber.Should().Be("TX-0412");
        colony.YardId.Should().Be(HomeYard);
        colony.HiveType.Should().Be("Langstroth 10-frame");
        colony.BoxCount.Should().Be(2);
        colony.Status.Should().Be(ColonyStatus.Active);
        colony.Origin.Should().Be(ColonyOrigin.Registered);
        colony.ParentColonyId.Should().BeNull();
        colony.Version.Should().Be(1);
        result.AddedColony.Should().BeSameAs(colony);
        result.AddedQueen.Should().BeSameAs(result.Colony.Queen);
        result.Colony.Queen!.QueenId.Should().Be(FirstQueenId);
        result.Colony.Queen.ColonyId.Should().Be(ParentId);
        result.Colony.Queen.AccountId.Should().Be(Account);
        result.Colony.Queen.Details.Should().Be(Queen(FirstQueenId).Details);
        result.Colony.Queen.ReplacedOn.Should().BeNull();
    }

    [Fact] // boundary: a colony may be registered before anyone knows its queen
    public void Apply_ShouldCreateAColonyWithNoQueen_WhenRegisteredWithoutOne()
    {
        var result = ColonyProjection.Apply(Event(ParentId, new ColonyRegistered { Colony = Profile("A-01") }), null);

        result.Colony.Queen.Should().BeNull();
        result.AddedQueen.Should().BeNull();
    }

    [Fact] // regression risk: a captured swarm is a new active colony whose origin says so
    public void Apply_ShouldCreateASwarmCaptureColony_WhenASwarmIsCaptured()
    {
        var result = ColonyProjection.Apply(Event(ParentId, new SwarmCaptured { Colony = Profile("S-07") }), null);

        result.Colony.Colony.Origin.Should().Be(ColonyOrigin.SwarmCapture);
        result.Colony.Colony.Status.Should().Be(ColonyStatus.Active);
        result.Colony.Colony.TagId.Should().Be("S-07");
        result.AddedColony.Should().BeSameAs(result.Colony.Colony);
    }

    [Fact] // invariant: a split creates the child from its parent and leaves the parent's recorded state alone
    public void Apply_ShouldCreateTheChildFromTheParent_WhenAColonyIsSplit()
    {
        var parent = Existing();
        var split = new ColonySplit { NewColonyId = ChildId, NewColony = Profile("A-02") with { YardId = OtherYard, BoxCount = 1 }, Queen = Queen(SecondQueenId) };

        var result = ColonyProjection.Apply(Event(ParentId, split), parent);

        var child = result.Colony.Colony;
        child.ColonyId.Should().Be(ChildId);
        child.Origin.Should().Be(ColonyOrigin.Split);
        child.ParentColonyId.Should().Be(ParentId);
        child.YardId.Should().Be(OtherYard);
        child.BoxCount.Should().Be(1);
        child.Status.Should().Be(ColonyStatus.Active);
        child.Version.Should().Be(1);
        result.AddedColony.Should().BeSameAs(child);
        result.AddedQueen!.ColonyId.Should().Be(ChildId);
        parent.Colony.YardId.Should().Be(HomeYard);
        parent.Colony.Status.Should().Be(ColonyStatus.Active);
        parent.Queen!.ReplacedOn.Should().BeNull();
        parent.Colony.Version.Should().Be(2);
    }

    [Fact] // invariant: a merged colony stops being active and records the colony it joined
    public void Apply_ShouldMarkTheColonyMergedIntoTheOther_WhenMerged()
    {
        var colony = Existing();

        var result = ColonyProjection.Apply(Event(ParentId, new ColonyMerged { IntoColonyId = ChildId }), colony, Target());

        result.Colony.Should().BeSameAs(colony);
        colony.Colony.Status.Should().Be(ColonyStatus.Merged);
        colony.Colony.MergedIntoColonyId.Should().Be(ChildId);
        colony.Colony.Version.Should().Be(2);
        result.AddedColony.Should().BeNull();
        result.AddedQueen.Should().BeNull();
    }

    [Fact] // invariant: a merge is an event in the target's history too, so it bumps the target's version and nothing else
    public void Apply_ShouldBumpTheTargetsVersion_WhenAColonyIsMergedIntoIt()
    {
        var target = Target();

        ColonyProjection.Apply(Event(ParentId, new ColonyMerged { IntoColonyId = ChildId }), Existing(), target);

        target.Version.Should().Be(2);
        target.Status.Should().Be(ColonyStatus.Active);
        target.MergedIntoColonyId.Should().BeNull();
    }

    [Theory] // boundary: a merge needs the colony it goes into, and only that one
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_ShouldThrow_WhenAMergeIsNotGivenTheColonyItGoesInto(bool giveAnotherColony)
    {
        var act = () => ColonyProjection.Apply(
            Event(ParentId, new ColonyMerged { IntoColonyId = ChildId }), Existing(), giveAnotherColony ? Existing().Colony : null);

        act.Should().Throw<ArgumentException>();
    }

    [Fact] // invariant: requeening ends the current queen on the introduction day and heads the colony with the new one (FR-5)
    public void Apply_ShouldReplaceTheCurrentQueen_WhenRequeened()
    {
        var colony = Existing();
        var oldQueen = colony.Queen!;
        var requeen = new ColonyRequeened { Queen = Queen(SecondQueenId), ReplacedQueenId = FirstQueenId };

        var result = ColonyProjection.Apply(Event(ParentId, requeen), colony);

        oldQueen.ReplacedOn.Should().Be(Queen(SecondQueenId).Details.IntroducedOn);
        oldQueen.ReplacedByQueenId.Should().Be(SecondQueenId);
        result.Colony.Queen.Should().BeSameAs(result.AddedQueen);
        result.Colony.Queen!.QueenId.Should().Be(SecondQueenId);
        result.Colony.Queen.ColonyId.Should().Be(ParentId);
        result.Colony.Queen.ReplacedOn.Should().BeNull();
        result.Colony.Colony.Version.Should().Be(2);
    }

    [Fact] // boundary: a colony with no known queen is requeened without replacing anyone
    public void Apply_ShouldIntroduceTheQueen_WhenRequeenedWithNoCurrentQueen()
    {
        var colony = Existing() with { Queen = null };

        var result = ColonyProjection.Apply(Event(ParentId, new ColonyRequeened { Queen = Queen(SecondQueenId) }), colony);

        result.Colony.Queen!.QueenId.Should().Be(SecondQueenId);
    }

    [Theory] // invariant: the queen a requeen replaces must be the colony's current one, or history would lie
    [InlineData(true)]
    [InlineData(false)]
    public void Apply_ShouldThrow_WhenTheReplacedQueenIsNotTheCurrentOne(bool colonyHasQueen)
    {
        var colony = colonyHasQueen ? Existing() : Existing() with { Queen = null };
        var requeen = new ColonyRequeened { Queen = Queen(SecondQueenId), ReplacedQueenId = colonyHasQueen ? SecondQueenId : FirstQueenId };

        var act = () => ColonyProjection.Apply(Event(ParentId, requeen), colony);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact] // invariant: a move changes only the colony's yard
    public void Apply_ShouldMoveTheColonyToTheNewYard_WhenMoved()
    {
        var colony = Existing();

        var result = ColonyProjection.Apply(Event(ParentId, new ColonyMoved { FromYardId = HomeYard, ToYardId = OtherYard }), colony);

        result.Colony.Colony.YardId.Should().Be(OtherYard);
        result.Colony.Colony.Status.Should().Be(ColonyStatus.Active);
        result.Colony.Colony.Version.Should().Be(2);
        result.AddedColony.Should().BeNull();
    }

    public static TheoryData<ColonyEventData> EventsAboutAnExistingColony() => new()
    {
        new ColonySplit { NewColonyId = ChildId, NewColony = Profile("A-02") },
        new ColonyMerged { IntoColonyId = ChildId },
        new ColonyRequeened { Queen = Queen(SecondQueenId) },
        new ColonyMoved { FromYardId = HomeYard, ToYardId = OtherYard },
    };

    [Theory] // boundary: an event about an existing colony needs that colony's state
    [MemberData(nameof(EventsAboutAnExistingColony))]
    public void Apply_ShouldThrow_WhenTheColonyTheEventIsAboutIsNotGiven(ColonyEventData data)
    {
        var act = () => ColonyProjection.Apply(Event(ParentId, data), null);

        act.Should().Throw<ArgumentException>();
    }

    [Theory] // boundary: the state given must be the colony the event is about
    [MemberData(nameof(EventsAboutAnExistingColony))]
    public void Apply_ShouldThrow_WhenTheStateGivenIsAnotherColony(ColonyEventData data)
    {
        var act = () => ColonyProjection.Apply(Event(ChildId, data), Existing());

        act.Should().Throw<ArgumentException>();
    }

    [Fact] // boundary: a registration never overwrites a colony that already exists
    public void Apply_ShouldThrow_WhenAColonyIsCreatedOverAnExistingOne()
    {
        var act = () => ColonyProjection.Apply(Event(ParentId, new ColonyRegistered { Colony = Profile("A-01") }), Existing());

        act.Should().Throw<ArgumentException>();
    }

    private static ColonyEvent Event(ColonyId colonyId, ColonyEventData data) => new(
        ColonyEventId.New(), Account, colonyId, SplitDay, new DateTimeOffset(2026, 4, 12, 18, 30, 0, TimeSpan.Zero), data);

    private static ColonyProfile Profile(string tagId) => new()
    {
        TagId = tagId,
        HiveNumber = "TX-0412",
        YardId = HomeYard,
        HiveType = "Langstroth 10-frame",
        BoxCount = 2,
    };

    private static NewQueen Queen(QueenId queenId) => new()
    {
        QueenId = queenId,
        Details = new QueenDetails
        {
            Source = "Synthetic Queen Breeder",
            MarkColour = QueenMarkColour.Green,
            MarkYear = 2024,
            IntroducedOn = new DateOnly(2026, 5, 3),
        },
    };

    // An active colony to merge into, as the store would load it.
    private static Colony Target() =>
        ColonyProjection.Apply(Event(ChildId, new ColonyRegistered { Colony = Profile("A-02") }), null).Colony.Colony;

    // A registered parent with its first queen, as the store would load it.
    private static ColonyState Existing() =>
        ColonyProjection.Apply(Event(ParentId, new ColonyRegistered { Colony = Profile("A-01"), Queen = Queen(FirstQueenId) }), null).Colony;
}
