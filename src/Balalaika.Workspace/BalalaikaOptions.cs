namespace Balalaika.Workspace;

public sealed class BalalaikaOptions
{
    /// <summary>Folders scanned recursively for *.sln / *.slnx files.</summary>
    public string[] Roots { get; set; } = [];

    /// <summary>SQLite file. Defaults to %LOCALAPPDATA%\Balalaika\index.db.</summary>
    public string? IndexPath { get; set; }

    /// <summary>
    /// Folder names whose files are kept out of the index, of reconcile and freshness checks, of watcher jobs and of
    /// live list results; <c>["Migrations"]</c> drops Entity Framework migrations, for example. Compared with every
    /// directory on a file's path, case-insensitively. The files stay in the compilation, so diagnostics still match a
    /// real build. Empty by default.
    /// </summary>
    public string[] IgnoredFolders { get; set; } = [];

    public int Port { get; set; } = 5301;

    /// <summary>Solutions (name without extension, or full path) loaded eagerly at startup. Never evicted.</summary>
    public string[] Preload { get; set; } = [];

    /// <summary>
    /// After the preloaded solutions are up, load every other discovered solution once, index it and evict it, so
    /// cross-repository index results exist for solutions nobody has opened. Solutions whose index already matches the
    /// disk are skipped. Runs one solution at a time in the background.
    /// </summary>
    public bool WarmUp { get; set; } = true;

    /// <summary>
    /// When files change under a solution that is not loaded, refresh its index (load, index, evict) once no further
    /// change has arrived for this many seconds. 0 disables the refresh; such solutions then keep their rows from the
    /// last load and hits are only marked <c>[stale]</c>.
    /// </summary>
    public int UnloadedRefreshSeconds { get; set; } = 60;

    /// <summary>Refresh an unloaded solution at the latest this many seconds after its first change, even if changes keep arriving.</summary>
    public int UnloadedRefreshMaxWaitSeconds { get; set; } = 600;

    /// <summary>
    /// Every this many seconds, forget known solutions whose <c>.sln</c> / <c>.slnx</c> file no longer exists (index rows
    /// included). New solutions are picked up from file-system events, not by this sweep. 0 disables the sweep; the
    /// same check still runs after every change batch.
    /// </summary>
    public int DiscoverySweepSeconds { get; set; } = 30;

    /// <summary>Solution used when a query does not name one. Defaults to the first preloaded or discovered solution.</summary>
    public string? DefaultSolution { get; set; }

    /// <summary>Evict a loaded solution after this many idle minutes. 0 disables eviction.</summary>
    public int IdleEvictionMinutes { get; set; } = 0;

    /// <summary>How long a query waits for a solution load before answering from the index only.</summary>
    public int LoadWaitSeconds { get; set; } = 120;

    /// <summary>Run <c>dotnet restore</c> on a project whose csproj / props changed before reloading it.</summary>
    public bool RestoreOnProjectChange { get; set; } = true;

    /// <summary>
    /// After a change batch, re-extract every project whose declared surface changed together with its transitive
    /// dependents (project references in the same solution, package or project references from other loaded
    /// solutions), so stored reference rows never point at removed symbols or superseded overloads. Costs a few
    /// seconds per affected project; disable to fall back to changed-documents-only indexing plus manual <c>reindex</c>.
    /// </summary>
    public bool ReextractDependents { get; set; } = true;

    /// <summary>Unreferenced blobs older than this are deleted at startup.</summary>
    public int BlobRetentionDays { get; set; } = 30;

    public int DebounceMilliseconds { get; set; } = 500;

    /// <summary>
    /// The git executable <c>review_changes</c> runs. Defaults to <c>git</c> on the PATH of the server process, which
    /// is not always the user's shell PATH when the server runs as a service or from an IDE launcher; set a full path
    /// then. Probed once at startup; <c>status</c> shows the result.
    /// </summary>
    public string GitPath { get; set; } = "git";

    /// <summary>
    /// Folder for the rolling log files (<c>balalaika-yyyyMMdd.clef</c>, one per day). Defaults to a <c>logs</c>
    /// folder next to the index database.
    /// </summary>
    public string? LogDirectory { get; set; }

    /// <summary>How many daily log files to keep. Older files are deleted when a new day starts.</summary>
    public int LogRetainedDays { get; set; } = 7;

    public string ResolveLogDirectory()
    {
        if (!string.IsNullOrWhiteSpace(this.LogDirectory))
        {
            return Environment.ExpandEnvironmentVariables(this.LogDirectory);
        }

        return Path.Combine(Path.GetDirectoryName(this.ResolveIndexPath()) ?? ".", "logs");
    }

    public string ResolveIndexPath()
    {
        if (!string.IsNullOrWhiteSpace(this.IndexPath))
        {
            return Environment.ExpandEnvironmentVariables(this.IndexPath);
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "Balalaika", "index.db");
    }
}
