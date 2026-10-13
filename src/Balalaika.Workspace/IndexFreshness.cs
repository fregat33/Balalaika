using Balalaika.Index;

namespace Balalaika.Workspace;

/// <summary>
/// Decides, without loading a solution, whether its index rows still describe the working tree: every tracked file
/// must exist with the recorded mtime and size, and no untracked <c>.cs</c> file may have appeared under a project
/// directory. Files the project's compilation excluded at its last load (recorded by the index pass) and files under
/// ignored folders do not count as untracked. Used by the startup warm-up to skip solutions that were indexed on an
/// earlier run and have not changed.
/// </summary>
public static class IndexFreshness
{
    private static readonly string[] SkippedFolders = ["bin", "obj", "node_modules", "TestResults"];

    /// <summary>True when the solution has index rows and none of them disagree with the disk. A solution never indexed is not fresh.</summary>
    public static bool IsFresh(IndexStore index, string solutionPath, SourceFilter? filter = null)
    {
        filter ??= SourceFilter.None;
        List<ProjectRecord> projects = index.GetProjects()
            .Where(p => string.Equals(p.SolutionPath, solutionPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (projects.Count == 0)
        {
            return false;
        }

        // Indexed before the current schema started recording some of its data (for example package versions): one more pass is needed.
        if (projects.Any(p => p.IndexedSchema < IndexStore.SchemaVersion))
        {
            return false;
        }

        HashSet<string> tracked = new(StringComparer.OrdinalIgnoreCase);
        foreach (ProjectRecord project in projects)
        {
            foreach (TreeEntry entry in index.GetTree(project.Id).Values)
            {
                // Rows under a folder that is ignored now must be dropped by an index pass before the index is right.
                if (filter.IsIgnored(entry.Path))
                {
                    return false;
                }

                tracked.Add(entry.Path);
                FileInfo info = new(entry.Path);
                if (!info.Exists || info.LastWriteTimeUtc.Ticks != entry.Mtime || info.Length != entry.Size)
                {
                    return false;
                }
            }
        }

        foreach (ProjectRecord project in projects)
        {
            string? projectDir = Path.GetDirectoryName(project.Path);
            if (projectDir is null || !Directory.Exists(projectDir))
            {
                return false;
            }

            IReadOnlySet<string> excluded = index.GetExcludedFiles(project.Id);
            if (EnumerateSourceFiles(projectDir, filter.IgnoredFolders).Any(file => !tracked.Contains(file) && !excluded.Contains(file)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// All <c>.cs</c> files under a project directory, skipping build output, package and tool folders, dot-folders and
    /// the given ignored folder names.
    /// </summary>
    public static IEnumerable<string> EnumerateSourceFiles(string projectDir, IReadOnlyCollection<string>? ignoredFolders = null)
    {
        Stack<string> dirs = new();
        dirs.Push(projectDir);
        while (dirs.Count > 0)
        {
            string dir = dirs.Pop();
            IEnumerable<string> files;
            IEnumerable<string> subs;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.cs");
                subs = Directory.EnumerateDirectories(dir);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string f in files)
            {
                yield return f;
            }

            foreach (string s in subs)
            {
                string name = Path.GetFileName(s);
                if (name.StartsWith('.')
                    || SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase)
                    || (ignoredFolders is { Count: > 0 } && ignoredFolders.Contains(name, StringComparer.OrdinalIgnoreCase)))
                {
                    continue;
                }

                dirs.Push(s);
            }
        }
    }
}
