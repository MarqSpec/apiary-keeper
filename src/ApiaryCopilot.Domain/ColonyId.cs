namespace ApiaryCopilot.Domain;

/// <summary>Identifies a colony. Never empty.</summary>
public readonly record struct ColonyId
{
    /// <summary>Creates an id from its value.</summary>
    /// <exception cref="ArgumentException">The value is <see cref="Guid.Empty"/>.</exception>
    public ColonyId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Validation: a colony id cannot be empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>The underlying value.</summary>
    public Guid Value { get; }

    /// <summary>A new, unique id.</summary>
    public static ColonyId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}
