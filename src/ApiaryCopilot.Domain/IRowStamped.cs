namespace ApiaryCopilot.Domain;

// The independent audit log is deferred.
/// <summary>
/// A record stamped with who created it and who last changed it, and when (UTC). Every apiary entity implements it.
/// The persistence layer sets the stamps on every save from the current user and the clock; nothing else writes them,
/// and a change never moves the created stamps (ARCHITECTURE.md D-8). The independent audit log is separate.
/// </summary>
public interface IRowStamped
{
    /// <summary>The user who created the record.</summary>
    UserId CreatedBy { get; }

    /// <summary>When the record was created, UTC.</summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>The user who last changed the record; its creator until it is changed.</summary>
    UserId LastModifiedBy { get; }

    /// <summary>When the record was last changed, UTC; its creation time until it is changed.</summary>
    DateTimeOffset LastModifiedAt { get; }
}
