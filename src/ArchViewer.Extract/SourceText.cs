using System.Text.RegularExpressions;

namespace ArchViewer.Extract;

/// <summary>
/// The repository's own C# text: where each type is declared, and how far
/// that declaration reaches.
/// <para>
/// A PDB describes executable lines and nothing else. A type holding no code
/// — an enum, an interface, a record of properties — produces no sequence
/// point at all and so has no location there, and the last statement of a
/// type is never the end of the type. Both answers are in the text, which is
/// on disk anyway.
/// </para>
/// </summary>
internal sealed class SourceText
{
    // Three shapes the first version missed, all of them ordinary C#.
    // "record struct Money" puts a second keyword before the name, and
    // matching only the first left the name as "struct": fifty-two value
    // types of one repository lost their text that way.
    // "delegate Task Execute(...)" puts a return type there instead, and was
    // not matched at all.
    // The name is therefore whatever identifier stands last before the thing
    // that opens the declaration — a brace, a parameter list, a base list or,
    // for a delegate, the semicolon.
    private static readonly Regex Declares = new(
        @"\b(class|record|struct|interface|enum)(?:\s+(?:struct|class))?\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    // A delegate names its return type before its own name, so the type
    // keywords above cannot find it: "public delegate Task Execute(...)".
    // The name is the identifier immediately before the parameter list.
    private static readonly Regex DeclaresDelegate = new(
        @"\bdelegate\s+.+?\s+([A-Za-z_][A-Za-z0-9_]*)\s*[(<]",
        RegexOptions.Compiled);

    private static readonly Regex Namespaces = new(
        @"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)",
        RegexOptions.Compiled);

    private readonly string _root;
    private readonly Dictionary<string, List<Declaration>> _byQualified = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Declaration>> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _lines = new(StringComparer.Ordinal);

    private SourceText(string root) => _root = root;

    /// <summary>Where a type is declared: the file, and the line declaring it.</summary>
    internal readonly record struct Declaration(string File, int Index);

    /// <summary>
    /// Reads every C# file under the repository once and records what each
    /// declares. The cost is one pass over text that is being read anyway.
    /// </summary>
    public static SourceText Scan(string root, IReadOnlyList<string> exclude)
    {
        var text = new SourceText(root);
        var skip = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

            if (relative.Split('/').Any(skip.Contains))
            {
                continue;
            }

            string[] lines;

            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }

            text._lines[relative] = lines;
            text.Index(relative, lines);
        }

