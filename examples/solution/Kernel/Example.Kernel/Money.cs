namespace Example.Kernel;

/// <summary>
/// An amount of money. A value, not an entity: two amounts of the same
/// size in the same currency are the same amount.
/// </summary>
public readonly record struct Money(decimal Amount, string Currency)
{
    /// <summary>
    /// Adds two amounts. Mixing currencies is refused rather than guessed at.
    /// </summary>
    /// <param name="other">The amount to add.</param>
    /// <returns>The sum, in the same currency.</returns>
    public Money Add(Money other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot add {other.Currency} to {Currency}: there is no rate here to convert with.");
        }

        return this with { Amount = Amount + other.Amount };
    }

    /// <summary>Renders the amount for display.</summary>
    public override string ToString() => $"{Amount:0.00} {Currency}";
}
