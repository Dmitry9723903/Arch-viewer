using System.Reflection;
using System.Text.Json;

namespace ArchViewer.Extract;

/// <summary>
/// Entry point: reads a repository and writes the model plus a self-contained page.
/// </summary>
public static class Program
{
    /// <summary>
    /// Usage: archview &lt;repository&gt; [--policy path] [--out path] [--no-types]
    /// </summary>
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("usage: archview <repository> [--policy <file>] [--out <file.html>] [--no-types]");
            return args.Length == 0 ? 1 : 0;
        }

        var root = Path.GetFullPath(args[0]);

        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"No such directory: {root}");
            return 1;
        }

        var policyPath = Option(args, "--policy");
        var outPath = Option(args, "--out") ?? Path.Combine(Environment.CurrentDirectory, "arch.html");
        var withTypes = !args.Contains("--no-types");

        var policy = Policy.Load(policyPath, root);
        var projects = ProjectReader.Read(root, policy);

        if (projects.Count == 0)
        {
            Console.Error.WriteLine($"No .csproj files found under {root}");
            return 1;
        }

        using var facts = withTypes
            ? AssemblyFacts.Load(root, projects.Select(p => p.Name).ToList(), policy.Exclude)
            : AssemblyFacts.Load(root, Array.Empty<string>(), policy.Exclude);

        var placements = Grouping.Place(projects, policy, facts);
        var model = Builder.Build(root, policy, placements, facts);

        var modelPath = Path.ChangeExtension(outPath, ".json");

        if (policyPath is not null
            && Path.GetFullPath(modelPath) == Path.GetFullPath(policyPath))
        {
            Console.Error.WriteLine(
                $"The model would be written over the policy file ({policyPath}). " +
                "Choose a different --out name.");
            return 1;
        }

        var json = JsonSerializer.Serialize(model, ModelJson.Options);
        File.WriteAllText(modelPath, json);
        File.WriteAllText(outPath, Page(json));

        Report(model, projects.Count, facts.Known.Count, outPath, facts);
        return 0;
    }

    private static void Report(
        Model model,
        int projects,
        int assemblies,
        string outPath,
        AssemblyFacts facts)
    {
        var references = model.Edges.Count(e => e.Violates is not null);
        var violations = Crossings(model);
        var types = Count(model.Nodes);

        Console.WriteLine($"projects   {projects}");
        Console.WriteLine($"assemblies {assemblies} read");
        Console.WriteLine($"types      {types}");
        Console.WriteLine(references == violations
            ? $"edges      {model.Edges.Count}, {violations} crossing a boundary"
            : $"edges      {model.Edges.Count}, {violations} crossings ({references} references)");
        Console.WriteLine($"written    {outPath}");

        if (facts.PassedOver > 0)
        {
            Console.WriteLine(
                $"           {facts.PassedOver} older copies passed over; the newest of each was read");
        }

        var stale = Stale(model.Nodes, facts.Built);

        if (stale > 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"{stale} types have no source shown: their files changed after the build was made.");
            Console.WriteLine("Rebuild the repository so metadata and text describe the same code.");
        }

        if (assemblies == 0)
        {
            Console.WriteLine();
            Console.WriteLine("No assemblies found: build the repository to get types and source lines.");
        }
    }

    private static int Stale(IReadOnlyList<Node> nodes, DateTime built)
    {
        if (built == DateTime.MinValue)
        {
            return 0;
        }

        return nodes.Sum(n =>
            n.Types.Count(t => t.File is not null && t.Source is null) + Stale(n.Children, built));
    }

    private static int Count(IReadOnlyList<Node> nodes) =>
        nodes.Sum(n => n.Types.Count + Count(n.Children));

    /// <summary>
    /// Boundaries broken, not references breaking them. The same crossing is
    /// recorded once between the projects and again between the types that
    /// make the reference; the screen counts crossings, and the terminal must
    /// say the same number or one of the two is lying.
    /// </summary>
    private static int Crossings(Model model)
    {
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);

        void Index(Node node)
        {
            foreach (var type in node.Types)
            {
                owner[type.Id] = node.Id;
            }

            foreach (var child in node.Children)
            {
                Index(child);
            }
        }

        foreach (var node in model.Nodes)
        {
            Index(node);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var edge in model.Edges)
        {
            if (edge.Violates is null)
            {
                continue;
            }

            var from = owner.GetValueOrDefault(edge.From, edge.From);
            var to = owner.GetValueOrDefault(edge.To, edge.To);
            seen.Add($"{from}\u0000{to}\u0000{edge.Violates}");
        }

        return seen.Count;
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string Page(string json)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("viewer.html", StringComparison.Ordinal));

        if (resource is null)
        {
            throw new InvalidOperationException("The viewer page is missing from this build.");
        }

        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        var template = reader.ReadToEnd();

        return template.Replace("/*MODEL*/null", json, StringComparison.Ordinal);
    }
}
