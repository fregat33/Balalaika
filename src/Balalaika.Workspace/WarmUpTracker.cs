namespace Balalaika.Workspace;

/// <summary>Progress of the startup warm-up (load, index, evict each solution nobody preloaded), shown by <c>status</c>.</summary>
public sealed class WarmUpTracker
{
    private readonly object _gate = new();
    private readonly List<string> _warmed = [];
    private readonly List<string> _skipped = [];
    private readonly List<string> _failed = [];
    private bool _started;
    private bool _finished;
    private int _total;
    private string? _current;

    public void Start(int total)
    {
        lock (this._gate)
        {
            this._started = true;
            this._total = total;
        }
    }

    public void Begin(string solutionName)
    {
        lock (this._gate)
        {
            this._current = solutionName;
        }
    }

    public void MarkWarmed(string solutionName)
    {
        this.Record(this._warmed, solutionName);
    }

    public void MarkSkipped(string solutionName)
    {
        this.Record(this._skipped, solutionName);
    }

    public void MarkFailed(string solutionName)
    {
        this.Record(this._failed, solutionName);
    }

    public void Finish()
    {
        lock (this._gate)
        {
            this._finished = true;
            this._current = null;
        }
    }

    /// <summary>One line for <c>status</c>, or null when warm-up never started (disabled, or nothing to warm).</summary>
    public string? Describe()
    {
        lock (this._gate)
        {
            if (!this._started)
            {
                return null;
            }

            string progress = this._finished
                ? $"done, {this._warmed.Count} warmed, {this._skipped.Count} already fresh"
                : $"{this._warmed.Count + this._skipped.Count + this._failed.Count} of {this._total} done" + (this._current is null ? "" : $", loading {this._current}");
            if (this._failed.Count > 0)
            {
                progress += $", {this._failed.Count} failed ({string.Join(", ", this._failed)})";
            }

            return progress;
        }
    }

    private void Record(List<string> list, string solutionName)
    {
        lock (this._gate)
        {
            list.Add(solutionName);
            this._current = null;
        }
    }
}
