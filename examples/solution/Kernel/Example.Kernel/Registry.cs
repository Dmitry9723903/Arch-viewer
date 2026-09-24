namespace Example.Kernel;

/// <summary>
/// Somewhere to say what is wired to what, in the shape every composition
/// root has: a call whose type argument names the thing being wired.
/// </summary>
public sealed class Registry
{
    /// <summary>
    /// Records that this application offers the named type.
    /// </summary>
    /// <typeparam name="T">The type being offered.</typeparam>
    public void Offer<T>()
    {
    }
}
