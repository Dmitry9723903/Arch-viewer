using Example.Kernel;

namespace Example.Timesheets.Domain;

/// <summary>
/// Hours one person recorded for one period, and whether anyone has
/// agreed to them.
/// </summary>
public sealed class Timesheet
{
    /// <summary>Creates a timesheet.</summary>
    /// <param name="employee">Whose hours these are.</param>
    /// <param name="hours">Hours recorded.</param>
    public Timesheet(EmployeeId employee, decimal hours)
    {
        Employee = employee;
        Hours = hours;
    }

    /// <summary>Whose hours these are.</summary>
    public EmployeeId Employee { get; }

    /// <summary>Hours recorded.</summary>
    public decimal Hours { get; }

    /// <summary>Whether someone has agreed to them.</summary>
    public bool Approved { get; private set; }

    /// <summary>
    /// Agrees to the hours. Approving twice is not an error: the second
    /// approval changes nothing, and refusing it would make retries unsafe.
    /// </summary>
    public void Approve() => Approved = true;
}
