using ApiaryCopilot.Domain;
using Microsoft.EntityFrameworkCore;

namespace ApiaryCopilot.Infrastructure;

// Reads go through the account filter, so another account's colony or inspection is simply not found; the
// one write takes its account from the caller's and refuses a colony the filter does not show.
internal sealed class EfCoreInspectionStore(ApiaryCopilotDbContext db, ICurrentAccount currentAccount, TimeProvider clock) : IInspectionStore
{
    public async Task<Inspection?> AddAsync(ColonyId colonyId, InspectionDetails details, CancellationToken cancellationToken)
    {
        var accountId = currentAccount.AccountId
            ?? throw new InvalidOperationException("No current account: an inspection cannot be written.");
        if (!await ColonyExistsAsync(colonyId, cancellationToken))
        {
            return null;
        }

        var inspection = new Inspection
        {
            InspectionId = InspectionId.New(),
            AccountId = accountId,
            ColonyId = colonyId,
            InspectedOn = details.InspectedOn,
            RecordedAt = clock.GetUtcNow(),
            Findings = details.Findings,
            Notes = details.Notes,
        };
        db.Inspections.Add(inspection);
        await db.SaveChangesAsync(cancellationToken);
        return inspection;
    }

    public async Task<IReadOnlyList<Inspection>?> ListAsync(ColonyId colonyId, int skip, int take, CancellationToken cancellationToken)
    {
        if (!await ColonyExistsAsync(colonyId, cancellationToken))
        {
            return null;
        }

        return await db.Inspections
            .AsNoTracking()
            .Where(i => i.ColonyId == colonyId)
            .OrderByDescending(i => i.InspectedOn)
            .ThenByDescending(i => i.RecordedAt)
            .ThenByDescending(i => i.InspectionId)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public Task<Inspection?> FindAsync(InspectionId inspectionId, CancellationToken cancellationToken) =>
        db.Inspections
            .AsNoTracking()
            .SingleOrDefaultAsync(i => i.InspectionId == inspectionId, cancellationToken);

    private Task<bool> ColonyExistsAsync(ColonyId colonyId, CancellationToken cancellationToken) =>
        db.Colonies.AnyAsync(c => c.ColonyId == colonyId, cancellationToken);
}
