using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Balalaika.Index;
using Balalaika.Workspace;

namespace Balalaika.Tests;

/// <summary>Simulates what happens on a git branch switch: file content changes, is reverted, and files disappear.</summary>
public sealed class SolutionIndexerTests : IDisposable
{
    private const string AWithCall = """
        public class A
        {
            public void M()
            {
                B.Go();
            }
        }
        """;

    private const string AWithoutCall = """
        public class A
        {
            public void M()
            {
            }
        }
        """;

    private const string AWithPrivateField = """
        public class A
        {
            private int _n;

            public void M()
            {
                B.Go();
            }
        }
        """;

    private const string AWithPublicMethod = """
        public class A
        {
            public void M()
            {
                B.Go();
            }

            public void N()
            {
            }
        }
        """;

    private const string BSource = """
        public static class B
        {
            public static void Go()
            {
            }
        }
        """;

    private readonly string _dir = TestWorkspace.NewTempDir();
    private readonly IndexStore _index;
    private readonly SolutionIndexer _indexer;
    private readonly LoadedSolution _loaded;
    private readonly string _aPath;
    private readonly string _bPath;

    public SolutionIndexerTests()
    {
        this._aPath = Path.Combine(this._dir, "A.cs");
        this._bPath = Path.Combine(this._dir, "B.cs");
        File.WriteAllText(this._aPath, AWithCall);
        File.WriteAllText(this._bPath, BSource);

        (AdhocWorkspace workspace, _) = TestWorkspace.CreateProject(this._dir, "Demo", ("A.cs", AWithCall), ("B.cs", BSource));
        this._loaded = new LoadedSolution(workspace, workspace.CurrentSolution, Path.Combine(this._dir, "Demo.sln"), []);
        this._index = new IndexStore(Path.Combine(this._dir, "index.db"));
        this._indexer = new SolutionIndexer(this._index, NullLogger<SolutionIndexer>.Instance);
    }

    [Fact]
    public async Task IndexSolution_RecordsFilesTheProjectDoesNotCompile()
    {
        // Arrange: Old.cs sits under the project directory but is not a document
        string excludedPath = Path.Combine(this._dir, "Old.cs");
        File.WriteAllText(excludedPath, "public class Old { }");
        (AdhocWorkspace workspace, Project project) = TestWorkspace.CreateProject(this._dir, "Demo", ("A.cs", AWithCall), ("B.cs", BSource));
        using LoadedSolution loaded = new(workspace, workspace.CurrentSolution, Path.Combine(this._dir, "Demo.sln"), []);

        // Act
        await this._indexer.IndexSolutionAsync(loaded, force: false, CancellationToken.None);

        // Assert
        int projectId = this._index.EnsureProject(project.FilePath!, project.Name, project.AssemblyName, loaded.SolutionPath);
        this._index.GetExcludedFiles(projectId).Should().BeEquivalentTo([excludedPath]);
        this._index.FindTreeEntries(excludedPath).Should().BeEmpty();
    }

    [Fact]
    public async Task IndexSolution_WithIgnoredFolder_SkipsItsDocumentsAndDropsTheirRows()
    {
        // Arrange: indexed once without a filter, so the migration has tree rows
        const string migrationSource = "public class M { }";
        Directory.CreateDirectory(Path.Combine(this._dir, "Migrations"));
        string migrationPath = Path.Combine(this._dir, "Migrations", "M.cs");
        File.WriteAllText(migrationPath, migrationSource);
        (AdhocWorkspace workspace, _) = TestWorkspace.CreateProject(this._dir, "Demo", ("A.cs", AWithCall), ("B.cs", BSource), (@"Migrations\M.cs", migrationSource));
        using LoadedSolution loaded = new(workspace, workspace.CurrentSolution, Path.Combine(this._dir, "Demo.sln"), []);
        await this._indexer.IndexSolutionAsync(loaded, force: false, CancellationToken.None);
        this._index.FindTreeEntries(migrationPath).Should().ContainSingle();

        // Act
        SolutionIndexer filtered = new(this._index, NullLogger<SolutionIndexer>.Instance, new SourceFilter(["Migrations"]));
        IndexReport report = await filtered.IndexSolutionAsync(loaded, force: false, CancellationToken.None);

        // Assert
        report.DocumentsVisited.Should().Be(2);
        report.TreeEntriesRemoved.Should().Be(1);
        this._index.FindTreeEntries(migrationPath).Should().BeEmpty();
        this._index.FindTreeEntries(this._aPath).Should().ContainSingle();
    }

