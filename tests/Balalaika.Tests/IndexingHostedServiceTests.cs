using FluentAssertions;
using Balalaika.Server;
using Balalaika.Watcher;
using Balalaika.Workspace;

namespace Balalaika.Tests;

public sealed class IndexingHostedServiceTests
{
    private static readonly string R1 = Path.Combine(Path.GetTempPath(), "balalaika-jobsfor", "root1");
    private static readonly string R2 = Path.Combine(Path.GetTempPath(), "balalaika-jobsfor", "root2");

    [Fact]
    public void JobsFor_GlobalOverflow_IsOneFullReconcileOfEverything()
    {
        // Arrange
        ChangeBatch batch = new([Path.Combine(R1, "A.cs"), Path.Combine(R2, "B.cs")], true);

        // Act
        IReadOnlyList<IndexJob> jobs = IndexingHostedService.JobsFor(batch);

        // Assert
        jobs.Should().HaveCount(1);
        jobs[0].Should().BeOfType<FullReconcileJob>().Which.Roots.Should().BeNull();
    }

    [Fact]
    public void JobsFor_ScopedOverflow_ReconcilesTheRootAndKeepsOnlyThePathsOutsideIt()
    {
        // Arrange
        string inside = Path.Combine(R1, "A.cs");
        string outside = Path.Combine(R2, "B.cs");
        string insideNew = Path.Combine(R1, "New1.cs");
        string outsideNew = Path.Combine(R2, "New2.cs");
        string outsideDir = Path.Combine(R2, "Dir");
        ChangeBatch batch = new([inside, outside], true, [insideNew, outsideNew], [outsideDir], [R1]);

        // Act
        IReadOnlyList<IndexJob> jobs = IndexingHostedService.JobsFor(batch);

        // Assert
        jobs.Should().HaveCount(2);
        jobs[0].Should().BeOfType<FullReconcileJob>().Which.Roots.Should().BeEquivalentTo([R1]);
        FileChangesJob changes = jobs[1].Should().BeOfType<FileChangesJob>().Subject;
        changes.Paths.Should().BeEquivalentTo([outside]);
        changes.CreatedPaths.Should().BeEquivalentTo([outsideNew]);
        changes.CreatedDirectories.Should().BeEquivalentTo([outsideDir]);
    }

    [Fact]
    public void JobsFor_ScopedOverflow_WithEverythingInsideTheRoot_IsOnlyTheReconcile()
    {
        // Arrange
        ChangeBatch batch = new([Path.Combine(R1, "A.cs")], true, [Path.Combine(R1, "New.cs")], [Path.Combine(R1, "Dir")], [R1]);

        // Act
        IReadOnlyList<IndexJob> jobs = IndexingHostedService.JobsFor(batch);

        // Assert
        jobs.Should().HaveCount(1);
        jobs[0].Should().BeOfType<FullReconcileJob>().Which.Roots.Should().BeEquivalentTo([R1]);
    }

    [Fact]
    public void JobsFor_NoOverflow_IsOneFileChangesJobWithTheWholeBatch()
    {
        // Arrange
        string[] paths = [Path.Combine(R1, "A.cs"), Path.Combine(R2, "B.cs")];
        string[] created = [Path.Combine(R2, "B.cs")];
        string[] directories = [Path.Combine(R1, "Dir")];
        ChangeBatch batch = new(paths, false, created, directories);

        // Act
        IReadOnlyList<IndexJob> jobs = IndexingHostedService.JobsFor(batch);

        // Assert
        jobs.Should().HaveCount(1);
        FileChangesJob changes = jobs[0].Should().BeOfType<FileChangesJob>().Subject;
        changes.Paths.Should().BeEquivalentTo(paths);
        changes.CreatedPaths.Should().BeEquivalentTo(created);
        changes.CreatedDirectories.Should().BeEquivalentTo(directories);
    }
}
