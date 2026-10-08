namespace ApiaryCopilot.Domain;

/// <summary>Identifies a yard. Never empty.</summary>
public readonly record struct YardId
{
    /// <summary>Creates an id from its value.</summary>
    /// <exception cref="ArgumentException">The value is <see cref="Guid.Empty"/>.</exception>
    public YardId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Validation: a yard id cannot be empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>The underlying value.</summary>
    public Guid Value { get; }

    /// <summary>A new, unique id.</summary>
    public static YardId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}
