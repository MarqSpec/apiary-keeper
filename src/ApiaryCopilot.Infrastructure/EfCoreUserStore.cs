using ApiaryCopilot.Domain;

namespace ApiaryCopilot.Infrastructure;

// The account is the caller's, never one the request names.
internal sealed class EfCoreUserStore(ApiaryCopilotDbContext db, ICurrentAccount currentAccount) : IUserStore
{
    public async Task<User> AddInvitedAsync(string name, string externalSubject, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalSubject);
        var accountId = currentAccount.AccountId
            ?? throw new InvalidOperationException("No current account: a user cannot be written.");
        var user = new User
        {
            UserId = new UserId(Guid.CreateVersion7()),
            AccountId = accountId,
            Name = name,
            ExternalSubject = externalSubject,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return user;
    }
}
