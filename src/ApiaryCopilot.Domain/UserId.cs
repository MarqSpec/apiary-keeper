namespace ApiaryCopilot.Domain;

/// <summary>Identifies a user: a person who acts on one account's records, and whose id stamps every row they write. Never empty.</summary>
public readonly record struct UserId
{
    /// <summary>Creates an id from its value.</summary>
    /// <exception cref="ArgumentException">The value is <see cref="Guid.Empty"/>.</exception>
    public UserId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Validation: a user id cannot be empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>The underlying value.</summary>
    public Guid Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}
