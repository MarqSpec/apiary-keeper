using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ApiaryCopilot.UnitTests.Infrastructure;

// The yard store over the in-memory provider, under the real model and its account filter
// (CONVENTIONS.md §8.1); relational behaviour is the integration tier's.
public sealed class EfCoreYardStoreTests
{
    private static readonly AccountId FirstId = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly AccountId SecondId = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));

    private readonly string _databaseName = Guid.NewGuid().ToString();

    [Fact] // invariant: a new yard belongs to the caller's account, never to one the caller names
    public async Task AddAsync_ShouldStampTheCurrentAccount_WhenAYardIsAdded()
    {
        var added = await StoreFor(FirstId).AddAsync(Details("Mesquite Flat"), CancellationToken.None);

        await using var db = CreateContext(FirstId);
        var stored = await db.Yards.SingleAsync(CancellationToken.None);
        stored.YardId.Should().Be(added.YardId);
        stored.AccountId.Should().Be(FirstId);
        stored.Details.Should().Be(Details("Mesquite Flat"));
    }

    [Fact] // boundary: with no current account nothing is written (fails closed)
    public async Task AddAsync_ShouldThrowAndWriteNothing_WhenNoAccountIsCurrent()
    {
        var act = () => StoreFor(null).AddAsync(Details("Mesquite Flat"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await using var db = CreateContext(FirstId);
        (await db.Yards.CountAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact] // invariant: one account's list never shows another's yards
    public async Task ListAsync_ShouldReturnOnlyTheCurrentAccountsYards_WhenAnotherAccountHasYards()
    {
        await StoreFor(FirstId).AddAsync(Details("Mesquite Flat"), CancellationToken.None);
        await StoreFor(SecondId).AddAsync(Details("Pecan Bottom"), CancellationToken.None);

        var listed = await StoreFor(FirstId).ListAsync(0, 10, CancellationToken.None);

        listed.Select(y => y.Details.Name).Should().Equal("Mesquite Flat");
    }

    [Fact] // invariant: pages are stable, ordered by name, and skip and take bound them
    public async Task ListAsync_ShouldReturnThePageInNameOrder_WhenSkipAndTakeAreGiven()
    {
        var store = StoreFor(FirstId);
        foreach (var name in new[] { "Cedar Ridge", "Agarita Draw", "Bluebonnet Hill", "Dewberry Creek" })
        {
            await store.AddAsync(Details(name), CancellationToken.None);
        }

        var page = await StoreFor(FirstId).ListAsync(1, 2, CancellationToken.None);

        page.Select(y => y.Details.Name).Should().Equal("Bluebonnet Hill", "Cedar Ridge");
    }

    [Fact] // invariant: another account's yard is not found, so the caller sees NotFound (§4.3)
    public async Task FindAsync_ShouldReturnNull_WhenTheYardBelongsToAnotherAccount()
    {
        var theirs = await StoreFor(SecondId).AddAsync(Details("Pecan Bottom"), CancellationToken.None);

        var found = await StoreFor(FirstId).FindAsync(theirs.YardId, CancellationToken.None);

        found.Should().BeNull();
    }

    [Fact] // regression risk: a read returns what was written, landowner included (FR-1, FR-2)
    public async Task FindAsync_ShouldReturnTheYard_WhenItBelongsToTheCurrentAccount()
    {
        var ours = await StoreFor(FirstId).AddAsync(Details("Mesquite Flat"), CancellationToken.None);

        var found = await StoreFor(FirstId).FindAsync(ours.YardId, CancellationToken.None);

        found.Should().NotBeNull();
        found!.Details.Should().Be(Details("Mesquite Flat"));
    }

    [Fact] // regression risk: an update replaces every field and persists it
    public async Task UpdateAsync_ShouldReplaceTheDetails_WhenTheYardBelongsToTheCurrentAccount()
    {
        var ours = await StoreFor(FirstId).AddAsync(Details("Mesquite Flat"), CancellationToken.None);
        var changed = Details("Mesquite Flat North") with
        {
            Landowner = Details("x").Landowner with { Terms = LandownerTerms.Fee, SprayContact = null },
        };

        var updated = await StoreFor(FirstId).UpdateAsync(ours.YardId, changed, CancellationToken.None);

        updated!.Details.Should().Be(changed);
        var reread = await StoreFor(FirstId).FindAsync(ours.YardId, CancellationToken.None);
        reread!.Details.Should().Be(changed);
    }

    [Fact] // invariant: another account's yard cannot be changed, and stays as it was
    public async Task UpdateAsync_ShouldReturnNullAndChangeNothing_WhenTheYardBelongsToAnotherAccount()
    {
        var theirs = await StoreFor(SecondId).AddAsync(Details("Pecan Bottom"), CancellationToken.None);

        var updated = await StoreFor(FirstId).UpdateAsync(theirs.YardId, Details("Taken Over"), CancellationToken.None);

        updated.Should().BeNull();
        var reread = await StoreFor(SecondId).FindAsync(theirs.YardId, CancellationToken.None);
        reread!.Details.Name.Should().Be("Pecan Bottom");
    }

    private static YardDetails Details(string name) => new()
    {
        Name = name,
        Location = new GeoLocation { Latitude = 30.2672, Longitude = -97.7431 },
        State = UsState.Texas,
        County = "Travis",
        AccessNotes = "Gate code from the landowner; north pasture track.",
        ForageNotes = "Huisache in February, mesquite in May.",
        Landowner = new Landowner
        {
            Contact = "Synthetic Landowner, 555-0100",
            Terms = LandownerTerms.Honey,
            NearbyCrops = "Cotton to the east.",
            SprayContact = "Synthetic Crop Duster, 555-0101",
        },
    };

    private EfCoreYardStore StoreFor(AccountId? current) => new(CreateContext(current), CurrentAccount(current));

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