    [Fact]
    public async Task IndexSolution_ThenChangeAndRevert_ReusesBlobsAndKeepsReferencesCorrect()
    {
        // Arrange / Act: initial index
        IndexReport first = await this._indexer.IndexSolutionAsync(this._loaded, force: false, CancellationToken.None);

        // Assert
        first.DocumentsExtracted.Should().Be(2);
        this._index.FindReferences("M:B.Go").Should().ContainSingle().Which.FilePath.Should().Be(this._aPath);

        // Act: "checkout feature branch" where A no longer calls B
        await this.WriteAndApplyAsync(this._aPath, AWithoutCall);

        // Assert
        this._index.FindReferences("M:B.Go").Should().BeEmpty();
        this._index.GetStats().Blobs.Should().Be(3);

        // Act: "checkout dev" again, content seen before
        IncrementalIndexReport reverted = await this.WriteAndApplyAsync(this._aPath, AWithCall);

        // Assert: nothing re-extracted, reference is back, no new blob
        reverted.DocumentsExtracted.Should().Be(0);
        this._index.FindReferences("M:B.Go").Should().ContainSingle();
        this._index.GetStats().Blobs.Should().Be(3);
    }

    [Fact]
    public async Task IndexSolution_SecondRun_SkipsUnchangedFilesByStat()
    {
        // Arrange
        await this._indexer.IndexSolutionAsync(this._loaded, force: false, CancellationToken.None);

        // Act
        IndexReport second = await this._indexer.IndexSolutionAsync(this._loaded, force: false, CancellationToken.None);

        // Assert
        second.DocumentsExtracted.Should().Be(0);
        second.DocumentsReused.Should().Be(2);
    }

    [Fact]
    public async Task IndexSolution_WithForce_ReExtractsEverything()
    {
        // Arrange
        await this._indexer.IndexSolutionAsync(this._loaded, force: false, CancellationToken.None);

        // Act
        IndexReport forced = await this._indexer.IndexSolutionAsync(this._loaded, force: true, CancellationToken.None);

        // Assert
        forced.DocumentsExtracted.Should().Be(2);
        this._index.GetStats().Blobs.Should().Be(2);
        this._index.FindReferences("M:B.Go").Should().ContainSingle();
    }

