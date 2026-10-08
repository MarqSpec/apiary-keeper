namespace ApiaryCopilot.Domain;

/// <summary>Identifies a colony event. Never empty.</summary>
public readonly record struct ColonyEventId
{
    /// <summary>Creates an id from its value.</summary>
    /// <exception cref="ArgumentException">The value is <see cref="Guid.Empty"/>.</exception>
    public ColonyEventId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Validation: a colony event id cannot be empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>The underlying value.</summary>
    public Guid Value { get; }

    /// <summary>A new, unique id.</summary>
    public static ColonyEventId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}
