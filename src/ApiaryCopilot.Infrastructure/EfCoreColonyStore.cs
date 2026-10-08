using ApiaryCopilot.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ApiaryCopilot.Infrastructure;

// Reads go through the account filter, so another account's colony or yard is simply not found. A write
// checks the rules, then appends the event and saves it with its projection in one SaveChanges; the colony's Version
// (and a merge's target's) makes a concurrent writer of the same colony fail instead of overwriting, and the tag's
// unique index turns a tag taken by a concurrent create into the same refusal the pre-check gives.
internal sealed class EfCoreColonyStore(ApiaryCopilotDbContext db, ICurrentAccount currentAccount, TimeProvider clock) : IColonyStore
{
    public Task<ColonyChangeResult> RegisterAsync(ColonyProfile colony, QueenDetails? queen, DateOnly occurredOn, CancellationToken cancellationToken) =>
        CreateAsync(colony, occurredOn, () => new ColonyRegistered { Colony = colony, Queen = Introduce(queen) }, cancellationToken);

    public Task<ColonyChangeResult> CaptureSwarmAsync(ColonyProfile colony, QueenDetails? queen, DateOnly occurredOn, CancellationToken cancellationToken) =>
        CreateAsync(colony, occurredOn, () => new SwarmCaptured { Colony = colony, Queen = Introduce(queen) }, cancellationToken);

    public async Task<ColonyChangeResult> SplitAsync(
        ColonyId parentColonyId, ColonyProfile newColony, QueenDetails? queen, DateOnly occurredOn, CancellationToken cancellationToken)
    {
        var accountId = RequireAccount();
        var parent = await LoadAsync(parentColonyId, cancellationToken);
        if (parent is null)
        {
            return ColonyChangeResult.NotFound;
        }

        var refusal = NotActive(parent) ?? await CheckNewColonyAsync(newColony, cancellationToken);
        if (refusal is not null)
        {
            return ColonyChangeResult.Refused(refusal.Value);
        }

        var split = new ColonySplit { NewColonyId = ColonyId.New(), NewColony = newColony, Queen = Introduce(queen) };
        return await AppendAsync(accountId, parentColonyId, occurredOn, split, parent, cancellationToken);
    }

    public async Task<ColonyChangeResult> MergeAsync(ColonyId colonyId, ColonyId intoColonyId, DateOnly occurredOn, CancellationToken cancellationToken)
    {
        var accountId = RequireAccount();
        var colony = await LoadAsync(colonyId, cancellationToken);
        if (colony is null)
        {
            return ColonyChangeResult.NotFound;
        }

        if ((NotActive(colony) ?? (colonyId == intoColonyId ? ColonyRefusal.MergeIntoItself : null)) is { } refusal)
        {
            return ColonyChangeResult.Refused(refusal);
        }

        // Tracked, so the save checks the target's version as well as the colony's.
        var target = await db.Colonies.SingleOrDefaultAsync(c => c.ColonyId == intoColonyId, cancellationToken);
        if (CheckMergeTarget(target) is { } targetRefusal)
        {
            return ColonyChangeResult.Refused(targetRefusal);
        }

        return await AppendAsync(accountId, colonyId, occurredOn, new ColonyMerged { IntoColonyId = intoColonyId }, colony, cancellationToken, target);
    }

    public async Task<ColonyChangeResult> RequeenAsync(ColonyId colonyId, QueenDetails queen, CancellationToken cancellationToken)
    {
        var accountId = RequireAccount();
        var colony = await LoadAsync(colonyId, cancellationToken);
        if (colony is null)
        {
            return ColonyChangeResult.NotFound;
        }

        if ((NotActive(colony) ?? InTheFuture(queen.IntroducedOn)) is { } refusal)
        {
            return ColonyChangeResult.Refused(refusal);
        }

        // Read before the projection marks the current queen replaced; the event itself is appended as given.
        var warning = colony.Queen is { } current && queen.IntroducedOn < current.Details.IntroducedOn
            ? new RequeenBeforeCurrentQueen(queen.IntroducedOn, current.Details.IntroducedOn)
            : null;
        var requeened = new ColonyRequeened { Queen = Introduce(queen)!, ReplacedQueenId = colony.Queen?.QueenId };
        var result = await AppendAsync(accountId, colonyId, queen.IntroducedOn, requeened, colony, cancellationToken);
        return result.Outcome == ColonyChangeOutcome.Recorded ? result with { Warning = warning } : result;
    }

    public async Task<ColonyChangeResult> MoveAsync(ColonyId colonyId, YardId toYardId, DateOnly occurredOn, CancellationToken cancellationToken)
    {
        var accountId = RequireAccount();
        var colony = await LoadAsync(colonyId, cancellationToken);
        if (colony is null)
        {
            return ColonyChangeResult.NotFound;
        }

        var refusal = NotActive(colony)
            ?? (colony.Colony.YardId == toYardId ? ColonyRefusal.AlreadyInThatYard : (ColonyRefusal?)null)
            ?? await CheckYardAsync(toYardId, cancellationToken);
        if (refusal is not null)
        {
            return ColonyChangeResult.Refused(refusal.Value);
        }

        var moved = new ColonyMoved { FromYardId = colony.Colony.YardId, ToYardId = toYardId };
        return await AppendAsync(accountId, colonyId, occurredOn, moved, colony, cancellationToken);
    }

