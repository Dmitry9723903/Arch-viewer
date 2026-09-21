namespace ArchViewer.Extract;

/// <summary>
/// Turns placements into the nested model and evaluates the policy's rules
/// against the edges.
/// </summary>
public static class Builder
{
    /// <summary>
    /// Builds the model: containers nested per the grouping, leaf containers
    /// for projects, edges between leaves with violations marked.
    /// </summary>
    public static Model Build(
        string root,
        Policy policy,
        IReadOnlyList<Placement> placements,
        AssemblyFacts facts)
    {
        var roots = new List<Node>();
        var byPath = new Dictionary<string, MutableNode>(StringComparer.Ordinal);
        var top = new List<MutableNode>();

        foreach (var placement in placements)
        {
            var parentList = top;
            var path = "";
            MutableNode? parent = null;

            for (var level = 0; level < placement.Groups.Count; level++)
            {
                path = path.Length == 0 ? placement.Groups[level] : $"{path}/{placement.Groups[level]}";

                if (!byPath.TryGetValue(path, out var node))
                {
                    node = new MutableNode(path, placement.Groups[level], policy.Group[level].Kind);
                    byPath[path] = node;
                    parentList.Add(node);
                }

                parent = node;
                parentList = node.Children;
            }

            var leaf = new MutableNode(placement.Project.Name, placement.Project.Name, "project")
            {
                Role = placement.Role,
                Project = placement.Project.RelativePath,
                Types = facts.Types(placement.Project.Name),
                GroupPath = placement.Groups,
            };

            parentList.Add(leaf);
            byPath[leaf.Id] = leaf;
            _ = parent;
        }

        var leaves = byPath.Values.Where(n => n.Project is not null).ToList();
        var edges = BuildEdges(placements, leaves, policy);

        foreach (var node in top)
        {
            roots.Add(node.Freeze());
        }

        return new Model
        {
            Title = policy.Title.Length > 0 ? policy.Title : new DirectoryInfo(root).Name,
            Root = root,
            Nodes = roots,
            Edges = edges,
        };
    }

    private static IReadOnlyList<Edge> BuildEdges(
        IReadOnlyList<Placement> placements,
        IReadOnlyList<MutableNode> leaves,
        Policy policy)
    {
        var byName = leaves.ToDictionary(l => l.Id, StringComparer.Ordinal);
        var edges = new List<Edge>();

        foreach (var placement in placements)
        {
            if (!byName.TryGetValue(placement.Project.Name, out var from))
            {
                continue;
            }

            foreach (var reference in placement.Project.References)
            {
                if (!byName.TryGetValue(reference, out var to))
                {
                    continue;
                }

                edges.Add(new Edge
                {
                    From = from.Id,
                    To = to.Id,
                    Kind = "dependency",
                    Violates = Rules.Check(from, to, policy),
                });
            }
        }

        return edges;
    }
}

/// <summary>
/// A container while it is still being assembled.
/// </summary>
internal sealed class MutableNode(string id, string label, string kind)
{
    /// <summary>Identity.</summary>
    public string Id { get; } = id;

    /// <summary>Displayed name.</summary>
    public string Label { get; } = label;

    /// <summary>Extractor's word for this container.</summary>
    public string Kind { get; } = kind;

    /// <summary>Role within the parent.</summary>
    public string? Role { get; init; }

    /// <summary>Project file, for a leaf.</summary>
    public string? Project { get; init; }

    /// <summary>Group names above this node, outermost first.</summary>
    public IReadOnlyList<string> GroupPath { get; init; } = Array.Empty<string>();

    /// <summary>Types, for a leaf.</summary>
    public IReadOnlyList<TypeNode> Types { get; init; } = Array.Empty<TypeNode>();

    /// <summary>Nested containers.</summary>
    public List<MutableNode> Children { get; } = new();

    /// <summary>Produces the immutable node written to the model.</summary>
    public Node Freeze() => new()
    {
        Id = Id,
        Label = Label,
        Kind = Kind,
        Role = Role,
        Project = Project,
        Children = Children.Select(c => c.Freeze()).ToList(),
        Types = Types,
    };
}

/// <summary>
/// Decides whether one container may reference another.
/// </summary>
internal static class Rules
{
    /// <summary>
    /// Id of the first rule the reference breaks, or null when it breaks none.
    /// </summary>
    public static string? Check(MutableNode from, MutableNode to, Policy policy)
    {
        foreach (var rule in policy.Rules)
        {
            if (!Matches(rule.Subject, from, from, policy))
            {
                continue;
            }

            var allowed = rule.MayReference.Any(selector => Matches(selector, to, from, policy));

            if (!allowed)
            {
                return rule.Id;
            }
        }

        return null;
    }

    private static bool Matches(Selector selector, MutableNode node, MutableNode subject, Policy policy)
    {
        if (selector.Role is not null
            && !string.Equals(selector.Role, node.Role, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (selector.Id is not null && !string.Equals(selector.Id, node.Id, StringComparison.Ordinal))
        {
            return false;
        }

        if (selector.Same is null)
        {
            return true;
        }

        // The second axis: the target must share the named ancestor with the subject.
        var level = IndexOfKind(policy, selector.Same);

        if (level < 0 || node.GroupPath.Count <= level || subject.GroupPath.Count <= level)
        {
            return false;
        }

        return string.Equals(node.GroupPath[level], subject.GroupPath[level], StringComparison.Ordinal);
    }

    private static int IndexOfKind(Policy policy, string kind)
    {
        for (var i = 0; i < policy.Group.Count; i++)
        {
            if (string.Equals(policy.Group[i].Kind, kind, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
