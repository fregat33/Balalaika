using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Balalaika.Workspace;

/// <summary>How a file in the working tree differs from the merge base.</summary>
public enum FileStatus
{
    Added,
    Modified,
    Deleted,
}

/// <summary>One changed file, by absolute path.</summary>
public sealed record ChangedFile(string Path, FileStatus Status);

/// <summary>The working tree compared with the merge base of a ref and HEAD.</summary>
public sealed record ChangeSet(string RepositoryRoot, string MergeBase, IReadOnlyList<ChangedFile> Files);

/// <summary>
/// What <c>review_changes</c> needs from version control: whether it is usable, the repository a solution sits in, the
/// files changed since the merge base, and the base-side text of a file. Tests implement it with strings and never run
/// git.
/// </summary>
public interface IBaseContentProvider
{
    /// <summary>Null when version control is usable; otherwise the text that explains why not. Probed once per process.</summary>
    Task<string?> ProbeAsync(CancellationToken cancellationToken);

    /// <summary>One line for <c>status</c>: the version found, or why none was.</summary>
    string Describe();

    /// <summary>The repository root that contains <paramref name="path"/>, or null when none does.</summary>
    string? FindRepositoryRoot(string path);

    /// <summary>
    /// The ref to review against when the caller named none: the branch <c>origin/HEAD</c> points at, else the first
    /// of <see cref="DefaultBaseCandidates"/> that exists; null when the repository has none of them.
    /// </summary>
    Task<string?> FindDefaultBaseAsync(string repositoryRoot, CancellationToken cancellationToken);

    /// <summary>The change set, or the error text when <paramref name="baseRef"/> is unknown or the command failed.</summary>
    Task<(ChangeSet? Changes, string? Error)> GetChangesAsync(string repositoryRoot, string baseRef, CancellationToken cancellationToken);

