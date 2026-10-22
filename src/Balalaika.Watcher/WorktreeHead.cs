namespace Balalaika.Watcher;

/// <param name="Head">The <c>HEAD</c> file.</param>
/// <param name="CommonDir">The git directory holding <c>refs/</c> and <c>packed-refs</c>.</param>
/// <param name="LooseRef">The loose ref file of the checked-out branch; null for a detached or unusual HEAD.</param>
/// <param name="PackedRefs">The <c>packed-refs</c> file; null together with <paramref name="LooseRef"/>.</param>
public sealed record GitHeadFiles(string Head, string CommonDir, string? LooseRef, string? PackedRefs);

/// <summary>
/// A linked git worktree (or submodule) has a <c>.git</c> <em>file</em> holding <c>gitdir: &lt;path&gt;</c>; its real
/// <c>HEAD</c> lives in that directory, outside the working tree, where a watcher on the working tree never sees it.
/// </summary>
public static class WorktreeHead
{
    /// <summary>
    /// The <c>HEAD</c> file of the worktree rooted at <paramref name="repositoryRoot"/>, or null when the root has no
    /// <c>.git</c> file (a normal repository, whose <c>.git/HEAD</c> the root watcher already sees) or it holds no usable <c>gitdir:</c> line.
    /// </summary>
    public static string? Find(string repositoryRoot)
    {
        string? gitDir = ReadGitDirFile(repositoryRoot);
        return gitDir is null ? null : Path.Combine(gitDir, "HEAD");
    }

    /// <summary>
    /// The files whose change moves what <c>HEAD</c> resolves to, for a normal repository or a linked worktree: the
    /// <c>HEAD</c> file itself and, while it is symbolic (<c>ref: refs/heads/&lt;branch&gt;</c>), the loose ref of that branch
    /// and <c>packed-refs</c> of the common git directory. Null when the root is no repository.
    /// </summary>
    public static GitHeadFiles? Resolve(string repositoryRoot)
    {
        string? gitDir = ReadGitDirFile(repositoryRoot);
        bool fromFile = gitDir is not null;
        if (gitDir is null)
        {
            string dotGit = Path.Combine(repositoryRoot, ".git");
            if (!Directory.Exists(dotGit))
            {
                return null;
            }

            gitDir = dotGit;
        }

        string head = Path.Combine(gitDir, "HEAD");
        string common = FindCommonDir(gitDir, fromFile);
        string? looseRef = null;
        try
        {
            const string prefix = "ref:";
            const string heads = "refs/heads/";
            string content = File.ReadAllText(head).Trim();
            if (content.StartsWith(prefix, StringComparison.Ordinal))
            {
                string target = content[prefix.Length..].Trim();
                if (target.StartsWith(heads, StringComparison.Ordinal) && target.Length > heads.Length && !target.Contains(".."))
                {
                    looseRef = Path.Combine(common, target.Replace('/', Path.DirectorySeparatorChar));
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new GitHeadFiles(head, common, looseRef, looseRef is null ? null : Path.Combine(common, "packed-refs"));
    }

    /// <summary>The directory holding <c>refs/</c> and <c>packed-refs</c>: the <c>commondir</c> of a worktree git dir, else the git dir itself.</summary>
    private static string FindCommonDir(string gitDir, bool gitDirFromFile)
    {
        try
        {
            string file = Path.Combine(gitDir, "commondir");
            if (File.Exists(file))
            {
                string target = File.ReadAllText(file).Trim();
                if (target.Length > 0)
                {
                    return Path.GetFullPath(target, gitDir);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        // A linked worktree's git dir (from a .git file) is <common>/worktrees/<name>; a plain .git directory is its own common dir.
        string? parent = Path.GetDirectoryName(gitDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return gitDirFromFile && parent is not null &&string.Equals(Path.GetFileName(parent), "worktrees", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(Path.Combine(gitDir, "..", ".."))
            : gitDir;
    }

    private static string? ReadGitDirFile(string repositoryRoot)
    {
        string dotGit = Path.Combine(repositoryRoot, ".git");
        if (!File.Exists(dotGit))
        {
            return null;
        }

        try
        {
            foreach (string line in File.ReadLines(dotGit))
            {
                if (!line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string target = line["gitdir:".Length..].Trim();
                if (target.Length == 0)
                {
                    return null;
                }

                // A relative gitdir is relative to the working tree root.
                return Path.GetFullPath(target, repositoryRoot);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }
}