    public async Task<IReadOnlyList<ColonyState>> ListAsync(int skip, int take, CancellationToken cancellationToken) =>
        await db.Colonies
            .AsNoTracking()
            .OrderBy(c => c.TagId)
            .Skip(skip)
            .Take(take)
            .Select(c => new ColonyState(c, db.Queens.SingleOrDefault(q => q.ColonyId == c.ColonyId && q.ReplacedOn == null)))
            .ToListAsync(cancellationToken);

    public async Task<ColonyState?> FindAsync(ColonyId colonyId, CancellationToken cancellationToken) =>
        await db.Colonies
            .AsNoTracking()
            .Where(c => c.ColonyId == colonyId)
            .Select(c => new ColonyState(c, db.Queens.SingleOrDefault(q => q.ColonyId == c.ColonyId && q.ReplacedOn == null)))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ColonyEvent>?> ListEventsAsync(ColonyId colonyId, int skip, int take, CancellationToken cancellationToken)
    {
        if (!await db.Colonies.AnyAsync(c => c.ColonyId == colonyId, cancellationToken))
        {
            return null;
        }

        return await db.ColonyEvents
            .AsNoTracking()
            .Where(e => e.ColonyId == colonyId || e.RelatedColonyId == colonyId)
            .OrderBy(e => e.RecordedAt)
            .ThenBy(e => e.EventId)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Queen>?> ListQueensAsync(ColonyId colonyId, int skip, int take, CancellationToken cancellationToken)
    {
        if (!await db.Colonies.AnyAsync(c => c.ColonyId == colonyId, cancellationToken))
        {
            return null;
        }

        return await db.Queens
            .AsNoTracking()
            .Where(q => q.ColonyId == colonyId)
            .OrderByDescending(q => q.Details.IntroducedOn)
            .ThenByDescending(q => q.QueenId)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    private async Task<ColonyChangeResult> CreateAsync(
        ColonyProfile colony, DateOnly occurredOn, Func<ColonyEventData> data, CancellationToken cancellationToken)
    {
        var accountId = RequireAccount();
        if (await CheckNewColonyAsync(colony, cancellationToken) is { } refusal)
        {
            return ColonyChangeResult.Refused(refusal);
        }

        var colonyId = ColonyId.New();
        return await AppendAsync(accountId, colonyId, occurredOn, data(), null, cancellationToken);
    }

    private async Task<ColonyChangeResult> AppendAsync(
        AccountId accountId,
        ColonyId colonyId,
        DateOnly occurredOn,
        ColonyEventData data,
        ColonyState? subject,
        CancellationToken cancellationToken,
        Colony? target = null)
    {
        var colonyEvent = new ColonyEvent(ColonyEventId.New(), accountId, colonyId, occurredOn, clock.GetUtcNow(), data);
        var projected = ColonyProjection.Apply(colonyEvent, subject, target);
        db.ColonyEvents.Add(colonyEvent);
        if (projected.AddedColony is { } addedColony)
        {
            db.Colonies.Add(addedColony);
        }

        if (projected.AddedQueen is { } addedQueen)
        {
            db.Queens.Add(addedQueen);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ColonyChangeResult.Conflict;
        }
        catch (DbUpdateException e) when (IsTagIdTaken(e))
        {
            // Another write took the tag after the pre-check read it free: the same refusal, not a 500.
            return ColonyChangeResult.Refused(ColonyRefusal.TagIdTaken);
        }

        return ColonyChangeResult.Recorded(projected.Colony);
    }

    private static bool IsTagIdTaken(DbUpdateException e) =>
        e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: ColonyModelExtensions.TagIdIndexName };

    private async Task<ColonyState?> LoadAsync(ColonyId colonyId, CancellationToken cancellationToken)
    {
        var colony = await db.Colonies.SingleOrDefaultAsync(c => c.ColonyId == colonyId, cancellationToken);
        if (colony is null)
        {
            return null;
        }

        var queen = await db.Queens.SingleOrDefaultAsync(q => q.ColonyId == colonyId && q.ReplacedOn == null, cancellationToken);
        return new ColonyState(colony, queen);
    }

    private async Task<ColonyRefusal?> CheckNewColonyAsync(ColonyProfile colony, CancellationToken cancellationToken)
    {
        if (await db.Colonies.AnyAsync(c => c.TagId == colony.TagId, cancellationToken))
        {
            return ColonyRefusal.TagIdTaken;
        }

        return await CheckYardAsync(colony.YardId, cancellationToken);
    }

    private async Task<ColonyRefusal?> CheckYardAsync(YardId yardId, CancellationToken cancellationToken) =>
        await db.Yards.AnyAsync(y => y.YardId == yardId, cancellationToken) ? null : ColonyRefusal.NoSuchYard;

    private static ColonyRefusal? CheckMergeTarget(Colony? target) => target?.Status switch
    {
        null => ColonyRefusal.NoSuchTargetColony,
        ColonyStatus.Active => null,
        _ => ColonyRefusal.TargetNotActive,
    };

    private static ColonyRefusal? NotActive(ColonyState colony) =>
        colony.Colony.Status == ColonyStatus.Active ? null : ColonyRefusal.NotActive;

    // Today in UTC is on or after today anywhere in the Americas, so a Texas entry for its own today is never refused.
    private ColonyRefusal? InTheFuture(DateOnly day) =>
        day > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime) ? ColonyRefusal.IntroducedInFuture : null;

    private static NewQueen? Introduce(QueenDetails? queen) =>
        queen is null ? null : new NewQueen { QueenId = QueenId.New(), Details = queen };

    private AccountId RequireAccount() =>
        currentAccount.AccountId
        ?? throw new InvalidOperationException("No current account: a colony event cannot be written.");
}
