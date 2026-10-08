namespace ApiaryCopilot.Domain;

/// <summary>Identifies a queen. Never empty.</summary>
public readonly record struct QueenId
{
    /// <summary>Creates an id from its value.</summary>
    /// <exception cref="ArgumentException">The value is <see cref="Guid.Empty"/>.</exception>
    public QueenId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Validation: a queen id cannot be empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>The underlying value.</summary>
    public Guid Value { get; }

    /// <summary>A new, unique id.</summary>
    public static QueenId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}
