using System.Text.Json;
using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ApiaryCopilot.UnitTests.Infrastructure;

// The inspection store over the in-memory provider, under the real model and its account filter, with the clock
// faked (CONVENTIONS.md §8.1); relational behaviour is the integration tier's.
public sealed class EfCoreInspectionStoreTests
{
    private static readonly AccountId FirstId = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly AccountId SecondId = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));
    private static readonly DateOnly Day = new(2026, 4, 12);
    private static readonly DateTimeOffset Now = new(2026, 4, 12, 18, 30, 0, TimeSpan.Zero);

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly TimeProvider _clock = A.Fake<TimeProvider>();
    private int _tick;

    public EfCoreInspectionStoreTests() =>
        A.CallTo(() => _clock.GetUtcNow()).ReturnsLazily(() => Now.AddSeconds(_tick++));

    [Fact] // invariant: a recorded inspection belongs to the caller's account and the colony, with everything given
    public async Task AddAsync_ShouldRecordTheInspection_WhenTheColonyIsTheAccounts()
    {
        var colony = await AddColonyAsync(FirstId);

        var added = await StoreFor(FirstId).AddAsync(colony, Details(Day, "Synthetic: queen laying well."), CancellationToken.None);

        added.Should().NotBeNull();
        await using var db = CreateContext(FirstId);
        var stored = await db.Inspections.SingleAsync(CancellationToken.None);
        stored.InspectionId.Should().Be(added!.InspectionId);
        stored.AccountId.Should().Be(FirstId);
        stored.ColonyId.Should().Be(colony);
        stored.InspectedOn.Should().Be(Day);
        stored.RecordedAt.Should().Be(Now);
        stored.Notes.Should().Be("Synthetic: queen laying well.");
        stored.Findings.Values["queenSeen"].GetBoolean().Should().BeTrue();
        stored.Findings.Values["diseaseSigns"].GetArrayLength().Should().Be(0);
    }

    [Fact] // boundary: with no current account nothing is written (fails closed)
    public async Task AddAsync_ShouldThrowAndWriteNothing_WhenNoAccountIsCurrent()
    {
        var colony = await AddColonyAsync(FirstId);

        var act = () => StoreFor(null).AddAsync(colony, Details(Day), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await using var db = CreateContext(FirstId);
        (await db.Inspections.CountAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact] // invariant (adversarial): another account's colony is NotFound, and nothing is written to it
    public async Task AddAsync_ShouldReturnNullAndWriteNothing_WhenTheColonyBelongsToAnotherAccount()
    {
        var theirs = await AddColonyAsync(SecondId);

        var added = await StoreFor(FirstId).AddAsync(theirs, Details(Day), CancellationToken.None);

        added.Should().BeNull();
        await using var theirDb = CreateContext(SecondId);
        (await theirDb.Inspections.CountAsync(CancellationToken.None)).Should().Be(0);
    }

    [Theory] // regression: no colony-status gate; the inspection that finds a colony dead is itself an inspection
    [InlineData(ColonyStatus.Active)]
    [InlineData(ColonyStatus.DeadOut)]
    [InlineData(ColonyStatus.Merged)]
    [InlineData(ColonyStatus.Sold)]
    public async Task AddAsync_ShouldRecordTheInspection_WhenTheColonyHasAnyStatus(ColonyStatus status)
    {
        var colony = await AddColonyAsync(FirstId);
        await using (var db = CreateContext(FirstId))
        {
            (await db.Colonies.SingleAsync(c => c.ColonyId == colony, CancellationToken.None)).Status = status;
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var added = await StoreFor(FirstId).AddAsync(colony, Details(Day, "Synthetic: found it so."), CancellationToken.None);

        added.Should().NotBeNull();
        await using var check = CreateContext(FirstId);
        (await check.Inspections.CountAsync(CancellationToken.None)).Should().Be(1);
    }

    [Fact] // boundary: a colony that does not exist is NotFound, and nothing is written
    public async Task AddAsync_ShouldReturnNullAndWriteNothing_WhenTheColonyDoesNotExist()
    {
        var added = await StoreFor(FirstId).AddAsync(ColonyId.New(), Details(Day), CancellationToken.None);

        added.Should().BeNull();
        await using var db = CreateContext(FirstId);
        (await db.Inspections.CountAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact] // invariant (FR-7): a colony's inspections list newest inspection day first, then newest recorded
    public async Task ListAsync_ShouldReturnNewestInspectionDayFirst_WhenInspectionsAreRecordedOutOfOrder()
    {
        var colony = await AddColonyAsync(FirstId);
        var store = StoreFor(FirstId);
        var middle = (await store.AddAsync(colony, Details(Day), CancellationToken.None))!;
        var oldest = (await store.AddAsync(colony, Details(Day.AddDays(-7)), CancellationToken.None))!;
        var newest = (await store.AddAsync(colony, Details(Day.AddDays(7)), CancellationToken.None))!;
        var sameDayLater = (await store.AddAsync(colony, Details(Day), CancellationToken.None))!;

        var listed = await store.ListAsync(colony, 0, 10, CancellationToken.None);

        listed!.Select(i => i.InspectionId).Should().Equal(newest.InspectionId, sameDayLater.InspectionId, middle.InspectionId, oldest.InspectionId);
    }

    [Fact] // invariant: a list holds one colony's inspections and no other's
    public async Task ListAsync_ShouldReturnOnlyTheColonysInspections_WhenAnotherColonyHasSome()
    {
        var first = await AddColonyAsync(FirstId);
        var second = await AddColonyAsync(FirstId);
        var store = StoreFor(FirstId);
        var ours = (await store.AddAsync(first, Details(Day), CancellationToken.None))!;
        await store.AddAsync(second, Details(Day), CancellationToken.None);

        var listed = await store.ListAsync(first, 0, 10, CancellationToken.None);

        listed!.Select(i => i.InspectionId).Should().Equal(ours.InspectionId);
    }

    [Fact] // boundary: a page is bounded by skip and take
    public async Task ListAsync_ShouldReturnOnePage_WhenSkipAndTakeAreGiven()
    {
        var colony = await AddColonyAsync(FirstId);
        var store = StoreFor(FirstId);
        for (var i = 0; i < 3; i++)
        {
            await store.AddAsync(colony, Details(Day.AddDays(i)), CancellationToken.None);
        }

        var listed = await store.ListAsync(colony, 1, 1, CancellationToken.None);

        listed!.Single().InspectedOn.Should().Be(Day.AddDays(1));
    }

    [Fact] // boundary: a colony with no inspections lists none, which is not the same as not found
    public async Task ListAsync_ShouldReturnAnEmptyList_WhenTheColonyHasNoInspections()
    {
        var colony = await AddColonyAsync(FirstId);

        var listed = await StoreFor(FirstId).ListAsync(colony, 0, 10, CancellationToken.None);

        listed.Should().BeEmpty();
    }

    [Fact] // invariant (adversarial): another account's colony lists as NotFound, its inspections never shown
    public async Task ListAsync_ShouldReturnNull_WhenTheColonyBelongsToAnotherAccount()
    {
        var theirs = await AddColonyAsync(SecondId);
        await StoreFor(SecondId).AddAsync(theirs, Details(Day), CancellationToken.None);

        var listed = await StoreFor(FirstId).ListAsync(theirs, 0, 10, CancellationToken.None);

        listed.Should().BeNull();
    }

    [Fact] // regression risk: an inspection reads back whole by its id
    public async Task FindAsync_ShouldReturnTheInspection_WhenTheAccountHasIt()
    {
        var colony = await AddColonyAsync(FirstId);
        var added = (await StoreFor(FirstId).AddAsync(colony, Details(Day, "Synthetic note"), CancellationToken.None))!;

        var found = await StoreFor(FirstId).FindAsync(added.InspectionId, CancellationToken.None);

        found!.ColonyId.Should().Be(colony);
        found.Notes.Should().Be("Synthetic note");
        found.Findings.Values["broodPattern"].GetString().Should().Be("solid");
    }

    [Fact] // invariant (adversarial): another account's inspection is NotFound by its id
    public async Task FindAsync_ShouldReturnNull_WhenTheInspectionBelongsToAnotherAccount()
    {
        var theirColony = await AddColonyAsync(SecondId);
        var theirs = (await StoreFor(SecondId).AddAsync(theirColony, Details(Day), CancellationToken.None))!;

        var found = await StoreFor(FirstId).FindAsync(theirs.InspectionId, CancellationToken.None);

        found.Should().BeNull();
    }

    [Fact] // boundary: with no current account nothing is found (fails closed)
    public async Task FindAsync_ShouldReturnNull_WhenNoAccountIsCurrent()
    {
        var colony = await AddColonyAsync(FirstId);
        var added = (await StoreFor(FirstId).AddAsync(colony, Details(Day), CancellationToken.None))!;

        var found = await StoreFor(null).FindAsync(added.InspectionId, CancellationToken.None);

        found.Should().BeNull();
    }

    private static InspectionDetails Details(DateOnly inspectedOn, string? notes = null) => new()
    {
        InspectedOn = inspectedOn,
        Notes = notes,
        Findings = InspectionFields.Check(new Dictionary<string, JsonElement>
        {
            ["queenSeen"] = JsonSerializer.SerializeToElement(true),
            ["broodPattern"] = JsonSerializer.SerializeToElement("solid"),
            ["diseaseSigns"] = JsonSerializer.SerializeToElement(Array.Empty<string>()),
        }).Findings!,
    };

    private async Task<ColonyId> AddColonyAsync(AccountId account)
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
        var profile = new ColonyProfile
        {
            TagId = $"T-{Guid.NewGuid():N}"[..20],
            HiveNumber = "TX-0412",
            YardId = yard.YardId,
            HiveType = "Langstroth 10-frame",
            BoxCount = 2,
        };
        var result = await new EfCoreColonyStore(CreateContext(account), CurrentAccount(account), TimeProvider.System)
            .RegisterAsync(profile, null, Day, CancellationToken.None);
        return result.Colony!.Colony.ColonyId;
    }

    private EfCoreInspectionStore StoreFor(AccountId? current) => new(CreateContext(current), CurrentAccount(current), _clock);

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
