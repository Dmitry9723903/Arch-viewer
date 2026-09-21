using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text.RegularExpressions;
using System.Reflection.PortableExecutable;

namespace ArchViewer.Extract;

/// <summary>
/// Everything read from compiled assemblies: attributes, types, members and —
/// when a portable PDB sits beside the assembly — file and line.
/// Assemblies are read through <see cref="MetadataLoadContext"/>, which
/// inspects without executing. Absent assemblies are not an error: the graph
/// is simply drawn without types.
/// </summary>
public sealed class AssemblyFacts : IDisposable
{
    private readonly MetadataLoadContext? _context;
    private readonly Dictionary<string, Assembly> _assemblies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceIndex> _sources = new(StringComparer.Ordinal);
    private readonly string _root;

    private AssemblyFacts(string root, MetadataLoadContext? context)
    {
        _root = root;
        _context = context;
    }

    /// <summary>Assembly names that were found and read.</summary>
    public IReadOnlyCollection<string> Known => _assemblies.Keys;

    /// <summary>
    /// Finds built assemblies under the repository and opens them for reading.
    /// </summary>
    public static AssemblyFacts Load(string root, IReadOnlyList<string> wanted)
    {
        var binaries = FindBinaries(root, wanted, out var everything);

        if (binaries.Count == 0)
        {
            return new AssemblyFacts(root, null);
        }

        var runtime = Directory.EnumerateFiles(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll");

        // The resolver gets every assembly found, not just the wanted ones:
        // resolving an attribute's type needs whatever assembly declared it,
        // which is often a package rather than a project of this repository.
        var resolver = new PathAssemblyResolver(everything.Concat(runtime));
        var context = new MetadataLoadContext(resolver);
        var facts = new AssemblyFacts(root, context);

        foreach (var (name, path) in binaries)
        {
            try
            {
                facts._assemblies[name] = context.LoadFromAssemblyPath(path);
                facts._sources[name] = SourceIndex.Open(path, root);
            }
            catch (Exception e) when (e is BadImageFormatException or FileLoadException)
            {
                // Unreadable assembly: the project still appears, without types.
            }
        }

        return facts;
    }

    /// <summary>
    /// Value of a constructor argument of an assembly-level attribute, or null.
    /// </summary>
    public string? Attribute(string assemblyName, string? attributeName, int argument)
    {
        if (attributeName is null || !_assemblies.TryGetValue(assemblyName, out var assembly))
        {
            return null;
        }

        foreach (var data in assembly.GetCustomAttributesData())
        {
            string? full, shortName;

            try
            {
                full = data.AttributeType.FullName;
                shortName = data.AttributeType.Name;
            }
            catch (Exception e) when (e is FileNotFoundException or TypeLoadException or BadImageFormatException)
            {
                // An attribute whose declaring assembly is not here. Skipping it
                // is correct: it cannot be the one the policy names, because the
                // policy names one of this repository's own.
                continue;
            }

            var matches = string.Equals(full, attributeName, StringComparison.Ordinal)
                          || string.Equals(shortName, attributeName, StringComparison.Ordinal)
                          || string.Equals(shortName, attributeName + "Attribute", StringComparison.Ordinal);

            if (!matches)
            {
                continue;
            }

            try
            {
                if (data.ConstructorArguments.Count <= argument)
                {
                    return null;
                }

                var value = data.ConstructorArguments[argument];
                return EnumName(value.ArgumentType, value.Value) ?? value.Value?.ToString();
            }
            catch (Exception e) when (Unresolvable(e))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Public types declared by an assembly, with their members.
    /// </summary>
    public IReadOnlyList<TypeNode> Types(string assemblyName)
    {
        if (!_assemblies.TryGetValue(assemblyName, out var assembly))
        {
            return Array.Empty<TypeNode>();
        }

        var source = _sources.GetValueOrDefault(assemblyName);
        var types = new List<TypeNode>();

        Type[] found;
        try
        {
            found = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            found = e.Types.Where(t => t is not null).Select(t => t!).ToArray();
        }

        foreach (var type in found)
        {
            if (!type.IsPublic && !type.IsNestedPublic)
            {
                continue;
            }

            if (type.Name.StartsWith('<'))
            {
                continue;
            }

            try
            {
                var location = source?.Locate(type);
                var fragment = location is { } at && source is not null
                    ? source.Fragment(at.File, at.First, at.Last, type.Name)
                    : null;

                types.Add(new TypeNode
                {
                    Id = type.FullName ?? type.Name,
                    Name = Pretty(type.Name),
                    Stereotype = Stereotype(type),
                    File = location?.File,
                    Line = fragment?.Start ?? location?.First,
                    EndLine = location?.Last,
                    Source = fragment?.Text,
                    Members = Members(type),
                });
            }
            catch (Exception e) when (Unresolvable(e))
            {
                // A type whose shape refers to something not present here.
                // It is still real, so it is listed — without its members.
                types.Add(new TypeNode
                {
                    Id = type.FullName ?? type.Name,
                    Name = Pretty(type.Name),
                    Stereotype = "class",
                });
            }
        }

        return types.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Members as C# writes them — type before name, not the UML "name : type".
    /// Besides being the language's own order, it keeps a member such as
    /// <c>ClientSecret</c> from reading as <c>client_secret: value</c> and
    /// tripping the secret scanners of the repository being inspected.
    /// </summary>
    private static IReadOnlyList<MemberNode> Members(Type type)
    {
        var members = new List<MemberNode>();

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance
                                   | BindingFlags.Static | BindingFlags.DeclaredOnly;

        Read(() =>
        {
            foreach (var property in type.GetProperties(flags))
            {
                Read(() => members.Add(new MemberNode
                {
                    Text = $"{Pretty(property.PropertyType.Name)} {property.Name}",
                }));
            }
        });

        Read(() =>
        {
            foreach (var method in type.GetMethods(flags))
            {
                if (method.IsSpecialName)
                {
                    continue;
                }

                Read(() =>
                {
                    var args = string.Join(", ", method.GetParameters().Select(p => Pretty(p.ParameterType.Name)));
                    members.Add(new MemberNode { Text = $"{Pretty(method.ReturnType.Name)} {method.Name}({args})" });
                });
            }
        });

        Read(() =>
        {
            foreach (var field in type.GetFields(flags))
            {
                if (!field.IsPublic)
                {
                    continue;
                }

                Read(() => members.Add(new MemberNode
                {
                    Text = $"{Pretty(field.FieldType.Name)} {field.Name}",
                }));
            }
        });

        return members;
    }

    /// <summary>
    /// Name of the enum member with this value. Assemblies read for inspection
    /// are not runtime types, so the value arrives as a number and the name has
    /// to be found among the enum's literal fields.
    /// </summary>
    private static string? EnumName(Type? type, object? value)
    {
        if (type is null || value is null || !type.IsEnum)
        {
            return null;
        }

        try
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (Equals(field.GetRawConstantValue(), value))
                {
                    return field.Name;
                }
            }
        }
        catch (Exception e) when (Unresolvable(e))
        {
        }

        return null;
    }

    /// <summary>
    /// References this assembly's types make to other types: what they
    /// inherit, what they implement, and what they hold in fields and
    /// properties.
    /// Method parameters and return types are left out on purpose. They would
    /// multiply the edges several times over while adding the least: a type
    /// that merely passes another one through is coupled to it far more
    /// loosely than one that stores it.
    /// </summary>
    /// <param name="assemblyName">Assembly whose types to read.</param>
    /// <param name="known">Full names of the types worth pointing at.</param>
    /// <returns>Edges between type identities.</returns>
    public IReadOnlyList<(string From, string To, string Kind)> TypeEdges(
        string assemblyName,
        ISet<string> known)
    {
        if (!_assemblies.TryGetValue(assemblyName, out var assembly))
        {
            return Array.Empty<(string, string, string)>();
        }

        var edges = new HashSet<(string From, string To, string Kind)>();

        Type[] found;
        try
        {
            found = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            found = e.Types.Where(t => t is not null).Select(t => t!).ToArray();
        }

        foreach (var type in found)
        {
            var from = type.FullName;

            if (from is null || !known.Contains(from))
            {
                continue;
            }

            Read(() =>
            {
                var baseType = Name(type.BaseType);

                if (baseType is not null && known.Contains(baseType) && baseType != from)
                {
                    edges.Add((from, baseType, "inheritance"));
                }
            });

            Read(() =>
            {
                foreach (var contract in type.GetInterfaces())
                {
                    var name = Name(contract);

                    if (name is not null && known.Contains(name) && name != from)
                    {
                        edges.Add((from, name, "implements"));
                    }
                }
            });

            const BindingFlags members = BindingFlags.Public | BindingFlags.NonPublic
                                         | BindingFlags.Instance | BindingFlags.Static
                                         | BindingFlags.DeclaredOnly;

            Read(() =>
            {
                foreach (var field in type.GetFields(members))
                {
                    Held(edges, from, field.FieldType, known);
                }
            });

            Read(() =>
            {
                foreach (var property in type.GetProperties(members))
                {
                    Held(edges, from, property.PropertyType, known);
                }
            });
        }

        return edges.ToList();
    }

    private static void Held(
        HashSet<(string, string, string)> edges,
        string from,
        Type? held,
        ISet<string> known)
    {
        foreach (var name in Unwrap(held))
        {
            if (known.Contains(name) && name != from)
            {
                edges.Add((from, name, "association"));
            }
        }
    }

    /// <summary>
    /// Names a type points at: itself, or — for an array or a generic such as
    /// a collection — the types inside it, which is what the holder is really
    /// coupled to.
    /// </summary>
    private static IEnumerable<string> Unwrap(Type? type)
    {
        if (type is null)
        {
            yield break;
        }

        if (type.IsArray)
        {
            foreach (var inner in Unwrap(type.GetElementType()))
            {
                yield return inner;
            }

            yield break;
        }

        var name = Name(type);

        if (name is not null)
        {
            yield return name;
        }

        if (!type.IsGenericType)
        {
            yield break;
        }

        Type[] arguments;

        try
        {
            arguments = type.GetGenericArguments();
        }
        catch (Exception e) when (Unresolvable(e))
        {
            yield break;
        }

        foreach (var argument in arguments)
        {
            foreach (var inner in Unwrap(argument))
            {
                yield return inner;
            }
        }
    }

    private static string? Name(Type? type)
    {
        if (type is null)
        {
            return null;
        }

        try
        {
            var full = type.IsGenericType && !type.IsGenericTypeDefinition
                ? type.GetGenericTypeDefinition().FullName
                : type.FullName;

            return full is null ? null : full.Split('[')[0];
        }
        catch (Exception e) when (Unresolvable(e))
        {
            return null;
        }
    }

    /// <summary>
    /// Runs a metadata read, swallowing the failures that mean "the assembly
    /// that declares this is not here". Anything else is a real fault and
    /// propagates.
    /// </summary>
    private static void Read(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (Unresolvable(e))
        {
        }
    }

    private static bool Unresolvable(Exception e) =>
        e is FileNotFoundException or TypeLoadException or BadImageFormatException
            or MissingMethodException or NotSupportedException or InvalidOperationException;

    private static string Stereotype(Type type)
    {
        if (type.IsInterface)
        {
            return "interface";
        }

        if (type.IsEnum)
        {
            return "enum";
        }

        if (type.IsValueType)
        {
            return "struct";
        }

        return type.IsAbstract && type.IsSealed ? "static"
            : type.IsAbstract ? "abstract"
            : "class";
    }

    private static string Pretty(string name)
    {
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick < 0 ? name : name[..tick];
    }

    private static Dictionary<string, string> FindBinaries(
        string root,
        IReadOnlyList<string> wanted,
        out IReadOnlyList<string> everything)
    {
        var want = new HashSet<string>(wanted, StringComparer.Ordinal);
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var all = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var dll in Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(dll);

            if (!all.ContainsKey(name))
            {
                all[name] = dll;
            }

            if (want.Contains(name) && !found.ContainsKey(name))
            {
                found[name] = dll;
            }
        }

        everything = all.Values.ToList();
        return found;
    }

    /// <summary>Releases the reading context.</summary>
    public void Dispose()
    {
        foreach (var source in _sources.Values)
        {
            source.Dispose();
        }

        _context?.Dispose();
    }
}

/// <summary>
/// Portable PDB beside an assembly: maps a type to the file and line that
/// declared it, by looking at the first sequence point of its first method.
/// </summary>
internal sealed class SourceIndex : IDisposable
{
    private readonly MetadataReaderProvider? _provider;
    private readonly MetadataReader? _reader;
    private readonly string _root;

