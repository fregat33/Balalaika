using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Balalaika.Index;
using Balalaika.Workspace;

namespace Balalaika.Tests;

/// <summary>
/// Skips a test when MSBuildWorkspace cannot load a project here (its out-of-process build host needs a named pipe and
/// the dotnet SDK; a restricted sandbox has neither). Where it can, the test runs the real loader.
/// </summary>
public sealed class MsBuildFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Unavailable = new(Probe);

    public MsBuildFactAttribute()
    {
        if (Unavailable.Value is { } reason)
        {
            this.Skip = "MSBuildWorkspace cannot load a project here: " + reason;
        }
    }

    private static string? Probe()
    {
        try
        {
            string dir = Path.GetFullPath(TestWorkspace.NewTempDir());
            File.WriteAllText(Path.Combine(dir, "Probe.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Path.Combine(dir, "Probe.slnx"), "<Solution><Project Path=\"Probe.csproj\" /></Solution>");
            using LoadedSolution loaded = LoadedSolution.LoadAsync(Path.Combine(dir, "Probe.slnx"), NullLogger.Instance, CancellationToken.None).GetAwaiter().GetResult();
            return loaded.Solution.Projects.Any() ? null : "the probe solution has no project";
        }
        catch (Exception e)
        {
            return e.GetType().Name;
        }
    }
}

/// <summary>
/// <c>find_references</c> answered <c>Live X: 0 references</c> on the running server for symbols with plain direct
/// usages while <c>get_call_graph</c> listed the callers. The cause was that <see cref="SymbolResolver.ResolveInSolutionAsync"/>
/// returned the symbol from the FIRST project whose compilation resolved the name. When that project depended on the
/// declaring one and bound a shared assembly (for example <c>Microsoft.Extensions.Options</c>, which the Web SDK
/// framework reference and a package reference carry in different versions) to another version than the declaring
/// project did, Roslyn handed out a <em>retargeting</em> copy of the declaring assembly: the symbol is source
/// (<c>Locations</c> are in source, so <c>IsSource</c> holds), but its assembly is a <c>NonSourceAssemblySymbol</c>
/// that is not the one of the declaring project's own compilation, and <c>SymbolFinder.FindReferencesAsync</c> finds nothing for it.
/// Now a name resolves in the declaring project: <see cref="SymbolResolver.ResolveInSolutionAsync"/> returns a source
/// symbol only when the project's own compilation declares it (its <c>ContainingAssembly</c> is that project's own), and
/// keeps a foreign source symbol only as a fallback when no project declares it. <c>FindTwinAsync</c> prefers the
/// declaring project the same way. Neither the order of projects alone nor warm or cold compilations decide it (the
/// <c>_Correct</c> tests).
/// </summary>
public sealed class LiveReferenceLoadTests
{
    private const string Spec = "Acme.Lib.Pricing.Round";
    private const string LibText = "namespace Acme.Lib;\npublic static class Pricing\n{\n    public static int Round(int x) => x;\n    public static int Twice(int x) => Round(x) + Round(x);\n}\n";
    private const string LibWithDepText = "namespace Acme.Lib;\npublic static class Pricing\n{\n    public static int Round(int x) => x;\n    public static int Twice(int x) => Round(x) + Round(x);\n    public static Acme.Dep.Marker Make() => new();\n}\n";
    private const string AppText = "namespace Acme.App;\npublic static class Use\n{\n    public static int Run() => Acme.Lib.Pricing.Round(3);\n}\n";

    // The real usages of Pricing.Round: two in Pricing.Twice, one in Use.Run.
    private const int RealUsages = 3;

    // ------------------------------------------------------------------ the solution of this repository, real loader

