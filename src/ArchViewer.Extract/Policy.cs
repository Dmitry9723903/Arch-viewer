using System.Text.Json;

namespace ArchViewer.Extract;

/// <summary>
/// Hand-written description of how to group containers and which
/// dependencies are forbidden.
/// </summary>
public sealed class Policy
{
    /// <summary>Name shown on screen.</summary>
    public string Title { get; init; } = "";

    /// <summary>Glob fragments of paths to skip entirely.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = new[] { "artifacts", "bin", "obj", "node_modules" };

    /// <summary>Grouping levels, outermost first.</summary>
    public IReadOnlyList<GroupRule> Group { get; init; } = Array.Empty<GroupRule>();

    /// <summary>Dependency rules.</summary>
    public IReadOnlyList<Rule> Rules { get; init; } = Array.Empty<Rule>();

    /// <summary>
    /// How the types of a project are arranged. Left out, they hang off the
    /// project in one flat list — which is fine for a repository whose
    /// projects are small, and useless for one whose structure lives a floor
    /// below the project.
    /// </summary>
    public TypeGrouping? Types { get; init; }

    /// <summary>
    /// Reads a policy file, or returns a default that groups by directory.
    /// </summary>
    public static Policy Load(string? path, string root)
    {
        if (path is not null && File.Exists(path))
        {
            return JsonSerializer.Deserialize<Policy>(File.ReadAllText(path), ModelJson.Options)
                   ?? throw new InvalidOperationException($"Policy file is empty: {path}");
        }

        var beside = Path.Combine(root, ".arch-viewer", "policy.json");
        if (File.Exists(beside))
        {
            return JsonSerializer.Deserialize<Policy>(File.ReadAllText(beside), ModelJson.Options)
                   ?? throw new InvalidOperationException($"Policy file is empty: {beside}");
        }

        // No policy: one level of grouping by the directory the project sits in.
        return new Policy
        {
            Title = new DirectoryInfo(root).Name,
            Group = new[] { new GroupRule { Kind = "folder", From = "path-segment", Index = 1 } },
        };
    }
}

/// <summary>
/// How types are arranged inside the project that declares them.
/// </summary>
public sealed class TypeGrouping
{
    /// <summary>
    /// "namespace" nests types by their namespace; anything else leaves them
    /// flat.
    /// </summary>
    public string By { get; init; } = "flat";

    /// <summary>
    /// Word written into the kind of the containers this produces.
    /// </summary>
    public string Kind { get; init; } = "namespace";

    /// <summary>
    /// Whether to drop the leading part of a namespace that merely repeats
    /// the assembly's own name. Nesting six levels to reach the one that
    /// differs helps nobody.
    /// </summary>
    public bool TrimAssemblyPrefix { get; init; } = true;

    /// <summary>
    /// Whether to record references between types — what a type inherits,
    /// implements, and holds. Without them the only edges are between
    /// projects, and a repository of few projects has nothing to constrain.
    /// </summary>
    public bool Edges { get; init; }
}

/// <summary>
/// One grouping level.
/// </summary>
public sealed class GroupRule
{
    /// <summary>Word written into the container's kind.</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Where the group name comes from: path-segment, name-part,
    /// assembly-attribute or literal.
    /// </summary>
    public required string From { get; init; }

    /// <summary>
    /// The group name itself, when <see cref="From"/> is literal. Used by a
    /// fallback that must say "this was not declared" rather than substitute
    /// some other property and pass it off as the declared one.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>Which path segment, counted from the repository root.</summary>
    public int Index { get; init; }

    /// <summary>Which dot-separated part of the project name.</summary>
    public int Part { get; init; }

    /// <summary>Full name of the assembly-level attribute to read.</summary>
    public string? Attribute { get; init; }

    /// <summary>Constructor argument of that attribute, counted from zero.</summary>
    public int Argument { get; init; }

    /// <summary>
    /// Where to look when this level yields nothing — typically when the
    /// repository has not been built and no assembly attribute can be read.
    /// </summary>
    public GroupRule? Fallback { get; init; }
}

/// <summary>
/// A dependency rule: who may reference what.
/// </summary>
public sealed class Rule
{
    /// <summary>Identity reported on a violating edge.</summary>
    public required string Id { get; init; }

    /// <summary>Which containers the rule constrains.</summary>
    public required Selector Subject { get; init; }

    /// <summary>What those containers may reference. Anything else violates.</summary>
    public IReadOnlyList<Selector> MayReference { get; init; } = Array.Empty<Selector>();
}

/// <summary>
/// Matches containers by role, by name, or by sharing an ancestor with the subject.
/// </summary>
public sealed class Selector
{
    /// <summary>Required role, or null for any.</summary>
    public string? Role { get; init; }

    /// <summary>
    /// Required id — the container named, or anything beneath it. Naming a
    /// container means naming what is inside it.
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// Required id, matched exactly. For the rare rule that means this
    /// container and nothing nested within it.
    /// </summary>
    public string? IdExactly { get; init; }

    /// <summary>
    /// Required label — the container's own name, without the path above it.
    /// This is how a rule names a namespace: "Controllers", not the whole
    /// dotted address it happens to sit at.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Required ending of the id. Lets a rule name a family of containers,
    /// such as every namespace called Entities under any project.
    /// </summary>
    public string? EndsWith { get; init; }

    /// <summary>
    /// A container this one must sit at or beneath. Naming a namespace almost
    /// always means naming what is under it as well: allowing "Interfaces" and
    /// not "Interfaces.Billing" would be a distinction nobody intends.
    /// </summary>
    public string? Under { get; init; }

    /// <summary>
    /// Kind of ancestor the target must share with the subject — the second axis.
    /// </summary>
    public string? Same { get; init; }
}