    private SourceIndex(string root, MetadataReaderProvider? provider, MetadataReader? reader)
    {
        _root = root;
        _provider = provider;
        _reader = reader;
    }

    /// <summary>Opens the PDB beside an assembly, or returns an index that knows nothing.</summary>
    public static SourceIndex Open(string assemblyPath, string root)
    {
        var pdb = Path.ChangeExtension(assemblyPath, ".pdb");

        if (!File.Exists(pdb))
        {
            return new SourceIndex(root, null, null);
        }

        try
        {
            var stream = File.OpenRead(pdb);
            var provider = MetadataReaderProvider.FromPortablePdbStream(stream, MetadataStreamOptions.PrefetchMetadata);
            return new SourceIndex(root, provider, provider.GetMetadataReader());
        }
        catch (Exception e) when (e is BadImageFormatException or IOException)
        {
            return new SourceIndex(root, null, null);
        }
    }

    /// <summary>
    /// File and the line range a type occupies, or null when unknown.
    /// The range is the span of every sequence point its methods produce:
    /// a PDB has no notion of "the type", only of executable lines.
    /// </summary>
    public (string File, int First, int Last)? Locate(Type type)
    {
        if (_reader is null)
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                   | BindingFlags.Instance | BindingFlags.Static
                                   | BindingFlags.DeclaredOnly;

        string? file = null;
        var first = int.MaxValue;
        var last = 0;

        foreach (var method in type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags)))
        {
            foreach (var point in Points(method.MetadataToken))
            {
                file ??= point.File;

                if (!string.Equals(point.File, file, StringComparison.Ordinal))
                {
                    // A partial type spread over files: keep the first one seen.
                    continue;
                }

                first = Math.Min(first, point.Line);
                last = Math.Max(last, point.Line);
            }
        }

