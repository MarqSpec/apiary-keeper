namespace ApiaryCopilot.Domain;

/// <summary>Identifies an account: the tenant every record belongs to (NFR-8). Never empty.</summary>
public readonly record struct AccountId
{
    /// <summary>Creates an id from its value.</summary>
    /// <exception cref="ArgumentException">The value is <see cref="Guid.Empty"/>.</exception>
    public AccountId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Validation: an account id cannot be empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>The underlying value.</summary>
    public Guid Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}
