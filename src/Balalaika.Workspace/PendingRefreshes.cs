namespace Balalaika.Workspace;

/// <summary>A refresh that became due: which solution, how many distinct files changed, and when the first change arrived.</summary>
public sealed record DueRefresh(string SolutionPath, int Files, DateTime FirstChangeUtc);

/// <summary>
/// Changes seen under solutions that are not loaded. Nothing can be applied to them until they are loaded, so the
/// paths are collected per solution and the solution is refreshed (load, index, evict) once it has been quiet for a
/// while, or once it has been changing for too long without a pause. Thread-safe; the watcher consumer records,
/// the refresh loop takes.
/// </summary>
public sealed class PendingRefreshes
{
    private sealed class Entry(DateTime first)
    {
        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTime FirstUtc { get; } = first;
        public DateTime LastUtc { get; set; } = first;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public int Completed { get; private set; }

    public int Count
    {
        get
        {
            lock (this._gate)
            {
                return this._entries.Count;
            }
        }
    }

    public void Record(string solutionPath, IEnumerable<string> changedPaths, DateTime nowUtc)
    {
        lock (this._gate)
        {
            if (!this._entries.TryGetValue(solutionPath, out Entry? entry))
            {
                entry = new Entry(nowUtc);
                this._entries[solutionPath] = entry;
            }

            entry.Paths.UnionWith(changedPaths);
            entry.LastUtc = nowUtc;
        }
    }

    /// <summary>Removes and returns every solution that has been quiet for <paramref name="quiet"/> or changing for longer than <paramref name="maxWait"/>.</summary>
    public IReadOnlyList<DueRefresh> TakeDue(TimeSpan quiet, TimeSpan maxWait, DateTime nowUtc)
    {
        lock (this._gate)
        {
            List<DueRefresh> due = [];
            foreach ((string path, Entry entry) in this._entries.ToList())
            {
                if (nowUtc - entry.LastUtc >= quiet || nowUtc - entry.FirstUtc >= maxWait)
                {
                    due.Add(new DueRefresh(path, entry.Paths.Count, entry.FirstUtc));
                    this._entries.Remove(path);
                }
            }

            return due;
        }
    }

    /// <summary>Drops a solution's pending changes, for example because it was loaded by other means and indexed on load.</summary>
    public void Discard(string solutionPath)
    {
        lock (this._gate)
        {
            this._entries.Remove(solutionPath);
        }
    }

    public void MarkCompleted()
    {
        lock (this._gate)
        {
            this.Completed++;
        }
    }

    /// <summary>One line for <c>status</c>, or null when nothing is pending and nothing was ever refreshed.</summary>
    public string? Describe(DateTime nowUtc)
    {
        lock (this._gate)
        {
            if (this._entries.Count == 0 && this.Completed == 0)
            {
                return null;
            }

            string pending = this._entries.Count == 0
                ? "none pending"
                : string.Join(", ", this._entries.Select(e => $"{SolutionDiscovery.SolutionName(e.Key)} ({e.Value.Paths.Count} files, last change {(int)(nowUtc - e.Value.LastUtc).TotalSeconds}s ago)"));
            return $"{pending}, {this.Completed} done";
        }
    }
}
