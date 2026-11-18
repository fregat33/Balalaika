using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Balalaika.Index;
using Balalaika.Workspace;

namespace Balalaika.Tests;

/// <summary>
/// Pins what <c>find_references</c> answers today for the shapes that researchers reported as 0 although the symbol is
/// used. Each shape lives in a declaring project (one usage) and a second project that references it (the other
/// usages). The live path is <see cref="QueryService.FindReferencesAsync"/> over an adopted in-memory solution; the
/// index path is the same call with <c>allSolutions</c>, over rows that <see cref="DocumentExtractor"/> wrote for a
/// second, not loaded solution path. Names ending in <c>_Defect</c> pin a count below the real number of usages (the
/// comment above the assert gives the real number); <c>_ByDesign</c> pins the Task 17 rule; <c>_Correct</c> pins a
/// count equal to the real number of usages.
/// </summary>
public sealed class ReferenceZeroTests : IDisposable
{
    private const string CoreSource = """
        namespace Acme.Core;

        public class PriceCalculator { public decimal Total(decimal amount) => amount; }

        public sealed class Checkout
        {
            public Checkout(PriceCalculator calculator) { }
        }

        public static class Registry
        {
            public static void Register<T>() where T : class { }
        }

        public record PlaceOrder(int OrderId);

        public static class Bus
        {
            public static void Dispatch<T>(T command) { }
        }

        public record Receipt(int Id);

        public static class Printer
        {
            public static void Print(Receipt receipt) { }
        }

        public record OrderInfo(int Id);

        public record ShipmentInfo(int Id);

        public static class OrderMappings
        {
            public static string ToView(this ShipmentInfo info) => "";

            public static string ToView(this OrderInfo info) => "";
        }

        public interface IPricing
        {
            decimal Quote(int units);
        }

        public class Pricing : IPricing
        {
            public decimal Quote(int units) => units;
        }

        public record Box<T>(T Value);

        public class Animal { public Animal(string name) { } }

        public class Dog : Animal { public Dog() : base("dog") { } }

        public class Bag { public System.Collections.Generic.IEnumerator<int> GetEnumerator() => throw new System.NotImplementedException(); }

        public static class LoopUsage
        {
            public static void Run(Bag bag)
            {
                foreach (int item in bag) { }
            }
        }

        public static class CoreUsage
        {
            public static void Run(IPricing pricing, OrderInfo info)
            {
                Box<int> box = new Box<int>(1);
                Bus.Dispatch(new PlaceOrder(1));
                Printer.Print(new(1));
                string dto = info.ToView();
                decimal quote = pricing.Quote(1);
            }
        }
        """;

    private const string AppSource = """
        using Acme.Core;

        namespace Acme.App;

        public sealed class Handler
        {
            public Handler(PriceCalculator calculator) { }
        }

        public static class AppUsage
        {
            public static void Run(IPricing pricing, OrderInfo info)
            {
                Box<int> box = new Box<int>(2);
                Registry.Register<PriceCalculator>();
                Bus.Dispatch(new PlaceOrder(2));
                Printer.Print(new(2));
                string reduced = info.ToView();
                string asStatic = OrderMappings.ToView(info);
                decimal quote = pricing.Quote(2);
            }
        }
        """;

    private const string BrokenAppSource = """
        using Acme.Core;

        namespace Acme.App;

        public static class AppUsage
        {
            public static void Run(IPricing pricing)
            {
                decimal quote = pricing.Quote(2);
            }
        }
        """;

    private static readonly Regex LiveCount = new(@"^Live \S+: (\d+) references", RegexOptions.Multiline);
    private static readonly Regex IndexCount = new(@"^Index \S+: (\d+) references", RegexOptions.Multiline);

    private readonly string _dir = TestWorkspace.NewTempDir();
    private readonly IndexStore _index;
    private readonly WorkspaceManager _workspaces;
    private readonly QueryService _query;
    private readonly Compilation[] _compilations;

    public ReferenceZeroTests()
    {
        string solutionPath = Path.Combine(this._dir, "Acme.slnx");
        File.WriteAllText(solutionPath, "");
        (AdhocWorkspace workspace, Project core) = TestWorkspace.CreateProject(Path.Combine(this._dir, "Core"), "Acme.Core", ("Core.cs", CoreSource));
        Project app = TestWorkspace.AddProject(workspace, Path.Combine(this._dir, "App"), "Acme.App", [core.Id], ("App.cs", AppSource));
        this._compilations = [core.Solution.GetProject(core.Id)!.GetCompilationAsync().GetAwaiter().GetResult()!, app.GetCompilationAsync().GetAwaiter().GetResult()!];
        (this._index, this._workspaces, this._query) = this.Build(workspace, solutionPath, indexAlso: true);
    }

