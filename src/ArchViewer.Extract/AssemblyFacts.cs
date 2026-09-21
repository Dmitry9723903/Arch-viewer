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
    /// How many older copies of the wanted assemblies were passed over.
    /// </summary>
    public int PassedOver { get; init; }

    /// <summary>
    /// When the oldest assembly actually read was written. Source newer than
    /// this describes code the metadata has never seen.
    /// </summary>
    public DateTime Built { get; init; }

    /// <summary>
    /// Finds built assemblies under the repository and opens them for reading.
    /// </summary>
    public static AssemblyFacts Load(string root, IReadOnlyList<string> wanted)
    {
        var binaries = FindBinaries(root, wanted, out var everything, out var passedOver);

        if (binaries.Count == 0)
        {
            return new AssemblyFacts(root, null) { PassedOver = passedOver };
        }

        var runtime = Directory.EnumerateFiles(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll");

        // The web framework ships beside the base libraries, not among them,
        // and without it no type that touches a framework type can be read —
        // not just that member, the whole type's members are lost at once.
        // The failure is silent by design, which is what made it hard to see.
        //
        // The resolver gets every assembly found, not just the wanted ones:
        // resolving an attribute's type needs whatever assembly declared it,
        // which is often a package rather than a project of this repository.
        //
        // One path per assembly name, and the repository's own copy wins: the
        // context refuses a name it has already loaded, and the same assembly
        // reaches us from the runtime directory and from a shared framework
        // both.
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in everything.Concat(SharedFrameworks()).Concat(runtime))
        {
            var name = Path.GetFileNameWithoutExtension(path);

            if (!paths.ContainsKey(name))
            {
                paths[name] = path;
            }
        }

        var resolver = new PathAssemblyResolver(paths.Values);
        var context = new MetadataLoadContext(resolver);
        var facts = new AssemblyFacts(root, context)
        {
            PassedOver = passedOver,
            Built = binaries.Values
                .Select(path => File.GetLastWriteTimeUtc(path))
                .DefaultIfEmpty(DateTime.MinValue)
                .Min(),
        };

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
                    ? source.Fragment(at.File, at.First, at.Last, type.Name, Built)
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

        foreach (var property in Listed(() => type.GetProperties(flags)))
        {
            Read(() => members.Add(new MemberNode
            {
                Text = $"{Pretty(property.PropertyType.Name)} {property.Name}",
            }));
        }

        foreach (var method in Listed(() => type.GetMethods(flags)))
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

        foreach (var field in Listed(() => type.GetFields(flags)))
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

            // One guard per member, not one around the loop. A single field
            // whose type cannot be resolved must cost that field, not every
            // field after it: guarding the whole loop lost sixty-six edges on
            // one repository, silently, because the first unresolvable member
            // ended the iteration.
            FieldInfo[] fields;

            try
            {
                fields = type.GetFields(members);
            }
            catch (Exception e) when (Unresolvable(e))
            {
                fields = Array.Empty<FieldInfo>();
            }

            foreach (var field in fields)
            {
                Read(() => Held(edges, from, field.FieldType, known));
            }

            PropertyInfo[] properties;

            try
            {
                properties = type.GetProperties(members);
            }
            catch (Exception e) when (Unresolvable(e))
            {
                properties = Array.Empty<PropertyInfo>();
            }

            foreach (var property in properties)
            {
                Read(() => Held(edges, from, property.PropertyType, known));
            }
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
    /// <summary>
    /// Lists members, or nothing when the listing itself fails. The guard is
    /// on obtaining the list; each member is then read under its own.
    /// </summary>
    private static T[] Listed<T>(Func<T[]> list)
    {
        try
        {
            return list();
        }
        catch (Exception e) when (Unresolvable(e))
        {
            return Array.Empty<T>();
        }
    }

    private static void Read(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (Unresolvable(e))
        {
            // Swallowed by design: a type whose declaring assembly is not
            // here cannot be a boundary of this repository. Set ARCHVIEW_LOUD
            // to see what is being skipped — silence here has hidden a fault
            // before.
            if (Environment.GetEnvironmentVariable("ARCHVIEW_LOUD") is not null)
            {
                Console.Error.WriteLine($"skipped: {e.GetType().Name}: {e.Message}");
            }
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

    /// <summary>
    /// Assemblies to read, one path per name.
    /// A repository holds the same assembly many times over — one per
    /// configuration, plus a copy in every project that references it — and
    /// which one is walked first is an accident of the file system.
    /// Two things decide, in order.
    /// A copy in the output of the project that built it wins over a copy the
    /// build put in some consumer's folder: the timestamp of a copy is when it
    /// was copied, not when it was compiled, so "newest file" can name an
    /// older build. Among equals, the newest wins — that is what tells this
    /// week's Debug from last week's Release.
    /// How many copies were passed over is reported, because reading a
    /// week-old build in silence is how a map comes to describe code that no
    /// longer exists.
    /// </summary>
    /// <summary>
    /// Assemblies of the shared frameworks installed beside the runtime —
    /// ASP.NET Core and the like. Newest version of each framework.
    /// </summary>
    private static IEnumerable<string> SharedFrameworks()
    {
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var shared = Path.GetDirectoryName(Path.GetDirectoryName(runtimeDirectory));

        if (shared is null || !Directory.Exists(shared))
        {
            return Array.Empty<string>();
        }

        var files = new List<string>();

        foreach (var framework in Directory.EnumerateDirectories(shared))
        {
            if (string.Equals(
                    Path.GetFileName(framework),
                    Path.GetFileName(Path.GetDirectoryName(runtimeDirectory)),
                    StringComparison.Ordinal))
            {
                continue;
            }

            var newest = Directory.EnumerateDirectories(framework)
                .OrderBy(version => version, StringComparer.Ordinal)
                .LastOrDefault();

            if (newest is not null)
            {
                files.AddRange(Directory.EnumerateFiles(newest, "*.dll"));
            }
        }

        return files;
    }

    private static Dictionary<string, string> FindBinaries(
        string root,
        IReadOnlyList<string> wanted,
        out IReadOnlyList<string> everything,
        out int copies)
    {
        var want = new HashSet<string>(wanted, StringComparer.Ordinal);
        var newest = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var all = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var seen = 0;

        foreach (var dll in Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(dll);

            DateTime written;

            try
            {
                written = File.GetLastWriteTimeUtc(dll);
            }
            catch (IOException)
            {
                continue;
            }

            var own = IsOwnOutput(dll, name);

            if (!all.TryGetValue(name, out var best) || Better(own, written, best))
            {
                all[name] = new Candidate(dll, written, own);
            }

            if (!want.Contains(name))
            {
                continue;
            }

            seen++;

            if (!newest.TryGetValue(name, out var current) || Better(own, written, current))
            {
                newest[name] = new Candidate(dll, written, own);
            }
        }

        copies = seen - newest.Count;
        everything = all.Values.Select(x => x.Path).ToList();
        return newest.ToDictionary(pair => pair.Key, pair => pair.Value.Path, StringComparer.Ordinal);
    }

    /// <summary>One copy of an assembly, and what is known about it.</summary>
    private readonly record struct Candidate(string Path, DateTime Written, bool Own);

    /// <summary>
    /// Whether a candidate beats the one already held: its own output first,
    /// then the newer file.
    /// </summary>
    private static bool Better(bool own, DateTime written, Candidate held) =>
        own != held.Own ? own : written > held.Written;

    /// <summary>
    /// Whether this path is the output of the project that builds the
    /// assembly, rather than a copy placed beside a consumer. The project's
    /// own folder carries its name.
    /// </summary>
    private static bool IsOwnOutput(string path, string assemblyName)
    {
        var directory = Path.GetDirectoryName(path);

        while (directory is not null)
        {
            var folder = Path.GetFileName(directory);

            if (string.Equals(folder, assemblyName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Walk up only through the build's own layers; anything else means
            // this is a copy sitting in some other project's output.
            if (!IsBuildFolder(folder))
            {
                return false;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return false;
    }

    private static bool IsBuildFolder(string folder) =>
        folder.StartsWith("net", StringComparison.OrdinalIgnoreCase)
        || string.Equals(folder, "bin", StringComparison.OrdinalIgnoreCase)
        || string.Equals(folder, "Debug", StringComparison.OrdinalIgnoreCase)
        || string.Equals(folder, "Release", StringComparison.OrdinalIgnoreCase)
        || folder.Contains('-', StringComparison.Ordinal);

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
        DateTime built,
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
            // Lines come from the PDB of a build; the text comes from disk
            // now. A file edited since that build describes something the
            // metadata has never seen, and showing the two side by side puts
            // a week-old declaration next to today's body. No text is better
            // than contradictory text.
            if (built != DateTime.MinValue && File.GetLastWriteTimeUtc(path) > built)
            {
                return null;
            }

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
