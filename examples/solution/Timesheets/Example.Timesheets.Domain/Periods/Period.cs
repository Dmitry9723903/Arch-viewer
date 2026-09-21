namespace Example.Timesheets.Domain.Periods;

/// <summary>
/// The stretch of days a timesheet covers.
/// </summary>
public readonly record struct Period(DateOnly From, DateOnly To)
{
    /// <summary>Whether a day falls inside the period.</summary>
    /// <param name="day">The day to test.</param>
    /// <returns>True when the day is within, ends included.</returns>
    public bool Covers(DateOnly day) => day >= From && day <= To;

    /// <summary>Renders the period for display.</summary>
    public override string ToString() => $"{From:yyyy-MM-dd}…{To:yyyy-MM-dd}";
}
