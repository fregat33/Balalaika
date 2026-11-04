using FluentAssertions;
using Balalaika.Workspace;

namespace Balalaika.Tests;

public sealed class LoadedSolutionStampTests
{
    [Fact]
    public void ProjectFilesChangedDuringLoad_ListsFilesWrittenAfterTheLoadStarted()
    {
        // Arrange
        string dir = Path.GetFullPath(TestWorkspace.NewTempDir());
        string sln = Path.Combine(dir, "Demo.slnx");
        string csproj = Path.Combine(dir, "Demo.csproj");
        string props = Path.Combine(dir, "Directory.Packages.props");
        File.WriteAllText(sln, "<Solution><Project Path=\"Demo.csproj\" /></Solution>");
        File.WriteAllText(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(props, "<Project />");
        (Microsoft.CodeAnalysis.AdhocWorkspace workspace, _) = TestWorkspace.CreateProject(dir, "Demo", ("A.cs", "namespace Acme.Orders; public class OrderA { }"));
        DateTime written = DateTime.UtcNow;

        // Act
        using LoadedSolution before = new(workspace, workspace.CurrentSolution, sln, [], written.AddMinutes(-1));
        using LoadedSolution after = new(workspace, workspace.CurrentSolution, sln, [], written.AddMinutes(1));
        using LoadedSolution unrecorded = new(workspace, workspace.CurrentSolution, sln, []);

        // Assert
        string[] expected = new[] { csproj, sln, props }.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        before.ProjectFilesChangedDuringLoad.Should().Equal(expected);
        after.ProjectFilesChangedDuringLoad.Should().BeEmpty();
        unrecorded.ProjectFilesChangedDuringLoad.Should().BeEmpty();
    }
}
