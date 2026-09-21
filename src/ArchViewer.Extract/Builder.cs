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

            foreach (var group in placement.Groups)
            {
                path = path.Length == 0 ? group.Name : $"{path}/{group.Name}";

                if (!byPath.TryGetValue(path, out var node))
                {
                    node = new MutableNode(path, group.Name, group.Kind);
                    byPath[path] = node;
                    parentList.Add(node);
                }

                parent = node;
                parentList = node.Children;
            }

            var declared = facts.Types(placement.Project.Name);

            var leaf = new MutableNode(placement.Project.Name, placement.Project.Name, "project")
            {
                Role = placement.Role,
                Project = placement.Project.RelativePath,
                Types = declared,
                GroupPath = placement.Groups.Select(g => g.Name).ToList(),
            };

            if (policy.Types is { } arrangement
                && string.Equals(arrangement.By, "namespace", StringComparison.OrdinalIgnoreCase))
            {
                Namespaces.Arrange(leaf, declared, placement.Project.Name, arrangement);
            }

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
/// Arranges a project's types into the tree its namespaces describe.
/// Where a project is the module — one assembly per boundary — this is
/// noise. Where a repository has few projects and many namespaces, this is
/// the only place its structure is written down.
/// </summary>
internal static class Namespaces
{
    /// <summary>
    /// Replaces a project's flat list of types with containers per namespace,
    /// keeping in place those declared in the project's own root namespace.
    /// </summary>
    public static void Arrange(
        MutableNode project,
        IReadOnlyList<TypeNode> types,
        string assemblyName,
        TypeGrouping arrangement)
    {
        var own = new List<TypeNode>();
        var nested = new Dictionary<string, MutableNode>(StringComparer.Ordinal);

        foreach (var type in types)
        {
            var space = NamespaceOf(type.Id);
            var tail = arrangement.TrimAssemblyPrefix ? Trim(space, assemblyName) : space;

            if (tail.Length == 0)
            {
                own.Add(type);
                continue;
            }

            Place(project, nested, tail, type, arrangement.Kind);
        }

        project.Types = own;
    }

    private static void Place(
        MutableNode project,
        Dictionary<string, MutableNode> nested,
        string tail,
        TypeNode type,
        string kind)
    {
        var parts = tail.Split('.');
        var parent = project;
        var path = project.Id;

        foreach (var part in parts)
        {
            path = $"{path}.{part}";

            if (!nested.TryGetValue(path, out var node))
            {
                node = new MutableNode(path, part, kind);
                nested[path] = node;
                parent.Children.Add(node);
            }

            parent = node;
        }

        parent.Types = parent.Types.Append(type).ToList();
    }

    private static string NamespaceOf(string fullName)
    {
        var cut = fullName.LastIndexOf('.');
        return cut < 0 ? "" : fullName[..cut];
    }

    private static string Trim(string space, string assemblyName)
    {
        if (string.Equals(space, assemblyName, StringComparison.Ordinal))
        {
            return "";
        }

        return space.StartsWith(assemblyName + ".", StringComparison.Ordinal)
            ? space[(assemblyName.Length + 1)..]
            : space;
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

    /// <summary>Types declared directly in this container.</summary>
    public IReadOnlyList<TypeNode> Types { get; set; } = Array.Empty<TypeNode>();

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
