using Example.Kernel;

namespace Example.Payroll.Domain.Rates;

/// <summary>
/// What each grade of work costs per hour. A nested namespace on purpose:
/// it is what makes this example exercise namespace grouping, and what
/// makes a viewer that loses edges below the project level fail here.
/// </summary>
public sealed class RateTable
{
    private readonly IReadOnlyDictionary<string, Money> _rates;

    /// <summary>Creates a table of rates by grade.</summary>
    /// <param name="rates">Rate for each grade.</param>
    public RateTable(IReadOnlyDictionary<string, Money> rates) => _rates = rates;

    /// <summary>
    /// The rate for a grade. An unknown grade is refused rather than
    /// defaulted: paying someone at a guessed rate is worse than not paying.
    /// </summary>
    /// <param name="grade">Grade of work.</param>
    /// <returns>The hourly rate.</returns>
    public Money For(string grade) =>
        _rates.TryGetValue(grade, out var rate)
            ? rate
            : throw new KeyNotFoundException($"No rate is recorded for grade '{grade}'.");
}
