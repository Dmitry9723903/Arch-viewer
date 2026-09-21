using Example.Kernel;
using Example.Payroll.Domain;

namespace Example.Payroll.Application;

/// <summary>
/// Paying a set of employees for one period. Holds the sequence of the
/// job; the arithmetic belongs to the domain.
/// </summary>
public sealed class PayrollRun
{
    private readonly IReadOnlyList<Employee> _employees;

    /// <summary>Creates a run over the given employees.</summary>
    /// <param name="employees">Everyone to be paid in this run.</param>
    public PayrollRun(IReadOnlyList<Employee> employees) => _employees = employees;

    /// <summary>
    /// Totals what the run will cost, given the hours worked by each.
    /// </summary>
    /// <param name="hours">Hours worked, by employee.</param>
    /// <returns>The total owed.</returns>
    public Money Total(IReadOnlyDictionary<EmployeeId, decimal> hours)
    {
        var total = new Money(0m, "EUR");

        foreach (var employee in _employees)
        {
            if (hours.TryGetValue(employee.Id, out var worked))
            {
                total = total.Add(employee.PayFor(worked));
            }
        }

        return total;
    }
}
