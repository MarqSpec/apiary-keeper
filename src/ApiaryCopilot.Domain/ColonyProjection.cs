namespace ApiaryCopilot.Domain;

/// <summary>A colony's current state: the colony and its current queen, if one is recorded.</summary>
public sealed record ColonyState(Colony Colony, Queen? Queen);

/// <summary>What applying an event did: the colony it concerns now, and any colony or queen it added.</summary>
/// <param name="Colony">The colony the event created, or the one it happened to, as it is now.</param>
/// <param name="AddedColony">A colony the event brought into being, to be stored.</param>
/// <param name="AddedQueen">A queen the event introduced, to be stored.</param>
public sealed record ColonyProjectionResult(ColonyState Colony, Colony? AddedColony, Queen? AddedQueen);

/// <summary>
/// Projects lineage events into colonies' current state (FR-6, ARCHITECTURE.md D-2): the read model is what these
/// events, applied in order, make it. Pure: it reads and changes only the state it is given.
/// </summary>
public static class ColonyProjection
{
    /// <summary>
    /// Applies <paramref name="colonyEvent"/>. An event that creates a colony takes no state; any other takes the
    /// state of the colony it happened to, changes it in place and bumps its <see cref="Colony.Version"/>. A merge
    /// also takes <paramref name="target"/>, the colony it goes into, and bumps its version too: the merge is in its
    /// history.
    /// </summary>
    /// <exception cref="ArgumentException">The state given does not fit the event.</exception>
    /// <exception cref="InvalidOperationException">A requeen names a replaced queen who is not the current one.</exception>
    public static ColonyProjectionResult Apply(ColonyEvent colonyEvent, ColonyState? subject, Colony? target = null)
    {
        ArgumentNullException.ThrowIfNull(colonyEvent);
        return colonyEvent.Data switch
        {
            ColonyRegistered registered => Create(colonyEvent, subject, colonyEvent.ColonyId, registered.Colony, registered.Queen, ColonyOrigin.Registered, null),
            SwarmCaptured captured => Create(colonyEvent, subject, colonyEvent.ColonyId, captured.Colony, captured.Queen, ColonyOrigin.SwarmCapture, null),
            ColonySplit split => Split(colonyEvent, Existing(colonyEvent, subject), split),
            ColonyMerged merged => Merge(Existing(colonyEvent, subject), merged, target),
            ColonyRequeened requeened => Requeen(colonyEvent, Existing(colonyEvent, subject), requeened),
            ColonyMoved moved => Move(Existing(colonyEvent, subject), moved),
            _ => throw new ArgumentException($"No projection for {colonyEvent.Data.GetType().Name}.", nameof(colonyEvent)),
        };
    }

    private static ColonyProjectionResult Create(
        ColonyEvent colonyEvent, ColonyState? subject, ColonyId colonyId, ColonyProfile profile, NewQueen? queen, ColonyOrigin origin, ColonyId? parentColonyId)
    {
        if (subject is not null)
        {
            throw new ArgumentException("An event that creates a colony takes no existing colony.", nameof(subject));
        }

        var colony = new Colony
        {
            ColonyId = colonyId,
            AccountId = colonyEvent.AccountId,
            TagId = profile.TagId,
            HiveNumber = profile.HiveNumber,
            HiveType = profile.HiveType,
            BoxCount = profile.BoxCount,
            Origin = origin,
            ParentColonyId = parentColonyId,
            YardId = profile.YardId,
            Status = ColonyStatus.Active,
            Version = 1,
        };
        var newQueen = queen is null ? null : Introduce(colonyEvent, colonyId, queen);
        return new ColonyProjectionResult(new ColonyState(colony, newQueen), colony, newQueen);
    }

    private static ColonyProjectionResult Split(ColonyEvent colonyEvent, ColonyState parent, ColonySplit split)
    {
        parent.Colony.Version++;
        return Create(colonyEvent, null, split.NewColonyId, split.NewColony, split.Queen, ColonyOrigin.Split, parent.Colony.ColonyId);
    }

    private static ColonyProjectionResult Merge(ColonyState colony, ColonyMerged merged, Colony? target)
    {
        if (target is null || target.ColonyId != merged.IntoColonyId)
        {
            throw new ArgumentException("A merge needs the colony it goes into.", nameof(target));
        }

        // So a concurrent change to the target, or another merge into it, loses on the target's version.
        target.Version++;
        colony.Colony.Status = ColonyStatus.Merged;
        colony.Colony.MergedIntoColonyId = merged.IntoColonyId;
        colony.Colony.Version++;
        return new ColonyProjectionResult(colony, null, null);
    }

    private static ColonyProjectionResult Requeen(ColonyEvent colonyEvent, ColonyState colony, ColonyRequeened requeened)
    {
        if (colony.Queen?.QueenId != requeened.ReplacedQueenId)
        {
            throw new InvalidOperationException("A requeen must replace the colony's current queen, or none when it has none.");
        }

        var queen = Introduce(colonyEvent, colony.Colony.ColonyId, requeened.Queen);
        if (colony.Queen is { } replaced)
        {
            replaced.ReplacedOn = queen.Details.IntroducedOn;
            replaced.ReplacedByQueenId = queen.QueenId;
        }

        colony.Colony.Version++;
        return new ColonyProjectionResult(colony with { Queen = queen }, null, queen);
    }

    private static ColonyProjectionResult Move(ColonyState colony, ColonyMoved moved)
    {
        colony.Colony.YardId = moved.ToYardId;
        colony.Colony.Version++;
        return new ColonyProjectionResult(colony, null, null);
    }

    private static Queen Introduce(ColonyEvent colonyEvent, ColonyId colonyId, NewQueen queen) => new()
    {
        QueenId = queen.QueenId,
        AccountId = colonyEvent.AccountId,
        ColonyId = colonyId,
        Details = queen.Details,
    };

    private static ColonyState Existing(ColonyEvent colonyEvent, ColonyState? subject)
    {
        if (subject is null || subject.Colony.ColonyId != colonyEvent.ColonyId)
        {
            throw new ArgumentException("The event needs the state of the colony it happened to.", nameof(subject));
        }

        return subject;
    }
}
