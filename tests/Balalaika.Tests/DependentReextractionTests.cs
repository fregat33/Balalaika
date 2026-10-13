using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Balalaika.Index;
using Balalaika.Workspace;

namespace Balalaika.Tests;

/// <summary>
/// The "new overload captures an existing call site" case: App calls <c>B.Go(1)</c>, which binds to <c>Go(object)</c>
/// until Lib adds <c>Go(int)</c>. App.cs did not change, so only re-extracting the dependent project fixes its rows.
/// </summary>
public sealed class DependentReextractionTests : IDisposable
{
    private const string LibV1 = """
        public static class B
        {
            public static void Go(object o)
            {
            }
        }
        """;

    private const string LibV2 = """
        public static class B
        {
            public static void Go(object o)
            {
            }

            public static void Go(int i)
            {
            }
        }
        """;

    private const string AppSource = """
        public class A
        {
            public void M()
            {
                B.Go(1);
            }
        }
        """;

    private readonly string _dir = TestWorkspace.NewTempDir();
    private readonly AdhocWorkspace _workspace;
    private readonly ProjectId _lib;
    private readonly ProjectId _app;
    private readonly string _libPath;
    private readonly string _appPath;
    private readonly IndexStore _index;
    private readonly SolutionIndexer _indexer;
    private readonly LoadedSolution _loaded;

    public DependentReextractionTests()
    {
        string libDir = Path.Combine(this._dir, "Lib");
        string appDir = Path.Combine(this._dir, "App");
        Directory.CreateDirectory(libDir);
        Directory.CreateDirectory(appDir);
        this._libPath = Path.Combine(libDir, "B.cs");
        this._appPath = Path.Combine(appDir, "A.cs");
        File.WriteAllText(this._libPath, LibV1);
        File.WriteAllText(this._appPath, AppSource);

        (this._workspace, Project lib) = TestWorkspace.CreateProject(libDir, "Lib", ("B.cs", LibV1));
        Project app = TestWorkspace.AddProject(this._workspace, appDir, "App", [lib.Id], ("A.cs", AppSource));
        this._lib = lib.Id;
        this._app = app.Id;

        this._loaded = new LoadedSolution(this._workspace, this._workspace.CurrentSolution, Path.Combine(this._dir, "Demo.sln"), []);
        this._index = new IndexStore(Path.Combine(this._dir, "index.db"));
        this._indexer = new SolutionIndexer(this._index, NullLogger<SolutionIndexer>.Instance);
    }

    [Fact]
    public async Task ReextractProjects_AfterNewOverloadInDependency_RebindsReferencesOfUnchangedDependent()
    {
        // Arrange
        await this._indexer.IndexSolutionAsync(this._loaded, force: false, CancellationToken.None);
        this._index.FindReferences("M:B.Go(System.Object)").Should().ContainSingle().Which.FilePath.Should().Be(this._appPath);

        // Act: Lib gains Go(int); only B.cs changed
        File.WriteAllText(this._libPath, LibV2);
        IReadOnlyList<DocumentId> ids = this._loaded.ApplyFileChange(this._libPath, SourceText.From(LibV2));
        IncrementalIndexReport report = await this._indexer.IndexDocumentsAsync(this._loaded, ids, CancellationToken.None);

        // Assert: the surface change is reported and App's stored rows are stale at this point
        report.SurfaceChangedProjects.Should().BeEquivalentTo([this._lib]);
        this._index.FindReferences("M:B.Go(System.Int32)").Should().BeEmpty();

        // Act: re-extract Lib and its dependents
        IReadOnlySet<ProjectId> targets = DependentProjects.WithTransitiveDependents(this._loaded.Solution, report.SurfaceChangedProjects);
        int extracted = await this._indexer.ReextractProjectsAsync(this._loaded, targets, CancellationToken.None);

        // Assert
        targets.Should().BeEquivalentTo([this._lib, this._app]);
        extracted.Should().Be(2);
        this._index.FindReferences("M:B.Go(System.Int32)").Should().ContainSingle().Which.FilePath.Should().Be(this._appPath);
        this._index.FindReferences("M:B.Go(System.Object)").Should().BeEmpty();
    }

    [Fact]
    public void WithTransitiveDependents_IncludesIndirectDependentsOnly()
    {
        // Arrange: Lib <- Mid <- Top, and Side depends on nothing
        Project mid = TestWorkspace.AddProject(this._workspace, Path.Combine(this._dir, "Mid"), "Mid", [this._lib]);
        Project top = TestWorkspace.AddProject(this._workspace, Path.Combine(this._dir, "Top"), "Top", [mid.Id]);
        Project side = TestWorkspace.AddProject(this._workspace, Path.Combine(this._dir, "Side"), "Side", []);
        Solution solution = this._workspace.CurrentSolution;

        // Act / Assert
        DependentProjects.WithTransitiveDependents(solution, [this._lib]).Should().BeEquivalentTo([this._lib, this._app, mid.Id, top.Id]);
        DependentProjects.WithTransitiveDependents(solution, [mid.Id]).Should().BeEquivalentTo([mid.Id, top.Id]);
        DependentProjects.WithTransitiveDependents(solution, [side.Id]).Should().BeEquivalentTo([side.Id]);
    }

    [Fact]
    public async Task ReferencingAssemblies_FindsConsumersInAnotherSolutionByDllNameOrProjectPath()
    {
        // Arrange: a second solution consumes Lib as a compiled DLL, the way a NuGet package reference does
        string dllPath = Path.Combine(this._dir, "packages", "Lib.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dllPath)!);
        Compilation compilation = (await this._workspace.CurrentSolution.GetProject(this._lib)!.GetCompilationAsync())!;
        EmitResult emitted = compilation.Emit(dllPath);
        emitted.Success.Should().BeTrue(string.Join("\n", emitted.Diagnostics));

        string consumerDir = Path.Combine(this._dir, "Consumer");
        List<MetadataReference> references = [.. TestWorkspace.DefaultReferences, MetadataReference.CreateFromFile(dllPath)];
        (AdhocWorkspace other, Project consumer) = TestWorkspace.CreateProject(consumerDir, "Consumer", references, ("C.cs", "public class C { public void X() { B.Go(1); } }"));
        Project unrelated = TestWorkspace.AddProject(other, Path.Combine(this._dir, "Unrelated"), "Unrelated", []);
        using AdhocWorkspace _ = other;
        Solution solution = other.CurrentSolution;

        // Act / Assert
        DependentProjects.ReferencingAssemblies(solution, ["Lib"], []).Should().BeEquivalentTo([consumer.Id]);
        DependentProjects.ReferencingAssemblies(solution, ["Other"], []).Should().BeEmpty();
        DependentProjects.ReferencingAssemblies(solution, [], [unrelated.FilePath!]).Should().BeEquivalentTo([unrelated.Id]);
        DependentProjects.ReferencingAssemblies(solution, [], []).Should().BeEmpty();
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
