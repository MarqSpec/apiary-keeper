namespace ApiaryCopilot.Domain;

/// <summary>
/// One thing that happened in a colony's lineage (FR-6), appended to its history and never changed or removed
/// (ARCHITECTURE.md D-2, NFR-5). It has no setters; the persistence layer refuses to update or delete one.
/// </summary>
public sealed class ColonyEvent : IAccountOwned, IRowStamped
{
    /// <summary>Records an event about <paramref name="colonyId"/>.</summary>
    public ColonyEvent(
        ColonyEventId eventId,
        AccountId accountId,
        ColonyId colonyId,
        DateOnly occurredOn,
        DateTimeOffset recordedAt,
        ColonyEventData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        EventId = eventId;
        AccountId = accountId;
        ColonyId = colonyId;
        OccurredOn = occurredOn;
        RecordedAt = recordedAt;
        Data = data;
        RelatedColonyId = data switch
        {
            ColonySplit split => split.NewColonyId,
            ColonyMerged merged => merged.IntoColonyId,
            _ => null,
        };
    }

    /// <summary>The event's id.</summary>
    public ColonyEventId EventId { get; }

    /// <inheritdoc />
    public AccountId AccountId { get; }

    /// <inheritdoc />
    public UserId CreatedBy { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; private set; }

    /// <inheritdoc />
    public UserId LastModifiedBy { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset LastModifiedAt { get; private set; }

    /// <summary>The colony the event happened to.</summary>
    public ColonyId ColonyId { get; }

    /// <summary>The other colony it involves: the new colony of a split, the colony merged into; otherwise none.</summary>
    public ColonyId? RelatedColonyId { get; }

    /// <summary>The day it happened, as the beekeeper records it.</summary>
    public DateOnly OccurredOn { get; }

    /// <summary>When the server recorded it.</summary>
    public DateTimeOffset RecordedAt { get; }

    /// <summary>What happened.</summary>
    public ColonyEventData Data { get; }
}

/// <summary>What a lineage event records (FR-6): one derived record per kind of event.</summary>
public abstract record ColonyEventData;

/// <summary>An existing colony was entered into the app.</summary>
public sealed record ColonyRegistered : ColonyEventData
{
    /// <summary>The colony as recorded.</summary>
    public required ColonyProfile Colony { get; init; }

    /// <summary>Its queen, when known.</summary>
    public NewQueen? Queen { get; init; }
}

/// <summary>A swarm was captured and hived as a new colony.</summary>
public sealed record SwarmCaptured : ColonyEventData
{
    /// <summary>The new colony.</summary>
    public required ColonyProfile Colony { get; init; }

    /// <summary>Its queen, when known.</summary>
    public NewQueen? Queen { get; init; }
}

/// <summary>A new colony was split from this one.</summary>
public sealed record ColonySplit : ColonyEventData
{
    /// <summary>The new colony's id.</summary>
    public required ColonyId NewColonyId { get; init; }

    /// <summary>The new colony.</summary>
    public required ColonyProfile NewColony { get; init; }

    /// <summary>The new colony's queen, when one was introduced.</summary>
    public NewQueen? Queen { get; init; }
}

/// <summary>This colony was merged into another.</summary>
public sealed record ColonyMerged : ColonyEventData
{
    /// <summary>The colony it joined.</summary>
    public required ColonyId IntoColonyId { get; init; }
}

/// <summary>A new queen was introduced, replacing the current one if there was one.</summary>
public sealed record ColonyRequeened : ColonyEventData
{
    /// <summary>The new queen.</summary>
    public required NewQueen Queen { get; init; }

    /// <summary>The queen she replaced, or <see langword="null"/> when the colony had none recorded.</summary>
    public QueenId? ReplacedQueenId { get; init; }
}

/// <summary>The colony was moved between yards.</summary>
public sealed record ColonyMoved : ColonyEventData
{
    /// <summary>The yard it left.</summary>
    public required YardId FromYardId { get; init; }

    /// <summary>The yard it went to.</summary>
    public required YardId ToYardId { get; init; }
}

/// <summary>A queen an event introduces.</summary>
public sealed record NewQueen
{
    /// <summary>Her id.</summary>
    public required QueenId QueenId { get; init; }

    /// <summary>What was recorded about her.</summary>
    public required QueenDetails Details { get; init; }
}
