using Microsoft.EntityFrameworkCore;

namespace ApiaryCopilot.Infrastructure;

internal sealed class EfCoreTenantDirectory(ApiaryCopilotDbContext db, ICurrentAccount currentAccount) : ITenantDirectory
{
    // Both reads go through the account filter, so a user row of another account - its subject included - reads as absent.
    public async Task<TenantLookup> FindActingUserAsync(string subject, CancellationToken cancellationToken)
    {
        if (currentAccount.AccountId is not { } accountId
            || !await db.Accounts.AnyAsync(a => a.AccountId == accountId, cancellationToken))
        {
            return TenantLookup.AccountMissing;
        }

        if (string.IsNullOrWhiteSpace(subject))
        {
            return TenantLookup.SubjectUnknown;
        }

        // The subject is unique across accounts (a unique index), so at most one row matches; compared exactly.
        var users = await db.Users.Where(u => u.ExternalSubject == subject).Select(u => u.UserId).Take(2).ToListAsync(cancellationToken);
        return users.Count == 1 ? TenantLookup.Ready(users[0]) : TenantLookup.SubjectUnknown;
    }
}
