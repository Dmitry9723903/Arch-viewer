using System.Xml.Linq;

namespace ArchViewer.Extract;

/// <summary>
/// One project file and the projects it references.
/// </summary>
public sealed record ProjectFile(string Name, string RelativePath, IReadOnlyList<string> References);

/// <summary>
/// Reads project files and their references straight from XML.
/// A reference is a fact of the project file and exists before any build.
/// </summary>
public static class ProjectReader
{
    /// <summary>
    /// Finds every project under <paramref name="root"/> that the policy does not exclude.
    /// </summary>
    public static IReadOnlyList<ProjectFile> Read(string root, Policy policy)
    {
        var projects = new List<ProjectFile>();

        foreach (var file in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

            if (IsExcluded(relative, policy.Exclude))
            {
                continue;
            }

            projects.Add(new ProjectFile(
                Path.GetFileNameWithoutExtension(file),
                relative,
                ReadReferences(file)));
        }

        return projects.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    private static bool IsExcluded(string relativePath, IReadOnlyList<string> exclude) =>
        relativePath.Split('/').Any(segment =>
            exclude.Any(x => string.Equals(segment, x, StringComparison.OrdinalIgnoreCase)));

    private static IReadOnlyList<string> ReadReferences(string projectFile)
    {
        XDocument document;

        try
        {
            document = XDocument.Load(projectFile);
        }
        catch (System.Xml.XmlException)
        {
            // An unreadable project file is reported as having no references
            // rather than stopping the whole run.
            return Array.Empty<string>();
        }

        return document
            .Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