    public void Dispose()
    {
        this._workspaces.Dispose();
        this._index.Dispose();
    }

    private (IndexStore, WorkspaceManager, QueryService) Build(AdhocWorkspace workspace, string solutionPath, bool indexAlso)
    {
        IndexStore index = new(Path.Combine(TestWorkspace.NewTempDir(), "index.db"));
        IOptions<BalalaikaOptions> options = Options.Create(new BalalaikaOptions());
        PendingRefreshes pending = new();
        WorkspaceManager workspaces = new(index, pending, options, NullLogger<WorkspaceManager>.Instance);
        workspaces.Adopt(new LoadedSolution(workspace, workspace.CurrentSolution, solutionPath, []));
        if (indexAlso)
        {
            // The index half: every document of the solution, extracted, under a second solution path that is not loaded.
            string indexedSolution = Path.Combine(this._dir, "AcmeIndexed.slnx");
            foreach (Project project in workspace.CurrentSolution.Projects)
            {
                int projectId = index.EnsureProject(project.FilePath!, project.Name, project.AssemblyName, indexedSolution);
                foreach (Document document in project.Documents)
                {
                    DocumentExtraction extraction = DocumentExtractor.ExtractAsync(document, CancellationToken.None).GetAwaiter().GetResult();
                    string hash = ContentHasher.HashText(document.GetTextAsync().GetAwaiter().GetResult().ToString());
                    index.WriteBlob(projectId, hash, extraction);
                    index.SetTreeEntry(projectId, document.FilePath!, hash, 1, 1);
                }
            }
        }

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
        return (index, workspaces, query);
    }

    // Index is null when the output has no "Index ..." line: the code prints that line only for a solution that has at least one index hit.
    private sealed record Measured(int Live, int? Index, string Output);

    private static async Task<Measured> MeasureAsync(QueryService query, string docId)
    {
        string output = await query.FindReferencesAsync(docId, null, true, null, null, 200, CancellationToken.None);
        Match live = LiveCount.Match(output);
        live.Success.Should().BeTrue("the symbol must resolve in the loaded fixture, so a 0 is a real answer:\n" + output);
        Match index = IndexCount.Match(output);
        return new Measured(int.Parse(live.Groups[1].Value), index.Success ? int.Parse(index.Groups[1].Value) : null, output);
    }

    [Fact]
    public void Fixture_CompilesWithoutErrors_Correct()
    {
        // Assert
        foreach (Compilation compilation in this._compilations)
        {
            compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        }
    }

    // ------------------------------------------------------------------ shape 1: class as constructor parameter and generic type argument

    [Fact]
    public async Task ClassUsedAsConstructorParameterAndGenericArgument_Correct()
    {
        // Act
        Measured m = await MeasureAsync(this._query, "T:Acme.Core.PriceCalculator");

        // Assert: real usages 3 (Checkout parameter, Handler parameter, Register<PriceCalculator>)
        m.Live.Should().Be(3, m.Output);
        m.Index.Should().Be(3, m.Output);
    }

    // ------------------------------------------------------------------ shape 2: record created with new Name(...), passed to a generic Dispatch

    [Fact]
    public async Task RecordCreatedWithNew_TypeSymbol_Correct()
    {
        // Act
        Measured m = await MeasureAsync(this._query, "T:Acme.Core.PlaceOrder");

        // Assert: real usages 2 (the type name in each of the two `new PlaceOrder(...)`); SymbolFinder files those names
        // under the constructor group, and CountsAsReference takes them back for a type search.
        m.Live.Should().Be(2, m.Output);
        m.Index.Should().Be(2, m.Output);
    }

    [Fact]
    public async Task RecordCreatedWithNew_TypeSymbol_PrintsKindCreation_Correct()
    {
        // Act
        Measured m = await MeasureAsync(this._query, "T:Acme.Core.PlaceOrder");

        // Assert
        string[] hits = m.Output.Split('\n').Where(l => Regex.IsMatch(l, @"^\s+\S+\.cs:\d+:\d+\s")).ToArray();
        hits.Should().NotBeEmpty(m.Output);
        hits.Should().OnlyContain(l => l.Contains(" creation"), m.Output);
    }

    [Fact]
    public async Task GenericRecordCreatedWithNew_TypeSymbol_LiveEqualsIndex_Correct()
    {
        // Act
        Measured m = await MeasureAsync(this._query, "T:Acme.Core.Box`1");

        // Assert: real usages 4 (the type name in each `new Box<int>(...)` plus the declared type of each local)
        m.Index.Should().NotBeNull(m.Output);
        m.Live.Should().Be(m.Index!.Value, m.Output);
        m.Live.Should().BeGreaterThanOrEqualTo(2, m.Output);
    }