    /// <summary>The full commit id <c>HEAD</c> points at in <paramref name="repositoryRoot"/>, or null when git can not tell (no commit yet, git missing).</summary>
    Task<string?> GetHeadAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        return Task.FromResult<string?>(null);
    }

    /// <summary>Branch names tried in order by <see cref="FindDefaultBaseAsync"/> when <c>origin/HEAD</c> is not set.</summary>
    static readonly string[] DefaultBaseCandidates = ["main", "master", "develop", "dev"];

    /// <summary>The text of <paramref name="path"/> at <paramref name="mergeBase"/>, or null when it did not exist there.</summary>
    Task<string?> GetBaseTextAsync(string repositoryRoot, string mergeBase, string path, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IBaseContentProvider"/> over the git command line. The executable comes from <see cref="BalalaikaOptions.GitPath"/>
/// (default <c>git</c> on the PATH of the server process, which is not always the user's shell PATH). The probe runs
/// <c>git --version</c> once and remembers the answer, so a server without git says so in one line per call instead of
/// failing on every step.
/// </summary>
public sealed class GitRepository(IOptions<BalalaikaOptions> options, ILogger<GitRepository> logger) : IBaseContentProvider
{
    private readonly Lazy<Task<(string? Version, string? Error)>> _probe = new(() => ProbeCoreAsync(options.Value.GitPath, logger));

    private string GitPath => options.Value.GitPath;

    public async Task<string?> ProbeAsync(CancellationToken cancellationToken)
    {
        (_, string? error) = await this._probe.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return error;
    }

    public string Describe()
    {
        if (!this._probe.IsValueCreated || !this._probe.Value.IsCompletedSuccessfully)
        {
            return "not probed yet";
        }

        (string? version, string? error) = this._probe.Value.Result;
        return version ?? "not found (" + error + ")";
    }

    public string? FindRepositoryRoot(string path)
    {
        string? dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        while (dir is not null)
        {
            // A worktree or submodule has a .git file instead of a directory; both mark the root.
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    public async Task<string?> FindDefaultBaseAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        // origin/HEAD is "origin/main" when the clone recorded the remote's default branch; the local branch of the
        // same name is preferred when it exists, so the merge base is taken against what the user actually works on.
        (int exit, string output, _) = await this.RunAsync(repositoryRoot, ["symbolic-ref", "--short", "-q", "refs/remotes/origin/HEAD"], cancellationToken).ConfigureAwait(false);
        if (exit == 0 && output.Trim() is { Length: > 0 } remoteHead)
        {
            string local = remoteHead.StartsWith("origin/", StringComparison.Ordinal) ? remoteHead["origin/".Length..] : remoteHead;
            return await this.RefExistsAsync(repositoryRoot, local, cancellationToken).ConfigureAwait(false) ? local : remoteHead;
        }

        foreach (string candidate in IBaseContentProvider.DefaultBaseCandidates)
        {
            if (await this.RefExistsAsync(repositoryRoot, candidate, cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task<bool> RefExistsAsync(string repositoryRoot, string name, CancellationToken cancellationToken)
    {
        (int exit, _, _) = await this.RunAsync(repositoryRoot, ["rev-parse", "--verify", "--quiet", name + "^{commit}"], cancellationToken).ConfigureAwait(false);
        return exit == 0;
    }

    public async Task<(ChangeSet? Changes, string? Error)> GetChangesAsync(string repositoryRoot, string baseRef, CancellationToken cancellationToken)
    {
        (int exit, string output, string error) = await this.RunAsync(repositoryRoot, ["merge-base", baseRef, "HEAD"], cancellationToken).ConfigureAwait(false);
        if (exit != 0)
        {
            return (null, $"git merge-base {baseRef} HEAD failed: {FirstLine(error, output)}. Pass an existing branch, tag or commit as base.");
        }

        string mergeBase = output.Trim();
        (exit, output, error) = await this.RunAsync(repositoryRoot, ["diff", "--name-status", "--no-renames", mergeBase], cancellationToken).ConfigureAwait(false);
        if (exit != 0)
        {
            return (null, $"git diff failed: {FirstLine(error, output)}");
        }

        List<ChangedFile> files = ParseNameStatus(repositoryRoot, output);
        (exit, output, error) = await this.RunAsync(repositoryRoot, ["ls-files", "--others", "--exclude-standard"], cancellationToken).ConfigureAwait(false);
        if (exit != 0)
        {
            return (null, $"git ls-files failed: {FirstLine(error, output)}");
        }

        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            files.Add(new ChangedFile(ToAbsolute(repositoryRoot, line), FileStatus.Added));
        }

        return (new ChangeSet(repositoryRoot, mergeBase, files), null);
    }

    public async Task<string?> GetHeadAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        try
        {
            (int exit, string output, _) = await this.RunAsync(repositoryRoot, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
            return exit == 0 && output.Trim() is { Length: > 0 } head ? head : null;
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public async Task<string?> GetBaseTextAsync(string repositoryRoot, string mergeBase, string path, CancellationToken cancellationToken)
    {
        string relative = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
        (int exit, string output, _) = await this.RunAsync(repositoryRoot, ["show", mergeBase + ":" + relative], cancellationToken).ConfigureAwait(false);
        return exit == 0 ? output : null;
    }

    /// <summary>Lines of <c>git diff --name-status --no-renames</c>: a status letter, a tab, a repository-relative path.</summary>
    public static List<ChangedFile> ParseNameStatus(string repositoryRoot, string output)
    {
        List<ChangedFile> files = [];
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 2 || parts[0].Length == 0)
            {
                continue;
            }

            FileStatus status = parts[0][0] switch
            {
                'A' => FileStatus.Added,
                'D' => FileStatus.Deleted,
                _ => FileStatus.Modified,
            };
            files.Add(new ChangedFile(ToAbsolute(repositoryRoot, parts[^1]), status));
        }

        return files;
    }

    private static string ToAbsolute(string repositoryRoot, string relative)
    {
        return Path.GetFullPath(Path.Combine(repositoryRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static async Task<(string? Version, string? Error)> ProbeCoreAsync(string gitPath, ILogger logger)
    {
        try
        {
            (int exit, string output, string error) = await RunCoreAsync(gitPath, null, ["--version"], CancellationToken.None).ConfigureAwait(false);
            if (exit != 0)
            {
                return (null, $"'{gitPath} --version' exited with {exit}: {FirstLine(error, output)}");
            }

            return (output.Trim(), null);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            logger.LogWarning("git is not available ({GitPath}): {Message}. review_changes is unavailable; install git or set Balalaika:GitPath.", gitPath, ex.Message);
            return (null, $"'{gitPath}' could not be started ({ex.Message}); install git or set Balalaika:GitPath");
        }
    }

    private Task<(int Exit, string Output, string Error)> RunAsync(string workingDirectory, string[] arguments, CancellationToken cancellationToken)
    {
        return RunCoreAsync(this.GitPath, workingDirectory, arguments, cancellationToken);
    }

    private static async Task<(int Exit, string Output, string Error)> RunCoreAsync(string gitPath, string? workingDirectory, string[] arguments, CancellationToken cancellationToken)
    {
        ProcessStartInfo psi = new(gitPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }

        foreach (string argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw;
        }

        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static string FirstLine(string error, string output)
    {
        string text = error.Length > 0 ? error : output;
        int newline = text.IndexOf('\n');
        return (newline < 0 ? text : text[..newline]).Trim();
    }
}
