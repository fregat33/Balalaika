using FluentAssertions;
using Microsoft.CodeAnalysis;
using Balalaika.Workspace;

namespace Balalaika.Tests;

/// <summary>A query that runs right after a save must see the saved text without waiting for the watcher.</summary>
public sealed class LiveTextSyncTests : IDisposable
{
    private readonly string _dir = TestWorkspace.NewTempDir();

    [Fact]
    public async Task SyncFromDiskAsync_WhenDiskTextDiffers_AppliesIt()
    {
        // Arrange: the solution was loaded with one text, then the file was saved with another
        string path = Path.Combine(this._dir, "A.cs");
        File.WriteAllText(path, "public class A { }");
        (AdhocWorkspace workspace, _) = TestWorkspace.CreateProject(this._dir, "Demo", ("A.cs", "public class A { }"));
        using LoadedSolution loaded = new(workspace, workspace.CurrentSolution, Path.Combine(this._dir, "Demo.sln"), []);
        File.WriteAllText(path, "public class A { public int N; }");

        // Act
        IReadOnlyList<DocumentId> changed = await LiveTextSync.SyncFromDiskAsync(loaded, path, CancellationToken.None);

        // Assert
        changed.Should().HaveCount(1);
        Document document = loaded.Solution.GetDocument(changed[0])!;
        (await document.GetTextAsync()).ToString().Should().Be("public class A { public int N; }");
    }

    [Fact]
    public async Task SyncFromDiskAsync_WhenDiskMatchesLiveText_ChangesNothing()
    {
        // Arrange
        string path = Path.Combine(this._dir, "A.cs");
        File.WriteAllText(path, "public class A { }");
        (AdhocWorkspace workspace, _) = TestWorkspace.CreateProject(this._dir, "Demo", ("A.cs", "public class A { }"));
        using LoadedSolution loaded = new(workspace, workspace.CurrentSolution, Path.Combine(this._dir, "Demo.sln"), []);
        Solution before = loaded.Solution;

        // Act
        IReadOnlyList<DocumentId> changed = await LiveTextSync.SyncFromDiskAsync(loaded, path, CancellationToken.None);

        // Assert: no fork of the solution for a no-op
        changed.Should().BeEmpty();
        loaded.Solution.Should().BeSameAs(before);
    }

    [Fact]
    public async Task SyncFromDiskAsync_WhenFileIsNewUnderProject_AddsDocument()
    {
        // Arrange
        File.WriteAllText(Path.Combine(this._dir, "A.cs"), "public class A { }");
        (AdhocWorkspace workspace, _) = TestWorkspace.CreateProject(this._dir, "Demo", ("A.cs", "public class A { }"));
        using LoadedSolution loaded = new(workspace, workspace.CurrentSolution, Path.Combine(this._dir, "Demo.sln"), []);
        string newPath = Path.Combine(this._dir, "New.cs");
        File.WriteAllText(newPath, "public class New { }");

        // Act
        IReadOnlyList<DocumentId> changed = await LiveTextSync.SyncFromDiskAsync(loaded, newPath, CancellationToken.None);

        // Assert
        changed.Should().HaveCount(1);
        loaded.FindDocuments(newPath).Should().HaveCount(1);
    }

    [Fact]
    public async Task SyncFromDiskAsync_WhenFileWasExcludedAtLoad_DoesNotAddDocument()
    {
        // Arrange: Old.cs is on disk but not a document, as with a Compile Remove glob
        string excludedPath = Path.Combine(this._dir, "Old.cs");
        File.WriteAllText(excludedPath, "public class Old { }");
        File.WriteAllText(Path.Combine(this._dir, "A.cs"), "public class A { }");
        (AdhocWorkspace workspace, _) = TestWorkspace.CreateProject(this._dir, "Demo", ("A.cs", "public class A { }"));
        using LoadedSolution loaded = new(workspace, workspace.CurrentSolution, Path.Combine(this._dir, "Demo.sln"), []);
        File.WriteAllText(excludedPath, "public class Old { public int N; }");

        // Act
        IReadOnlyList<DocumentId> changed = await LiveTextSync.SyncFromDiskAsync(loaded, excludedPath, CancellationToken.None);

        // Assert
        changed.Should().BeEmpty();
        loaded.FindDocuments(excludedPath).Should().BeEmpty();
    }

    [Fact]
    public async Task SyncFromDiskAsync_WhenFileWasDeleted_RemovesDocument()
    {
        // Arrange
        string path = Path.Combine(this._dir, "A.cs");
        File.WriteAllText(path, "public class A { }");
        (AdhocWorkspace workspace, _) = TestWorkspace.CreateProject(this._dir, "Demo", ("A.cs", "public class A { }"));
        using LoadedSolution loaded = new(workspace, workspace.CurrentSolution, Path.Combine(this._dir, "Demo.sln"), []);
        File.Delete(path);

        // Act
        IReadOnlyList<DocumentId> changed = await LiveTextSync.SyncFromDiskAsync(loaded, path, CancellationToken.None);

        // Assert
        changed.Should().HaveCount(1);
        loaded.FindDocuments(path).Should().BeEmpty();
    }

    [Fact]
    public async Task SyncFromDiskAsync_WhenFileNeverExisted_ChangesNothing()
    {
        // Arrange
        File.WriteAllText(Path.Combine(this._dir, "A.cs"), "public class A { }");
        (AdhocWorkspace workspace, _) = TestWorkspace.CreateProject(this._dir, "Demo", ("A.cs", "public class A { }"));
        using LoadedSolution loaded = new(workspace, workspace.CurrentSolution, Path.Combine(this._dir, "Demo.sln"), []);

        // Act
        IReadOnlyList<DocumentId> changed = await LiveTextSync.SyncFromDiskAsync(loaded, Path.Combine(this._dir, "Missing.cs"), CancellationToken.None);

        // Assert
        changed.Should().BeEmpty();
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
