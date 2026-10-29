using Microsoft.Extensions.Logging;
using Balalaika.Index;

namespace Balalaika.Watcher;

/// <param name="CreatedPaths">The subset of <paramref name="Paths"/> that was reported as created or as the target of a rename.</param>
/// <param name="CreatedDirectories">Existing directories reported as created or as the target of a rename (a folder moved into a root raises no events for its content).</param>
/// <param name="Overflowed">True when events were dropped, under the <paramref name="OverflowedRoots"/> or (when that is empty) anywhere.</param>
/// <param name="OverflowedRoots">The configured roots whose watcher overflowed; empty with <paramref name="Overflowed"/> set means the overflow could not be attributed to a root.</param>
public sealed record ChangeBatch(IReadOnlyCollection<string> Paths, bool Overflowed, IReadOnlyCollection<string>? CreatedPaths = null, IReadOnlyCollection<string>? CreatedDirectories = null, IReadOnlyCollection<string>? OverflowedRoots = null)
{
    /// <summary>An overflow that is not limited to <see cref="OverflowedRoots"/>: everything must be reconciled.</summary>
    public bool OverflowedGlobally => this.Overflowed && (this.OverflowedRoots is null || this.OverflowedRoots.Count == 0);
}

/// <summary>
/// One FileSystemWatcher per repository root. Events are filtered by <see cref="ChangeClassifier"/>, debounced, and
/// emitted as a batch. A buffer overflow is reported as a batch with <c>Overflowed</c> set (and the configured roots
/// that overflowed) so the consumer can fall back to a reconcile of those roots, or of everything.
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
    private readonly Dictionary<string, (string Head, FileSystemWatcher? Watcher, object Token)> _worktrees = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _repointGate = new();
    private bool _disposed;
    private Timer? _timer;
    private DateTime _firstEventUtc;
    private bool _overflowed;
    private bool _overflowedGlobally;
    private readonly HashSet<string> _overflowedRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _roots = [];

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
        object token = new();
        lock (this._gate)
        {
            if (this._disposed || !this._watchedHeads.Add(files.Head))
            {
                return false;
            }

            this._worktrees[worktreeRoot] = (files.Head, null, token);
        }

        FileSystemWatcher headWatcher = this.CreateWatcher(
            dir,
            "HEAD",
            reported,
            worktreeRoot,
            () =>
            {
                // Deleting the worktree's git directory raises a Deleted event on some platforms instead of an Error.
                if (Directory.Exists(dir))
                {
                    this.RepointRefWatchers(worktreeRoot, files.Head, reported);
                }
                else
                {
                    this.UnwatchWorktreeHead(worktreeRoot);
                }
            },
            onGone: () => this.UnwatchWorktreeHead(worktreeRoot));
        this.BeforeHeadPublishForTests?.Invoke(worktreeRoot);
        bool published;
        lock (this._gate)
        {
            // Unwatched (or disposed) while the watcher was being created, possibly watched again by another call
            // (same Head, other token): nothing may keep it.
            published = !this._disposed
                && this._worktrees.TryGetValue(worktreeRoot, out (string Head, FileSystemWatcher? Watcher, object Token) entry)
                && ReferenceEquals(entry.Token, token)
                && entry.Watcher is null;
            if (published)
            {
                this._watchers.Add(headWatcher);
                this._worktrees[worktreeRoot] = (files.Head, headWatcher, token);
            }
        }

        if (!published)
        {
            headWatcher.EnableRaisingEvents = false;
            headWatcher.Dispose();
            return false;
        }

        this.RepointRefWatchers(worktreeRoot, files.Head, reported);
        this._logger.LogInformation("Watching git HEAD {Head} for {Root}", files.Head, worktreeRoot);
        return true;
    }

    /// <summary>
    /// Releases the HEAD, ref and <c>packed-refs</c> watchers that <see cref="WatchWorktreeHead"/> created for
    /// <paramref name="worktreeRoot"/>, so a worktree created again at the same path is watched again. Returns false
    /// when nothing was watched for that root.
    /// </summary>
    public bool UnwatchWorktreeHead(string worktreeRoot)
    {
        List<FileSystemWatcher> released = [];
        string head;
        lock (this._gate)
        {
            if (!this._worktrees.Remove(worktreeRoot, out (string Head, FileSystemWatcher? Watcher, object Token) entry))
            {
                return false;
            }

            head = entry.Head;
            this._watchedHeads.Remove(head);
            if (entry.Watcher is not null)
            {
                released.Add(entry.Watcher);
            }

            if (this._refWatchers.Remove(head, out (string Signature, List<FileSystemWatcher> Watchers) refs))
            {
                released.AddRange(refs.Watchers);
            }

            foreach (FileSystemWatcher w in released)
            {
                this._watchers.Remove(w);
            }
        }

        foreach (FileSystemWatcher w in released)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }

        this._logger.LogInformation("Released git HEAD watchers {Head} of {Root}", head, worktreeRoot);
        return true;
    }

    /// <summary>
    /// A non-recursive watcher on one file name (or one name pattern) of a directory; every event is reported as <paramref name="reported"/>.
    /// <paramref name="onGone"/> is called instead of scheduling a full reconcile when the watcher fails because its directory went away.
    /// </summary>
    private FileSystemWatcher CreateWatcher(string dir, string filter, string reported, string root, Action? afterEvent = null, Action? onGone = null)
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
            // Anything but a buffer overflow on a HEAD watcher means the worktree's git directory is gone (Windows: "Access is denied").
            if (onGone is not null && e.GetException() is not InternalBufferOverflowException)
            {
                onGone();
                return;
            }

            string? configured = this.ConfiguredRootContaining(root);
            this._logger.LogWarning(e.GetException(), "Git ref watcher error for {Root}; scheduling reconcile of {Scope}", root, configured ?? "all roots");
            if (configured is null)
            {
                this.OnOverflow();
            }
            else
            {
                this.OnOverflow(configured);
            }
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
            if (this._disposed || !this._watchedHeads.Contains(headPath))
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
            // Also dropped when the worktree was unwatched meanwhile, or the watchers would outlive it.
            disposed = this._disposed || !this._watchedHeads.Contains(headPath);
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

    /// <summary>The longest configured root that contains <paramref name="path"/> (or equals it); null when none does.</summary>
    private string? ConfiguredRootContaining(string path)
    {
        lock (this._gate)
        {
            string? best = null;
            foreach (string r in this._roots)
            {
                if (IsUnder(path, r) && (best is null || r.Length > best.Length))
                {
                    best = r;
                }
            }

            return best;
        }
    }

    /// <summary>The root a watcher error of <paramref name="path"/> (a worktree or repository root) is attributed to; for tests.</summary>
    public string? ConfiguredRootForTests(string path) => this.ConfiguredRootContaining(path);

    /// <summary>True when <paramref name="path"/> equals <paramref name="root"/> or lies below it (full paths, case-insensitive).</summary>
    public static bool IsUnder(string path, string root)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Equals(r, StringComparison.OrdinalIgnoreCase)
            || (full + Path.DirectorySeparatorChar).StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Called with the root after a HEAD watcher was created and before it is published; for tests.</summary>
    public Action<string>? BeforeHeadPublishForTests { get; set; }

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
            this._logger.LogWarning(e.GetException(), "File watcher overflow under {Root}; scheduling reconcile of that root", root);
            this.OnOverflow(root);
        };
        watcher.EnableRaisingEvents = true;
        this._watchers.Add(watcher);
        lock (this._gate)
        {
            this._roots.Add(root);
        }

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
    public void OnOverflow() => this.RecordOverflow(null);

    /// <summary>
    /// Like <see cref="OnOverflow()"/> for the watcher of one configured <paramref name="root"/>: the batch names the
    /// root, so the consumer reconciles only what lies under it. Roots overflowing in the same quiet period are merged.
    /// </summary>
    public void OnOverflow(string root) => this.RecordOverflow(root);

    private void RecordOverflow(string? root)
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
            if (root is null)
            {
                this._overflowedGlobally = true;
            }
            else
            {
                this._overflowedRoots.Add(root);
            }

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

            // A global overflow covers every root, so the roots are dropped (an empty list with Overflowed means global).
            string[] roots = this._overflowedGlobally ? [] : this._overflowedRoots.ToArray();
            batch = new ChangeBatch(this._pending.ToArray(), this._overflowed, this._created.ToArray(), this._createdDirectories.ToArray(), roots);
            this._pending.Clear();
            this._created.Clear();
            this._createdDirectories.Clear();
            this._overflowed = false;
            this._overflowedGlobally = false;
            this._overflowedRoots.Clear();
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
            this._worktrees.Clear();
            this._watchedHeads.Clear();
        }

        foreach (FileSystemWatcher w in watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }

        this._timer?.Dispose();
    }
}
