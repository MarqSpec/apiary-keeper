using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ApiaryCopilot.UnitTests.Infrastructure;

// The colony store over the in-memory provider, under the real model and its account filter, with the clock
// faked (CONVENTIONS.md §8.1); relational behaviour is the integration tier's.
public sealed class EfCoreColonyStoreTests
{
    private static readonly AccountId FirstId = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly AccountId SecondId = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));
    private static readonly DateOnly Day = new(2026, 4, 12);
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 18, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 7, 1);

    // The tag's unique index as the committed migration names it.
    private const string TagIndex = "IX_Colonies_AccountId_TagId";

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly TimeProvider _clock = A.Fake<TimeProvider>();
    private int _tick;

    public EfCoreColonyStoreTests() =>
        A.CallTo(() => _clock.GetUtcNow()).ReturnsLazily(() => Now.AddSeconds(_tick++));

    [Fact] // invariant: a registered colony and its first event belong to the caller's account
    public async Task RegisterAsync_ShouldRecordTheColonyAndItsEvent_WhenTheYardIsTheAccounts()
    {
        var yard = await AddYardAsync(FirstId);

        var result = await StoreFor(FirstId).RegisterAsync(Profile("A-01", yard), Queen("Synthetic Queen Breeder"), Day, CancellationToken.None);

        result.Outcome.Should().Be(ColonyChangeOutcome.Recorded);
        await using var db = CreateContext(FirstId);
        var colony = await db.Colonies.SingleAsync(CancellationToken.None);
        colony.ColonyId.Should().Be(result.Colony!.Colony.ColonyId);
        colony.AccountId.Should().Be(FirstId);
        colony.TagId.Should().Be("A-01");
        var queen = await db.Queens.SingleAsync(CancellationToken.None);
        queen.Details.Should().Be(Queen("Synthetic Queen Breeder"));
        queen.AccountId.Should().Be(FirstId);
        var recorded = await db.ColonyEvents.SingleAsync(CancellationToken.None);
        recorded.ColonyId.Should().Be(colony.ColonyId);
        recorded.AccountId.Should().Be(FirstId);
        recorded.OccurredOn.Should().Be(Day);
        recorded.RecordedAt.Should().Be(Now);
        recorded.Data.Should().BeOfType<ColonyRegistered>()
            .Which.Queen!.QueenId.Should().Be(queen.QueenId);
    }

    [Fact] // boundary: with no current account nothing is written (fails closed)
    public async Task RegisterAsync_ShouldThrowAndWriteNothing_WhenNoAccountIsCurrent()
    {
        var yard = await AddYardAsync(FirstId);

        var act = () => StoreFor(null).RegisterAsync(Profile("A-01", yard), null, Day, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await using var db = CreateContext(FirstId);
        (await db.ColonyEvents.CountAsync(CancellationToken.None)).Should().Be(0);
        (await db.Colonies.CountAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact] // invariant (adversarial): a colony cannot be placed in another account's yard
    public async Task RegisterAsync_ShouldRefuseTheYard_WhenItBelongsToAnotherAccount()
    {
        var theirs = await AddYardAsync(SecondId);

        var result = await StoreFor(FirstId).RegisterAsync(Profile("A-01", theirs), null, Day, CancellationToken.None);

        result.Should().Be(ColonyChangeResult.Refused(ColonyRefusal.NoSuchYard));
        await AssertNothingRecordedAsync(FirstId);
    }

    [Fact] // invariant: a tag id is unique within the account, whatever became of the colony that carries it
    public async Task RegisterAsync_ShouldRefuseTheTag_WhenAnotherColonyOfTheAccountCarriesIt()
    {
        var yard = await AddYardAsync(FirstId);
        await StoreFor(FirstId).RegisterAsync(Profile("A-01", yard), null, Day, CancellationToken.None);

        var result = await StoreFor(FirstId).CaptureSwarmAsync(Profile("A-01", yard), null, Day, CancellationToken.None);

        result.Should().Be(ColonyChangeResult.Refused(ColonyRefusal.TagIdTaken));
        await using var db = CreateContext(FirstId);
        (await db.ColonyEvents.CountAsync(CancellationToken.None)).Should().Be(1);
    }

    // The in-memory provider has no unique index, so the save is made to fail as PostgreSQL fails it; the real race
    // is ColonyEndpointTests.RegisterColony_ShouldRefuseTheTag_WhenAnotherRegistrationOfItCommitsFirst.
    [Fact] // regression: a create that loses the tag race to the unique index is the pre-check's refusal, not a 500
    public async Task RegisterAsync_ShouldRefuseTheTag_WhenTheDatabaseRejectsItAsADuplicate()
    {
        var yard = await AddYardAsync(FirstId);

        var result = await StoreFailingSaveWith(FirstId, UniqueViolation(TagIndex)).RegisterAsync(Profile("A-01", yard), null, Day, CancellationToken.None);

        result.Should().Be(ColonyChangeResult.Refused(ColonyRefusal.TagIdTaken));
    }

    [Fact] // regression: a split's new colony loses the tag race the same way
    public async Task SplitAsync_ShouldRefuseTheTag_WhenTheDatabaseRejectsItAsADuplicate()
    {
        var yard = await AddYardAsync(FirstId);
        var parent = await RegisterAsync(FirstId, "A-01", yard);

        var result = await StoreFailingSaveWith(FirstId, UniqueViolation(TagIndex)).SplitAsync(parent, Profile("A-02", yard), null, Day, CancellationToken.None);

        result.Should().Be(ColonyChangeResult.Refused(ColonyRefusal.TagIdTaken));
    }

    public static TheoryData<string, string?> FailuresThatAreNotTheTag() => new()
    {
        { PostgresErrorCodes.UniqueViolation, "PK_Colonies" },
        { PostgresErrorCodes.ForeignKeyViolation, TagIndex },
        { PostgresErrorCodes.UniqueViolation, null },
    };

    [Theory] // boundary: only the tag's unique index is a taken tag; any other failure stays a bug (CONVENTIONS.md §4.3)
    [MemberData(nameof(FailuresThatAreNotTheTag))]
    public async Task RegisterAsync_ShouldRethrow_WhenTheSaveFailsOnAnythingButTheTagIndex(string sqlState, string? constraint)
    {
        var yard = await AddYardAsync(FirstId);
        var failure = new DbUpdateException("synthetic", new PostgresException("synthetic", "ERROR", "ERROR", sqlState, constraintName: constraint));

        var act = () => StoreFailingSaveWith(FirstId, failure).RegisterAsync(Profile("A-01", yard), null, Day, CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact] // boundary: a failed save with no database error behind it is not taken for the tag either
    public async Task RegisterAsync_ShouldRethrow_WhenTheSaveFailsWithNoDatabaseError()
    {
        var yard = await AddYardAsync(FirstId);

        var act = () => StoreFailingSaveWith(FirstId, new DbUpdateException("synthetic")).RegisterAsync(Profile("A-01", yard), null, Day, CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact] // boundary: another account's tags do not count against this one's
    public async Task RegisterAsync_ShouldAcceptTheTag_WhenOnlyAnotherAccountCarriesIt()
    {
        var theirYard = await AddYardAsync(SecondId);
        await StoreFor(SecondId).RegisterAsync(Profile("A-01", theirYard), null, Day, CancellationToken.None);
        var ourYard = await AddYardAsync(FirstId);

        var result = await StoreFor(FirstId).RegisterAsync(Profile("A-01", ourYard), null, Day, CancellationToken.None);

        result.Outcome.Should().Be(ColonyChangeOutcome.Recorded);
    }

    [Fact] // regression risk: a captured swarm is recorded as its own kind of event and origin
    public async Task CaptureSwarmAsync_ShouldRecordASwarmCapture_WhenTheRequestIsValid()
    {
        var yard = await AddYardAsync(FirstId);

        var result = await StoreFor(FirstId).CaptureSwarmAsync(Profile("S-01", yard), null, Day, CancellationToken.None);

        result.Colony!.Colony.Origin.Should().Be(ColonyOrigin.SwarmCapture);
        await using var db = CreateContext(FirstId);
        (await db.ColonyEvents.SingleAsync(CancellationToken.None)).Data.Should().BeOfType<SwarmCaptured>();
    }

    [Fact] // invariant: a split records one event that both colonies' histories show, and creates the child
    public async Task SplitAsync_ShouldCreateTheChildAndRecordTheSplitInBothHistories_WhenTheParentIsActive()
    {
        var yard = await AddYardAsync(FirstId);
        var parent = await RegisterAsync(FirstId, "A-01", yard);

        var result = await StoreFor(FirstId).SplitAsync(parent, Profile("A-02", yard), Queen("Raised in the yard"), Day, CancellationToken.None);

        result.Outcome.Should().Be(ColonyChangeOutcome.Recorded);
        var child = result.Colony!.Colony;
        child.ParentColonyId.Should().Be(parent);
        child.Origin.Should().Be(ColonyOrigin.Split);
        result.Colony.Queen!.Details.Source.Should().Be("Raised in the yard");
        var parentHistory = await StoreFor(FirstId).ListEventsAsync(parent, 0, 10, CancellationToken.None);
        var childHistory = await StoreFor(FirstId).ListEventsAsync(child.ColonyId, 0, 10, CancellationToken.None);
        parentHistory!.Select(e => e.Data.GetType()).Should().Equal(typeof(ColonyRegistered), typeof(ColonySplit));
        childHistory!.Should().ContainSingle().Which.EventId.Should().Be(parentHistory![1].EventId);
        childHistory![0].RelatedColonyId.Should().Be(child.ColonyId);
    }

    [Fact] // boundary: splitting a colony the account does not have is NotFound, and nothing is written
    public async Task SplitAsync_ShouldReturnNotFound_WhenTheParentBelongsToAnotherAccount()
    {
        var theirYard = await AddYardAsync(SecondId);
        var theirs = await RegisterAsync(SecondId, "B-01", theirYard);
        var ourYard = await AddYardAsync(FirstId);

        var result = await StoreFor(FirstId).SplitAsync(theirs, Profile("A-02", ourYard), null, Day, CancellationToken.None);

        result.Should().Be(ColonyChangeResult.NotFound);
        await AssertNothingRecordedAsync(FirstId);
    }

    [Fact] // invariant: only an active colony has lineage events after its end
    public async Task SplitAsync_ShouldRefuse_WhenTheParentIsNoLongerActive()
    {
        var yard = await AddYardAsync(FirstId);
        var merged = await RegisterAsync(FirstId, "A-01", yard);
        var into = await RegisterAsync(FirstId, "A-02", yard);
        await StoreFor(FirstId).MergeAsync(merged, into, Day, CancellationToken.None);

        var result = await StoreFor(FirstId).SplitAsync(merged, Profile("A-03", yard), null, Day, CancellationToken.None);

        result.Should().Be(ColonyChangeResult.Refused(ColonyRefusal.NotActive));
    }

    [Fact] // regression risk: a merge ends the colony and records where it went
    public async Task MergeAsync_ShouldMarkTheColonyMerged_WhenBothColoniesAreActive()
    {
        var yard = await AddYardAsync(FirstId);
        var weak = await RegisterAsync(FirstId, "A-01", yard);
        var strong = await RegisterAsync(FirstId, "A-02", yard);

        var result = await StoreFor(FirstId).MergeAsync(weak, strong, Day, CancellationToken.None);

        result.Colony!.Colony.Status.Should().Be(ColonyStatus.Merged);
        var reread = await StoreFor(FirstId).FindAsync(weak, CancellationToken.None);
        reread!.Colony.Status.Should().Be(ColonyStatus.Merged);
        reread.Colony.MergedIntoColonyId.Should().Be(strong);
        (await StoreFor(FirstId).ListEventsAsync(strong, 0, 10, CancellationToken.None))!.Should().HaveCount(2);
    }

    [Fact] // boundary: a colony cannot be merged into itself
    public async Task MergeAsync_ShouldRefuse_WhenMergingAColonyIntoItself()
    {
        var yard = await AddYardAsync(FirstId);
        var colony = await RegisterAsync(FirstId, "A-01", yard);

        var result = await StoreFor(FirstId).MergeAsync(colony, colony, Day, CancellationToken.None);

        result.Should().Be(ColonyChangeResult.Refused(ColonyRefusal.MergeIntoItself));
    }

    [Fact] // invariant (adversarial): a colony cannot be merged into another account's colony
    public async Task MergeAsync_ShouldRefuseTheTarget_WhenItBelongsToAnotherAccount()
    {
        var theirYard = await AddYardAsync(SecondId);
        var theirs = await RegisterAsync(SecondId, "B-01", theirYard);
        var ourYard = await AddYardAsync(FirstId);
        var ours = await RegisterAsync(FirstId, "A-01", ourYard);

        var result = await StoreFor(FirstId).MergeAsync(ours, theirs, Day, CancellationToken.None);

        result.Should().Be(ColonyChangeResult.Refused(ColonyRefusal.NoSuchTargetColony));
    }

    [Fact] // boundary: a colony cannot be merged into one that has already ended
    public async Task MergeAsync_ShouldRefuseTheTarget_WhenItIsNoLongerActive()
    {
        var yard = await AddYardAsync(FirstId);
        var first = await RegisterAsync(FirstId, "A-01", yard);
        var second = await RegisterAsync(FirstId, "A-02", yard);
        var third = await RegisterAsync(FirstId, "A-03", yard);
        await StoreFor(FirstId).MergeAsync(second, third, Day, CancellationToken.None);

        var result = await StoreFor(FirstId).MergeAsync(first, second, Day, CancellationToken.None);

        result.Should().Be(ColonyChangeResult.Refused(ColonyRefusal.TargetNotActive));
    }

    [Fact] // invariant: requeening keeps the replaced queen in the history, newest first (FR-5)
    public async Task RequeenAsync_ShouldKeepTheReplacementHistory_WhenTheColonyIsRequeened()
    {
        var yard = await AddYardAsync(FirstId);
        var colony = (await StoreFor(FirstId).RegisterAsync(Profile("A-01", yard), Queen("Synthetic Queen Breeder"), Day, CancellationToken.None)).Colony!;
        var replacement = Queen("Raised in the yard") with { IntroducedOn = new DateOnly(2026, 6, 1) };

        var result = await StoreFor(FirstId).RequeenAsync(colony.Colony.ColonyId, replacement, CancellationToken.None);

        result.Colony!.Queen!.Details.Should().Be(replacement);
        var queens = await StoreFor(FirstId).ListQueensAsync(colony.Colony.ColonyId, 0, 10, CancellationToken.None);
        queens!.Select(q => q.Details.Source).Should().Equal("Raised in the yard", "Synthetic Queen Breeder");
        queens![1].ReplacedOn.Should().Be(new DateOnly(2026, 6, 1));
        queens[1].ReplacedByQueenId.Should().Be(queens[0].QueenId);
        var recorded = (await StoreFor(FirstId).ListEventsAsync(colony.Colony.ColonyId, 0, 10, CancellationToken.None))![1];
        recorded.OccurredOn.Should().Be(new DateOnly(2026, 6, 1));
        recorded.Data.Should().BeOfType<ColonyRequeened>().Which.ReplacedQueenId.Should().Be(colony.Queen!.QueenId);
    }

    [Fact] // boundary: a requeen dated after today (UTC) has not happened, so it is refused and nothing is appended
    public async Task RequeenAsync_ShouldRefuseAndRecordNothing_WhenTheQueenIsIntroducedAfterToday()
    {
        var yard = await AddYardAsync(FirstId);
        var colony = (await StoreFor(FirstId).RegisterAsync(Profile("A-01", yard), Queen("Synthetic Queen Breeder"), Day, CancellationToken.None)).Colony!;

        var result = await StoreFor(FirstId).RequeenAsync(colony.Colony.ColonyId, Queen("Raised in the yard") with { IntroducedOn = Today.AddDays(1) }, CancellationToken.None);

        result.Should().Be(ColonyChangeResult.Refused(ColonyRefusal.IntroducedInFuture));
        (await StoreFor(FirstId).ListEventsAsync(colony.Colony.ColonyId, 0, 10, CancellationToken.None)).Should().ContainSingle();
        (await StoreFor(FirstId).FindAsync(colony.Colony.ColonyId, CancellationToken.None))!.Queen!.QueenId.Should().Be(colony.Queen!.QueenId);
    }

    [Fact] // invariant (FR-6, D-2): a requeen dated before the current queen's introduction is kept exactly as given, and warned about
    public async Task RequeenAsync_ShouldRecordTheEventAsGivenAndWarn_WhenTheQueenIsIntroducedBeforeTheCurrentQueen()
    {
        var yard = await AddYardAsync(FirstId);
        var colony = (await StoreFor(FirstId).RegisterAsync(Profile("A-01", yard), Queen("Synthetic Queen Breeder"), Day, CancellationToken.None)).Colony!;
        var backdated = Queen("Raised in the yard") with { IntroducedOn = new DateOnly(2026, 3, 1) };

        var result = await StoreFor(FirstId).RequeenAsync(colony.Colony.ColonyId, backdated, CancellationToken.None);

        result.Outcome.Should().Be(ColonyChangeOutcome.Recorded);
        result.Warning.Should().Be(new RequeenBeforeCurrentQueen(new DateOnly(2026, 3, 1), Day));
        var recorded = (await StoreFor(FirstId).ListEventsAsync(colony.Colony.ColonyId, 0, 10, CancellationToken.None))![1];
        recorded.OccurredOn.Should().Be(new DateOnly(2026, 3, 1));
        recorded.Data.Should().Be(new ColonyRequeened
        {
            Queen = new NewQueen { QueenId = result.Colony!.Queen!.QueenId, Details = backdated },
            ReplacedQueenId = colony.Queen!.QueenId,
        });
    }

    [Theory] // boundary: a requeen on or after the current queen's introduction day is ordinary, with no warning
    [InlineData(0)]
    [InlineData(1)]
    public async Task RequeenAsync_ShouldNotWarn_WhenTheQueenIsIntroducedOnOrAfterTheCurrentQueensDay(int daysAfter)
    {
        var yard = await AddYardAsync(FirstId);
        var colony = (await StoreFor(FirstId).RegisterAsync(Profile("A-01", yard), Queen("Synthetic Queen Breeder"), Day, CancellationToken.None)).Colony!;

        var result = await StoreFor(FirstId).RequeenAsync(colony.Colony.ColonyId, Queen("Raised in the yard") with { IntroducedOn = Day.AddDays(daysAfter) }, CancellationToken.None);

        result.Outcome.Should().Be(ColonyChangeOutcome.Recorded);
        result.Warning.Should().BeNull();
    }

    [Fact] // boundary: a colony with no recorded queen has no introduction day to precede, so no warning
    public async Task RequeenAsync_ShouldNotWarn_WhenTheColonyHasNoQueen()
    {
        var yard = await AddYardAsync(FirstId);
        var colony = await RegisterAsync(FirstId, "A-01", yard);

        var result = await StoreFor(FirstId).RequeenAsync(colony, Queen("Raised in the yard") with { IntroducedOn = new DateOnly(2020, 1, 1) }, CancellationToken.None);

        result.Outcome.Should().Be(ColonyChangeOutcome.Recorded);
        result.Warning.Should().BeNull();
    }

    [Fact] // boundary: a requeen dated today (UTC) is not in the future, and is recorded
    public async Task RequeenAsync_ShouldRecord_WhenTheQueenIsIntroducedToday()
    {
        var yard = await AddYardAsync(FirstId);
        var colony = await RegisterAsync(FirstId, "A-01", yard);

        var result = await StoreFor(FirstId).RequeenAsync(colony, Queen("Raised in the yard") with { IntroducedOn = Today }, CancellationToken.None);

        result.Outcome.Should().Be(ColonyChangeOutcome.Recorded);
        result.Colony!.Queen!.Details.IntroducedOn.Should().Be(Today);
    }

    [Fact] // regression risk: a move records both yards and changes the colony's yard
    public async Task MoveAsync_ShouldMoveTheColony_WhenTheYardIsAnotherOfTheAccounts()
    {
        var home = await AddYardAsync(FirstId);
        var away = await AddYardAsync(FirstId);
        var colony = await RegisterAsync(FirstId, "A-01", home);

        var result = await StoreFor(FirstId).MoveAsync(colony, away, Day, CancellationToken.None);

        result.Colony!.Colony.YardId.Should().Be(away);
        (await StoreFor(FirstId).FindAsync(colony, CancellationToken.None))!.Colony.YardId.Should().Be(away);
        var moved = (await StoreFor(FirstId).ListEventsAsync(colony, 0, 10, CancellationToken.None))![1].Data;
        moved.Should().Be(new ColonyMoved { FromYardId = home, ToYardId = away });
    }

    [Fact] // boundary: a move to the yard the colony is already in records nothing
    public async Task MoveAsync_ShouldRefuse_WhenTheColonyIsAlreadyInThatYard()
    {
        var yard = await AddYardAsync(FirstId);
        var colony = await RegisterAsync(FirstId, "A-01", yard);

        var result = await StoreFor(FirstId).MoveAsync(colony, yard, Day, CancellationToken.None);

        result.Should().Be(ColonyChangeResult.Refused(ColonyRefusal.AlreadyInThatYard));
    }

    [Fact] // invariant (adversarial): a colony cannot be moved into another account's yard
    public async Task MoveAsync_ShouldRefuseTheYard_WhenItBelongsToAnotherAccount()
    {
        var theirs = await AddYardAsync(SecondId);
        var ours = await AddYardAsync(FirstId);
        var colony = await RegisterAsync(FirstId, "A-01", ours);

        var result = await StoreFor(FirstId).MoveAsync(colony, theirs, Day, CancellationToken.None);

        result.Should().Be(ColonyChangeResult.Refused(ColonyRefusal.NoSuchYard));
    }

    [Fact] // boundary: a requeen or move of a colony the account does not have is NotFound
    public async Task RequeenAndMove_ShouldReturnNotFound_WhenTheColonyIsUnknown()
    {
        var yard = await AddYardAsync(FirstId);
        var unknown = ColonyId.New();

        (await StoreFor(FirstId).RequeenAsync(unknown, Queen("x"), CancellationToken.None)).Should().Be(ColonyChangeResult.NotFound);
        (await StoreFor(FirstId).MoveAsync(unknown, yard, Day, CancellationToken.None)).Should().Be(ColonyChangeResult.NotFound);
        (await StoreFor(FirstId).MergeAsync(unknown, unknown, Day, CancellationToken.None)).Should().Be(ColonyChangeResult.NotFound);
    }

    [Fact] // invariant: two writers of one colony cannot both win; the second learns it lost and its change is not applied
    public async Task MergeAsync_ShouldReturnConflict_WhenTheColonyChangedSinceItWasRead()
    {
        var home = await AddYardAsync(FirstId);
        var away = await AddYardAsync(FirstId);
        var colony = await RegisterAsync(FirstId, "A-01", home);
        var into = await RegisterAsync(FirstId, "A-02", home);
        await using var staleContext = CreateContext(FirstId);
        var stale = new EfCoreColonyStore(staleContext, CurrentAccount(FirstId), _clock);
        await staleContext.Colonies.SingleAsync(c => c.ColonyId == colony, CancellationToken.None);
        await StoreFor(FirstId).MoveAsync(colony, away, Day, CancellationToken.None);

        var result = await stale.MergeAsync(colony, into, Day, CancellationToken.None);

        // The in-memory provider has no transactions, so that the event is not kept either is the integration tier's.
        result.Should().Be(ColonyChangeResult.Conflict);
        var reread = await StoreFor(FirstId).FindAsync(colony, CancellationToken.None);
        reread!.Colony.Status.Should().Be(ColonyStatus.Active);
        reread.Colony.YardId.Should().Be(away);
    }

    // The interleaved race and that the merge's event is not kept are ColonyEndpointTests' MergeColony races.
    [Fact] // invariant: a merge into a colony that changed after it was read is the retry result, not a merge into stale state
    public async Task MergeAsync_ShouldReturnConflict_WhenTheTargetChangedSinceItWasRead()
    {
        var home = await AddYardAsync(FirstId);
        var away = await AddYardAsync(FirstId);
        var colony = await RegisterAsync(FirstId, "A-01", home);
        var into = await RegisterAsync(FirstId, "A-02", home);
        await using var staleContext = CreateContext(FirstId);
        var stale = new EfCoreColonyStore(staleContext, CurrentAccount(FirstId), _clock);
        await staleContext.Colonies.SingleAsync(c => c.ColonyId == into, CancellationToken.None);
        await StoreFor(FirstId).MoveAsync(into, away, Day, CancellationToken.None);

        var result = await stale.MergeAsync(colony, into, Day, CancellationToken.None);

        result.Should().Be(ColonyChangeResult.Conflict);
        var target = await StoreFor(FirstId).FindAsync(into, CancellationToken.None);
        target!.Colony.YardId.Should().Be(away);
        target.Colony.Version.Should().Be(2);
    }

    [Fact] // invariant: a recorded merge bumps the target's version, so a writer holding the target's old state loses
    public async Task MergeAsync_ShouldBumpTheTargetsVersion_WhenTheMergeIsRecorded()
    {
        var yard = await AddYardAsync(FirstId);
        var colony = await RegisterAsync(FirstId, "A-01", yard);
        var into = await RegisterAsync(FirstId, "A-02", yard);

        await StoreFor(FirstId).MergeAsync(colony, into, Day, CancellationToken.None);

        var target = await StoreFor(FirstId).FindAsync(into, CancellationToken.None);
        target!.Colony.Version.Should().Be(2);
        target.Colony.Status.Should().Be(ColonyStatus.Active);
    }

    [Fact] // invariant: one account's list and reads never show another's colonies
    public async Task ListAndFind_ShouldShowOnlyTheCurrentAccountsColonies_WhenAnotherAccountHasColonies()
    {
        var theirYard = await AddYardAsync(SecondId);
        var theirs = await RegisterAsync(SecondId, "B-01", theirYard);
        var ourYard = await AddYardAsync(FirstId);
        await RegisterAsync(FirstId, "A-01", ourYard);

        var listed = await StoreFor(FirstId).ListAsync(0, 10, CancellationToken.None);

        listed.Select(c => c.Colony.TagId).Should().Equal("A-01");
        (await StoreFor(FirstId).FindAsync(theirs, CancellationToken.None)).Should().BeNull();
        (await StoreFor(FirstId).ListEventsAsync(theirs, 0, 10, CancellationToken.None)).Should().BeNull();
        (await StoreFor(FirstId).ListQueensAsync(theirs, 0, 10, CancellationToken.None)).Should().BeNull();
    }

    [Fact] // invariant: pages are stable, ordered by tag, each with its current queen, and skip and take bound them
    public async Task ListAsync_ShouldReturnThePageInTagOrderWithCurrentQueens_WhenSkipAndTakeAreGiven()
    {
        var yard = await AddYardAsync(FirstId);
        foreach (var tag in new[] { "C-01", "A-01", "B-01", "D-01" })
        {
            await StoreFor(FirstId).RegisterAsync(Profile(tag, yard), Queen($"Queen of {tag}"), Day, CancellationToken.None);
        }

        var page = await StoreFor(FirstId).ListAsync(1, 2, CancellationToken.None);

        page.Select(c => c.Colony.TagId).Should().Equal("B-01", "C-01");
        page.Select(c => c.Queen!.Details.Source).Should().Equal("Queen of B-01", "Queen of C-01");
    }

    [Fact] // invariant (FR-5): queens list by introduction day, so a backdated new queen sorts after the one she replaced
    public async Task ListQueensAsync_ShouldOrderByIntroductionDay_WhenTheNewQueenIsBackdated()
    {
        var yard = await AddYardAsync(FirstId);
        var colony = (await StoreFor(FirstId).RegisterAsync(Profile("A-01", yard), Queen("Synthetic Queen Breeder"), Day, CancellationToken.None)).Colony!.Colony.ColonyId;
        await StoreFor(FirstId).RequeenAsync(colony, Queen("Raised in the yard") with { IntroducedOn = new DateOnly(2026, 3, 1) }, CancellationToken.None);

        var queens = await StoreFor(FirstId).ListQueensAsync(colony, 0, 10, CancellationToken.None);

        queens.Should().NotBeNull();
        queens!.Select(q => q.Details.Source).Should().Equal("Synthetic Queen Breeder", "Raised in the yard");
        queens![0].ReplacedOn.Should().Be(new DateOnly(2026, 3, 1));
        queens[0].ReplacedByQueenId.Should().Be(queens[1].QueenId);
        queens[1].ReplacedOn.Should().BeNull();
    }

    [Fact] // invariant: event and queen histories are bounded by skip and take
    public async Task ListEventsAndQueens_ShouldBeBounded_WhenSkipAndTakeAreGiven()
    {
        var yard = await AddYardAsync(FirstId);
        var colony = (await StoreFor(FirstId).RegisterAsync(Profile("A-01", yard), Queen("First"), Day, CancellationToken.None)).Colony!.Colony.ColonyId;
        await StoreFor(FirstId).RequeenAsync(colony, Queen("Second") with { IntroducedOn = Day.AddDays(1) }, CancellationToken.None);
        await StoreFor(FirstId).RequeenAsync(colony, Queen("Third") with { IntroducedOn = Day.AddDays(2) }, CancellationToken.None);

        var events = await StoreFor(FirstId).ListEventsAsync(colony, 1, 1, CancellationToken.None);
        var queens = await StoreFor(FirstId).ListQueensAsync(colony, 1, 1, CancellationToken.None);

        events!.Single().Data.Should().BeOfType<ColonyRequeened>().Which.Queen.Details.Source.Should().Be("Second");
        queens!.Single().Details.Source.Should().Be("Second");
    }

    private async Task<ColonyId> RegisterAsync(AccountId account, string tag, YardId yard) =>
        (await StoreFor(account).RegisterAsync(Profile(tag, yard), null, Day, CancellationToken.None)).Colony!.Colony.ColonyId;

    private async Task<YardId> AddYardAsync(AccountId account)
    {
        var yard = await new EfCoreYardStore(CreateContext(account), CurrentAccount(account)).AddAsync(
            new YardDetails
            {
                Name = $"Synthetic Yard {Guid.NewGuid():N}",
                Location = new GeoLocation { Latitude = 30.2672, Longitude = -97.7431 },
                State = UsState.Texas,
                County = "Travis",
                Landowner = new Landowner { Contact = "Synthetic Landowner, 555-0100", Terms = LandownerTerms.Free },
            },
            CancellationToken.None);
        return yard.YardId;
    }

    private async Task AssertNothingRecordedAsync(AccountId account)
    {
        await using var db = CreateContext(account);
        (await db.ColonyEvents.CountAsync(CancellationToken.None)).Should().Be(0);
        (await db.Colonies.CountAsync(CancellationToken.None)).Should().Be(0);
    }

    private static ColonyProfile Profile(string tagId, YardId yard) => new()
    {
        TagId = tagId,
        HiveNumber = "TX-0412",
        YardId = yard,
        HiveType = "Langstroth 10-frame",
        BoxCount = 2,
    };

    private static QueenDetails Queen(string source) => new()
    {
        Source = source,
        MarkColour = QueenMarkColour.Green,
        MarkYear = 2024,
        IntroducedOn = Day,
    };

    private EfCoreColonyStore StoreFor(AccountId? current) => new(CreateContext(current), CurrentAccount(current), _clock);

    private EfCoreColonyStore StoreFailingSaveWith(AccountId current, Exception failure) =>
        new(CreateContext(current, new FailingSave(failure)), CurrentAccount(current), _clock);

    private ApiaryCopilotDbContext CreateContext(AccountId? current, params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<ApiaryCopilotDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .AddInterceptors(interceptors)
            .Options;
        return new ApiaryCopilotDbContext(options, CurrentAccount(current), TestUsers.Current(TestUsers.Acting), TimeProvider.System);
    }

    private static DbUpdateException UniqueViolation(string constraint) =>
        new("synthetic", new PostgresException("synthetic", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation, constraintName: constraint));

    // Fails every save as the database would, before anything is written.
    private sealed class FailingSave(Exception failure) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) => throw failure;
    }

    private static ICurrentAccount CurrentAccount(AccountId? current)
    {
        var currentAccount = A.Fake<ICurrentAccount>();
        A.CallTo(() => currentAccount.AccountId).Returns(current);
        return currentAccount;
    }
}
