using ApiaryCopilot.Domain;
using ApiaryCopilot.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ApiaryCopilot.UnitTests.Infrastructure;

// An invited user's row, written when the invitation is made: in the inviting caller's account, with the Keycloak user
// id the invitation created as its ExternalSubject, so the user acts as this row from its first sign-in - over the
// in-memory provider under the real model and its account filter (CONVENTIONS.md §8.1).
public sealed class EfCoreUserStoreTests
{
    private const string KeycloakUserId = "5b0b3a1e-6f4c-4d9e-8a7b-1c2d3e4f5a6b";
    private static readonly AccountId Account = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly AccountId OtherAccount = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));

    private readonly string _databaseName = Guid.NewGuid().ToString();

    [Fact] // invariant: the row holds the Keycloak user id as its subject from the start, in the caller's account
    public async Task AddInvitedAsync_ShouldRecordTheUserWithItsSubject_InTheCurrentAccount()
    {
        await using (var db = Context(Account))
        {
            var user = await new EfCoreUserStore(db, CurrentAccount(Account)).AddInvitedAsync("Synthetic Helper", KeycloakUserId, CancellationToken.None);

            user.AccountId.Should().Be(Account);
            user.ExternalSubject.Should().Be(KeycloakUserId);
            user.Name.Should().Be("Synthetic Helper");
        }

        await using var read = Context(Account);
        var stored = await read.Users.SingleAsync();
        stored.ExternalSubject.Should().Be(KeycloakUserId);
        stored.AccountId.Should().Be(Account);
    }

    [Fact] // invariant (NFR-8): another account never sees the row
    public async Task AddInvitedAsync_ShouldWriteARowOtherAccountsCannotSee_WhenCalled()
    {
        await using (var db = Context(Account))
        {
            await new EfCoreUserStore(db, CurrentAccount(Account)).AddInvitedAsync("Synthetic Helper", KeycloakUserId, CancellationToken.None);
        }

        await using var other = Context(OtherAccount);
        (await other.Users.AnyAsync()).Should().BeFalse();
    }

    [Fact] // boundary: with no current account nothing is written
    public async Task AddInvitedAsync_ShouldThrow_WhenNoAccountIsCurrent()
    {
        await using var db = Context(null);

        var add = () => new EfCoreUserStore(db, CurrentAccount(null)).AddInvitedAsync("Synthetic Helper", KeycloakUserId, CancellationToken.None);

        await add.Should().ThrowAsync<InvalidOperationException>();
    }

    private ApiaryCopilotDbContext Context(AccountId? account) =>
        new(new DbContextOptionsBuilder<ApiaryCopilotDbContext>().UseInMemoryDatabase(_databaseName).Options,
            CurrentAccount(account), TestUsers.Current(TestUsers.Acting), TimeProvider.System);

    private static ICurrentAccount CurrentAccount(AccountId? account)
    {
        var current = A.Fake<ICurrentAccount>();
        A.CallTo(() => current.AccountId).Returns(account);
        return current;
    }
}
