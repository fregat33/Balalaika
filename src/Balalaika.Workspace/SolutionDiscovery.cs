namespace Balalaika.Workspace;

public static class SolutionDiscovery
{
    private static readonly string[] SkippedDirectories = ["bin", "obj", "node_modules", ".git", ".vs", "packages", "TestResults"];

    /// <summary>Finds every *.sln / *.slnx under the given roots, skipping build output folders.</summary>
    public static IReadOnlyList<string> FindSolutions(IEnumerable<string> roots)
    {
        List<string> result = [];
        foreach (string root in roots)
        {
            string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(root));
            if (!Directory.Exists(full))
            {
                continue;
            }

            Walk(full, result, depth: 0);
        }

        return Deduplicate(result);
    }

    /// <summary>
    /// Finds the solutions below <paramref name="directory"/> that a scan of <paramref name="roots"/> would report: the
    /// walk starts at the directory's depth below its root, so the depth limit and skipped folders still apply. Empty
    /// when the directory is outside every root, too deep, or inside a skipped folder.
    /// </summary>
    public static IReadOnlyList<string> FindSolutionsIn(string directory, IEnumerable<string> roots)
    {
        string full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(full))
        {
            return [];
        }

        foreach (string root in roots)
        {
            string rootFull = Path.GetFullPath(Environment.ExpandEnvironmentVariables(root)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string prefix = rootFull + Path.DirectorySeparatorChar;
            if (!(full + Path.DirectorySeparatorChar).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] segments = full.Length == rootFull.Length ? [] : full[prefix.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > MaxDepth || segments.Any(s => SkippedDirectories.Contains(s, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            List<string> result = [];
            Walk(full, result, segments.Length);
            return Deduplicate(result);
        }

        return [];
    }

    private static List<string> Deduplicate(List<string> found)
    {
        // A folder holding both Foo.sln and Foo.slnx describes one solution; keep the .sln.
        List<string> deduplicated = found
            .GroupBy(p => Path.Combine(Path.GetDirectoryName(p) ?? "", Path.GetFileNameWithoutExtension(p)), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(p => Path.GetExtension(p).Equals(".sln", StringComparison.OrdinalIgnoreCase) ? 0 : 1).First())
            .ToList();
        deduplicated.Sort(StringComparer.OrdinalIgnoreCase);
        return deduplicated;
    }

    private const int MaxDepth = 6;

    /// <summary>
    /// True when <paramref name="solutionPath"/> is a *.sln / *.slnx that <see cref="FindSolutions"/> would report for
    /// one of <paramref name="roots"/>: depth within the walk limit, no skipped folder on the way, and no same-name
    /// *.sln next to a *.slnx. Does not check that the file exists.
    /// </summary>
    public static bool QualifiesForDiscovery(string solutionPath, IEnumerable<string> roots)
    {
        string ext = Path.GetExtension(solutionPath);
        bool isSln = ext.Equals(".sln", StringComparison.OrdinalIgnoreCase);
        if (!isSln && !ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string full = Path.GetFullPath(solutionPath);
        string? dir = Path.GetDirectoryName(full);
        if (dir is null)
        {
            return false;
        }

        foreach (string root in roots)
        {
            string rootFull = Path.GetFullPath(Environment.ExpandEnvironmentVariables(root)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string prefix = rootFull + Path.DirectorySeparatorChar;
            if (!(dir + Path.DirectorySeparatorChar).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] segments = dir.Length == rootFull.Length ? [] : dir[prefix.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > MaxDepth || segments.Any(s => SkippedDirectories.Contains(s, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!isSln && File.Exists(Path.ChangeExtension(full, ".sln")))
            {
                return false;
            }

            return true;
        }

        return false;
    }

    private static void Walk(string dir, List<string> result, int depth)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(dir))
            {
                string ext = Path.GetExtension(file);
                if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) || ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(file);
                }
            }

            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                if (SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                Walk(sub, result, depth + 1);
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    public static string SolutionName(string solutionPath)
    {
        return Path.GetFileNameWithoutExtension(solutionPath);
    }
}
