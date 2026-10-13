namespace Balalaika.Watcher;

public enum ChangeKind
{
    Ignored,
    Source,
    ProjectFile,
    SolutionFile,
    Git,
}

public static class ChangeClassifier
{
    private static readonly string[] IgnoredSegments =
    [
        @"\bin\", @"\obj\", @"\node_modules\", @"\.vs\", @"\TestResults\", @"\.idea\",
    ];

    private static readonly string[] ProjectFileNames =
    [
        "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "packages.lock.json", "global.json", "nuget.config",
    ];

    /// <param name="path">Full path reported by the file system watcher or found on disk.</param>
    /// <param name="ignoredFolders">Folder names from configuration; a path with such a directory on it is <see cref="ChangeKind.Ignored"/>.</param>
    public static ChangeKind Classify(string path, IReadOnlyCollection<string>? ignoredFolders = null)
    {
        string normalized = path.Replace('/', '\\');

        int gitIndex = normalized.IndexOf(@"\.git\", StringComparison.OrdinalIgnoreCase);
        if (gitIndex >= 0)
        {
            string insideGit = normalized[(gitIndex + 6)..];
            return insideGit.Equals("HEAD", StringComparison.OrdinalIgnoreCase) ? ChangeKind.Git : ChangeKind.Ignored;
        }

        foreach (string segment in IgnoredSegments)
        {
            if (normalized.Contains(segment, StringComparison.OrdinalIgnoreCase))
            {
                return ChangeKind.Ignored;
            }
        }

        if (ignoredFolders is { Count: > 0 })
        {
            foreach (string folder in ignoredFolders)
            {
                string trimmed = folder.Trim().Trim('\\', '/');
                if (trimmed.Length > 0 && normalized.Contains('\\' + trimmed + '\\', StringComparison.OrdinalIgnoreCase))
                {
                    return ChangeKind.Ignored;
                }
            }
        }

        string ext = Path.GetExtension(normalized);
        string name = Path.GetFileName(normalized);

        if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return ChangeKind.Source;
        }

        if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".props", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".targets", StringComparison.OrdinalIgnoreCase)
            || ProjectFileNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return ChangeKind.ProjectFile;
        }

        if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) || ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return ChangeKind.SolutionFile;
        }

        return ChangeKind.Ignored;
    }
}