    [MsBuildFact]
    public async Task RealLoader_ThisRepositorySolution_FindReferencesOfAWorkspaceMemberResolvesInTheDeclaringProjectAndCountsItsUsages_Correct()
    {
        // Arrange: Balalaika.slnx lists Balalaika.Server (Web SDK, references Balalaika.Workspace) before Balalaika.Workspace
        string? found = FindRepositorySolution();
        found.Should().NotBeNull("the test runs from a checkout of this repository");
        string sln = found!;

        using Server server = Server.Create();
        server.Workspaces.RegisterSolutions([sln]);
        LoadedSolution loaded = await server.Workspaces.GetOrLoadAsync(sln, CancellationToken.None);
        const string spec = "Balalaika.Workspace.QueryService.BindsToDeclaration";

        // Act (compilations are checked last so that the check does not change what the queries see)
        string refs = await server.Query.FindReferencesAsync(spec, null, false, null, null, 50, CancellationToken.None);
        string graph = await server.Query.GetCallGraphAsync(spec, null, "callers", false, null, true, 50, CancellationToken.None);
        ResolvedSymbol? resolved = await SymbolResolver.ResolveInSolutionAsync(spec, loaded, CancellationToken.None);
        ISymbol own = (await loaded.Solution.Projects.Single(p => p.Name == "Balalaika.Workspace").GetCompilationAsync())!
            .GetTypeByMetadataName("Balalaika.Workspace.QueryService")!.GetMembers("BindsToDeclaration").Single();
        int viaResolved = await CountBindingLocationsAsync(resolved!.Symbol, loaded.Solution);
        int viaOwn = await CountBindingLocationsAsync(own, loaded.Solution);
        AssertNoCompileErrors(loaded.Solution);

        // Assert: the real count is at least 3 (QueryService.cs twice, CountRuleTests once; this file adds one more)
        resolved.Project.Name.Should().Be("Balalaika.Workspace");
        viaResolved.Should().Be(viaOwn);
        viaOwn.Should().BeGreaterThanOrEqualTo(3, "the declaring project's own symbol finds the usages");
        refs.Should().Contain($"Live Balalaika: {viaOwn} references");
        graph.Should().Contain("Callers (");
        graph.Should().Contain(Path.Combine("src", "Balalaika.Workspace", "QueryService.cs"), "a production caller of BindsToDeclaration exists");
    }

    [MsBuildFact]
    public async Task RealLoader_TwoProjectsWithoutVersionSkew_DependentListedFirst_FindsEveryUsage_Correct()
    {
        // Arrange: App (references Lib) is listed first, as Server is before Workspace; both bind the same framework
        string sln = WriteOnDisk();
        using Server server = Server.Create();
        server.Workspaces.RegisterSolutions([sln]);
        LoadedSolution loaded = await server.Workspaces.GetOrLoadAsync(sln, CancellationToken.None);

        // Act
        string refs = await server.Query.FindReferencesAsync(Spec, null, false, null, null, 50, CancellationToken.None);
        ResolvedSymbol? resolved = await SymbolResolver.ResolveInSolutionAsync(Spec, loaded, CancellationToken.None);
        AssertNoCompileErrors(loaded.Solution);

        // Assert: the order of projects alone does not produce the zero
        resolved!.Project.Name.Should().Be("Lib");
        refs.Should().Contain("Live Demo: 3 references");
    }

    // ------------------------------------------------------------------ same shape, workspace without MSBuild

