namespace ApiaryCopilot.Domain;

/// <summary>Identifies a photo or video attached to an inspection (FR-8). Never empty.</summary>
public readonly record struct MediaId
{
    /// <summary>Creates an id from its value.</summary>
    /// <exception cref="ArgumentException">The value is <see cref="Guid.Empty"/>.</exception>
    public MediaId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Validation: a media id cannot be empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>The underlying value.</summary>
    public Guid Value { get; }

    /// <summary>A new, unique id.</summary>
    public static MediaId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}
