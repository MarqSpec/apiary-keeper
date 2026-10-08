using ApiaryCopilot.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ApiaryCopilot.Infrastructure;

// Reads go through the account filter, so another account's inspection or media is simply not found; the one
// write takes its account from the caller's and refuses a key the caller was not issued.
internal sealed class EfCoreInspectionMediaStore(ApiaryCopilotDbContext db, ICurrentAccount currentAccount, TimeProvider clock) : IInspectionMediaStore
{
    public Task<bool> InspectionExistsAsync(InspectionId inspectionId, CancellationToken cancellationToken) =>
        db.Inspections.AnyAsync(i => i.InspectionId == inspectionId, cancellationToken);

    public async Task<MediaAttachResult> AttachAsync(InspectionId inspectionId, MediaObjectKey key, StoredObject stored, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(stored);
        var accountId = currentAccount.AccountId
            ?? throw new InvalidOperationException("No current account: media cannot be attached.");
        if (!await InspectionExistsAsync(inspectionId, cancellationToken))
        {
            return MediaAttachResult.Refused(MediaAttachOutcome.NoSuchInspection);
        }

        if (!key.BelongsTo(accountId, inspectionId))
        {
            return MediaAttachResult.Refused(MediaAttachOutcome.KeyNotForThisInspection);
        }

        if (await db.InspectionMedia.AnyAsync(m => m.ObjectKey == key, cancellationToken))
        {
            return MediaAttachResult.Refused(MediaAttachOutcome.AlreadyAttached);
        }

        var media = new InspectionMedia
        {
            MediaId = MediaId.New(),
            AccountId = accountId,
            InspectionId = inspectionId,
            ObjectKey = key,
            ContentType = stored.ContentType,
            SizeBytes = stored.SizeBytes,
            AttachedAt = clock.GetUtcNow(),
        };
        db.InspectionMedia.Add(media);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (IsKeyTaken(e))
        {
            // Another attach of the same key won after the pre-check read it free: the same refusal, not a 500.
            return MediaAttachResult.Refused(MediaAttachOutcome.AlreadyAttached);
        }

        return MediaAttachResult.Attached(media);
    }

    public async Task<IReadOnlyList<InspectionMedia>?> ListAsync(InspectionId inspectionId, int skip, int take, CancellationToken cancellationToken)
    {
        if (!await InspectionExistsAsync(inspectionId, cancellationToken))
        {
            return null;
        }

        return await db.InspectionMedia
            .AsNoTracking()
            .Where(m => m.InspectionId == inspectionId)
            .OrderBy(m => m.AttachedAt)
            .ThenBy(m => m.MediaId)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public Task<InspectionMedia?> FindAsync(MediaId mediaId, CancellationToken cancellationToken) =>
        db.InspectionMedia
            .AsNoTracking()
            .SingleOrDefaultAsync(m => m.MediaId == mediaId, cancellationToken);

    private static bool IsKeyTaken(DbUpdateException e) =>
        e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: InspectionMediaModelExtensions.ObjectKeyIndexName };
}