    [Fact]
    public async Task DependentProjectBindsAnotherVersionOfASharedAssembly_NameResolvesInTheDeclaringProject_AndFindReferencesCountsEveryUsage_Correct()
    {
        // Arrange: App is listed first and references Lib; Lib binds Acme.Dep 1.0, App binds Acme.Dep 2.0
        (AdhocWorkspace workspace, LoadedSolution loaded) = Build(appFirst: true, versionSkew: true);
        using Server server = Server.Create();
        server.Workspaces.Adopt(loaded);

        // Act
        string refs = await server.Query.FindReferencesAsync(Spec, null, false, null, null, 50, CancellationToken.None);
        string graph = await server.Query.GetCallGraphAsync(Spec, null, "callers", false, null, false, 50, CancellationToken.None);
        ResolvedSymbol? resolved = await SymbolResolver.ResolveInSolutionAsync(Spec, loaded, CancellationToken.None);
        ISymbol own = OwnSymbol(loaded.Solution);
        int viaResolved = await CountBindingLocationsAsync(resolved!.Symbol, loaded.Solution);
        int viaOwn = await CountBindingLocationsAsync(own, loaded.Solution);
        AssertNoCompileErrors(loaded.Solution);

        // Assert
        resolved.Project.Name.Should().Be("Lib", "the name resolves in the project that declares it, not in the dependent that sees a retargeting copy");
        resolved.IsSource.Should().BeTrue();
        SymbolEqualityComparer.Default.Equals(resolved.Symbol, own).Should().BeTrue();

        refs.Should().Contain($"Live Demo: {RealUsages} references");
        viaResolved.Should().Be(RealUsages);

        // The declaring project's own symbol and the call graph see all of them
        viaOwn.Should().Be(RealUsages);
        graph.Should().Contain("Callers (3, live");
        workspace.Dispose();
    }

    [Fact]
    public async Task TwinLookup_SecondSolutionListsTheDependentFirstWithVersionSkew_AllSolutionsCountsTheUsagesInBoth_Correct()
    {
        // Arrange: the first solution has Lib first, the second lists App first and binds another version of Acme.Dep
        (AdhocWorkspace w1, LoadedSolution a) = Build(appFirst: false, versionSkew: true);
        (AdhocWorkspace w2, LoadedSolution b) = Build(appFirst: true, versionSkew: true);
        using Server server = Server.Create();
        server.Workspaces.Adopt(a);
        server.Workspaces.Adopt(b);

        // Act
        string all = await server.Query.FindReferencesAsync(Spec, null, true, null, null, 50, CancellationToken.None);
        ISymbol? twin = await SymbolResolver.FindTwinAsync("M:Acme.Lib.Pricing.Round(System.Int32)", "Lib", b, CancellationToken.None);

        // Assert
        all.Split("Live Demo: 3 references").Length.Should().Be(3, "one live block per solution, the twin in the second one included");
        twin.Should().NotBeNull();
        SymbolEqualityComparer.Default.Equals(twin, OwnSymbol(b.Solution)).Should().BeTrue("the twin is the symbol of the declaring project");
        w1.Dispose();
        w2.Dispose();
    }

    [Fact]
    public async Task MetadataOnlySymbolStillResolves_AndAnUnknownSpecAnswersSymbolNotFound_Correct()
    {
        // Arrange
        (AdhocWorkspace workspace, LoadedSolution loaded) = Build(appFirst: true, versionSkew: true);
        using Server server = Server.Create();
        server.Workspaces.Adopt(loaded);

        // Act
        ResolvedSymbol? framework = await SymbolResolver.ResolveInSolutionAsync("System.String", loaded, CancellationToken.None);
        string missing = await server.Query.FindReferencesAsync("Acme.Nothing.Missing", null, false, null, null, 50, CancellationToken.None);

        // Assert
        framework.Should().NotBeNull();
        framework!.IsSource.Should().BeFalse();
        missing.Should().Contain("Symbol not found");
        workspace.Dispose();
    }

    [Fact]
    public async Task DependentProjectBindsAnotherVersionOfASharedAssembly_ASourcePositionResolvesInTheDeclaringProject_AndFindsEveryUsage_Correct()
    {
        // Arrange
        (AdhocWorkspace workspace, LoadedSolution loaded) = Build(appFirst: true, versionSkew: true);
        using Server server = Server.Create();
        server.Workspaces.Adopt(loaded);
        string libFile = loaded.Solution.Projects.Single(p => p.Name == "Lib").Documents.Single().FilePath!;

        // Act: the spec form that goes through the document; the running server answered 3 for it
        string refs = await server.Query.FindReferencesAsync(libFile + ":4:23", null, false, null, null, 50, CancellationToken.None);

        // Assert
        refs.Should().Contain("Live Demo: 3 references");
        workspace.Dispose();
    }

