using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Balalaika.Index;
using Balalaika.Workspace;

namespace Balalaika.Tests;

/// <summary>The warm-up skips a solution only when its index rows still match the disk exactly.</summary>
public sealed class IndexFreshnessTests : IDisposable
{
    private readonly string _dir = TestWorkspace.NewTempDir();
    private readonly string _solutionPath;
    private readonly string _aPath;
    private readonly IndexStore _index;
    private readonly AdhocWorkspace _workspace;
    private readonly LoadedSolution _loaded;

    public IndexFreshnessTests()
    {
        this._solutionPath = Path.Combine(this._dir, "Demo.sln");
        this._aPath = Path.Combine(this._dir, "A.cs");
        File.WriteAllText(this._aPath, "public class A { }");
        Directory.CreateDirectory(Path.Combine(this._dir, "obj"));
        File.WriteAllText(Path.Combine(this._dir, "obj", "Generated.cs"), "class Generated { }");

        (this._workspace, _) = TestWorkspace.CreateProject(this._dir, "Demo", ("A.cs", "public class A { }"));
        this._loaded = new LoadedSolution(this._workspace, this._workspace.CurrentSolution, this._solutionPath, []);
        this._index = new IndexStore(Path.Combine(this._dir, "index.db"));
    }

    [Fact]
    public void IsFresh_WhenSolutionWasNeverIndexed_IsFalse()
    {
        // Act / Assert
        IndexFreshness.IsFresh(this._index, this._solutionPath).Should().BeFalse();
    }

    [Fact]
    public async Task IsFresh_WhenIndexMatchesDisk_IsTrue()
    {
        // Arrange
        await this.IndexAsync();

        // Act / Assert: the file under obj/ is ignored
        IndexFreshness.IsFresh(this._index, this._solutionPath).Should().BeTrue();
    }

    [Fact]
    public async Task IsFresh_WhenTrackedFileChanged_IsFalse()
    {
        // Arrange
        await this.IndexAsync();
        File.WriteAllText(this._aPath, "public class A { public void M() { } }");

        // Act / Assert
        IndexFreshness.IsFresh(this._index, this._solutionPath).Should().BeFalse();
    }

    [Fact]
    public async Task IsFresh_WhenTrackedFileDeleted_IsFalse()
    {
        // Arrange
        await this.IndexAsync();
        File.Delete(this._aPath);

        // Act / Assert
        IndexFreshness.IsFresh(this._index, this._solutionPath).Should().BeFalse();
    }

    [Fact]
    public async Task IsFresh_WhenNewSourceFileAppears_IsFalse()
    {
        // Arrange
        await this.IndexAsync();
        Directory.CreateDirectory(Path.Combine(this._dir, "Sub"));
        File.WriteAllText(Path.Combine(this._dir, "Sub", "B.cs"), "public class B { }");

        // Act / Assert
        IndexFreshness.IsFresh(this._index, this._solutionPath).Should().BeFalse();
    }

    [Fact]
    public async Task IsFresh_IgnoresOtherSolutions()
    {
        // Arrange
        await this.IndexAsync();

        // Act / Assert
        IndexFreshness.IsFresh(this._index, Path.Combine(this._dir, "Other.sln")).Should().BeFalse();
    }

    [Fact]
    public async Task IsFresh_WhenUntrackedFileWasExcludedAtLoad_IsTrue()
    {
        // Arrange: a file the project does not compile (a Compile Remove glob) exists before the solution is loaded
        File.WriteAllText(Path.Combine(this._dir, "Old.cs"), "public class Old { }");
        using LoadedSolution loaded = new(null, this._workspace.CurrentSolution, this._solutionPath, []);
        await this.IndexAsync(loaded);

        // Act / Assert
        IndexFreshness.IsFresh(this._index, this._solutionPath).Should().BeTrue();
    }

    [Fact]
    public async Task IsFresh_WhenNewFileAppearsUnderIgnoredFolder_IsTrueOnlyWithTheFilter()
    {
        // Arrange
        await this.IndexAsync();
        Directory.CreateDirectory(Path.Combine(this._dir, "Migrations"));
        File.WriteAllText(Path.Combine(this._dir, "Migrations", "M.cs"), "public class M { }");

        // Act / Assert
        IndexFreshness.IsFresh(this._index, this._solutionPath, new SourceFilter(["Migrations"])).Should().BeTrue();
        IndexFreshness.IsFresh(this._index, this._solutionPath).Should().BeFalse();
    }

    [Fact]
    public async Task IsFresh_WhenTrackedFileIsUnderNewlyIgnoredFolder_IsFalse()
    {
        // Arrange: indexed without the filter, so the migration has rows that the next index pass must drop
        Directory.CreateDirectory(Path.Combine(this._dir, "Migrations"));
        File.WriteAllText(Path.Combine(this._dir, "Migrations", "M.cs"), "public class M { }");
        (AdhocWorkspace workspace, _) = TestWorkspace.CreateProject(this._dir, "Demo", ("A.cs", "public class A { }"), (@"Migrations\M.cs", "public class M { }"));
        using LoadedSolution loaded = new(workspace, workspace.CurrentSolution, this._solutionPath, []);
        await this.IndexAsync(loaded);
        IndexFreshness.IsFresh(this._index, this._solutionPath).Should().BeTrue();

        // Act / Assert
        IndexFreshness.IsFresh(this._index, this._solutionPath, new SourceFilter(["Migrations"])).Should().BeFalse();
    }

    private Task IndexAsync()
    {
        return this.IndexAsync(this._loaded);
    }

    private async Task IndexAsync(LoadedSolution loaded)
    {
        SolutionIndexer indexer = new(this._index, NullLogger<SolutionIndexer>.Instance);
        await indexer.IndexSolutionAsync(loaded, force: false, CancellationToken.None);
    }

    public void Dispose()
    {
        this._index.Dispose();
        this._loaded.Dispose();
        try
        {
            Directory.Delete(this._dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
