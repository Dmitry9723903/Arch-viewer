namespace ArchViewer.Extract;

/// <summary>
/// One level of grouping as it actually resolved: the name, and the kind of
/// the rule that produced it. A fallback may describe a different sort of
/// thing than the rule it stands in for, and saying so is the point.
/// </summary>
public sealed record Group(string Name, string Kind);

/// <summary>
/// Where a project ends up: the chain of groups above it, outermost first.
/// </summary>
public sealed record Placement(ProjectFile Project, IReadOnlyList<Group> Groups, string? Role);

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
            var groups = new List<Group>();

            foreach (var level in policy.Group)
            {
                groups.Add(GroupFor(project, level, facts));
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

    private static Group GroupFor(ProjectFile project, GroupRule level, AssemblyFacts facts)
    {
        var name = NameFor(project, level, facts);

        if (!string.IsNullOrWhiteSpace(name))
        {
            return new Group(name!, level.Kind);
        }

        return level.Fallback is null
            ? new Group("(ungrouped)", level.Kind)
            : GroupFor(project, level.Fallback, facts);
    }

    private static string? NameFor(ProjectFile project, GroupRule level, AssemblyFacts facts)
    {
        return level.From switch
        {
            "path-segment" => Segment(project.RelativePath, level.Index),
            "name-part" => Part(project.Name, level.Part),
            "assembly-attribute" => facts.Attribute(project.Name, level.Attribute, level.Argument),
            "literal" => level.Value,
            _ => null,
        };
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
