using Microsoft.CodeAnalysis;

namespace Balalaika.Workspace;

/// <summary>
/// Finds the projects whose stored reference rows may bind differently after the declared surface of other projects
/// changed: inside a solution through project references, across loaded solutions through the assembly name (a
/// consumer that references the changed project as a NuGet package or a DLL) or the csproj path (a consumer solution
/// that includes the same project file).
/// </summary>
public static class DependentProjects
{
    /// <summary>The given projects plus everything that references them, directly or transitively, within the solution.</summary>
    public static IReadOnlySet<ProjectId> WithTransitiveDependents(Solution solution, IEnumerable<ProjectId> projects)
    {
        HashSet<ProjectId> result = [];
        ProjectDependencyGraph graph = solution.GetProjectDependencyGraph();
        foreach (ProjectId id in projects)
        {
            if (!solution.ContainsProject(id))
            {
                continue;
            }

            result.Add(id);
            result.UnionWith(graph.GetProjectsThatTransitivelyDependOnThisProject(id));
        }

        return result;
    }

    /// <summary>
    /// Projects of <paramref name="solution"/> that consume one of the changed projects from outside its own
    /// solution: by a metadata reference whose file name is one of <paramref name="assemblyNames"/>, or because the
    /// project itself is one of <paramref name="projectPaths"/> (the same csproj included in two solutions).
    /// </summary>
    public static IReadOnlySet<ProjectId> ReferencingAssemblies(Solution solution, IReadOnlyCollection<string> assemblyNames, IReadOnlyCollection<string> projectPaths)
    {
        HashSet<ProjectId> result = [];
        HashSet<string> wantedAssemblies = new(assemblyNames, StringComparer.OrdinalIgnoreCase);
        HashSet<string> wantedPaths = new(projectPaths, StringComparer.OrdinalIgnoreCase);
        if (wantedAssemblies.Count == 0 && wantedPaths.Count == 0)
        {
            return result;
        }

        foreach (Project project in solution.Projects)
        {
            if (project.FilePath is null)
            {
                continue;
            }

            if (wantedPaths.Contains(project.FilePath))
            {
                result.Add(project.Id);
                continue;
            }

            foreach (MetadataReference reference in project.MetadataReferences)
            {
                if (reference is PortableExecutableReference { FilePath: { } filePath }
                    && wantedAssemblies.Contains(Path.GetFileNameWithoutExtension(filePath)))
                {
                    result.Add(project.Id);
                    break;
                }
            }
        }

        return result;
    }
}