    [Fact]
    public async Task IndexSolution_RemovesTreeEntriesForDeletedDocuments()
    {
        // Arrange
        await this._indexer.IndexSolutionAsync(this._loaded, force: false, CancellationToken.None);
        this._index.FindSymbolsByDocId("T:B").Should().ContainSingle();

        // Act: file deleted on disk and removed from the solution
        File.Delete(this._bPath);
        this._loaded.ApplyFileChange(this._bPath, null);
        IndexReport report = await this._indexer.IndexSolutionAsync(this._loaded, force: false, CancellationToken.None);

        // Assert
        report.TreeEntriesRemoved.Should().Be(1);
        this._index.FindSymbolsByDocId("T:B").Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyFileChange_ForNewFile_AddsDocumentToOwningProject()
    {
        // Arrange
        await this._indexer.IndexSolutionAsync(this._loaded, force: false, CancellationToken.None);
        string cPath = Path.Combine(this._dir, "Sub", "C.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(cPath)!);
        const string cSource = "public class C { public void X() { B.Go(); } }";
        File.WriteAllText(cPath, cSource);

        // Act
        IReadOnlyList<DocumentId> ids = this._loaded.ApplyFileChange(cPath, SourceText.From(cSource));
        IncrementalIndexReport report = await this._indexer.IndexDocumentsAsync(this._loaded, ids, CancellationToken.None);

        // Assert: a new file declaring a public type is a surface change of its project
        ids.Should().ContainSingle();
        this._index.FindReferences("M:B.Go").Select(h => h.FilePath).Should().BeEquivalentTo([this._aPath, cPath]);
        report.SurfaceChangedProjects.Should().BeEquivalentTo([this._loaded.Solution.ProjectIds.Single()]);
    }

    [Fact]
    public async Task IndexDocuments_WhenOnlyMethodBodyChanges_ReportsNoSurfaceChange()
    {
        // Arrange
        await this._indexer.IndexSolutionAsync(this._loaded, force: false, CancellationToken.None);

        // Act
        IncrementalIndexReport report = await this.WriteAndApplyAsync(this._aPath, AWithoutCall);

        // Assert
        report.DocumentsExtracted.Should().Be(1);
        report.SurfaceChangedProjects.Should().BeEmpty();
    }

    [Fact]
    public async Task IndexDocuments_WhenPrivateMemberAdded_ReportsNoSurfaceChange()
    {
        // Arrange
        await this._indexer.IndexSolutionAsync(this._loaded, force: false, CancellationToken.None);

        // Act
        IncrementalIndexReport report = await this.WriteAndApplyAsync(this._aPath, AWithPrivateField);

        // Assert
        report.SurfaceChangedProjects.Should().BeEmpty();
    }

    [Fact]
    public async Task IndexDocuments_WhenPublicMemberAdded_ReportsProjectSurfaceChanged()
    {
        // Arrange
        await this._indexer.IndexSolutionAsync(this._loaded, force: false, CancellationToken.None);

        // Act
        IncrementalIndexReport report = await this.WriteAndApplyAsync(this._aPath, AWithPublicMethod);

        // Assert
        report.SurfaceChangedProjects.Should().BeEquivalentTo([this._loaded.Solution.ProjectIds.Single()]);
    }

    [Fact]
    public async Task IndexDocuments_WhenRevertedContentReusesBlob_StillDetectsSurfaceChange()
    {
        // Arrange: the public method exists, then is removed again by reverting to content whose blob is already stored
        await this._indexer.IndexSolutionAsync(this._loaded, force: false, CancellationToken.None);
        await this.WriteAndApplyAsync(this._aPath, AWithPublicMethod);

        // Act
        IncrementalIndexReport report = await this.WriteAndApplyAsync(this._aPath, AWithCall);

        // Assert: nothing extracted, but the removal of a public member is still a surface change
        report.DocumentsExtracted.Should().Be(0);
        report.SurfaceChangedProjects.Should().BeEquivalentTo([this._loaded.Solution.ProjectIds.Single()]);
    }

    [Fact]
    public async Task RemoveDocument_WhenDeletedFileDeclaredTypes_ReportsSurfaceChangeAndDropsTreeEntry()
    {
        // Arrange
        await this._indexer.IndexSolutionAsync(this._loaded, force: false, CancellationToken.None);
        File.Delete(this._bPath);

        // Act
        IReadOnlyCollection<ProjectId> changed = this._indexer.RemoveDocument(this._loaded, this._bPath);
        this._loaded.ApplyFileChange(this._bPath, null);

        // Assert
        changed.Should().BeEquivalentTo([this._loaded.Solution.ProjectIds.Single()]);
        this._index.FindSymbolsByDocId("T:B").Should().BeEmpty();
        this._indexer.RemoveDocument(this._loaded, this._bPath).Should().BeEmpty("the tree entry is already gone");
    }

    [Fact]
    public async Task IndexSolution_WhenProjectWasStampedWithAnOlderSchema_ReextractsEveryDocumentOnce()
    {
        // Arrange: indexed under the current schema, then stamped as if an older server had written it
        await this._indexer.IndexSolutionAsync(this._loaded, force: false, CancellationToken.None);
        int projectId = this._index.GetProjects().Single().Id;
        this._index.GetIndexedSchema(projectId).Should().Be(IndexStore.SchemaVersion);
        using (Microsoft.Data.Sqlite.SqliteConnection connection = new($"Data Source={this._index.DatabasePath}"))
        {
            connection.Open();
            using Microsoft.Data.Sqlite.SqliteCommand downgrade = connection.CreateCommand();
            downgrade.CommandText = "UPDATE project SET indexed_schema = indexed_schema - 1; UPDATE reference SET from_doc_id = NULL;";
            downgrade.ExecuteNonQuery();
        }

        this._index.FindReferences("M:B.Go").Should().ContainSingle().Which.Enclosing.Should().BeNull();

        // Act
        IndexReport upgrade = await this._indexer.IndexSolutionAsync(this._loaded, force: false, CancellationToken.None);
        IndexReport steady = await this._indexer.IndexSolutionAsync(this._loaded, force: false, CancellationToken.None);

        // Assert: the stat fast path would have reused both files; the stamp forces one re-extraction, after which the project is stamped current
        upgrade.DocumentsExtracted.Should().Be(2);
        this._index.FindReferences("M:B.Go").Should().ContainSingle().Which.Enclosing.Should().Be("A.M");
        this._index.GetIndexedSchema(projectId).Should().Be(IndexStore.SchemaVersion);
        steady.DocumentsExtracted.Should().Be(0);
    }

    private async Task<IncrementalIndexReport> WriteAndApplyAsync(string path, string content)
    {
        File.WriteAllText(path, content);
        IReadOnlyList<DocumentId> ids = this._loaded.ApplyFileChange(path, SourceText.From(content));
        return await this._indexer.IndexDocumentsAsync(this._loaded, ids, CancellationToken.None);
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
