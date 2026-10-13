using Microsoft.Extensions.Logging;

namespace Balalaika.Watcher;

public sealed record ChangeBatch(IReadOnlyCollection<string> Paths, bool Overflowed);

/// <summary>
/// One FileSystemWatcher per repository root. Events are filtered by <see cref="ChangeClassifier"/>, debounced, and
/// emitted as a batch. A buffer overflow is reported as a batch with <c>Overflowed</c> set so the consumer can fall
/// back to a full reconcile.
/// </summary>
public sealed class RepoWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly TimeSpan _quietPeriod;
    private readonly TimeSpan _maxDelay;
    private readonly ILogger _logger;
    private readonly Action<ChangeBatch> _onBatch;
    private readonly IReadOnlyCollection<string>? _ignoredFolders;
    private Timer? _timer;
    private DateTime _firstEventUtc;
    private bool _overflowed;

    /// <param name="ignoredFolders">Folder names whose events are dropped before debouncing, in addition to build output and git internals.</param>
    public RepoWatcher(Action<ChangeBatch> onBatch, TimeSpan quietPeriod, ILogger logger, IReadOnlyCollection<string>? ignoredFolders = null)
    {
        this._onBatch = onBatch;
        this._quietPeriod = quietPeriod;
        this._maxDelay = TimeSpan.FromSeconds(Math.Max(3, quietPeriod.TotalSeconds * 6));
        this._logger = logger;
        this._ignoredFolders = ignoredFolders;
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
        watcher.Created += (_, e) => this.OnEvent(e.FullPath);
        watcher.Deleted += (_, e) => this.OnEvent(e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            this.OnEvent(e.OldFullPath);
            this.OnEvent(e.FullPath);
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

    private void OnEvent(string path)
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

            this._pending.Add(Path.GetFullPath(path));
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
            if (this._pending.Count == 0 && !this._overflowed)
            {
                return;
            }

            batch = new ChangeBatch(this._pending.ToArray(), this._overflowed);
            this._pending.Clear();
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
    }

    public void Dispose()
    {
        foreach (FileSystemWatcher w in this._watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }

        this._timer?.Dispose();
    }
}