    [Fact]
    public async Task ClassConstructorCalledOnlyThroughBaseInitializer_TypeSymbol_LiveEqualsIndex_Correct()
    {
        // Act
        Measured m = await MeasureAsync(this._query, "T:Acme.Core.Animal");

        // Assert: the `: base("dog")` initializer is not a creation of the type, in neither path
        m.Index.Should().NotBeNull(m.Output);
        m.Live.Should().Be(m.Index!.Value, m.Output);
        m.Output.Should().NotContain(" creation", "no `new Animal(...)` exists");
    }

    [Fact]
    public async Task MethodUsedOnlyByForeach_GetEnumerator_StaysZero_Correct()
    {
        // Act
        Measured m = await MeasureAsync(this._query, "M:Acme.Core.Bag.GetEnumerator");

        // Assert: SymbolFinder returns the foreach as an implicit location; the index has no row for it
        m.Live.Should().Be(0, m.Output);
    }

    [Fact]
    public async Task RecordCreatedWithNew_SymbolFinderFilesTheTypeNamesUnderTheConstructorGroup_Correct()
    {
        // Arrange
        Project core = this._workspaces.Loaded.Single().Solution.Projects.Single(p => p.Name == "Acme.Core");
        Compilation compilation = (await core.GetCompilationAsync())!;
        INamedTypeSymbol type = compilation.GetTypeByMetadataName("Acme.Core.PlaceOrder")!;

        // Act
        IEnumerable<ReferencedSymbol> groups = await SymbolFinder.FindReferencesAsync(type, this._workspaces.Loaded.Single().Solution, CancellationToken.None);
        int underType = groups.Where(g => SymbolEqualityComparer.Default.Equals(g.Definition, type)).Sum(g => g.Locations.Count());
        int underConstructor = groups.Where(g => g.Definition is IMethodSymbol { MethodKind: MethodKind.Constructor }).Sum(g => g.Locations.Count());

        // Assert
        underType.Should().Be(0);
        underConstructor.Should().Be(2);
    }

    [Fact]
    public async Task RecordCreatedWithNew_ConstructorSymbol_Correct()
    {
        // Act
        Measured m = await MeasureAsync(this._query, "M:Acme.Core.PlaceOrder.#ctor(System.Int32)");

        // Assert: real usages 2 (the two `new PlaceOrder(...)` expressions)
        m.Live.Should().Be(2, m.Output);
        m.Index.Should().Be(2, m.Output);
    }

    // ------------------------------------------------------------------ shape 3: record created only with target-typed new(...)

    [Fact]
    public async Task RecordCreatedWithTargetTypedNew_TypeSymbol_Correct()
    {
        // Act
        Measured m = await MeasureAsync(this._query, "T:Acme.Core.Receipt");

        // Assert: the only place that writes the type name is the parameter of Printer.Print
        m.Live.Should().Be(1, m.Output);
        m.Index.Should().Be(1, m.Output);
    }

    [Fact]
    public async Task RecordCreatedWithTargetTypedNew_ConstructorSymbol_Correct()
    {
        // Act
        Measured m = await MeasureAsync(this._query, "M:Acme.Core.Receipt.#ctor(System.Int32)");

        // Assert: real usages 2 (`Printer.Print(new(1))` and `Printer.Print(new(2))`); SymbolFinder flags the location
        // of a target-typed new as IsImplicit, and CountsAsReference keeps it.
        m.Live.Should().Be(2, m.Output);
        m.Index.Should().Be(2, m.Output);
    }

    [Fact]
    public async Task RecordCreatedWithTargetTypedNew_SymbolFinderReturnsBothLocationsFlaggedImplicit_Correct()
    {
        // Arrange
        LoadedSolution loaded = this._workspaces.Loaded.Single();
        Project core = loaded.Solution.Projects.Single(p => p.Name == "Acme.Core");
        INamedTypeSymbol type = (await core.GetCompilationAsync())!.GetTypeByMetadataName("Acme.Core.Receipt")!;
        IMethodSymbol constructor = type.InstanceConstructors.Single(c => c.Parameters.Length == 1 && c.Parameters[0].Type.SpecialType == SpecialType.System_Int32);

        // Act
        IEnumerable<ReferencedSymbol> groups = await SymbolFinder.FindReferencesAsync(constructor, loaded.Solution, CancellationToken.None);

        // Assert: real usages 2; SymbolFinder finds both, flagged IsImplicit, which is why CountsAsReference checks the syntax node of an implicit location
        ReferenceLocation[] locations = groups.SelectMany(g => g.Locations).ToArray();
        locations.Should().HaveCount(2);
        locations.Should().OnlyContain(l => l.IsImplicit);
    }

