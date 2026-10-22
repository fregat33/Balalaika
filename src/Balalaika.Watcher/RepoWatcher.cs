using Microsoft.Extensions.Logging;
using Balalaika.Index;

namespace Balalaika.Watcher;

/// <param name="CreatedPaths">The subset of <paramref name="Paths"/> that was reported as created or as the target of a rename.</param>
/// <param name="CreatedDirectories">Existing directories reported as created or as the target of a rename (a folder moved into a root raises no events for its content).</param>
public sealed record ChangeBatch(IReadOnlyCollection<string> Paths, bool Overflowed, IReadOnlyCollection<string>? CreatedPaths = null, IReadOnlyCollection<string>? CreatedDirectories = null);

/// <summary>
/// One FileSystemWatcher per repository root. Events are filtered by <see cref="ChangeClassifier"/>, debounced, and
/// emitted as a batch. A buffer overflow is reported as a batch with <c>Overflowed</c> set so the consumer can fall
/// back to a full reconcile.
/// </summary>
public sealed class RepoWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _created = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _createdDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly TimeSpan _quietPeriod;
    private readonly TimeSpan _maxDelay;
    private readonly ILogger _logger;
    private readonly Action<ChangeBatch> _onBatch;
    private readonly IReadOnlyCollection<string>? _ignoredFolders;
    private readonly IndexActivity? _activity;
    private readonly HashSet<string> _watchedHeads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Signature, List<FileSystemWatcher> Watchers)> _refWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _repointGate = new();
    private bool _disposed;
    private Timer? _timer;
    private DateTime _firstEventUtc;
    private bool _overflowed;

    /// <param name="ignoredFolders">Folder names whose events are dropped before debouncing, in addition to build output and git internals.</param>
    /// <param name="activity">Receives the number of events held back by the debouncer, for the readiness line of <c>status</c>.</param>
    public RepoWatcher(Action<ChangeBatch> onBatch, TimeSpan quietPeriod, ILogger logger, IReadOnlyCollection<string>? ignoredFolders = null, IndexActivity? activity = null)
    {
        this._onBatch = onBatch;
        this._quietPeriod = quietPeriod;
        this._maxDelay = TimeSpan.FromSeconds(Math.Max(3, quietPeriod.TotalSeconds * 6));
        this._logger = logger;
        this._ignoredFolders = ignoredFolders;
        this._activity = activity;
    }

    /// <summary>
    /// Watches the <c>HEAD</c> of a linked worktree, which lives outside the watched roots, and reports a change as
    /// a git event of <paramref name="worktreeRoot"/> (as if <c>&lt;root&gt;/.git/HEAD</c> had changed). Does nothing for
    /// a root that has no <c>.git</c> file or whose HEAD is already being watched. Returns true when a watch was started.
    /// </summary>
    public bool WatchWorktreeHead(string worktreeRoot)
    {
        GitHeadFiles? files = WorktreeHead.Resolve(worktreeRoot);
        string? dir = files is null ? null : Path.GetDirectoryName(files.Head);
        if (files is null || dir is null || !Directory.Exists(dir))
        {
            return false;
        }

        string reported = Path.Combine(worktreeRoot, ".git", "HEAD");
        lock (this._gate)
        {
            if (!this._watchedHeads.Add(files.Head))
            {
                return false;
            }
        }

        FileSystemWatcher headWatcher = this.CreateWatcher(dir, "HEAD", reported, worktreeRoot, () => this.RepointRefWatchers(worktreeRoot, files.Head, reported));
        lock (this._gate)
        {
            this._watchers.Add(headWatcher);
        }

        this.RepointRefWatchers(worktreeRoot, files.Head, reported);
        this._logger.LogInformation("Watching git HEAD {Head} for {Root}", files.Head, worktreeRoot);
        return true;
    }

    /// <summary>A non-recursive watcher on one file name (or one name pattern) of a directory; every event is reported as <paramref name="reported"/>.</summary>
    private FileSystemWatcher CreateWatcher(string dir, string filter, string reported, string root, Action? afterEvent = null)
    {
        FileSystemWatcher watcher = new(dir, filter)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
        };
        void OnChange(object? _, FileSystemEventArgs e)
        {
            this.OnEvent(reported);
            afterEvent?.Invoke();
        }

        watcher.Changed += OnChange;
        watcher.Created += OnChange;
        watcher.Deleted += OnChange;
        watcher.Renamed += (_, e) => OnChange(null, e);
        watcher.Error += (_, e) =>
        {
            this._logger.LogWarning(e.GetException(), "Git ref watcher error for {Root}; scheduling full reconcile", root);
            this.OnOverflow();
        };
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    /// <summary>
    /// Re-resolves what <c>HEAD</c> points at and replaces the watchers of the branch's loose ref and of
    /// <c>packed-refs</c> when that changed. Called at start and on every event of the HEAD file and of the ref
    /// watchers (a missing ref directory is watched through its nearest existing ancestor until it appears).
    /// </summary>
    private void RepointRefWatchers(string root, string headPath, string reported)
    {
        // Serialized: two events of one checkout (HEAD file and ref file) fire on different threads and must not publish two sets.
        lock (this._repointGate)
        {
            this.RepointRefWatchersCore(root, headPath, reported);
        }
    }

    private void RepointRefWatchersCore(string root, string headPath, string reported)
    {
        GitHeadFiles? files = WorktreeHead.Resolve(root);
        List<(string Dir, string Filter)> targets = [];
        if (files?.LooseRef is not null && files.PackedRefs is not null)
        {
            string dir = Path.GetDirectoryName(files.LooseRef)!;
            string filter = Path.GetFileName(files.LooseRef);
            string heads = Path.Combine(files.CommonDir, "refs", "heads");
            while (!Directory.Exists(dir) && dir.Length > heads.Length && Path.GetDirectoryName(dir) is { } parent)
            {
                filter = Path.GetFileName(dir);
                dir = parent;
            }

            if (Directory.Exists(dir))
            {
                targets.Add((dir, filter));
            }

            if (Directory.Exists(files.CommonDir))
            {
                targets.Add((files.CommonDir, "packed-refs"));
            }
        }

        string signature = string.Join('|', targets.Select(t => Path.Combine(t.Dir, t.Filter)));
        List<FileSystemWatcher> old;
        lock (this._gate)
        {
            if (this._disposed)
            {
                return;
            }

            if (this._refWatchers.TryGetValue(headPath, out (string Signature, List<FileSystemWatcher> Watchers) current) && current.Signature == signature)
            {
                return;
            }

            old = this._refWatchers.TryGetValue(headPath, out current) ? current.Watchers : [];
            this._refWatchers.Remove(headPath);
            foreach (FileSystemWatcher w in old)
            {
                this._watchers.Remove(w);
            }
        }

        foreach (FileSystemWatcher w in old)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }

        List<FileSystemWatcher> created = [];
        foreach ((string dir, string filter) in targets)
        {
            try
            {
                created.Add(this.CreateWatcher(dir, filter, reported, root, () => this.RepointRefWatchers(root, headPath, reported)));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                this._logger.LogWarning(ex, "Cannot watch git ref {Dir}/{Filter} for {Root}", dir, filter, root);
            }
        }

        bool disposed;
        lock (this._gate)
        {
            disposed = this._disposed;
            if (!disposed)
            {
                this._refWatchers[headPath] = (signature, created);
                this._watchers.AddRange(created);
            }
        }

        if (disposed)
        {
            foreach (FileSystemWatcher w in created)
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
        }
    }

    /// <summary>Number of live watchers (HEAD, ref and packed-refs watchers of every root plus recursive root watchers); for tests.</summary>
    public int WatcherCountForTests
    {
        get
        {
            lock (this._gate)
            {
                return this._watchers.Count;
            }
        }
    }

    /// <summary>Runs the re-point path of <paramref name="root"/> (as a HEAD or ref event would); for tests.</summary>
    public void RepointForTests(string root)
    {
        GitHeadFiles? files = WorktreeHead.Resolve(root);
        if (files is not null)
        {
            this.RepointRefWatchers(root, files.Head, Path.Combine(root, ".git", "HEAD"));
        }
    }

    /// <summary>Must be called with the gate held.</summary>
    private void PublishBuffered()
    {
        this._activity?.SetBuffered(this._pending.Count + this._createdDirectories.Count + (this._overflowed ? 1 : 0));
    }

    public void Watch(string root)
    {
        if (!Directory.Exists(root))
        {
            this._logger.LogWarning("Watch root does not exist: {Root}", root);
            return;
        }

        FileSystemWatcher watcher = new(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        watcher.Changed += (_, e) => this.OnEvent(e.FullPath);
        watcher.Created += (_, e) =>
        {
            this.OnEvent(e.FullPath, created: true);
            this.OnDirectoryCreated(e.FullPath);
        };
        watcher.Deleted += (_, e) => this.OnEvent(e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            this.OnEvent(e.OldFullPath);
            this.OnEvent(e.FullPath, created: true);
            this.OnDirectoryCreated(e.FullPath);
        };
        watcher.Error += (_, e) =>
        {
            this._logger.LogWarning(e.GetException(), "File watcher overflow under {Root}; scheduling full reconcile", root);
            this.OnOverflow();
        };
        watcher.EnableRaisingEvents = true;
        this._watchers.Add(watcher);
        this._logger.LogInformation("Watching {Root}", root);
    }

    private void OnEvent(string path, bool created = false)
    {
        if (ChangeClassifier.Classify(path, this._ignoredFolders) == ChangeKind.Ignored)
        {
            return;
        }

        lock (this._gate)
        {
            if (this._pending.Count == 0 && !this._overflowed)
            {
                this._firstEventUtc = DateTime.UtcNow;
            }

            string full = Path.GetFullPath(path);
            this._pending.Add(full);
            if (created)
            {
                this._created.Add(full);
            }

            this.PublishBuffered();
        }

        this.Schedule();
    }

    /// <summary>
    /// A directory created or moved into the root: the classifier ignores directory paths and the files inside a moved
    /// folder raise no events, so the directory itself is carried in the batch for the consumer to scan.
    /// </summary>
    private void OnDirectoryCreated(string path)
    {
        if (!Directory.Exists(path) || ChangeClassifier.Classify(Path.Combine(path, "x.cs"), this._ignoredFolders) == ChangeKind.Ignored)
        {
            return;
        }

        lock (this._gate)
        {
            if (this._pending.Count == 0 && this._createdDirectories.Count == 0 && !this._overflowed)
            {
                this._firstEventUtc = DateTime.UtcNow;
            }

            this._createdDirectories.Add(Path.GetFullPath(path));
            this.PublishBuffered();
        }

        this.Schedule();
    }

    /// <summary>
    /// Records that the file system dropped events. The next batch is flagged <see cref="ChangeBatch.Overflowed"/> and
    /// goes through the same quiet period as ordinary events, so a burst of overflow notifications (a large checkout
    /// raises one per watcher buffer) produces one batch, not one per notification. Public so tests can drive it.
    /// </summary>
    public void OnOverflow()
    {
        lock (this._gate)
        {
            // Same start-of-batch bookkeeping as OnEvent; without it Schedule sees a stale first-event time, treats the
            // max delay as exceeded and flushes immediately for every notification.
            if (this._pending.Count == 0 && !this._overflowed)
            {
                this._firstEventUtc = DateTime.UtcNow;
            }

            this._overflowed = true;
            this.PublishBuffered();
        }

        this.Schedule();
    }

    private void Schedule()
    {
        lock (this._gate)
        {
            TimeSpan sinceFirst = DateTime.UtcNow - this._firstEventUtc;
            TimeSpan delay = sinceFirst >= this._maxDelay ? TimeSpan.Zero : this._quietPeriod;
            this._timer ??= new Timer(_ => this.Flush(), null, Timeout.Infinite, Timeout.Infinite);
            this._timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        ChangeBatch batch;
        lock (this._gate)
        {
            if (this._pending.Count == 0 && this._createdDirectories.Count == 0 && !this._overflowed)
            {
                return;
            }

            batch = new ChangeBatch(this._pending.ToArray(), this._overflowed, this._created.ToArray(), this._createdDirectories.ToArray());
            this._pending.Clear();
            this._created.Clear();
            this._createdDirectories.Clear();
            this._overflowed = false;
        }

        try
        {
            this._onBatch(batch);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Change batch handler failed");
        }
        finally
        {
            // The batch only stops being "buffered" once the handler has queued its job, or status would claim Ready in between.
            lock (this._gate)
            {
                this.PublishBuffered();
            }
        }
    }

    public void Dispose()
    {
        FileSystemWatcher[] watchers;
        lock (this._gate)
        {
            this._disposed = true;
            watchers = this._watchers.ToArray();
            this._watchers.Clear();
            this._refWatchers.Clear();
        }

        foreach (FileSystemWatcher w in watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }

        this._timer?.Dispose();
    }
}
