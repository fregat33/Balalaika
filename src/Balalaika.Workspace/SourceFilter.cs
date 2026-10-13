namespace Balalaika.Workspace;

/// <summary>
/// Folder names that configuration (<see cref="BalalaikaOptions.IgnoredFolders"/>) keeps out of the index. A path is
/// ignored when any directory on it carries one of the names, so <c>Migrations</c> matches
/// <c>src\Db\Migrations\Init.cs</c> but not <c>src\Db\MigrationsHelper.cs</c>. Ignored files stay in the live
/// compilation so diagnostics match a real build; they get no index rows, are skipped by reconcile and freshness
/// checks, and are left out of live list results so both halves of a tool's output agree.
/// </summary>
public sealed class SourceFilter
{
    public static readonly SourceFilter None = new([]);

    private readonly string[] _folders;

    public SourceFilter(IEnumerable<string> ignoredFolders)
    {
        this._folders = ignoredFolders
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim().Trim('\\', '/'))
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyCollection<string> IgnoredFolders => this._folders;

    /// <summary>True when a directory on the path is named like an ignored folder. The file name itself is not compared.</summary>
    public bool IsIgnored(string path)
    {
        if (this._folders.Length == 0)
        {
            return false;
        }

        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        foreach (string segment in directory.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (this._folders.Contains(segment, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>All <c>.cs</c> files under a project directory except those in build output or ignored folders.</summary>
    public IEnumerable<string> EnumerateSourceFiles(string projectDir)
    {
        return IndexFreshness.EnumerateSourceFiles(projectDir, this._folders);
    }
}
