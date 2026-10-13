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

        // A folder holding both Foo.sln and Foo.slnx describes one solution; keep the .sln.
        List<string> deduplicated = result
            .GroupBy(p => Path.Combine(Path.GetDirectoryName(p) ?? "", Path.GetFileNameWithoutExtension(p)), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(p => Path.GetExtension(p).Equals(".sln", StringComparison.OrdinalIgnoreCase) ? 0 : 1).First())
            .ToList();
        deduplicated.Sort(StringComparer.OrdinalIgnoreCase);
        return deduplicated;
    }

    private static void Walk(string dir, List<string> result, int depth)
    {
        if (depth > 6)
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
