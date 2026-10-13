using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Balalaika.Workspace;

namespace Balalaika.Tests;

/// <summary>Files a project leaves out of its compilation must stay out when the disk is reconciled with the live solution.</summary>
public sealed class LoadedSolutionTests : IDisposable
{
    private readonly string _dir = TestWorkspace.NewTempDir();

    [Fact]
    public void Constructor_RecordsSourceFilesTheProjectDoesNotCompile()
    {
        // Arrange: Old.cs is on disk under the project directory but is not a document (as with a Compile Remove glob)
        string excludedPath = Path.Combine(this._dir, "Old.cs");
        File.WriteAllText(excludedPath, "public class Old { }");
        File.WriteAllText(Path.Combine(this._dir, "A.cs"), "public class A { }");
        Directory.CreateDirectory(Path.Combine(this._dir, "obj"));
        File.WriteAllText(Path.Combine(this._dir, "obj", "Generated.cs"), "class Generated { }");
        (AdhocWorkspace workspace, Project project) = TestWorkspace.CreateProject(this._dir, "Demo", ("A.cs", "public class A { }"));

        // Act
        using LoadedSolution loaded = new(workspace, workspace.CurrentSolution, Path.Combine(this._dir, "Demo.sln"), []);

        // Assert: build output is not counted, only real source files the project skipped
        loaded.ExcludedSourceFilesOf(project.FilePath!).Should().BeEquivalentTo([excludedPath]);
        loaded.IsExcludedSourceFile(excludedPath).Should().BeTrue();
        loaded.IsExcludedSourceFile(Path.Combine(this._dir, "A.cs")).Should().BeFalse();
    }

    [Fact]
    public void ApplyFileChange_WhenFileWasExcludedAtLoad_DoesNotAddDocument()
    {
        // Arrange
        string excludedPath = Path.Combine(this._dir, "Old.cs");
        File.WriteAllText(excludedPath, "public class Old { }");
        File.WriteAllText(Path.Combine(this._dir, "A.cs"), "public class A { }");
        (AdhocWorkspace workspace, _) = TestWorkspace.CreateProject(this._dir, "Demo", ("A.cs", "public class A { }"));
        using LoadedSolution loaded = new(workspace, workspace.CurrentSolution, Path.Combine(this._dir, "Demo.sln"), []);

        // Act: a checkout touched the excluded file and the watcher reported it
        IReadOnlyList<DocumentId> changed = loaded.ApplyFileChange(excludedPath, SourceText.From("public class Old { public int N; }"));

        // Assert
        changed.Should().BeEmpty();
        loaded.Solution.GetDocumentIdsWithFilePath(excludedPath).Should().BeEmpty();
    }

    [Fact]
    public void ApplyFileChange_WhenFileIsCreatedAfterLoad_AddsDocument()
    {
        // Arrange
        File.WriteAllText(Path.Combine(this._dir, "A.cs"), "public class A { }");
        (AdhocWorkspace workspace, _) = TestWorkspace.CreateProject(this._dir, "Demo", ("A.cs", "public class A { }"));
        using LoadedSolution loaded = new(workspace, workspace.CurrentSolution, Path.Combine(this._dir, "Demo.sln"), []);
        string newPath = Path.Combine(this._dir, "New.cs");
        File.WriteAllText(newPath, "public class New { }");

        // Act
        IReadOnlyList<DocumentId> changed = loaded.ApplyFileChange(newPath, SourceText.From("public class New { }"));

        // Assert
        changed.Should().ContainSingle();
        loaded.Solution.GetDocumentIdsWithFilePath(newPath).Should().ContainSingle();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this._dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