    // ------------------------------------------------------------------ shape 4: extension method in reduced and static form

    [Fact]
    public async Task ExtensionMethodReducedAndStaticForm_ByDocId_Correct()
    {
        // Act
        Measured m = await MeasureAsync(this._query, "M:Acme.Core.OrderMappings.ToView(Acme.Core.OrderInfo)");

        // Assert: real usages 3 (info.ToView() in Core, info.ToView() and OrderMappings.ToView(info) in App)
        m.Live.Should().Be(3, m.Output);
        m.Index.Should().Be(3, m.Output);
    }

    [Fact]
    public async Task ExtensionMethodOverloaded_ByQualifiedName_Defect()
    {
        // Act
        Measured m = await MeasureAsync(this._query, "Acme.Core.OrderMappings.ToView");

        // Assert: real usages of the OrderInfo overload 3; the name matches both overloads and the resolver takes the first
        // declared one (SymbolResolver.PickMember), the ShipmentInfo overload, which nobody calls.
        // The index has no row for that overload either, so QueryService.cs:212 prints no Index line at all.
        m.Live.Should().Be(0, m.Output);
        m.Index.Should().BeNull(m.Output);
    }

    // ------------------------------------------------------------------ shape 5: interface member and its implementation

    [Fact]
    public async Task InterfaceMemberCalledThroughInterface_Correct()
    {
        // Act
        Measured m = await MeasureAsync(this._query, "M:Acme.Core.IPricing.Quote(System.Int32)");

        // Assert: real usages 2 (pricing.Quote in Core and in App)
        m.Live.Should().Be(2, m.Output);
        m.Index.Should().Be(2, m.Output);
    }

    [Fact]
    public async Task ImplementationCalledOnlyThroughInterface_ByDesign()
    {
        // Act
        Measured m = await MeasureAsync(this._query, "M:Acme.Core.Pricing.Quote(System.Int32)");

        // Assert: Task 17 binds to the declaration; both calls bind to IPricing.Quote
        // No index row binds to the implementation, so QueryService.cs:212 prints no Index line at all.
        m.Live.Should().Be(0, m.Output);
        m.Index.Should().BeNull(m.Output);
    }

    [Fact]
    public async Task ImplementationCalledOnlyThroughInterface_PrintsNoPointerToTheFamily_ByDesign()
    {
        // Act
        Measured m = await MeasureAsync(this._query, "M:Acme.Core.Pricing.Quote(System.Int32)");

        // Assert: the answer is the bare count, with no hint to find_implementations or get_call_graph
        m.Output.Should().Contain("Live Acme: 0 references");
        m.Output.Should().NotContain("find_implementations");
        m.Output.Should().NotContain("get_call_graph");
        m.Output.Should().NotContain("interface");
    }

    // ------------------------------------------------------------------ shape 5 state: the consumer's compilation lacks the declaring project

    [Fact]
    public async Task ConsumerWithoutItsProjectReference_CountsOneOfTwoWithoutNotice_Defect()
    {
        // Arrange: the same declaring project, but the consumer has no reference to it, as when a load could not resolve it
        string dir = TestWorkspace.NewTempDir();
        (AdhocWorkspace workspace, Project core) = TestWorkspace.CreateProject(Path.Combine(dir, "Core"), "Acme.Core", ("Core.cs", CoreSource));
        Project app = TestWorkspace.AddProject(workspace, Path.Combine(dir, "App"), "Acme.App", [], ("App.cs", BrokenAppSource));
        (await app.GetCompilationAsync())!.GetDiagnostics().Should().Contain(d => d.Severity == DiagnosticSeverity.Error);
        (IndexStore index, WorkspaceManager workspaces, QueryService query) = this.Build(workspace, Path.Combine(dir, "Broken.slnx"), indexAlso: false);
        using (index)
        using (workspaces)
        {
            // Act
            Measured m = await MeasureAsync(query, "M:Acme.Core.IPricing.Quote(System.Int32)");

            // Assert: real usages 1 (pricing.Quote in the consumer) plus 1 in Core; the consumer's call does not bind, and nothing says so
            m.Live.Should().Be(1, m.Output);
            m.Output.Should().NotContain("error");
            m.Output.Should().NotContain("diagnostic");
        }
    }
}
