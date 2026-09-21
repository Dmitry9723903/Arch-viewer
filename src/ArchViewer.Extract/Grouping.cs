namespace ArchViewer.Extract;

/// <summary>
/// Where a project ends up: the chain of group names above it, outermost first.
/// </summary>
public sealed record Placement(ProjectFile Project, IReadOnlyList<string> Groups, string? Role);

/// <summary>
/// Applies the policy's grouping levels to the projects that were found.
/// </summary>
public static class Grouping
{
    /// <summary>
    /// Works out, for each project, which containers it sits inside.
    /// </summary>
    public static IReadOnlyList<Placement> Place(
        IReadOnlyList<ProjectFile> projects,
        Policy policy,
        AssemblyFacts facts)
    {
        var placements = new List<Placement>();

        foreach (var project in projects)
        {
            var groups = new List<string>();

            foreach (var level in policy.Group)
            {
                var name = NameFor(project, level, facts);
                groups.Add(string.IsNullOrWhiteSpace(name) ? "(ungrouped)" : name!);
            }

            placements.Add(new Placement(project, groups, RoleFor(project)));
        }

        return placements;
    }

    /// <summary>
    /// The role is the last dot-separated part of the project name — the
    /// conventional way a .NET repository says what a project is.
    /// </summary>
    private static string? RoleFor(ProjectFile project)
    {
        var parts = project.Name.Split('.');
        return parts.Length > 1 ? parts[^1].ToLowerInvariant() : null;
    }

    private static string? NameFor(ProjectFile project, GroupRule level, AssemblyFacts facts)
    {
        var name = level.From switch
        {
            "path-segment" => Segment(project.RelativePath, level.Index),
            "name-part" => Part(project.Name, level.Part),
            "assembly-attribute" => facts.Attribute(project.Name, level.Attribute, level.Argument),
            _ => null,
        };

        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return level.Fallback is null ? null : NameFor(project, level.Fallback, facts);
    }

    private static string? Segment(string relativePath, int index)
    {
        var segments = relativePath.Split('/');

        // The last segment is the file itself, the one before it the project folder.
        return index >= 0 && index < segments.Length - 1 ? segments[index] : null;
    }

    private static string? Part(string name, int part)
    {
        var parts = name.Split('.');
        return part >= 0 && part < parts.Length ? parts[part] : null;
    }
}