    [Fact]
    public async Task DependencyListedFirst_EvenWithVersionSkew_FindsEveryUsage_Correct()
    {
        // Arrange: same skew, but Lib comes first, so the name resolves in its own compilation
        (AdhocWorkspace workspace, LoadedSolution loaded) = Build(appFirst: false, versionSkew: true);
        using Server server = Server.Create();
        server.Workspaces.Adopt(loaded);

        // Act
        ResolvedSymbol? resolved = await SymbolResolver.ResolveInSolutionAsync(Spec, loaded, CancellationToken.None);
        string refs = await server.Query.FindReferencesAsync(Spec, null, false, null, null, 50, CancellationToken.None);
        AssertNoCompileErrors(loaded.Solution);

        // Assert
        resolved!.Project.Name.Should().Be("Lib");
        refs.Should().Contain("Live Demo: 3 references");
        workspace.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DependentProjectListedFirst_WithoutVersionSkew_FindsEveryUsage_Correct(bool warmCompilationsFirst)
    {
        // Arrange: App first, no skew; cold or warm compilations
        (AdhocWorkspace workspace, LoadedSolution loaded) = Build(appFirst: true, versionSkew: false);
        using Server server = Server.Create();
        server.Workspaces.Adopt(loaded);
        if (warmCompilationsFirst)
        {
            AssertNoCompileErrors(loaded.Solution);
        }

        // Act
        string refs = await server.Query.FindReferencesAsync(Spec, null, false, null, null, 50, CancellationToken.None);
        ResolvedSymbol? resolved = await SymbolResolver.ResolveInSolutionAsync(Spec, loaded, CancellationToken.None);
        AssertNoCompileErrors(loaded.Solution);

        // Assert: the name resolves in the declaring project, and the order alone does not produce the zero
        resolved!.Project.Name.Should().Be("Lib");
        refs.Should().Contain("Live Demo: 3 references");
        workspace.Dispose();
    }

    // ------------------------------------------------------------------ the other hypotheses

    [Fact]
    public async Task H1_ALiveTextChangeBetweenResolveAndSearch_DoesNotLoseTheUsages_Correct()
    {
        // Arrange: the declaring project first, so the name resolution itself is fine
        (AdhocWorkspace workspace, LoadedSolution loaded) = Build(appFirst: false, versionSkew: false);
        using Server server = Server.Create();
        server.Workspaces.Adopt(loaded);
        string appFile = loaded.Solution.Projects.Single(p => p.Name == "App").Documents.Single().FilePath!;
        string libFile = loaded.Solution.Projects.Single(p => p.Name == "Lib").Documents.Single().FilePath!;

        // Act: the snapshot is replaced as a file-change refresh does, in both projects, then queried
        loaded.ApplyFileChange(appFile, Microsoft.CodeAnalysis.Text.SourceText.From(AppText + "// edited\n"));
        loaded.ApplyFileChange(libFile, Microsoft.CodeAnalysis.Text.SourceText.From(LibText + "// edited\n"));
        string refs = await server.Query.FindReferencesAsync(Spec, null, false, null, null, 50, CancellationToken.None);

        // Assert
        refs.Should().Contain("Live Demo: 3 references");
        workspace.Dispose();
    }

    [Fact]
    public async Task H2_ASolutionPathWhoseCaseDiffersFromTheDisk_DoesNotLoseTheUsages_Correct()
    {
        // Arrange: registered as C:\...\DEMO\Demo.slnx while the documents live under the real casing
        (AdhocWorkspace workspace, LoadedSolution loaded) = Build(appFirst: false, versionSkew: false, upperCaseSolutionFolder: true);
        using Server server = Server.Create();
        server.Workspaces.Adopt(loaded);

        // Act
        string refs = await server.Query.FindReferencesAsync(Spec, null, false, null, null, 50, CancellationToken.None);

        // Assert
        refs.Should().Contain("Live Demo: 3 references");
        workspace.Dispose();
    }

    [Fact]
    public async Task H3_TwoLoadedSolutionsWithTheSameName_EachAnswersForItself_Correct()
    {
        // Arrange: two copies of the same layout (an old clone), same solution name, different folders
        (AdhocWorkspace w1, LoadedSolution a) = Build(appFirst: false, versionSkew: false);
        (AdhocWorkspace w2, LoadedSolution b) = Build(appFirst: false, versionSkew: false);
        using Server server = Server.Create();
        server.Workspaces.Adopt(a);
        server.Workspaces.Adopt(b);
        a.Name.Should().Be(b.Name);

        // Act: defining solution only; then every solution
        string one = await server.Query.FindReferencesAsync(Spec, null, false, null, null, 50, CancellationToken.None);
        string all = await server.Query.FindReferencesAsync(Spec, null, true, null, null, 50, CancellationToken.None);

        // Assert: the search stays inside the solution that resolved the symbol, and each solution answers for itself
        one.Should().Contain("Live Demo: 3 references");
        all.Split("Live Demo: 3 references").Length.Should().Be(3, "one block per loaded solution");
        w1.Dispose();
        w2.Dispose();
    }

    // ------------------------------------------------------------------ helpers

    private static ISymbol OwnSymbol(Solution solution)
    {
        return solution.Projects.Single(p => p.Name == "Lib").GetCompilationAsync().GetAwaiter().GetResult()!
            .GetTypeByMetadataName("Acme.Lib.Pricing")!.GetMembers("Round").Single();
    }

    private static string? FindRepositorySolution()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            string candidate = Path.Combine(dir, "Balalaika.slnx");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void AssertNoCompileErrors(Solution solution)
    {
        foreach (Project project in solution.Projects)
        {
            project.GetCompilationAsync().GetAwaiter().GetResult()!.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty("the fixture compiles: a 0 must not come from a broken fixture ({0})", project.Name);
        }
    }

    private static async Task<int> CountBindingLocationsAsync(ISymbol symbol, Solution solution)
    {
        int count = 0;
        foreach (ReferencedSymbol group in await SymbolFinder.FindReferencesAsync(symbol, solution, CancellationToken.None))
        {
            if (QueryService.BindsToDeclaration(group, symbol))
            {
                count += group.Locations.Count(l => !l.IsImplicit && l.Location.IsInSource);
            }
        }

        return count;
    }

    private static string WriteOnDisk()
    {
        string root = Path.GetFullPath(TestWorkspace.NewTempDir());
        Directory.CreateDirectory(Path.Combine(root, "Lib"));
        Directory.CreateDirectory(Path.Combine(root, "App"));
        File.WriteAllText(Path.Combine(root, "Lib", "Lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(root, "Lib", "Pricing.cs"), LibText);
        File.WriteAllText(Path.Combine(root, "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../Lib/Lib.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(root, "App", "Use.cs"), AppText);
        string sln = Path.Combine(root, "Demo.slnx");
        File.WriteAllText(sln, "<Solution><Project Path=\"App/App.csproj\" /><Project Path=\"Lib/Lib.csproj\" /></Solution>");
        return sln;
    }

    /// <summary>An assembly <c>Acme.Dep</c> with one public type, in the given version, as a metadata reference.</summary>
    private static MetadataReference DepAssembly(string version)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Acme.Dep",
            [CSharpSyntaxTree.ParseText("[assembly: System.Reflection.AssemblyVersion(\"" + version + "\")]\nnamespace Acme.Dep { public class Marker { } }")],
            TestWorkspace.DefaultReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using MemoryStream stream = new();
        compilation.Emit(stream).Success.Should().BeTrue();
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    /// <summary>
    /// Two projects, App referencing Lib; <paramref name="appFirst"/> puts App first in <c>Solution.Projects</c>, as a solution
    /// file that lists it first does. With <paramref name="versionSkew"/> Lib binds <c>Acme.Dep</c> 1.0 and App binds 2.0.
    /// </summary>
    private static (AdhocWorkspace Workspace, LoadedSolution Loaded) Build(bool appFirst, bool versionSkew, bool upperCaseSolutionFolder = false)
    {
        string root = Path.GetFullPath(TestWorkspace.NewTempDir());
        string libText = versionSkew ? LibWithDepText : LibText;
        List<MetadataReference> appRefs = [.. TestWorkspace.DefaultReferences];
        List<MetadataReference> libRefs = [.. TestWorkspace.DefaultReferences];
        if (versionSkew)
        {
            libRefs.Add(DepAssembly("1.0.0.0"));
            appRefs.Add(DepAssembly("2.0.0.0"));
        }

        AdhocWorkspace workspace = new();
        ProjectId? libId;
        ProjectId? appId;
        if (appFirst)
        {
            appId = AddProject(workspace, Path.Combine(root, "App"), "App", appRefs, ("Use.cs", AppText));
            libId = AddProject(workspace, Path.Combine(root, "Lib"), "Lib", libRefs, ("Pricing.cs", libText));
            workspace.TryApplyChanges(workspace.CurrentSolution.AddProjectReference(appId, new ProjectReference(libId))).Should().BeTrue();
        }
        else
        {
            libId = AddProject(workspace, Path.Combine(root, "Lib"), "Lib", libRefs, ("Pricing.cs", libText));
            appId = AddProject(workspace, Path.Combine(root, "App"), "App", appRefs, ("Use.cs", AppText));
            workspace.TryApplyChanges(workspace.CurrentSolution.AddProjectReference(appId, new ProjectReference(libId))).Should().BeTrue();
        }

        string solutionPath = Path.Combine(upperCaseSolutionFolder ? root.ToUpperInvariant() : root, "Demo.slnx");
        return (workspace, new LoadedSolution(workspace, workspace.CurrentSolution, solutionPath, []));
    }

    private static ProjectId AddProject(AdhocWorkspace workspace, string dir, string name, IReadOnlyList<MetadataReference> references, (string FileName, string Text) document)
    {
        ProjectId id = ProjectId.CreateNewId();
        workspace.AddProject(ProjectInfo.Create(
            id,
            VersionStamp.Create(),
            name,
            name,
            LanguageNames.CSharp,
            filePath: Path.Combine(dir, name + ".csproj"),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
            metadataReferences: references));
        string path = Path.Combine(dir, document.FileName);
        workspace.AddDocument(DocumentInfo.Create(
            DocumentId.CreateNewId(id),
            document.FileName,
            loader: TextLoader.From(TextAndVersion.Create(Microsoft.CodeAnalysis.Text.SourceText.From(document.Text), VersionStamp.Create(), path)),
            filePath: path));
        return id;
    }

    /// <summary>The workspace manager, resolver and query service, wired the way the server does.</summary>
    private sealed class Server : IDisposable
    {
        private readonly IndexStore _index;

        private Server(IndexStore index, WorkspaceManager workspaces, QueryService query)
        {
            this._index = index;
            this.Workspaces = workspaces;
            this.Query = query;
        }

        public WorkspaceManager Workspaces { get; }

        public QueryService Query { get; }

        public static Server Create()
        {
            IOptions<BalalaikaOptions> options = Options.Create(new BalalaikaOptions());
            IndexStore index = new(Path.Combine(Path.GetFullPath(TestWorkspace.NewTempDir()), "index.db"));
            PendingRefreshes pending = new();
            WorkspaceManager workspaces = new(index, pending, options, NullLogger<WorkspaceManager>.Instance);
            QueryService query = new(
                workspaces,
                new SymbolResolver(workspaces, index, options),
                index,
                new IndexJobQueue(),
                new ResultPager(),
                new WarmUpTracker(),
                pending,
                options,
                new SourceFilter([]),
                new GitRepository(options, NullLogger<GitRepository>.Instance),
                NullLogger<QueryService>.Instance);
            return new Server(index, workspaces, query);
        }

        public void Dispose()
        {
            this.Workspaces.Dispose();
            this._index.Dispose();
        }
    }
}
