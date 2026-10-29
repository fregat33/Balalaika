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

    [Fact]
    public async Task OnOverflow_OfTwoRootsWithinTheQuietPeriod_FlushesOneBatchNamingBoth()
    {
        // Arrange
        List<ChangeBatch> batches = [];
        using RepoWatcher watcher = new(b => { lock (batches) { batches.Add(b); } }, TimeSpan.FromMilliseconds(150), NullLogger.Instance);

        // Act
        watcher.OnOverflow(@"C:\rootA");
        watcher.OnOverflow(@"C:\rootB");
        watcher.OnOverflow(@"C:\rootA");
        await WaitForBatchesAsync(batches, expected: 1);
        await Task.Delay(400);

        // Assert
        lock (batches)
        {
            ChangeBatch batch = batches.Should().ContainSingle().Subject;
            batch.Overflowed.Should().BeTrue();
            batch.OverflowedGlobally.Should().BeFalse();
            batch.OverflowedRoots.Should().BeEquivalentTo([@"C:\rootA", @"C:\rootB"]);
        }
    }

    [Fact]
    public async Task OnOverflow_OfOneRoot_FlushesOnlyThatRoot()
    {
        // Arrange
        List<ChangeBatch> batches = [];
        using RepoWatcher watcher = new(b => { lock (batches) { batches.Add(b); } }, TimeSpan.FromMilliseconds(150), NullLogger.Instance);

        // Act
        watcher.OnOverflow(@"C:\rootA");
        await WaitForBatchesAsync(batches, expected: 1);

        // Assert
        lock (batches)
        {
            batches.Single().OverflowedRoots.Should().BeEquivalentTo([@"C:\rootA"]);
            batches.Single().OverflowedGlobally.Should().BeFalse();
        }
    }

    [Fact]
    public async Task OnOverflow_WithoutARootOrMixedWithOne_IsGlobal()
    {
        // Arrange
        List<ChangeBatch> batches = [];
        using RepoWatcher watcher = new(b => { lock (batches) { batches.Add(b); } }, TimeSpan.FromMilliseconds(150), NullLogger.Instance);

        // Act
        watcher.OnOverflow(@"C:\rootA");
        watcher.OnOverflow();
        await WaitForBatchesAsync(batches, expected: 1);

        // Assert
        lock (batches)
        {
            batches.Single().OverflowedGlobally.Should().BeTrue();
            batches.Single().OverflowedRoots.Should().BeEmpty();
        }
    }

    [Fact]
    public void ConfiguredRootForTests_PicksTheLongestRootContainingThePath_AndNullWhenNoneDoes()
    {
        // Arrange: a root nested in another one, as when the workspace root lies inside a repositories root
        string outer = Path.GetFullPath(TestWorkspace.NewTempDir());
        string inner = Path.Combine(outer, "workspace");
        string other = Path.GetFullPath(TestWorkspace.NewTempDir());
        Directory.CreateDirectory(inner);
        using RepoWatcher watcher = new(_ => { }, TimeSpan.FromMilliseconds(150), NullLogger.Instance);
        watcher.Watch(outer);
        watcher.Watch(inner);

        // Act + Assert
        watcher.ConfiguredRootForTests(Path.Combine(inner, "slot-1")).Should().Be(inner);
        watcher.ConfiguredRootForTests(Path.Combine(outer, "repo")).Should().Be(outer);
        watcher.ConfiguredRootForTests(inner).Should().Be(inner);
        watcher.ConfiguredRootForTests(inner + "-sibling").Should().Be(outer, "a sibling of the inner root with a shared name prefix is not under it");
        watcher.ConfiguredRootForTests(Path.Combine(other, "repo")).Should().BeNull();
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