        return file is null ? null : (file, first, last);
    }

    /// <summary>
    /// The type's own lines of source, read from disk at extraction time,
    /// together with the number of the first line returned.
    /// A PDB points only at executable lines, so the declaration itself, its
    /// documentation and its attributes are never in the range it gives —
    /// they are found by walking up to the line that declares the type.
    /// </summary>
    public (int Start, string Text)? Fragment(
        string relativeFile,
        int first,
        int last,
        string typeName,
        int limit = 400)
    {
        var path = Path.Combine(_root, relativeFile);

        if (!File.Exists(path))
        {
            return null;
        }

        string[] lines;

        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return null;
        }

        var from = Declaration(lines, first, typeName);
        var to = Math.Min(lines.Length, last + 1);
        var count = Math.Min(to - from, limit);

        return count <= 0 ? null : (from + 1, string.Join('\n', lines.Skip(from).Take(count)));
    }

    /// <summary>
    /// Index of the line the fragment starts at: the type's declaration, with
    /// the documentation and attributes sitting above it.
    /// </summary>
    private static int Declaration(string[] lines, int first, string typeName)
    {
        var bare = typeName.Split('`')[0];
        var declares = new Regex(
            @"\b(class|record|struct|interface|enum)\s+" + Regex.Escape(bare) + @"\b");

        var floor = Math.Max(0, first - 80);

        for (var i = Math.Min(first - 1, lines.Length - 1); i >= floor; i--)
        {
            if (!declares.IsMatch(lines[i]))
            {
                continue;
            }

            // Take whatever documents or annotates it, immediately above.
            var top = i;

            while (top > 0)
            {
                var above = lines[top - 1].TrimStart();

                if (above.StartsWith("///", StringComparison.Ordinal)
                    || above.StartsWith("//", StringComparison.Ordinal)
                    || above.StartsWith('['))
                {
                    top--;
                    continue;
                }

                break;
            }

            return top;
        }

        // No declaration found — a compiler-generated type, or one whose name
        // does not appear in its own file. Start where the PDB pointed.
        return Math.Max(0, first - 1);
    }

    private IEnumerable<(string File, int Line)> Points(int metadataToken)
    {
        var found = new List<(string File, int Line)>();

        try
        {
            var rowNumber = metadataToken & 0x00FFFFFF;

            if (rowNumber == 0 || rowNumber > _reader!.GetTableRowCount(TableIndex.MethodDebugInformation))
            {
                return found;
            }

            var handle = MetadataTokens.MethodDebugInformationHandle(rowNumber);
            var info = _reader!.GetMethodDebugInformation(handle);

            if (info.SequencePointsBlob.IsNil)
            {
                return found;
            }

            foreach (var point in info.GetSequencePoints())
            {
                if (point.IsHidden)
                {
                    continue;
                }

                var document = _reader.GetDocument(point.Document);
                var path = _reader.GetString(document.Name);
                var relative = Path.GetRelativePath(_root, path).Replace('\\', '/');

                found.Add((relative, point.StartLine));
                found.Add((relative, point.EndLine));
            }

            return found;
        }
        catch (BadImageFormatException)
        {
            return found;
        }
    }

    /// <summary>Closes the PDB.</summary>
    public void Dispose() => _provider?.Dispose();
}
