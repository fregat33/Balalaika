using FluentAssertions;
using Balalaika.Workspace;

namespace Balalaika.Tests;

/// <summary>A burst of overflow notifications must not turn into a burst of full reconciles.</summary>
public sealed class IndexJobQueueTests
{
    [Fact]
    public void Enqueue_WhenFullReconcileAlreadyQueued_CoalescesIntoIt()
    {
        // Arrange
        IndexJobQueue queue = new();

        // Act
        Task first = queue.Enqueue(new FullReconcileJob());
        Task second = queue.Enqueue(new FullReconcileJob());
        Task third = queue.Enqueue(new FullReconcileJob());

        // Assert
        queue.Pending.Should().Be(1);
        second.Should().BeSameAs(first);
        third.Should().BeSameAs(first);
    }

    [Fact]
    public async Task Enqueue_WhenQueuedReconcileWasHandedToConsumer_QueuesANewOne()
    {
        // Arrange: the consumer has taken the first reconcile and may already be half-way through the disk
        IndexJobQueue queue = new();
        Task first = queue.Enqueue(new FullReconcileJob());
        await using IAsyncEnumerator<QueuedJob> consumer = queue.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        (await consumer.MoveNextAsync()).Should().BeTrue();
        QueuedJob running = consumer.Current;

        // Act
        Task second = queue.Enqueue(new FullReconcileJob());

        // Assert
        second.Should().NotBeSameAs(first);
        queue.Pending.Should().Be(2);

        // Completing the running job releases only the first waiter
        queue.MarkCompleted(running);
        first.IsCompleted.Should().BeTrue();
        second.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public void Enqueue_OtherJobKinds_AreNeverCoalesced()
    {
        // Arrange
        IndexJobQueue queue = new();

        // Act
        queue.Enqueue(new FileChangesJob([@"C:\repo\A.cs"]));
        queue.Enqueue(new FileChangesJob([@"C:\repo\A.cs"]));
        queue.Enqueue(new IndexSolutionJob(@"C:\repo\Demo.sln", Force: false));
        queue.Enqueue(new IndexSolutionJob(@"C:\repo\Demo.sln", Force: false));

        // Assert
        queue.Pending.Should().Be(4);
    }

    [Fact]
    public async Task ReadAllAsync_DeliversJobsInOrder_AndMarkCompletedReleasesEachWaiter()
    {
        // Arrange
        IndexJobQueue queue = new();
        Task changes = queue.Enqueue(new FileChangesJob([@"C:\repo\A.cs"]));
        Task reconcile = queue.Enqueue(new FullReconcileJob());
        await using IAsyncEnumerator<QueuedJob> consumer = queue.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();

        // Act / Assert
        (await consumer.MoveNextAsync()).Should().BeTrue();
        consumer.Current.Job.Should().BeOfType<FileChangesJob>();
        queue.MarkCompleted(consumer.Current);
        changes.IsCompleted.Should().BeTrue();
        reconcile.IsCompleted.Should().BeFalse();

        (await consumer.MoveNextAsync()).Should().BeTrue();
        consumer.Current.Job.Should().BeOfType<FullReconcileJob>();
        queue.MarkCompleted(consumer.Current);
        reconcile.IsCompleted.Should().BeTrue();
        queue.Pending.Should().Be(0);
        queue.LastJobDescription.Should().Contain("FullReconcileJob");
    }
}
