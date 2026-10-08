using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ApiaryCopilot.UnitTests.Infrastructure;

// Tenant isolation (NFR-8, ARCHITECTURE.md D-8). The in-memory provider applies global query filters
// exactly as the relational one does; relational behaviour is the integration tier's (CONVENTIONS.md §8.1).
public sealed class ApiaryCopilotDbContextTests
{
    private static readonly AccountId FirstId = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly AccountId SecondId = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));

    private static readonly UserId OtherUser = new(Guid.Parse("44444444-4444-4444-8444-444444444444"));
    private static readonly DateTimeOffset Created = new(2026, 4, 12, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 4, 13, 16, 5, 0, TimeSpan.Zero);

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly TimeProvider _clock = A.Fake<TimeProvider>();

    public ApiaryCopilotDbContextTests() => A.CallTo(() => _clock.GetUtcNow()).Returns(Created);

    [Fact] // invariant: every entity's rows are visible to their own account only - all of them, not only accounts
    public async Task EveryEntitySet_ShouldReturnOnlyTheCurrentAccountsRows_WhenBothAccountsHaveRows()
    {
        await SeedEveryEntityAsync(FirstId);
        await SeedEveryEntityAsync(SecondId);
        await using var db = CreateContext(FirstId);

        var seen = EntityTypes(db).ToDictionary(t => t.Name, t => VisibleRows(db, t));

        seen.Keys.Should().BeEquivalentTo(EntityTypes(db).Select(t => t.Name));
        seen.Should().AllSatisfy(set => set.Value.Should().ContainSingle(
            $"the seed writes one {set.Key} per account and the filter must leave only the first account's")
            .Which.AccountId.Should().Be(FirstId));
    }

    [Fact] // boundary: the seed above covers every entity type, so a new one cannot escape that test unseeded
    public async Task SeedEveryEntity_ShouldWriteARowOfEveryEntityType_WhenRun()
    {
        await SeedEveryEntityAsync(FirstId);
        await using var db = CreateContext(FirstId);

        EntityTypes(db).Should().AllSatisfy(t => VisibleRows(db, t).Should().NotBeEmpty(t.Name));
    }

    [Fact] // invariant: every apiary entity carries the row stamps and their foreign keys to the user who wrote it
    public void Model_ShouldStampEveryApiaryEntityType_WhenTheModelIsBuilt()
    {
        using var db = CreateContext(FirstId);

        var apiary = db.Model.GetEntityTypes()
            .Where(t => !t.IsOwned() && t.ClrType != typeof(Account) && t.ClrType != typeof(User))
            .ToList();

        apiary.Should().HaveCount(6);
        apiary.Should().AllSatisfy(t =>
        {
            t.ClrType.Should().BeAssignableTo<IRowStamped>();
            t.GetForeignKeys().Where(fk => fk.PrincipalEntityType.ClrType == typeof(User))
                .Select(fk => fk.Properties.Single().Name)
                .Should().BeEquivalentTo(nameof(IRowStamped.CreatedBy), nameof(IRowStamped.LastModifiedBy));
        });
    }

    [Fact] // invariant: a user's Keycloak subject is optional (set when the invitation is accepted) and maps to one user only
    public void Model_ShouldGiveUsersAnOptionalUniqueExternalSubject_WhenTheModelIsBuilt()
    {
        using var db = CreateContext(FirstId);

        var users = db.Model.FindEntityType(typeof(User))!;
        var subject = users.FindProperty(nameof(User.ExternalSubject))!;

        subject.IsNullable.Should().BeTrue("a user invited but not yet signed in has none, and no backfill is invented");
        subject.GetMaxLength().Should().Be(User.ExternalSubjectMaxLength);
        users.GetIndexes().Should().ContainSingle(i => i.Properties.Count == 1 && i.Properties[0] == subject)
            .Which.IsUnique.Should().BeTrue("one sign-in must never act as two users, in this account or another");
        User.ExternalSubjectMaxLength.Should().Be(255, "OpenID Connect caps sub at 255 ASCII characters");
    }

    [Theory] // invariant: a new row is stamped with the acting user and the clock, whichever save is called
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveChanges_ShouldStampTheActingUserAndTheClock_WhenARowIsAdded(bool async)
    {
        await using (var db = CreateContext(FirstId))
        {
            db.Yards.Add(NewYard(FirstId, "Creek Yard"));
            await Save(db, async);
        }

        await using var reread = CreateContext(FirstId);
        var stored = await reread.Yards.SingleAsync(CancellationToken.None);
        stored.CreatedBy.Should().Be(TestUsers.Acting);
        stored.CreatedAt.Should().Be(Created);
        stored.LastModifiedBy.Should().Be(TestUsers.Acting);
        stored.LastModifiedAt.Should().Be(Created);
    }

    [Fact] // invariant: an append-only row is stamped too (it is only ever added)
    public async Task SaveChanges_ShouldStampTheEventAndItsColony_WhenAnEventIsRecorded()
    {
        await RecordEventAsync();

        await using var db = CreateContext(FirstId);
        (await db.ColonyEvents.SingleAsync(CancellationToken.None)).CreatedBy.Should().Be(TestUsers.Acting);
        (await db.Colonies.SingleAsync(CancellationToken.None)).CreatedAt.Should().Be(Created);
    }

    [Theory] // invariant: a change moves the last-modified stamps to the changing user and time, never the created ones
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveChanges_ShouldMoveOnlyTheLastModifiedStamps_WhenARowIsChanged(bool async)
    {
        await RecordEventAsync();
        A.CallTo(() => _clock.GetUtcNow()).Returns(Later);
        await using (var db = CreateContext(FirstId, OtherUser))
        {
            (await db.Colonies.SingleAsync(CancellationToken.None)).Status = ColonyStatus.Sold;
            await Save(db, async);
        }

        await using var reread = CreateContext(FirstId);
        var stored = await reread.Colonies.SingleAsync(CancellationToken.None);
        stored.CreatedBy.Should().Be(TestUsers.Acting);
        stored.CreatedAt.Should().Be(Created);
        stored.LastModifiedBy.Should().Be(OtherUser);
        stored.LastModifiedAt.Should().Be(Later);
    }

    [Fact] // regression risk: replacing an owned part (a yard's details) leaves the yard's own entry Unchanged
    public async Task SaveChanges_ShouldMoveTheLastModifiedStamps_WhenOnlyAnOwnedPartIsReplaced()
    {
        await using (var db = CreateContext(FirstId))
        {
            db.Yards.Add(NewYard(FirstId, "Creek Yard"));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        A.CallTo(() => _clock.GetUtcNow()).Returns(Later);
        await using (var db = CreateContext(FirstId, OtherUser))
        {
            var yard = await db.Yards.SingleAsync(CancellationToken.None);
            yard.Details = yard.Details with { Name = "Pecan Bottom" };
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using var reread = CreateContext(FirstId);
        var stored = await reread.Yards.SingleAsync(CancellationToken.None);
        stored.Details.Name.Should().Be("Pecan Bottom");
        stored.CreatedBy.Should().Be(TestUsers.Acting);
        stored.LastModifiedBy.Should().Be(OtherUser);
        stored.LastModifiedAt.Should().Be(Later);
    }

    [Fact] // invariant: a created stamp set by hand is put back on save; only the context writes the stamps
    public async Task SaveChanges_ShouldKeepTheCreatedStamps_WhenAChangeTriesToRewriteThem()
    {
        await RecordEventAsync();
        await using (var db = CreateContext(FirstId, OtherUser))
        {
            var colony = await db.Colonies.SingleAsync(CancellationToken.None);
            colony.Status = ColonyStatus.Sold;
            db.Entry(colony).Property(nameof(IRowStamped.CreatedBy)).CurrentValue = OtherUser;
            db.Entry(colony).Property(nameof(IRowStamped.CreatedAt)).CurrentValue = Later;
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using var reread = CreateContext(FirstId);
        var stored = await reread.Colonies.SingleAsync(CancellationToken.None);
        stored.CreatedBy.Should().Be(TestUsers.Acting);
        stored.CreatedAt.Should().Be(Created);
    }

    [Fact] // invariant: reading - and saving with nothing changed - leaves every stamp as it was
    public async Task SaveChanges_ShouldLeaveTheStamps_WhenRowsAreOnlyRead()
    {
        await RecordEventAsync();
        A.CallTo(() => _clock.GetUtcNow()).Returns(Later);
        await using (var db = CreateContext(FirstId, OtherUser))
        {
            _ = await db.Colonies.ToListAsync(CancellationToken.None);
            _ = await db.ColonyEvents.ToListAsync(CancellationToken.None);
            (await db.SaveChangesAsync(CancellationToken.None)).Should().Be(0);
        }

        await using var reread = CreateContext(FirstId);
        var stored = await reread.Colonies.SingleAsync(CancellationToken.None);
        stored.LastModifiedBy.Should().Be(TestUsers.Acting);
        stored.LastModifiedAt.Should().Be(Created);
    }

    [Theory] // boundary: with no current user nothing is written (fails closed), whichever save is called
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveChanges_ShouldThrowAndWriteNothing_WhenNoUserIsCurrent(bool async)
    {
        await using (var db = CreateContext(FirstId, null))
        {
            db.Yards.Add(NewYard(FirstId, "Creek Yard"));

            var act = () => Save(db, async);

            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("No current user");
        }

        await using var reread = CreateContext(FirstId);
        (await reread.Yards.AnyAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact] // boundary: a save with nothing to stamp needs no user
    public async Task SaveChanges_ShouldSaveNothingWithoutThrowing_WhenNoUserIsCurrentAndNothingChanged()
    {
        await using var db = CreateContext(FirstId, null);

        (await db.SaveChangesAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact] // invariant: one account never reads another's rows
    public async Task Accounts_ShouldReturnOnlyTheCurrentAccount_WhenAnotherAccountHasRows()
    {
        await SeedBothAccountsAsync();
        await using var db = CreateContext(FirstId);

        var visible = await db.Accounts.ToListAsync(CancellationToken.None);

        visible.Select(o => o.AccountId).Should().Equal(FirstId);
    }

    [Fact] // invariant: a lookup by another account's id finds nothing, so the caller sees NotFound (§4.3)
    public async Task Accounts_ShouldReturnNothing_WhenQueriedByAnotherAccountsId()
    {
        await SeedBothAccountsAsync();
        await using var db = CreateContext(FirstId);

        var found = await db.Accounts
            .Where(o => o.AccountId == SecondId)
            .SingleOrDefaultAsync(CancellationToken.None);

        found.Should().BeNull();
    }

    [Fact] // boundary: with no current account the filter fails closed
    public async Task Accounts_ShouldReturnNothing_WhenNoAccountIsCurrent()
    {
        await SeedBothAccountsAsync();
        await using var db = CreateContext(null);

        var visible = await db.Accounts.ToListAsync(CancellationToken.None);

        visible.Should().BeEmpty();
    }

    [Fact] // regression risk: the model is cached per context type, so the filter must read each context's own account
    public async Task Accounts_ShouldReturnEachContextsOwnAccount_WhenTwoContextsShareTheCachedModel()
    {
        await SeedBothAccountsAsync();
        await using var first = CreateContext(FirstId);
        await using var second = CreateContext(SecondId);

        var seenByFirst = await first.Accounts.ToListAsync(CancellationToken.None);
        var seenBySecond = await second.Accounts.ToListAsync(CancellationToken.None);

        seenByFirst.Select(o => o.AccountId).Should().Equal(FirstId);
        seenBySecond.Select(o => o.AccountId).Should().Equal(SecondId);
    }

    [Fact] // invariant: an entity added without AccountId or without the filter is unscoped
    public void Model_ShouldFilterEveryEntityTypeByAccount_WhenTheModelIsBuilt()
    {
        using var db = CreateContext(FirstId);

        var entityTypes = db.Model.GetEntityTypes().Where(t => !t.IsOwned()).ToList();

        entityTypes.Should().NotBeEmpty();
        entityTypes.Should().AllSatisfy(t =>
        {
            t.ClrType.Should().BeAssignableTo<IAccountOwned>();
            t.GetDeclaredQueryFilters().Should().NotBeEmpty();
        });
    }

    [Fact] // invariant: nothing in production code switches the tenant filter off (NFR-8, CONVENTIONS.md §11)
    public void IgnoreQueryFilters_ShouldBeCalledNowhere_WhenProductionAssembliesAreScanned()
    {
        var production = new[]
        {
            typeof(Account).Assembly,
            typeof(ApiaryCopilotDbContext).Assembly,
            typeof(Program).Assembly,
        };

        var scan = MethodCallScanner.FindCallsTo(nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters), production);

        scan.MethodsScannedByAssembly.Should().HaveCount(production.Length)
            .And.AllSatisfy(scanned => scanned.Value.Should().BePositive());
        scan.Callers.Should().BeEmpty();
    }

    [Theory] // invariant: a recorded lineage event is never deleted, whichever save is called (FR-6, D-2)
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveChanges_ShouldRefuseAndKeepTheEvent_WhenARecordedEventIsDeleted(bool async)
    {
        var recorded = await RecordEventAsync();
        await using var db = CreateContext(FirstId);
        db.ColonyEvents.Remove(await db.ColonyEvents.SingleAsync(CancellationToken.None));

        var act = () => Save(db, async);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("append-only");
        await using var reread = CreateContext(FirstId);
        (await reread.ColonyEvents.SingleAsync(CancellationToken.None)).EventId.Should().Be(recorded);
    }

    [Theory] // invariant: a recorded lineage event is never changed, whichever save is called (FR-6, D-2)
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveChanges_ShouldRefuseAndKeepTheEvent_WhenARecordedEventIsModified(bool async)
    {
        await RecordEventAsync();
        await using var db = CreateContext(FirstId);
        var stored = await db.ColonyEvents.SingleAsync(CancellationToken.None);
        db.Entry(stored).Property(e => e.OccurredOn).CurrentValue = new DateOnly(2020, 1, 1);

        var act = () => Save(db, async);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await using var reread = CreateContext(FirstId);
        (await reread.ColonyEvents.SingleAsync(CancellationToken.None)).OccurredOn.Should().Be(new DateOnly(2026, 4, 12));
    }

    [Fact] // boundary: appending an event, and changing the colonies it projects into, is still allowed
    public async Task SaveChanges_ShouldSave_WhenAnEventIsAddedAndAColonyChanged()
    {
        await RecordEventAsync();
        await using var db = CreateContext(FirstId);
        var colony = await db.Colonies.SingleAsync(CancellationToken.None);
        colony.Status = ColonyStatus.Sold;
        db.ColonyEvents.Add(NewEvent(colony.ColonyId, new ColonyMoved { FromYardId = colony.YardId, ToYardId = YardId.New() }));

        var saved = await db.SaveChangesAsync(CancellationToken.None);

        saved.Should().Be(2);
    }

    [Theory] // invariant: nothing in production code bulk-updates or bulk-deletes, which would bypass the append-only check
    [InlineData(nameof(EntityFrameworkQueryableExtensions.ExecuteDelete))]
    [InlineData(nameof(EntityFrameworkQueryableExtensions.ExecuteDeleteAsync))]
    [InlineData(nameof(EntityFrameworkQueryableExtensions.ExecuteUpdate))]
    [InlineData(nameof(EntityFrameworkQueryableExtensions.ExecuteUpdateAsync))]
    public void BulkWrites_ShouldBeCalledNowhere_WhenProductionAssembliesAreScanned(string method)
    {
        var production = new[]
        {
            typeof(Account).Assembly,
            typeof(ApiaryCopilotDbContext).Assembly,
            typeof(Program).Assembly,
        };

        var scan = MethodCallScanner.FindCallsTo(method, production);

        scan.MethodsScannedByAssembly.Should().HaveCount(production.Length)
            .And.AllSatisfy(scanned => scanned.Value.Should().BePositive());
        scan.Callers.Should().BeEmpty();
    }

    private static Task<int> Save(ApiaryCopilotDbContext db, bool async) =>
        async ? db.SaveChangesAsync(CancellationToken.None) : Task.FromResult(db.SaveChanges());

    // A registered colony and its event, written as the store writes them.
    private async Task<ColonyEventId> RecordEventAsync()
    {
        await using var db = CreateContext(FirstId);
        var registered = NewEvent(ColonyId.New(), new ColonyRegistered
        {
            Colony = new ColonyProfile { TagId = "A-01", HiveNumber = "TX-0412", YardId = YardId.New(), HiveType = "Langstroth 10-frame", BoxCount = 2 },
        });
        db.ColonyEvents.Add(registered);
        db.Colonies.Add(ColonyProjection.Apply(registered, null).AddedColony!);
        await db.SaveChangesAsync(CancellationToken.None);
        return registered.EventId;
    }

    private static ColonyEvent NewEvent(ColonyId colonyId, ColonyEventData data) =>
        new(ColonyEventId.New(), FirstId, colonyId, new DateOnly(2026, 4, 12), DateTimeOffset.UnixEpoch, data);

    private static IEnumerable<Type> EntityTypes(ApiaryCopilotDbContext db) =>
        db.Model.GetEntityTypes().Where(t => !t.IsOwned()).Select(t => t.ClrType);

    private static List<IAccountOwned> VisibleRows(ApiaryCopilotDbContext db, Type entityType)
    {
        var set = (IQueryable)typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!
            .MakeGenericMethod(entityType).Invoke(db, null)!;
        return set.Cast<IAccountOwned>().ToList();
    }

    // One row of every entity type for the account, its user, a yard, a colony with its event and queen, an
    // inspection and a photo, written as the stores write them.
    private async Task SeedEveryEntityAsync(AccountId account)
    {
        await using var db = CreateContext(account);
        var colonyId = ColonyId.New();
        var inspectionId = InspectionId.New();
        db.Accounts.Add(new Account { AccountId = account, Name = $"Synthetic Apiary {account}" });
        db.Users.Add(new User { UserId = new UserId(Guid.NewGuid()), AccountId = account, Name = "Synthetic Beekeeper" });
        var yard = NewYard(account, "Creek Yard");
        db.Yards.Add(yard);
        var registered = new ColonyEvent(ColonyEventId.New(), account, colonyId, new DateOnly(2026, 4, 12), Created, new ColonyRegistered
        {
            Colony = new ColonyProfile { TagId = "A-01", HiveNumber = "TX-0412", YardId = yard.YardId, HiveType = "Langstroth 10-frame", BoxCount = 2 },
            Queen = new NewQueen { QueenId = QueenId.New(), Details = new QueenDetails { Source = "Synthetic breeder", IntroducedOn = new DateOnly(2026, 4, 1) } },
        });
        var projected = ColonyProjection.Apply(registered, null);
        db.ColonyEvents.Add(registered);
        db.Colonies.Add(projected.AddedColony!);
        db.Queens.Add(projected.AddedQueen!);
        db.Inspections.Add(new Inspection
        {
            InspectionId = inspectionId,
            AccountId = account,
            ColonyId = colonyId,
            InspectedOn = new DateOnly(2026, 4, 12),
            RecordedAt = Created,
            Findings = InspectionFindings.Empty,
        });
        db.InspectionMedia.Add(new InspectionMedia
        {
            MediaId = MediaId.New(),
            AccountId = account,
            InspectionId = inspectionId,
            ObjectKey = MediaObjectKey.New(account, inspectionId),
            ContentType = "image/jpeg",
            SizeBytes = 1024,
            AttachedAt = Created,
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static Yard NewYard(AccountId account, string name) => new()
    {
        YardId = YardId.New(),
        AccountId = account,
        Details = new YardDetails
        {
            Name = name,
            Location = new GeoLocation { Latitude = 30.2672, Longitude = -97.7431 },
            State = UsState.Texas,
            County = "Travis",
            Landowner = new Landowner { Contact = "Synthetic Landowner, 555-0100", Terms = LandownerTerms.Honey },
        },
    };

    private ApiaryCopilotDbContext CreateContext(AccountId? current) => CreateContext(current, TestUsers.Acting);

    private ApiaryCopilotDbContext CreateContext(AccountId? current, UserId? user)
    {
        var currentAccount = A.Fake<ICurrentAccount>();
        A.CallTo(() => currentAccount.AccountId).Returns(current);
        var options = new DbContextOptionsBuilder<ApiaryCopilotDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options;
        return new ApiaryCopilotDbContext(options, currentAccount, TestUsers.Current(user), _clock);
    }

    private async Task SeedBothAccountsAsync()
    {
        await using var db = CreateContext(null);
        db.Accounts.AddRange(
            new Account { AccountId = FirstId, Name = "Synthetic Apiary One" },
            new Account { AccountId = SecondId, Name = "Synthetic Apiary Two" });
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
