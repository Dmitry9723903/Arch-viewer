using System.Text.Json;

namespace ArchViewer.Extract;

/// <summary>
/// Applies a policy to a model somebody else produced.
/// <para>
/// The rules of an architecture are not a property of a language. Until
/// this existed, only the .NET extractor applied them, so a Python, PHP,
/// TypeScript or C++ repository was drawn without one line being marked —
/// not because it kept its rules, but because nobody checked. A map that
/// cannot show a violation is a map that quietly reports none.
/// </para>
/// <para>
/// The alternative was a policy reader in each extractor, in four
/// languages. That is the same knowledge written four times, and the day
/// they disagree the answer depends on which language a module happens to
/// be written in.
/// </para>
/// </summary>
internal static class Judge
{
    /// <summary>
    /// Reads a model, marks every reference that breaks a rule, and writes
    /// it back with the page beside it.
    /// </summary>
    /// <param name="modelPath">Model file to judge.</param>
    /// <param name="policyPath">Policy to apply.</param>
    /// <param name="outPath">Page to write, or null to write beside the model.</param>
    /// <returns>Zero when the model was judged.</returns>
    public static int Apply(string modelPath, string? policyPath, string? outPath)
    {
        if (!File.Exists(modelPath))
        {
            Console.Error.WriteLine($"No such model: {modelPath}");
            return 1;
        }

        Model? model;

        try
        {
            model = JsonSerializer.Deserialize<Model>(
                File.ReadAllText(modelPath), ModelJson.Options);
        }
        catch (JsonException problem)
        {
            Console.Error.WriteLine($"{modelPath} is not a model file: {problem.Message}");
            return 1;
        }

        if (model is null)
        {
            Console.Error.WriteLine($"{modelPath} is empty.");
            return 1;
        }

        Policy policy;

        try
        {
            policy = Policy.Load(
                policyPath,
                string.IsNullOrEmpty(model.Root) ? Directory.GetCurrentDirectory() : model.Root);
        }
        catch (PolicyException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        var judged = Judged(model, policy, out var crossings, out var unknown);
        var json = JsonSerializer.Serialize(judged, ModelJson.Options);
        var page = outPath ?? Path.ChangeExtension(modelPath, ".html");

        File.WriteAllText(Path.ChangeExtension(page, ".json"), json);
        File.WriteAllText(page, Program.Page(json));

        var broken = judged.Edges.Count(e => e.Violates is not null);

        Console.WriteLine($"rules      {policy.Rules.Count}");
        Console.WriteLine(broken == crossings
            ? $"edges      {judged.Edges.Count}, {crossings} crossing a boundary"
            : $"edges      {judged.Edges.Count}, {crossings} "
              + $"crossing{(crossings == 1 ? "" : "s")} ({broken} references)");
        Console.WriteLine($"written    {page}");

        if (unknown > 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"{unknown} references name something this model does not hold, "
                + "and were left unjudged.");
        }

        return 0;
    }

    /// <summary>
    /// The same model with every reference judged against the policy.
    /// </summary>
    private static Model Judged(Model model, Policy policy, out int crossings, out int unknown)
    {
        var index = new Dictionary<string, MutableNode>(StringComparer.Ordinal);

        foreach (var node in model.Nodes)
        {
            Place(node, Array.Empty<string>(), null, index);
        }

        var edges = new List<Edge>(model.Edges.Count);
        var broken = new HashSet<string>(StringComparer.Ordinal);
        var missing = 0;

        foreach (var edge in model.Edges)
        {
            if (!index.TryGetValue(edge.From, out var from)
                || !index.TryGetValue(edge.To, out var to))
            {
                missing++;
                edges.Add(Same(edge, null));
                continue;
            }

            // Two containers with the same owner are one boundary's inside.
            // What a module does within itself is its own business, and the
            // policy speaks about boundaries.
            var crosses = !ReferenceEquals(Owner(from), Owner(to));
            var violates = crosses ? Rules.Check(from, to, policy, edge.Kind) : null;

            if (violates is not null)
            {
                broken.Add($"{Owner(from)?.Id}\u0000{Owner(to)?.Id}\u0000{violates}");
            }

            edges.Add(Same(edge, violates));
        }

        crossings = broken.Count;
        unknown = missing;

        return new Model
        {
            Title = model.Title,
            Root = model.Root,
            Nodes = model.Nodes,
            Edges = edges,
        };
    }

    /// <summary>
    /// The same reference, with the verdict this run reached. An edge is
    /// re-judged from the policy every time, never trusted from the file:
    /// a stale verdict is the one thing worse than no verdict.
    /// </summary>
    private static Edge Same(Edge edge, string? violates) => new()
    {
        From = edge.From,
        To = edge.To,
        Kind = edge.Kind,
        Origin = edge.Origin,
        Violates = violates,
    };

    /// <summary>
    /// The container whose rules apply to a node: itself when it carries a
    /// role, otherwise the nearest ancestor that does.
    /// </summary>
    private static MutableNode? Owner(MutableNode node) =>
        node.Role is not null ? node : node.Boundary;

    /// <summary>
    /// Records a node and everything inside it, remembering what stands above.
    /// </summary>
    private static void Place(
        Node node,
        IReadOnlyList<string> groups,
        MutableNode? boundary,
        Dictionary<string, MutableNode> index)
    {
        var placed = new MutableNode(node.Id, node.Label, node.Kind)
        {
            Role = node.Role,
            Boundary = boundary,
            Project = node.Project,
            GroupPath = groups,
        };

        index[node.Id] = placed;

        var own = node.Role is not null ? placed : boundary;
        var below = groups.Append(node.Label).ToList();

        foreach (var type in node.Types)
        {
            // A type answers to whatever container declares it, and stands
            // at the same place in the grouping. A rule about roles judges
            // a reference between types exactly as it judges one between
            // the modules holding them.
            index[type.Id] = new MutableNode(type.Id, type.Name, "type")
            {
                Boundary = own,
                GroupPath = groups,
            };
        }

        foreach (var child in node.Children)
        {
            Place(child, below, own, index);
        }
    }
}
