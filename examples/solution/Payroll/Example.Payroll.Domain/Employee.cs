using Example.Kernel;

namespace Example.Payroll.Domain;

/// <summary>
/// Someone who is paid. The domain knows the rate and the hours; it does
/// not know where either came from.
/// </summary>
public sealed class Employee
{
    /// <summary>Creates an employee at a given hourly rate.</summary>
    /// <param name="id">Identity of the employee.</param>
    /// <param name="name">Name, as it should appear on the payslip.</param>
    /// <param name="hourlyRate">What one hour of their work costs.</param>
    public Employee(EmployeeId id, string name, Money hourlyRate)
    {
        Id = id;
        Name = name;
        HourlyRate = hourlyRate;
    }

    /// <summary>Identity of the employee.</summary>
    public EmployeeId Id { get; }

    /// <summary>Name, as it should appear on the payslip.</summary>
    public string Name { get; }

    /// <summary>What one hour of their work costs.</summary>
    public Money HourlyRate { get; }

    /// <summary>
    /// What they are owed for the hours given. Hours are not stored on the
    /// employee: they belong to a period, and the employee outlives it.
    /// </summary>
    /// <param name="hours">Hours worked in the period.</param>
    /// <returns>The amount owed.</returns>
    public Money PayFor(decimal hours)
    {
        if (hours < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hours), hours, "Hours cannot be negative.");
        }

        return HourlyRate with { Amount = HourlyRate.Amount * hours };
    }
}
