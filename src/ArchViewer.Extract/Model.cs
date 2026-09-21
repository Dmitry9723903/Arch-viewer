using System.Text.Json.Serialization;

namespace ArchViewer.Extract;

/// <summary>
/// The model file: a tree of containers plus the edges between them.
/// </summary>
public sealed class Model
{
    /// <summary>Human-readable name of the analysed repository.</summary>
    public required string Title { get; init; }

    /// <summary>Absolute path the relative paths in this model resolve against.</summary>
    public required string Root { get; init; }

    /// <summary>Top-level containers.</summary>
    public required IReadOnlyList<Node> Nodes { get; init; }

    /// <summary>Edges between leaf containers, in terms of their ids.</summary>
    public required IReadOnlyList<Edge> Edges { get; init; }
}

/// <summary>
/// A container: a group, a module, an assembly — whatever the extractor decided.
/// The viewer nests and expands these; it does not interpret <see cref="Kind"/>.
/// </summary>
public sealed class Node
{
    /// <summary>Identity, unique across the model.</summary>
    public required string Id { get; init; }

    /// <summary>What to draw on the box.</summary>
    public required string Label { get; init; }

    /// <summary>Extractor's word for what this container is.</summary>
    public required string Kind { get; init; }

    /// <summary>What this container is within its parent, or null when it has no role.</summary>
    public string? Role { get; init; }

    /// <summary>Project file this container came from, relative to the root.</summary>
    public string? Project { get; init; }

    /// <summary>Nested containers.</summary>
    public IReadOnlyList<Node> Children { get; init; } = Array.Empty<Node>();

    /// <summary>Types declared in this container.</summary>
    public IReadOnlyList<TypeNode> Types { get; init; } = Array.Empty<TypeNode>();
}

/// <summary>
/// A type read from assembly metadata.
/// </summary>
public sealed class TypeNode
{
    /// <summary>Full name of the type.</summary>
    public required string Id { get; init; }

    /// <summary>Short name, as drawn.</summary>
    public required string Name { get; init; }

    /// <summary>class, interface, record, enum, struct or abstract.</summary>
    public required string Stereotype { get; init; }

    /// <summary>Source file, relative to the root, when a PDB supplied one.</summary>
    public string? File { get; init; }

    /// <summary>1-based declaration line, when a PDB supplied one.</summary>
    public int? Line { get; init; }

    /// <summary>Last line the type occupies, when a PDB supplied one.</summary>
    public int? EndLine { get; init; }

    /// <summary>
    /// The type's own source lines, read from the file at extraction time.
    /// Carried inside the model because the page is opened from disk and a
    /// browser cannot read local files on its own.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>Public members, as displayed.</summary>
    public IReadOnlyList<MemberNode> Members { get; init; } = Array.Empty<MemberNode>();
}

/// <summary>
/// One member of a type.
/// </summary>
public sealed class MemberNode
{
    /// <summary>Signature as displayed.</summary>
    public required string Text { get; init; }

    /// <summary>1-based line, when a PDB supplied one.</summary>
    public int? Line { get; init; }
}

/// <summary>
/// A dependency between two containers.
/// </summary>
public sealed class Edge
{
    /// <summary>Id of the referencing container.</summary>
    public required string From { get; init; }

    /// <summary>Id of the referenced container.</summary>
    public required string To { get; init; }

    /// <summary>dependency, implements, inheritance or association.</summary>
    public required string Kind { get; init; }

    /// <summary>Id of the rule this edge breaks, or null when it breaks none.</summary>
    public string? Violates { get; init; }
}

/// <summary>
/// Serialisation settings shared by reader and writer.
/// </summary>
public static class ModelJson
{
    /// <summary>camelCase, indented, nulls omitted.</summary>
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
