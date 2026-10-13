using FluentAssertions;
using Balalaika.Workspace;

namespace Balalaika.Tests;

public sealed class PendingRefreshesTests
{
    private static readonly DateTime T0 = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(600);
    private const string Sln = @"C:\x\A\A.sln";

    [Fact]
    public void TakeDue_BeforeQuietPeriodElapsed_ReturnsNothing()
    {
        // Arrange
        PendingRefreshes pending = new();
        pending.Record(Sln, [@"C:\x\A\F.cs"], T0);

        // Act / Assert
        pending.TakeDue(Quiet, MaxWait, T0.AddSeconds(30)).Should().BeEmpty();
        pending.Count.Should().Be(1);
    }

    [Fact]
    public void TakeDue_AfterQuietPeriod_ReturnsAndRemovesEntryWithDistinctFileCount()
    {
        // Arrange
        PendingRefreshes pending = new();
        pending.Record(Sln, [@"C:\x\A\F.cs", @"C:\x\A\G.cs"], T0);
        pending.Record(Sln, [@"C:\x\A\f.cs"], T0.AddSeconds(10));

        // Act
        IReadOnlyList<DueRefresh> due = pending.TakeDue(Quiet, MaxWait, T0.AddSeconds(70));

        // Assert
        due.Should().ContainSingle().Which.Should().BeEquivalentTo(new DueRefresh(Sln, 2, T0));
        pending.Count.Should().Be(0);
    }

    [Fact]
    public void TakeDue_WhileChangesKeepArriving_WaitsUntilMaxWait()
    {
        // Arrange: a change every 30 s, never quiet for 60 s
        PendingRefreshes pending = new();
        DateTime now = T0;
        for (int i = 0; i < 19; i++)
        {
            pending.Record(Sln, [$@"C:\x\A\F{i}.cs"], now);
            pending.TakeDue(Quiet, MaxWait, now).Should().BeEmpty();
            now = now.AddSeconds(30);
        }

        // Act: at T0 + 600 s the max wait is reached although a change arrived just now
        now = T0.AddSeconds(600);
        pending.Record(Sln, [@"C:\x\A\Last.cs"], now);
        IReadOnlyList<DueRefresh> due = pending.TakeDue(Quiet, MaxWait, now);

        // Assert
        due.Should().ContainSingle().Which.Files.Should().Be(20);
    }

    [Fact]
    public void Discard_RemovesPendingEntry()
    {
        // Arrange
        PendingRefreshes pending = new();
        pending.Record(Sln, [@"C:\x\A\F.cs"], T0);

        // Act
        pending.Discard(Sln);

        // Assert
        pending.Count.Should().Be(0);
        pending.TakeDue(Quiet, MaxWait, T0.AddHours(1)).Should().BeEmpty();
    }

    [Fact]
    public void Describe_ReportsPendingSolutionsAndCompletedCount()
    {
        // Arrange
        PendingRefreshes pending = new();
        pending.Describe(T0).Should().BeNull("nothing pending and nothing done");
        pending.Record(Sln, [@"C:\x\A\F.cs", @"C:\x\A\G.cs"], T0);
        pending.MarkCompleted();

        // Act / Assert
        pending.Describe(T0.AddSeconds(42)).Should().Be("A (2 files, last change 42s ago), 1 done");
        pending.TakeDue(Quiet, MaxWait, T0.AddHours(1));
        pending.Describe(T0).Should().Be("none pending, 1 done");
    }
}
