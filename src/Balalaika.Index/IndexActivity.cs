namespace Balalaika.Index;

/// <summary>
/// What is going on outside the job queue, for the <c>Ready:</c> line of <c>status</c>: the file events the watcher
/// holds back while it debounces, and the unloaded-solution refreshes queued or running after a git change. One
/// singleton shared by the watcher, the hosted service and the query service.
/// </summary>
public sealed class IndexActivity
{
    private int _buffered;
    private int _refreshing;

    /// <summary>Paths, created directories and an overflow flag the watcher has seen but not yet handed on as a batch.</summary>
    public int Buffered => Volatile.Read(ref this._buffered);

    /// <summary>Unloaded solutions waiting for, or in the middle of, a refresh started by a git change.</summary>
    public int Refreshing => Volatile.Read(ref this._refreshing);

    public void SetBuffered(int count)
    {
        Volatile.Write(ref this._buffered, count);
    }

    public void RefreshStarted()
    {
        Interlocked.Increment(ref this._refreshing);
    }

    public void RefreshFinished()
    {
        Interlocked.Decrement(ref this._refreshing);
    }
}
