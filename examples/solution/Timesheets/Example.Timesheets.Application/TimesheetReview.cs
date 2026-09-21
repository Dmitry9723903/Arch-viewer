using Example.Timesheets.Domain;

namespace Example.Timesheets.Application;

/// <summary>
/// Going over the timesheets of a period before payroll runs.
/// </summary>
public sealed class TimesheetReview
{
    private readonly IReadOnlyList<Timesheet> _sheets;

    /// <summary>Creates a review over the given timesheets.</summary>
    /// <param name="sheets">Timesheets of the period.</param>
    public TimesheetReview(IReadOnlyList<Timesheet> sheets) => _sheets = sheets;

    /// <summary>Whether every timesheet has been agreed to.</summary>
    /// <returns>True when nothing is outstanding.</returns>
    public bool AllApproved() => _sheets.All(sheet => sheet.Approved);
}
