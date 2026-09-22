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
            Console.WriteLine("       archview all <repository> [--out <file.html>] [--title <name>] [--policy <file>] [--no-build]");
            Console.WriteLine("       archview merge <model.json> … [--out <file.html>] [--title <name>]");
            return args.Length == 0 ? 1 : 0;
        }

        if (args[0] == "merge")
        {
            return Joined(args);
        }

        if (args[0] == "all")
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("all needs a repository to read.");
                return 1;
            }

            return Everything.Read(
                Path.GetFullPath(args[1]),
                Option(args, "--out") ?? Path.Combine(Environment.CurrentDirectory, "arch.html"),
                Option(args, "--title"),
                Option(args, "--policy"),
                build: !args.Contains("--no-build"));
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

        Policy policy;

        try
        {
            policy = Policy.Load(policyPath, root);
        }
        catch (PolicyException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }

        // A name given on the command line wins over the policy's, so a
        // caller joining several models can name each part by its ecosystem
        // rather than have every one of them carry the repository's name.
        if (Option(args, "--title") is { Length: > 0 } named)
        {
            policy.Title = named;
        }

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
        ReportUnread(root, policy, projects, Option(args, "--covered"));
        return 0;
    }

    /// <summary>
    /// Joins models written by several extractors into one page.
    /// </summary>
    private static int Joined(string[] args)
    {
        var models = args.Skip(1)
            .TakeWhile(argument => !argument.StartsWith("--", StringComparison.Ordinal))
            .ToList();

        if (models.Count == 0)
        {
            Console.Error.WriteLine("merge needs model files to join.");
            return 1;
        }

        var model = Merge.Read(models, Option(args, "--title"));

        if (model is null)
        {
            Console.Error.WriteLine("Nothing could be read.");
            return 1;
        }

        var outPath = Option(args, "--out") ?? Path.Combine(Environment.CurrentDirectory, "arch.html");
        var json = JsonSerializer.Serialize(model, ModelJson.Options);

        File.WriteAllText(Path.ChangeExtension(outPath, ".json"), json);
        File.WriteAllText(outPath, Page(json));

        Console.WriteLine($"parts      {models.Count}");
        Console.WriteLine($"types      {Count(model.Nodes)}");
        Console.WriteLine($"edges      {model.Edges.Count}");
        Console.WriteLine($"written    {outPath}");
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

    /// <summary>
    /// Areas of the repository this extractor cannot read. It knows .NET
    /// projects and nothing else, so a directory of Python or TypeScript is
    /// simply absent from the map — and a reader who sees it in the file
    /// listing but not on the screen has no way to tell whether it was
    /// skipped or lost. Saying so is the difference.
    /// </summary>
    /// <summary>
    /// Names the areas of the repository this run did not read.
    /// <para>
    /// <paramref name="covered"/> lists the languages another extractor in
    /// the same run will read. Without it a single command would announce
    /// Python as unread and then read it a second later, which is worse than
    /// saying nothing: a report that contradicts the run teaches the reader
    /// to stop reading reports.
    /// </para>
    /// </summary>
    private static void ReportUnread(
        string root,
        Policy policy,
        IReadOnlyList<ProjectFile> projects,
        string? covered = null)
    {
        var elsewhere = (covered ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var read = projects
            .Select(p => p.RelativePath.Split('/')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unread = new List<(string Area, string Language, int Files)>();
        var excluded = new List<(string Area, string Language, int Files)>();

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var area = Path.GetFileName(directory);

            if (area.StartsWith('.') || read.Contains(area))
            {
                continue;
            }

            var byPolicy = policy.Exclude.Contains(area, StringComparer.OrdinalIgnoreCase);

            var counted = Languages
                .Where(pair => !elsewhere.Contains(pair.Language))
                .Select(pair => (pair.Language, Files: CountFiles(directory, pair.Pattern)))
                .Where(x => x.Files > 0)
                .OrderByDescending(x => x.Files)
                .FirstOrDefault();

            if (counted.Files == 0)
            {
                continue;
            }

            (byPolicy ? excluded : unread).Add((area, counted.Language, counted.Files));
        }

        if (unread.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine(elsewhere.Count > 0
                ? "Not read — no extractor here reads these:"
                : "Not read — this extractor knows .NET projects only:");

            foreach (var (area, language, files) in unread.OrderBy(x => x.Area, StringComparer.Ordinal))
            {
                Console.WriteLine($"  {area}/ — {files} {language} files, no .csproj");
            }
        }

        if (excluded.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Excluded by the policy, and holding code:");

            foreach (var (area, language, files) in excluded.OrderBy(x => x.Area, StringComparer.Ordinal))
            {
                Console.WriteLine($"  {area}/ — {files} {language} files");
            }
        }
    }

    private static readonly (string Language, string Pattern)[] Languages =
    {
        ("Python", "*.py"),
        ("PHP", "*.php"),
        ("TypeScript", "*.ts"),
        ("JavaScript", "*.js"),
        ("C++", "*.cpp"),
        ("C", "*.c"),
        ("Java", "*.java"),
        ("Go", "*.go"),
        ("Rust", "*.rs"),
    };

    private static int CountFiles(string directory, string pattern)
    {
        try
        {
            // Other people's libraries are not this repository's code, and
            // counting them turns a note about thirteen files into one about
            // two thousand. The same places every extractor here skips.
            string[] theirs =
            {
                "/node_modules/", "/__pycache__/", "/dist/", "/build/",
                "/.venv/", "/venv/", "/env/", "/site-packages/", "/vendor/",
                "/.tox/", "/.mypy_cache/", "/.pytest_cache/",
            };

            return Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
                .Where(f => !theirs.Any(place => f.Contains(place, StringComparison.Ordinal)))
                .Take(5000)
                .Count();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
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
