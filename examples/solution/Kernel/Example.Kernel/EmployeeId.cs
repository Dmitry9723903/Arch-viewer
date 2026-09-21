namespace Example.Kernel;

/// <summary>
/// Identity of an employee. Wrapped rather than left as a bare Guid so
/// that it cannot be passed where some other identity is expected.
/// </summary>
public readonly record struct EmployeeId(Guid Value)
{
    /// <summary>Creates a fresh identity.</summary>
    public static EmployeeId New() => new(Guid.CreateVersion7());

    /// <summary>Renders the identity for display.</summary>
    public override string ToString() => Value.ToString();
}
