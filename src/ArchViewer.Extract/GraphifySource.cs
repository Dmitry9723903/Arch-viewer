using System.Text.Json;

namespace ArchViewer.Extract;

/// <summary>
/// References taken from a graph another tool produced, when one is lying
/// beside the repository.
/// <para>
/// Metadata cannot see inside a method: this tool reads what a type inherits,
/// implements and holds, and stops there on purpose. What a method accepts
/// and returns, and what it calls, is therefore invisible — and for a layer
/// whose whole job is transport, that is most of its coupling.
/// </para>
/// <para>
/// Such a graph is a different kind of evidence and is kept apart: these
/// edges carry their own kinds and say where they came from, so a rule can
/// decline to judge them and a reader can tell them from the metadata.
/// Only what that tool marked as extracted is taken; what it inferred is a
/// guess, and guesses do not belong beside measurements.
/// </para>
/// </summary>
internal static class GraphifySource
{
    /// <summary>
    /// Reads the graph, if there is one, and returns references between types
    /// this model already knows.
    /// </summary>
    /// <param name="root">Repository root.</param>
    /// <param name="typesByName">Model types, by simple name.</param>
    /// <param name="typesByFile">Model types, by file and simple name.</param>
    /// <returns>Edges, or nothing when no graph is present.</returns>
    public static IReadOnlyList<(string From, string To, string Kind)> Read(
        string root,
        IReadOnlyDictionary<string, IReadOnlyList<string>> typesByName,
        IReadOnlyDictionary<(string File, string Name), string> typesByFile)
    {
        var path = Path.Combine(root, "graphify-out", "graph.json");

        if (!File.Exists(path))
        {
            return Array.Empty<(string, string, string)>();
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return Array.Empty<(string, string, string)>();
        }

        using (document)
        {
            return Collect(document.RootElement, typesByName, typesByFile);
        }
    }

    private static IReadOnlyList<(string From, string To, string Kind)> Collect(
        JsonElement graph,
        IReadOnlyDictionary<string, IReadOnlyList<string>> typesByName,
        IReadOnlyDictionary<(string File, string Name), string> typesByFile)
    {
        if (!graph.TryGetProperty("nodes", out var nodes)
            || !graph.TryGetProperty("links", out var links))
        {
            return Array.Empty<(string, string, string)>();
        }

        var label = new Dictionary<string, string>(StringComparer.Ordinal);
        var file = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var node in nodes.EnumerateArray())
        {
            var id = Text(node, "id");

            if (id is null)
            {
                continue;
            }

            label[id] = Text(node, "label") ?? "";
            file[id] = (Text(node, "source_file") ?? "").Replace('\\', '/');
        }

        // Which type declares each method: a reference made inside a method
        // is made by the type that declares it, and that is the boundary a
        // rule speaks about.
        var declaredBy = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var link in links.EnumerateArray())
        {
            if (Text(link, "relation") == "method"
                && Text(link, "source") is { } owner
                && Text(link, "target") is { } method)
            {
                declaredBy[method] = owner;
            }
        }

        var edges = new HashSet<(string From, string To, string Kind)>();

        foreach (var link in links.EnumerateArray())
        {
            var relation = Text(link, "relation");

            var kind = relation switch
            {
                "references" => "signature",
                "calls" => "call",
                _ => null,
            };

            if (kind is null || Text(link, "confidence") != "EXTRACTED")
            {
                continue;
            }

            var from = Text(link, "source");
            var to = Text(link, "target");

            if (from is null || to is null || !declaredBy.TryGetValue(from, out var owner))
            {
                // Only what a method does. A reference from a type itself is
                // a field or a property, which the metadata already saw and
                // saw more precisely.
                continue;
            }

            var source = Resolve(owner, label, file, typesByFile);
            var target = Match(label.GetValueOrDefault(to), typesByName);

            if (source is null || target is null || source == target)
            {
                continue;
            }

            edges.Add((source, target, kind));
        }

        return edges.ToList();
    }

    /// <summary>
    /// The model's type for a node that declares something: matched on file
    /// and name together, because a class is declared in its own file.
    /// </summary>
    private static string? Resolve(
        string node,
        IReadOnlyDictionary<string, string> label,
        IReadOnlyDictionary<string, string> file,
        IReadOnlyDictionary<(string File, string Name), string> typesByFile) =>
        typesByFile.GetValueOrDefault((file.GetValueOrDefault(node, ""), label.GetValueOrDefault(node, "")));

    /// <summary>
    /// The model's type for a node that is merely mentioned. Its recorded
    /// file is where the mention sits, not where the type lives, so only the
    /// name is usable — and a name shared by two types is dropped rather than
    /// guessed at.
    /// </summary>
    private static string? Match(
        string? name,
        IReadOnlyDictionary<string, IReadOnlyList<string>> typesByName)
    {
        if (string.IsNullOrEmpty(name) || !typesByName.TryGetValue(name, out var found))
        {
            return null;
        }

        return found.Count == 1 ? found[0] : null;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
