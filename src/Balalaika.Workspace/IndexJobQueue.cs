using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Balalaika.Workspace;

public abstract record IndexJob;

/// <summary>Walk every document of a loaded solution and bring the index in line with it.</summary>
public sealed record IndexSolutionJob(string SolutionPath, bool Force) : IndexJob;

/// <summary>A debounced batch of changed paths from the file watcher.</summary>
public sealed record FileChangesJob(IReadOnlyCollection<string> Paths) : IndexJob;

/// <summary>Compare every loaded solution with the disk (git checkout, watcher overflow).</summary>
public sealed record FullReconcileJob : IndexJob;

/// <summary>Reload a solution from MSBuild because project or solution files changed.</summary>
public sealed record ReloadSolutionJob(string SolutionPath, IReadOnlyCollection<string> ChangedProjectFiles) : IndexJob;

/// <summary>A job as handed to the consumer. <see cref="Completion"/> finishes when the consumer has processed it.</summary>
public sealed class QueuedJob
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public QueuedJob(IndexJob job)
    {
        this.Job = job;
    }

    public IndexJob Job { get; }

    public Task Completion => this._completion.Task;

    internal void Complete()
    {
        this._completion.TrySetResult();
    }
}

/// <summary>Single-consumer queue that serialises all index mutations.</summary>
public sealed class IndexJobQueue
{
    private readonly Channel<QueuedJob> _channel = Channel.CreateUnbounded<QueuedJob>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    private readonly object _gate = new();
    private QueuedJob? _queuedReconcile;
    private int _pending;

    public int Pending => Volatile.Read(ref this._pending);

    public DateTime? LastCompletedUtc { get; private set; }
    public string? LastJobDescription { get; private set; }

    /// <summary>
    /// Queues a job. The returned task completes when the consumer has processed it (successfully or not); callers may
    /// ignore it. A <see cref="FullReconcileJob"/> is coalesced: while one is queued and not yet started, further ones
    /// return its completion instead of adding to the queue, since a reconcile that has not begun will see every change
    /// the later request was raised for. One that is already running does not absorb new requests.
    /// </summary>
    public Task Enqueue(IndexJob job)
    {
        lock (this._gate)
        {
            if (job is FullReconcileJob && this._queuedReconcile is { } waiting)
            {
                return waiting.Completion;
            }

            QueuedJob queued = new(job);
            if (job is FullReconcileJob)
            {
                this._queuedReconcile = queued;
            }

            Interlocked.Increment(ref this._pending);
            this._channel.Writer.TryWrite(queued);
            return queued.Completion;
        }
    }

    /// <summary>Hands jobs to the single consumer in order. A job stops being coalescable the moment it is handed out.</summary>
    public async IAsyncEnumerable<QueuedJob> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (QueuedJob queued in this._channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (this._gate)
            {
                if (ReferenceEquals(this._queuedReconcile, queued))
                {
                    this._queuedReconcile = null;
                }
            }

            yield return queued;
        }
    }

    public void MarkCompleted(QueuedJob queued)
    {
        Interlocked.Decrement(ref this._pending);
        this.LastCompletedUtc = DateTime.UtcNow;
        this.LastJobDescription = queued.Job.ToString();
        queued.Complete();
    }
}
