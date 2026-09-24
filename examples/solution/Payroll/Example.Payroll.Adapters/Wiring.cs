using System.Collections.Generic;
using Example.Kernel;

namespace Example.Payroll.Adapters;

/// <summary>
/// What this application offers, said the way a composition root says it.
/// <para>
/// The second call is the one that matters to the reader of this example:
/// its type argument is wrapped in a collection. A reader of compiled code
/// that keeps only the outermost name sees <c>IReadOnlyList</c>, which
/// belongs to the framework and names nothing in this repository, and the
/// reference to Money is then missing from the map with nothing to say it
/// ever existed. The first call, written without the wrapper, is found
/// either way — which is what made the gap so quiet.
/// </para>
/// </summary>
public static class Wiring
{
    /// <summary>
    /// Says what this application offers.
    /// </summary>
    /// <param name="registry">Where the offers are recorded.</param>
    public static void Compose(Registry registry)
    {
        registry.Offer<Money>();
        registry.Offer<IReadOnlyList<Money>>();
        registry.Offer<IReadOnlyList<IReadOnlyList<EmployeeId>>>();
    }
}
