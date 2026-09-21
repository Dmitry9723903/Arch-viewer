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
        var edges = BuildEdges(placements, leaves, policy).ToList();

        if (policy.Types?.Edges == true)
        {
            edges.AddRange(TypeEdges(placements, top, facts, policy));
        }

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

    /// <summary>
    /// References between types, with the rules applied to the containers that
    /// hold them: a rule speaks about boundaries, and a type's boundary is the
    /// container it sits in.
    /// </summary>
    private static IEnumerable<Edge> TypeEdges(
        IReadOnlyList<Placement> placements,
        IReadOnlyList<MutableNode> roots,
        AssemblyFacts facts,
        Policy policy)
    {
        // Walked, not taken from the dictionary of groups: namespace
        // containers are built while placing a project's types and never
        // enter it.
        var holder = new Dictionary<string, MutableNode>(StringComparer.Ordinal);

        void Collect(MutableNode node)
        {
            foreach (var type in node.Types)
            {
                holder[type.Id] = node;
            }

            foreach (var child in node.Children)
            {
                Collect(child);
            }
        }

        foreach (var root in roots)
        {
            Collect(root);
        }

        var known = new HashSet<string>(holder.Keys, StringComparer.Ordinal);
        var edges = new List<Edge>();

        foreach (var placement in placements)
        {
            foreach (var (from, to, kind) in facts.TypeEdges(placement.Project.Name, known))
            {
                if (!holder.TryGetValue(from, out var source)
                    || !holder.TryGetValue(to, out var target))
                {
                    continue;
                }

                // A reference that stays inside the subject — to itself, or to
                // a container nested within it — crosses no boundary, so no
                // rule about boundaries applies. The edge is kept: "who sits
                // on this base class" is a question worth answering, and it is
                // usually answered within one namespace.
                var crosses = !Encloses(source, target) && !Encloses(target, source);

                edges.Add(new Edge
                {
                    From = from,
                    To = to,
                    Kind = kind,
                    Violates = crosses ? Rules.Check(source, target, policy) : null,
                });
            }
        }

        return edges;
    }

    /// <summary>
    /// Whether one container is the other or holds it somewhere beneath.
    /// </summary>
    private static bool Encloses(MutableNode outer, MutableNode inner) =>
        ReferenceEquals(outer, inner)
        || inner.Id.StartsWith(outer.Id + ".", StringComparison.Ordinal)
        || inner.Id.StartsWith(outer.Id + "/", StringComparison.Ordinal);

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
                Link(edges, byName, from, reference, "dependency", policy);
            }

            // A package that is also a project here is this repository
            // depending on itself through its own feed.
            foreach (var package in placement.Project.Packages)
            {
                Link(edges, byName, from, package, "package", policy);
            }
        }

        return edges;
    }

    private static void Link(
        List<Edge> edges,
        Dictionary<string, MutableNode> byName,
        MutableNode from,
        string target,
        string kind,
        Policy policy)
    {
        if (!byName.TryGetValue(target, out var to) || ReferenceEquals(from, to))
        {
            return;
        }

        edges.Add(new Edge
        {
            From = from.Id,
            To = to.Id,
            Kind = kind,
            Violates = Rules.Check(from, to, policy),
        });
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
                node = new MutableNode(path, part, kind)
                {
                    // A namespace container inherits the placement of the
                    // project that declares it: a rule saying "within the same
                    // module" must hold for it too.
                    GroupPath = project.GroupPath,
                    Role = part.ToLowerInvariant(),
                };

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

        // Naming a container means naming what is inside it. Allowing
        // "CIP.Platform" but not "CIP.Platform.Time" is a distinction nobody
        // intends, and every policy written before types had containers relied
        // on the two being the same thing.
        if (selector.Id is not null && !SitsUnder(node.Id, selector.Id))
        {
            return false;
        }

        if (selector.IdExactly is not null
            && !string.Equals(selector.IdExactly, node.Id, StringComparison.Ordinal))
        {
            return false;
        }

        if (selector.Name is not null
            && !string.Equals(selector.Name, node.Label, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (selector.EndsWith is not null
            && !node.Id.EndsWith(selector.EndsWith, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (selector.Under is not null && !SitsUnder(node.Id, selector.Under))
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

    /// <summary>
    /// Whether an id is the named container or something beneath it.
    /// Compared by separator, so "Interfaces" does not swallow "InterfacesX".
    /// </summary>
    private static bool SitsUnder(string id, string ancestor)
    {
        if (id.EndsWith(ancestor, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var index = id.IndexOf(ancestor, StringComparison.OrdinalIgnoreCase);

        if (index < 0)
        {
            return false;
        }

        var after = index + ancestor.Length;
        return after < id.Length && (id[after] == '.' || id[after] == '/');
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
