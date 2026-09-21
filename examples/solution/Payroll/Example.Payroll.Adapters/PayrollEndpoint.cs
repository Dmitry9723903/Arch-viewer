using Example.Payroll.Application;
using Example.Timesheets.Application;

namespace Example.Payroll.Adapters;

/// <summary>
/// Where a payroll run is asked for from outside.
/// </summary>
public sealed class PayrollEndpoint
{
    private readonly PayrollRun _run;

    // The deliberate violation this example exists to show: an adapter of
    // one module reaching into another module's application layer instead
    // of going through its published contract. The viewer draws it red.
    private readonly TimesheetReview _review;

    /// <summary>Creates the endpoint.</summary>
    /// <param name="run">The payroll run to drive.</param>
    /// <param name="review">Timesheet review, reached into across the boundary.</param>
    public PayrollEndpoint(PayrollRun run, TimesheetReview review)
    {
        _run = run;
        _review = review;
    }

    /// <summary>Reports whether a run may proceed.</summary>
    /// <returns>True when every timesheet has been approved.</returns>
    public bool CanRun() => _review.AllApproved();
}
