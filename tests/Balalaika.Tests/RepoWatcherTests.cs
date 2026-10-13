using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Balalaika.Watcher;

namespace Balalaika.Tests;

/// <summary>Overflow notifications are debounced like ordinary events: a burst yields one flagged batch.</summary>
public sealed class RepoWatcherTests
{
    [Fact]
    public async Task OnOverflow_WhenNotifiedRepeatedlyWithinTheQuietPeriod_FlushesOneOverflowedBatch()
    {
        // Arrange
        List<ChangeBatch> batches = [];
        using RepoWatcher watcher = new(b => { lock (batches) { batches.Add(b); } }, TimeSpan.FromMilliseconds(150), NullLogger.Instance);

        // Act: a large checkout raises one overflow per watcher buffer in quick succession
        for (int i = 0; i < 17; i++)
        {
            watcher.OnOverflow();
        }

        await WaitForBatchesAsync(batches, expected: 1);
        await Task.Delay(400); // long enough for any stray second flush to have happened

        // Assert
        lock (batches)
        {
            batches.Should().ContainSingle().Which.Overflowed.Should().BeTrue();
        }
    }

    [Fact]
    public async Task OnOverflow_AfterAnEarlierBatchWasFlushed_StillDebounces()
    {
        // Arrange: an old batch left a stale first-event time behind, which used to defeat the quiet period
        List<ChangeBatch> batches = [];
        using RepoWatcher watcher = new(b => { lock (batches) { batches.Add(b); } }, TimeSpan.FromMilliseconds(150), NullLogger.Instance);
        watcher.OnOverflow();
        await WaitForBatchesAsync(batches, expected: 1);

        // Act
        for (int i = 0; i < 5; i++)
        {
            watcher.OnOverflow();
        }

        await WaitForBatchesAsync(batches, expected: 2);
        await Task.Delay(400);

        // Assert
        lock (batches)
        {
            batches.Should().HaveCount(2);
        }
    }

    private static async Task WaitForBatchesAsync(List<ChangeBatch> batches, int expected)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (batches)
            {
                if (batches.Count >= expected)
                {
                    return;
                }
            }

            await Task.Delay(25);
        }
    }
}
