using System.Text.Json;

namespace ArchViewer.Extract;

/// <summary>
/// Joins models read by different extractors into one map.
/// <para>
/// A repository is often more than one ecosystem: a .NET solution with a
/// TypeScript client, a Python test harness beside a server. Each is read by
/// its own extractor, and each produces its own model — which leaves the
/// reader with two pages and no way to see one beside the other.
/// </para>
/// <para>
/// Each model becomes a container of its own, named by its title. They are not
/// blended: a namespace of one ecosystem and a folder of another are different
/// things, and putting them on one row unlabelled is the mistake this
/// repository has already made once and written down.
/// </para>
/// </summary>
internal static class Merge
{
    /// <summary>
    /// Reads the given models and returns one holding all of them.
    /// </summary>
    /// <param name="paths">Model files to join.</param>
    /// <param name="title">Name for the whole.</param>
    /// <returns>The joined model, or null when nothing could be read.</returns>
    public static Model? Read(IReadOnlyList<string> paths, string? title)
    {
        var parts = new List<(string Path, Model Model)>();

        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"No such model: {path}");
                continue;
            }

            try
            {
                var model = JsonSerializer.Deserialize<Model>(File.ReadAllText(path), ModelJson.Options);

                if (model is not null)
                {
                    parts.Add((path, model));
                }
            }
            catch (JsonException problem)
            {
                Console.Error.WriteLine($"Not a model: {path}: {problem.Message}");
            }
        }

        if (parts.Count == 0)
        {
            return null;
        }

        var nodes = new List<Node>();
        var edges = new List<Edge>();
        var taken = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (path, model) in parts)
        {
            var name = Distinct(model.Title, Path.GetFileNameWithoutExtension(path), taken);

            nodes.Add(new Node
            {
                Id = name,
                Label = name,
                Kind = "part",
                Children = model.Nodes,
                Types = Array.Empty<TypeNode>(),
            });

            edges.AddRange(model.Edges);
        }

        return new Model
        {
            Title = title ?? string.Join(" + ", parts.Select(p => p.Model.Title)),
            Root = Common(parts.Select(p => p.Model.Root).ToList()),
            Nodes = nodes,
            Edges = edges,
        };
    }

    /// <summary>
    /// A name no other part has taken. Two models titled the same would
    /// otherwise become one box holding both, which says something false.
    /// </summary>
    private static string Distinct(string preferred, string fallback, HashSet<string> taken)
    {
        if (taken.Add(preferred))
        {
            return preferred;
        }

        if (taken.Add(fallback))
        {
            return fallback;
        }

        for (var n = 2; ; n++)
        {
            var candidate = $"{preferred} ({n})";

            if (taken.Add(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// The deepest directory all the parts sit under, which is the repository
    /// they describe. Source links resolve against it.
    /// </summary>
    private static string Common(IReadOnlyList<string> roots)
    {
        if (roots.Count == 0)
        {
            return "";
        }

        var shared = roots[0].Replace('\\', '/').TrimEnd('/').Split('/');

        foreach (var root in roots.Skip(1))
        {
            var parts = root.Replace('\\', '/').TrimEnd('/').Split('/');
            var keep = 0;

            while (keep < shared.Length && keep < parts.Length
                   && string.Equals(shared[keep], parts[keep], StringComparison.Ordinal))
            {
                keep++;
            }

            shared = shared[..keep];
        }

        return shared.Length == 0 ? roots[0] : string.Join('/', shared);
    }
}
