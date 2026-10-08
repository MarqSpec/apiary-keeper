using ApiaryCopilot.Domain;

namespace ApiaryCopilot.Infrastructure;

/// <summary>
/// The current account's colonies, their queens and their lineage (UC-3; FR-4, FR-5, FR-6). Every change is an
/// event appended to the colony's history and projected into its current state in the same save (ARCHITECTURE.md
/// D-2); nothing here changes or removes a recorded event. Every read and write is scoped to
/// <see cref="ICurrentAccount"/>; another account's colony or yard is never found.
/// </summary>
/// <remarks>Each write throws <see cref="InvalidOperationException"/> when there is no current account, and writes nothing.</remarks>
public interface IColonyStore
{
    /// <summary>Records an existing colony, and its queen when known, in one of the account's yards.</summary>
    Task<ColonyChangeResult> RegisterAsync(ColonyProfile colony, QueenDetails? queen, DateOnly occurredOn, CancellationToken cancellationToken);

    /// <summary>Records a captured swarm as a new colony in one of the account's yards.</summary>
    Task<ColonyChangeResult> CaptureSwarmAsync(ColonyProfile colony, QueenDetails? queen, DateOnly occurredOn, CancellationToken cancellationToken);

    /// <summary>Splits a new colony from an active one; the result is the new colony.</summary>
    Task<ColonyChangeResult> SplitAsync(ColonyId parentColonyId, ColonyProfile newColony, QueenDetails? queen, DateOnly occurredOn, CancellationToken cancellationToken);

    /// <summary>
    /// Merges an active colony into another active one; the result is the merged colony. A change to either colony
    /// while the merge is being made is a <see cref="ColonyChangeOutcome.Conflict"/>.
    /// </summary>
    Task<ColonyChangeResult> MergeAsync(ColonyId colonyId, ColonyId intoColonyId, DateOnly occurredOn, CancellationToken cancellationToken);

    /// <summary>
    /// Introduces a queen to an active colony on her introduction day, replacing the current one. A day later than
    /// today (UTC) is refused as <see cref="ColonyRefusal.IntroducedInFuture"/>; a day before the current queen's
    /// introduction is recorded as given, with a <see cref="RequeenBeforeCurrentQueen"/> warning.
    /// </summary>
    Task<ColonyChangeResult> RequeenAsync(ColonyId colonyId, QueenDetails queen, CancellationToken cancellationToken);

    /// <summary>Moves an active colony to another of the account's yards.</summary>
    Task<ColonyChangeResult> MoveAsync(ColonyId colonyId, YardId toYardId, DateOnly occurredOn, CancellationToken cancellationToken);

    /// <summary>One page of the current account's colonies with their current queens, ordered by tag id.</summary>
    Task<IReadOnlyList<ColonyState>> ListAsync(int skip, int take, CancellationToken cancellationToken);

    /// <summary>The colony and its current queen, or <see langword="null"/> when the account has none with that id.</summary>
    Task<ColonyState?> FindAsync(ColonyId colonyId, CancellationToken cancellationToken);

    /// <summary>
    /// One page of the colony's history, oldest first: every event about it, and the split that created it or a merge
    /// into it. <see langword="null"/> when the account has no colony with that id.
    /// </summary>
    Task<IReadOnlyList<ColonyEvent>?> ListEventsAsync(ColonyId colonyId, int skip, int take, CancellationToken cancellationToken);

    /// <summary>
    /// One page of the colony's queens by introduction day, newest first: its replacement history. A backdated requeen
    /// sorts by the day it gave, so the current queen is not always first. <see langword="null"/> when the
    /// account has no colony with that id.
    /// </summary>
    Task<IReadOnlyList<Queen>?> ListQueensAsync(ColonyId colonyId, int skip, int take, CancellationToken cancellationToken);
}

/// <summary>How a colony change ended.</summary>
public enum ColonyChangeOutcome
{
    /// <summary>The event was appended and projected.</summary>
    Recorded,

    /// <summary>The account has no colony with that id; nothing was written.</summary>
    NotFound,

    /// <summary>The change breaks a rule (<see cref="ColonyChangeResult.Refusal"/>); nothing was written.</summary>
    Refused,

    /// <summary>
    /// The colony, or for a merge the colony it goes into, changed while this change was being made; nothing was
    /// written, and a retry may succeed.
    /// </summary>
    Conflict,
}

/// <summary>Why a colony change was refused.</summary>
public enum ColonyRefusal
{
    /// <summary>Another of the account's colonies carries the tag id.</summary>
    TagIdTaken,

    /// <summary>The account has no yard with that id.</summary>
    NoSuchYard,

    /// <summary>The colony is already in that yard.</summary>
    AlreadyInThatYard,

    /// <summary>The colony is no longer active: merged, dead out or sold.</summary>
    NotActive,

    /// <summary>The account has no colony to merge into with that id.</summary>
    NoSuchTargetColony,

    /// <summary>The colony to merge into is no longer active.</summary>
    TargetNotActive,

    /// <summary>A colony cannot be merged into itself.</summary>
    MergeIntoItself,

    /// <summary>The queen's introduction day is later than today (UTC): a requeen records what has happened.</summary>
    IntroducedInFuture,
}

/// <summary>
/// Something about a recorded change that may be a mistake. The change was recorded anyway, exactly as given: the
/// warning goes back with it and is never stored, so the append-only history is the same with or without it.
/// </summary>
public abstract record ColonyWarning;

/// <summary>
/// A requeen dated before the queen she replaces was introduced: possibly a real correction, possibly a typo.
/// </summary>
/// <param name="IntroducedOn">The day the requeen gave.</param>
/// <param name="CurrentQueenIntroducedOn">The day the queen she replaces was introduced.</param>
public sealed record RequeenBeforeCurrentQueen(DateOnly IntroducedOn, DateOnly CurrentQueenIntroducedOn) : ColonyWarning;

/// <summary>
/// The outcome of a colony change and, when it was recorded, the colony as it is now and any warning about it.
/// </summary>
public sealed record ColonyChangeResult(
    ColonyChangeOutcome Outcome, ColonyState? Colony = null, ColonyRefusal? Refusal = null, ColonyWarning? Warning = null)
{
    /// <summary>Nothing was written: the account has no colony with that id.</summary>
    public static ColonyChangeResult NotFound { get; } = new(ColonyChangeOutcome.NotFound);

    /// <summary>Nothing was written: the colony, or a merge's target, changed meanwhile.</summary>
    public static ColonyChangeResult Conflict { get; } = new(ColonyChangeOutcome.Conflict);

    /// <summary>
    /// The event was recorded; <paramref name="colony"/> is the colony it created or changed, and
    /// <paramref name="warning"/> what may be wrong with it, if anything.
    /// </summary>
    public static ColonyChangeResult Recorded(ColonyState colony, ColonyWarning? warning = null) =>
        new(ColonyChangeOutcome.Recorded, colony, Warning: warning);

    /// <summary>Nothing was written, for <paramref name="refusal"/>.</summary>
    public static ColonyChangeResult Refused(ColonyRefusal refusal) => new(ColonyChangeOutcome.Refused, Refusal: refusal);
}
