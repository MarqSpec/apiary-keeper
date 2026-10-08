using ApiaryCopilot.Domain;
using Microsoft.EntityFrameworkCore;

namespace ApiaryCopilot.Infrastructure;

// Reads go through the account filter; the one write that names an account takes it from the caller's.
internal sealed class EfCoreYardStore(ApiaryCopilotDbContext db, ICurrentAccount currentAccount) : IYardStore
{
    public async Task<Yard> AddAsync(YardDetails details, CancellationToken cancellationToken)
    {
        var accountId = currentAccount.AccountId
            ?? throw new InvalidOperationException("No current account: a yard cannot be written.");
        var yard = new Yard { YardId = YardId.New(), AccountId = accountId, Details = details };
        db.Yards.Add(yard);
        await db.SaveChangesAsync(cancellationToken);
        return yard;
    }

    public async Task<IReadOnlyList<Yard>> ListAsync(int skip, int take, CancellationToken cancellationToken) =>
        await db.Yards
            .AsNoTracking()
            .OrderBy(y => y.Details.Name)
            .ThenBy(y => y.YardId)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

    public Task<Yard?> FindAsync(YardId yardId, CancellationToken cancellationToken) =>
        db.Yards
            .AsNoTracking()
            .SingleOrDefaultAsync(y => y.YardId == yardId, cancellationToken);

    public async Task<Yard?> UpdateAsync(YardId yardId, YardDetails details, CancellationToken cancellationToken)
    {
        var yard = await db.Yards.SingleOrDefaultAsync(y => y.YardId == yardId, cancellationToken);
        if (yard is null)
        {
            return null;
        }

        yard.Details = details;
        await db.SaveChangesAsync(cancellationToken);
        return yard;
    }
}
