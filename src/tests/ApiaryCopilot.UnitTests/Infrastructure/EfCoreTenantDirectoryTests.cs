using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ApiaryCopilot.UnitTests.Infrastructure;

// The signed-in caller resolved against the database: its token's account must have a row, and the user it acts as is
// the one of that account whose ExternalSubject is the token's subject - over the in-memory provider under the real model
// and its account filter (CONVENTIONS.md §8.1); the relational path is the integration tier's.
public sealed class EfCoreTenantDirectoryTests
{
    private const string FirstSubject = "4e97dd9a-9f79-4493-8185-795ac0034b7d";
    private const string SecondSubject = "b2c1a6f0-1d4e-4a8b-9c3f-0e5d7a2b6c91";
    private static readonly AccountId FirstId = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly AccountId SecondId = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));
    private static readonly UserId FirstUser = new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly UserId SecondUser = new(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));

    private readonly string _databaseName = Guid.NewGuid().ToString();

    [Fact] // invariant: the caller acts as the user of its account that holds its subject
    public async Task FindActingUserAsync_ShouldReturnTheUser_WhenOneOfTheAccountsUsersHasTheSubject()
    {
        await SeedAccountAsync(FirstId, FirstUser, FirstSubject);

        var lookup = await DirectoryFor(FirstId).FindActingUserAsync(FirstSubject, CancellationToken.None);

        lookup.Should().Be(TenantLookup.Ready(FirstUser));
    }

    [Fact] // regression risk: an account with no row answered 200 or 500 instead of failing closed
    public async Task FindActingUserAsync_ShouldReportTheAccountMissing_WhenTheTokensAccountHasNoRow()
    {
        await SeedAccountAsync(SecondId, SecondUser, FirstSubject);

        var lookup = await DirectoryFor(FirstId).FindActingUserAsync(FirstSubject, CancellationToken.None);

        lookup.Should().Be(TenantLookup.AccountMissing);
    }

    [Fact] // boundary: with no current account (no signed-in caller) nothing is ready, so the lookup fails closed
    public async Task FindActingUserAsync_ShouldReportTheAccountMissing_WhenNoAccountIsCurrent()
    {
        await SeedAccountAsync(FirstId, FirstUser, FirstSubject);

        var lookup = await DirectoryFor(null).FindActingUserAsync(FirstSubject, CancellationToken.None);

        lookup.Should().Be(TenantLookup.AccountMissing);
    }

    [Fact] // invariant: an unknown subject is refused - no user of the account has signed in as it
    public async Task FindActingUserAsync_ShouldReportTheSubjectUnknown_WhenNoUserHasIt()
    {
        await SeedAccountAsync(FirstId, FirstUser, FirstSubject);

        var lookup = await DirectoryFor(FirstId).FindActingUserAsync(SecondSubject, CancellationToken.None);

        lookup.Should().Be(TenantLookup.SubjectUnknown);
    }

    [Fact] // regression risk: a user of another account was accepted and stamped onto this account's rows
    public async Task FindActingUserAsync_ShouldReportTheSubjectUnknown_WhenTheSubjectsUserBelongsToAnotherAccount()
    {
        await SeedAccountAsync(FirstId, FirstUser, FirstSubject);
        await SeedAccountAsync(SecondId, SecondUser, SecondSubject);

        var lookup = await DirectoryFor(FirstId).FindActingUserAsync(SecondSubject, CancellationToken.None);

        lookup.Should().Be(TenantLookup.SubjectUnknown);
    }

    [Fact] // boundary: a user who has not signed in yet (no subject) is no one's acting user
    public async Task FindActingUserAsync_ShouldReportTheSubjectUnknown_WhenTheUserHasNoSubjectYet()
    {
        await SeedAccountAsync(FirstId, FirstUser, null);

        var lookup = await DirectoryFor(FirstId).FindActingUserAsync(FirstSubject, CancellationToken.None);

        lookup.Should().Be(TenantLookup.SubjectUnknown);
    }

    [Theory] // invariant: the subject is compared exactly - never by case, never blank
    [InlineData("4E97DD9A-9F79-4493-8185-795AC0034B7D")]
    [InlineData(" 4e97dd9a-9f79-4493-8185-795ac0034b7d")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task FindActingUserAsync_ShouldReportTheSubjectUnknown_WhenTheSubjectDoesNotMatchExactly(string subject)
    {
        await SeedAccountAsync(FirstId, FirstUser, FirstSubject);

        var lookup = await DirectoryFor(FirstId).FindActingUserAsync(subject, CancellationToken.None);

        lookup.Should().Be(TenantLookup.SubjectUnknown);
    }

    private async Task SeedAccountAsync(AccountId accountId, UserId userId, string? subject)
    {
        await using var db = CreateContext(accountId);
        db.Accounts.Add(new Account { AccountId = accountId, Name = "Synthetic Apiary" });
        db.Users.Add(new User { UserId = userId, AccountId = accountId, Name = "Synthetic Beekeeper", ExternalSubject = subject });
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private EfCoreTenantDirectory DirectoryFor(AccountId? account) => new(CreateContext(account), CurrentAccount(account));

    private ApiaryCopilotDbContext CreateContext(AccountId? account)
    {
        var options = new DbContextOptionsBuilder<ApiaryCopilotDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options;
        return new ApiaryCopilotDbContext(options, CurrentAccount(account), TestUsers.Current(TestUsers.Acting), TimeProvider.System);
    }

    private static ICurrentAccount CurrentAccount(AccountId? account)
    {
        var currentAccount = A.Fake<ICurrentAccount>();
        A.CallTo(() => currentAccount.AccountId).Returns(account);
        return currentAccount;
    }
}
