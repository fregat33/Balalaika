namespace Balalaika.Workspace;

/// <summary>
/// Where a directory sits in git: the working tree root, the common git directory shared by a repository and its
/// linked worktrees, and whether the working tree is a linked worktree (its <c>--git-dir</c> differs from its
/// <c>--git-common-dir</c>). Read from the <c>.git</c> entry itself, which is what <c>git rev-parse</c> reads, without starting a process.
/// </summary>
public sealed record RepositoryInfo(string WorktreeRoot, string CommonDir, bool IsLinkedWorktree)
{
    /// <summary>Null when <paramref name="directory"/> is not inside a git working tree.</summary>
    public static RepositoryInfo? Find(string directory)
    {
        string? current = Path.GetFullPath(directory);
        while (!string.IsNullOrEmpty(current))
        {
            string dotGit = Path.Combine(current, ".git");
            if (Directory.Exists(dotGit))
            {
                return new RepositoryInfo(current, Normalize(dotGit), false);
            }

            if (File.Exists(dotGit))
            {
                return FromGitFile(current, dotGit);
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    public static string Normalize(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static RepositoryInfo? FromGitFile(string root, string dotGit)
    {
        try
        {
            string? target = File.ReadLines(dotGit)
                .Where(l => l.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                .Select(l => l["gitdir:".Length..].Trim())
                .FirstOrDefault(l => l.Length > 0);
            if (target is null)
            {
                return null;
            }

            string gitDir = Path.GetFullPath(target, root);
            string commonFile = Path.Combine(gitDir, "commondir");
            if (!File.Exists(commonFile))
            {
                // A submodule: its git directory is its own common directory.
                return new RepositoryInfo(root, Normalize(gitDir), false);
            }

            string common = Path.GetFullPath(File.ReadAllText(commonFile).Trim(), gitDir);
            return new RepositoryInfo(root, Normalize(common), !Normalize(common).Equals(Normalize(gitDir), StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
