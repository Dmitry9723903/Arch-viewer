using System.Xml.Linq;

namespace ArchViewer.Extract;

/// <summary>
/// One project file, the projects it references, and the packages it consumes.
/// A package whose name matches a project of this same repository is an
/// internal dependency wearing a package's clothes: a repository that ships
/// its platform as packages and consumes them that way has real edges no
/// reading of ProjectReference will find.
/// </summary>
public sealed record ProjectFile(
    string Name,
    string RelativePath,
    IReadOnlyList<string> References,
    IReadOnlyList<string> Packages);

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

            var (references, packages) = Read(file);

            projects.Add(new ProjectFile(
                Path.GetFileNameWithoutExtension(file),
                relative,
                references,
                packages));
        }

        return projects.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    private static bool IsExcluded(string relativePath, IReadOnlyList<string> exclude) =>
        relativePath.Split('/').Any(segment =>
            exclude.Any(x => string.Equals(segment, x, StringComparison.OrdinalIgnoreCase)));

    private static (IReadOnlyList<string> References, IReadOnlyList<string> Packages) Read(
        string projectFile)
    {
        XDocument document;

        try
        {
            document = XDocument.Load(projectFile);
        }
        catch (System.Xml.XmlException)
        {
            // An unreadable project file is reported as having nothing rather
            // than stopping the whole run.
            return (Array.Empty<string>(), Array.Empty<string>());
        }

        var elements = document.Descendants().ToList();

        var references = elements
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var packages = elements
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => include!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return (references, packages);
    }
}