        return text;
    }

    /// <summary>
    /// Where a type is declared, preferring the file the PDB named — a
    /// partial type is declared in several, and the build knows which one
    /// carries the part that mattered.
    /// </summary>
    public Declaration? Find(string? @namespace, string typeName, string? preferredFile)
    {
        var bare = Bare(typeName);

        var candidates = @namespace is not null
                         && _byQualified.TryGetValue($"{@namespace}.{bare}", out var qualified)
            ? qualified
            : _byName.GetValueOrDefault(bare);

        if (candidates is null || candidates.Count == 0)
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate.File, preferredFile, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    /// <summary>
    /// The whole of a type as written: its documentation and attributes, its
    /// declaration, and its body down to the brace that closes it.
    /// </summary>
    public (int Start, int End, string Text)? Fragment(Declaration declaration, DateTime built, int limit = 3000)
    {
        if (!_lines.TryGetValue(declaration.File, out var lines))
        {
            return null;
        }

        // Lines and members come from a build; the text comes from disk now.
        // A file edited since that build describes something the metadata has
        // never seen, and no text is better than contradictory text.
        if (built != DateTime.MinValue && Changed(declaration.File, built))
        {
            return null;
        }

        var from = Above(lines, declaration.Index);
        var to = End(lines, declaration.Index);

        if (to < declaration.Index)
        {
            return null;
        }

        var count = Math.Min(to - from + 1, limit);

        return count <= 0 ? null : (from + 1, from + count, string.Join('\n', lines.Skip(from).Take(count)));
    }

    private bool Changed(string relative, DateTime built)
    {
        try
        {
            return File.GetLastWriteTimeUtc(Path.Combine(_root, relative)) > built;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private void Index(string relative, string[] lines)
    {
        var @namespace = "";

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith('*'))
            {
                continue;
            }

            var inNamespace = Namespaces.Match(line);

            if (inNamespace.Success)
            {
                @namespace = inNamespace.Groups[1].Value;
                continue;
            }

            var declaration = Declares.Match(line);
            var name = declaration.Success
                ? declaration.Groups[2].Value
                : DeclaresDelegate.Match(line) is { Success: true } asDelegate
                    ? asDelegate.Groups[1].Value
                    : null;

            // "new class" is not a declaration, and neither is a mention
            // inside a string; requiring the keyword to open a word of the
            // line's code is as far as text goes without a parser.
            if (name is null)
            {
                continue;
            }
            var found = new Declaration(relative, i);

            Add(_byName, name, found);

            if (@namespace.Length > 0)
            {
                Add(_byQualified, $"{@namespace}.{name}", found);
            }
        }
    }

    private static void Add(Dictionary<string, List<Declaration>> into, string key, Declaration declaration)
    {
        if (!into.TryGetValue(key, out var list))
        {
            list = new List<Declaration>();
            into[key] = list;
        }

        list.Add(declaration);
    }

    /// <summary>
    /// The first line of the fragment: whatever documents or annotates the
    /// declaration, immediately above it.
    /// </summary>
    private static int Above(string[] lines, int declaration)
    {
        var top = declaration;

        while (top > 0)
        {
            var above = lines[top - 1].TrimStart();

            if (above.StartsWith("///", StringComparison.Ordinal)
                || above.StartsWith("//", StringComparison.Ordinal)
                || above.StartsWith('[')
                || above.StartsWith('*')
                || above.StartsWith("/*", StringComparison.Ordinal))
            {
                top--;
                continue;
            }

            break;
        }

        return top;
    }

    /// <summary>
    /// The line closing the type: the brace matching the one that opens its
    /// body, counted over code only — braces inside comments, strings and
    /// characters are not braces.
    /// </summary>
    private static int End(string[] lines, int declaration)
    {
        var depth = 0;
        var opened = false;
        var inBlockComment = false;

        for (var i = declaration; i < lines.Length; i++)
        {
            var line = lines[i];

            for (var c = 0; c < line.Length; c++)
            {
                if (inBlockComment)
                {
                    if (line[c] == '*' && c + 1 < line.Length && line[c + 1] == '/')
                    {
                        inBlockComment = false;
                        c++;
                    }

                    continue;
                }

                if (line[c] == '/' && c + 1 < line.Length && line[c + 1] == '/')
                {
                    break;
                }

                if (line[c] == '/' && c + 1 < line.Length && line[c + 1] == '*')
                {
                    inBlockComment = true;
                    c++;
                    continue;
                }

                if (line[c] == '"' || line[c] == '\'')
                {
                    c = SkipQuoted(line, c);
                    continue;
                }

                if (line[c] == '{')
                {
                    depth++;
                    opened = true;
                    continue;
                }

                if (line[c] == '}')
                {
                    depth--;

                    if (opened && depth == 0)
                    {
                        return i;
                    }

                    continue;
                }

                // A record declared with a parameter list and no body ends at
                // its semicolon: "public sealed record Money(decimal Amount);"
                if (!opened && line[c] == ';')
                {
                    return i;
                }
            }
        }

        return opened ? lines.Length - 1 : declaration;
    }

    /// <summary>
    /// Index of the quote closing a string or character literal, so that what
    /// it contains is never read as code.
    /// </summary>
    private static int SkipQuoted(string line, int start)
    {
        var quote = line[start];
        var verbatim = start > 0 && line[start - 1] == '@';

        for (var i = start + 1; i < line.Length; i++)
        {
            if (!verbatim && line[i] == '\\')
            {
                i++;
                continue;
            }

            if (line[i] != quote)
            {
                continue;
            }

            if (verbatim && i + 1 < line.Length && line[i + 1] == quote)
            {
                i++;
                continue;
            }

            return i;
        }

        // Unterminated on this line — a verbatim string spanning lines. Give
        // up on the rest of the line rather than read it as code.
        return line.Length;
    }

    private static string Bare(string typeName)
    {
        var nested = typeName.LastIndexOf('+');
        var name = nested >= 0 ? typeName[(nested + 1)..] : typeName;
        return name.Split('`')[0];
    }
}
