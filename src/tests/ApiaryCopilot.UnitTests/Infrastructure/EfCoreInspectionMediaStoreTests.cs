using System.Text.Json;
using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ApiaryCopilot.UnitTests.Infrastructure;

// The inspection media store over the in-memory provider, under the real model and its account filter, with the
// clock faked (CONVENTIONS.md §8.1); the unique key index and relational behaviour are the integration tier's.
public sealed class EfCoreInspectionMediaStoreTests
{
    private static readonly AccountId FirstId = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly AccountId SecondId = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));
    private static readonly DateOnly Day = new(2026, 4, 12);
    private static readonly DateTimeOffset Now = new(2026, 4, 12, 18, 30, 0, TimeSpan.Zero);
    private static readonly StoredObject Photo = new("image/jpeg", 2_400_000);

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly TimeProvider _clock = A.Fake<TimeProvider>();
    private int _tick;

    public EfCoreInspectionMediaStoreTests() =>
        A.CallTo(() => _clock.GetUtcNow()).ReturnsLazily(() => Now.AddSeconds(_tick++));

    [Fact] // invariant (FR-8): an attached object belongs to the caller's account and the inspection, stamped with when it was attached
    public async Task AttachAsync_ShouldRecordTheMediaWithItsTimestamp_WhenTheKeyIsTheInspections()
    {
        var inspection = await AddInspectionAsync(FirstId);
        var key = MediaObjectKey.New(FirstId, inspection);

        var result = await StoreFor(FirstId).AttachAsync(inspection, key, Photo, CancellationToken.None);

        result.Outcome.Should().Be(MediaAttachOutcome.Attached);
        await using var db = CreateContext(FirstId);
        var stored = await db.InspectionMedia.SingleAsync(CancellationToken.None);
        stored.MediaId.Should().Be(result.Media!.MediaId);
        stored.AccountId.Should().Be(FirstId);
        stored.InspectionId.Should().Be(inspection);
        stored.ObjectKey.Should().Be(key);
        stored.ContentType.Should().Be("image/jpeg");
        stored.SizeBytes.Should().Be(2_400_000);
        stored.AttachedAt.Should().Be(Now);
    }

    [Fact] // boundary: with no current account nothing is written (fails closed)
    public async Task AttachAsync_ShouldThrowAndWriteNothing_WhenNoAccountIsCurrent()
    {
        var inspection = await AddInspectionAsync(FirstId);

        var act = () => StoreFor(null).AttachAsync(inspection, MediaObjectKey.New(FirstId, inspection), Photo, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await CountMediaAsync(FirstId)).Should().Be(0);
    }

    [Fact] // invariant (adversarial, NFR-8): another account's inspection is not found, and nothing is attached to it
    public async Task AttachAsync_ShouldReturnNoSuchInspectionAndWriteNothing_WhenTheInspectionBelongsToAnotherAccount()
    {
        var theirs = await AddInspectionAsync(SecondId);

        var result = await StoreFor(FirstId).AttachAsync(theirs, MediaObjectKey.New(FirstId, theirs), Photo, CancellationToken.None);

        result.Outcome.Should().Be(MediaAttachOutcome.NoSuchInspection);
        (await CountMediaAsync(SecondId)).Should().Be(0);
        (await CountMediaAsync(FirstId)).Should().Be(0);
    }

    [Fact] // invariant (adversarial, NFR-8): a key issued to another account is refused, even on the caller's own inspection
    public async Task AttachAsync_ShouldRefuseTheKeyAndWriteNothing_WhenTheKeyBelongsToAnotherAccount()
    {
        var inspection = await AddInspectionAsync(FirstId);

        var result = await StoreFor(FirstId).AttachAsync(inspection, MediaObjectKey.New(SecondId, inspection), Photo, CancellationToken.None);

        result.Outcome.Should().Be(MediaAttachOutcome.KeyNotForThisInspection);
        (await CountMediaAsync(FirstId)).Should().Be(0);
    }

    [Fact] // invariant: a key issued for one inspection is refused on another of the account's inspections
    public async Task AttachAsync_ShouldRefuseTheKeyAndWriteNothing_WhenTheKeyWasIssuedForAnotherInspection()
    {
        var inspection = await AddInspectionAsync(FirstId);
        var other = await AddInspectionAsync(FirstId);

        var result = await StoreFor(FirstId).AttachAsync(inspection, MediaObjectKey.New(FirstId, other), Photo, CancellationToken.None);

        result.Outcome.Should().Be(MediaAttachOutcome.KeyNotForThisInspection);
        (await CountMediaAsync(FirstId)).Should().Be(0);
    }

    [Fact] // invariant: an object is attached once; attaching its key again is refused and records nothing more
    public async Task AttachAsync_ShouldRefuseAndKeepOneRecord_WhenTheKeyIsAlreadyAttached()
    {
        var inspection = await AddInspectionAsync(FirstId);
        var key = MediaObjectKey.New(FirstId, inspection);
        await StoreFor(FirstId).AttachAsync(inspection, key, Photo, CancellationToken.None);

        var again = await StoreFor(FirstId).AttachAsync(inspection, key, Photo, CancellationToken.None);

        again.Outcome.Should().Be(MediaAttachOutcome.AlreadyAttached);
        (await CountMediaAsync(FirstId)).Should().Be(1);
    }

    [Fact] // boundary: an inspection that does not exist has nothing attached
    public async Task AttachAsync_ShouldReturnNoSuchInspection_WhenTheInspectionDoesNotExist()
    {
        var missing = InspectionId.New();

        var result = await StoreFor(FirstId).AttachAsync(missing, MediaObjectKey.New(FirstId, missing), Photo, CancellationToken.None);

        result.Outcome.Should().Be(MediaAttachOutcome.NoSuchInspection);
        (await CountMediaAsync(FirstId)).Should().Be(0);
    }

    [Fact] // invariant (NFR-8): an inspection exists for the account that has it, and for no other
    public async Task InspectionExistsAsync_ShouldBeTrueOnlyForTheOwningAccount_WhenAsked()
    {
        var inspection = await AddInspectionAsync(FirstId);

        (await StoreFor(FirstId).InspectionExistsAsync(inspection, CancellationToken.None)).Should().BeTrue();
        (await StoreFor(SecondId).InspectionExistsAsync(inspection, CancellationToken.None)).Should().BeFalse();
    }

    [Fact] // invariant: an inspection's media list in the order attached, a page at a time, and only that inspection's
    public async Task ListAsync_ShouldReturnTheInspectionsMediaOldestFirst_WhenSeveralAreAttached()
    {
        var inspection = await AddInspectionAsync(FirstId);
        var other = await AddInspectionAsync(FirstId);
        var keys = new List<MediaObjectKey>();
        for (var i = 0; i < 3; i++)
        {
            var key = MediaObjectKey.New(FirstId, inspection);
            keys.Add(key);
            await StoreFor(FirstId).AttachAsync(inspection, key, Photo, CancellationToken.None);
        }

        await StoreFor(FirstId).AttachAsync(other, MediaObjectKey.New(FirstId, other), Photo, CancellationToken.None);

        var first = await StoreFor(FirstId).ListAsync(inspection, 0, 2, CancellationToken.None);
        var second = await StoreFor(FirstId).ListAsync(inspection, 2, 2, CancellationToken.None);

        first!.Concat(second!).Select(m => m.ObjectKey).Should().Equal(keys);
    }

    [Fact] // invariant (adversarial, NFR-8): another account's inspection has no list to read
    public async Task ListAsync_ShouldReturnNull_WhenTheInspectionBelongsToAnotherAccount()
    {
        var theirs = await AddInspectionAsync(SecondId);
        await StoreFor(SecondId).AttachAsync(theirs, MediaObjectKey.New(SecondId, theirs), Photo, CancellationToken.None);

        (await StoreFor(FirstId).ListAsync(theirs, 0, 10, CancellationToken.None)).Should().BeNull();
    }

    [Fact] // invariant (adversarial, NFR-8): another account's media is never found by id
    public async Task FindAsync_ShouldReturnNull_WhenTheMediaBelongsToAnotherAccount()
    {
        var theirs = await AddInspectionAsync(SecondId);
        var attached = await StoreFor(SecondId).AttachAsync(theirs, MediaObjectKey.New(SecondId, theirs), Photo, CancellationToken.None);

        (await StoreFor(FirstId).FindAsync(attached.Media!.MediaId, CancellationToken.None)).Should().BeNull();
        (await StoreFor(SecondId).FindAsync(attached.Media.MediaId, CancellationToken.None)).Should().NotBeNull();
    }

    private async Task<int> CountMediaAsync(AccountId account)
    {
        await using var db = CreateContext(account);
        return await db.InspectionMedia.CountAsync(CancellationToken.None);
    }

    private async Task<InspectionId> AddInspectionAsync(AccountId account)
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
        var colony = await new EfCoreColonyStore(CreateContext(account), CurrentAccount(account), TimeProvider.System)
            .RegisterAsync(
                new ColonyProfile { TagId = $"T-{Guid.NewGuid():N}"[..20], HiveNumber = "TX-0412", YardId = yard.YardId, HiveType = "Langstroth 10-frame", BoxCount = 2 },
                null,
                Day,
                CancellationToken.None);
        var inspection = await new EfCoreInspectionStore(CreateContext(account), CurrentAccount(account), TimeProvider.System).AddAsync(
            colony.Colony!.Colony.ColonyId,
            new InspectionDetails
            {
                InspectedOn = Day,
                Findings = InspectionFields.Check(new Dictionary<string, JsonElement> { ["queenSeen"] = JsonSerializer.SerializeToElement(true) }).Findings!,
            },
            CancellationToken.None);
        return inspection!.InspectionId;
    }

    private EfCoreInspectionMediaStore StoreFor(AccountId? current) => new(CreateContext(current), CurrentAccount(current), _clock);

    private ApiaryCopilotDbContext CreateContext(AccountId? current)
    {
        var options = new DbContextOptionsBuilder<ApiaryCopilotDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options;
        return new ApiaryCopilotDbContext(options, CurrentAccount(current), TestUsers.Current(TestUsers.Acting), TimeProvider.System);
    }

    private static ICurrentAccount CurrentAccount(AccountId? current)
    {
        var currentAccount = A.Fake<ICurrentAccount>();
        A.CallTo(() => currentAccount.AccountId).Returns(current);
        return currentAccount;
    }
}
