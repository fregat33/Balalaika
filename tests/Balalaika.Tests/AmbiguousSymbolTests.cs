using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Balalaika.Index;
using Balalaika.Workspace;

namespace Balalaika.Tests;

/// <summary>A member name that matches several overloads is refused with the doc id of each overload, in every symbol tool.</summary>
public sealed class AmbiguousSymbolTests : IDisposable
{
    private const string Source = """
        namespace Demo
        {
            public class Foo
            {
                public Foo() { }
                public Foo(int x) { }
                public void Run() { }
                public void Run(int times) { }
                public void Single(int a) { }
            }

            public record Rec(int A);

            public record RecExtra(int A)
            {
                public RecExtra() : this(0) { }
            }
        }
        """;

    private readonly string _dir = TestWorkspace.NewTempDir();
    private readonly Compilation _compilation;
    private readonly IndexStore _index;
    private readonly WorkspaceManager _workspaces;
    private readonly QueryService _query;

    public AmbiguousSymbolTests()
    {
        string solutionPath = Path.Combine(this._dir, "Demo.slnx");
        File.WriteAllText(solutionPath, "");
        (AdhocWorkspace workspace, Project project) = TestWorkspace.CreateProject(Path.Combine(this._dir, "Demo"), "Demo", ("Demo.cs", Source));
        this._compilation = project.GetCompilationAsync().GetAwaiter().GetResult()!;
        this._index = new IndexStore(Path.Combine(TestWorkspace.NewTempDir(), "index.db"));
        IOptions<BalalaikaOptions> options = Options.Create(new BalalaikaOptions());
        PendingRefreshes pending = new();
        this._workspaces = new WorkspaceManager(this._index, pending, options, NullLogger<WorkspaceManager>.Instance);
        this._workspaces.Adopt(new LoadedSolution(workspace, workspace.CurrentSolution, solutionPath, []));
        this._query = new QueryService(
            this._workspaces,
            new SymbolResolver(this._workspaces, this._index, options),
            this._index,
            new IndexJobQueue(),
            new ResultPager(),
            new WarmUpTracker(),
            pending,
            options,
            new SourceFilter([]),
            new GitRepository(options, NullLogger<GitRepository>.Instance),
            NullLogger<QueryService>.Instance);
    }

    public void Dispose()
    {
        this._workspaces.Dispose();
        this._index.Dispose();
    }

    private static string[] Lines(string output) => output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();

    [Fact]
    public async Task SymbolInfoAndCallGraph_ForOverloadedName_AnswerTheSameRefusal()
    {
        // Act
        string info = await this._query.GetSymbolInfoAsync("Demo.Foo.Run", null, CancellationToken.None);
        string graph = await this._query.GetCallGraphAsync("Demo.Foo.Run", null, "both", false, null, false, 50, CancellationToken.None);

        // Assert
        string[] expected =
        [
            "Ambiguous symbol: Demo.Foo.Run matches 2 overloads; pass the doc id or the parameter types:",
            "  M:Demo.Foo.Run",
            "  M:Demo.Foo.Run(System.Int32)",
        ];
        Lines(info).Should().Equal(expected);
        Lines(graph).Should().Equal(expected);
    }

    [Fact]
    public async Task ParameterTextMatchingNoOverload_AnswersTheRefusal()
    {
        // Act
        string output = await this._query.GetSymbolInfoAsync("Demo.Foo.Run(string)", null, CancellationToken.None);

        // Assert
        Lines(output)[0].Should().Be("Ambiguous symbol: Demo.Foo.Run(string) matches 2 overloads; pass the doc id or the parameter types:");
    }

    [Theory]
    [InlineData("Demo.Foo.Run()", "M:Demo.Foo.Run")]
    [InlineData("Demo.Foo.Run(int)", "M:Demo.Foo.Run(System.Int32)")]
    [InlineData("Demo.Foo.Foo(int)", "M:Demo.Foo.#ctor(System.Int32)")]
    [InlineData("M:Demo.Foo.Run(System.Int32)", "M:Demo.Foo.Run(System.Int32)")]
    public void ParameterTypesOrDocId_StillResolveToOneSymbol(string spec, string expectedDocId)
    {
        // Act
        ISymbol? symbol = SymbolResolver.ResolveInCompilation(spec, this._compilation, out IReadOnlyList<ISymbol>? ambiguous);

        // Assert
        ambiguous.Should().BeNull();
        symbol.Should().NotBeNull();
        symbol!.GetDocumentationCommentId().Should().Be(expectedDocId);
    }

    [Theory]
    [InlineData("Demo.Foo.Run")]
    [InlineData("Demo.Foo.Run(string)")]
    [InlineData("Demo.Foo.Foo")]
    public void OverloadedName_WithoutMatchingParameters_ReportsEveryOverload(string spec)
    {
        // Act
        ISymbol? symbol = SymbolResolver.ResolveInCompilation(spec, this._compilation, out IReadOnlyList<ISymbol>? ambiguous);

        // Assert
        symbol.Should().BeNull();
        ambiguous.Should().HaveCount(2);
    }

    [Fact]
    public void OverloadedConstructors_NamedWithoutParameterTypes_ReportBothInDeclarationOrder()
    {
        // Act
        SymbolResolver.ResolveInCompilation("Demo.Foo.Foo", this._compilation, out IReadOnlyList<ISymbol>? ambiguous);

        // Assert
        ambiguous!.Select(s => s.GetDocumentationCommentId()).Should().Equal("M:Demo.Foo.#ctor", "M:Demo.Foo.#ctor(System.Int32)");
    }

    [Fact]
    public void RecordWithPrimaryConstructorOnly_ResolvesToThePrimaryConstructor()
    {
        // Act
        ISymbol? symbol = SymbolResolver.ResolveInCompilation("Demo.Rec.Rec", this._compilation, out IReadOnlyList<ISymbol>? ambiguous);

        // Assert
        ambiguous.Should().BeNull();
        symbol!.GetDocumentationCommentId().Should().Be("M:Demo.Rec.#ctor(System.Int32)");
    }

    [Fact]
    public void RecordWithPrimaryAndExtraConstructor_ReportsOnlyThoseTwo()
    {
        // Act
        ISymbol? symbol = SymbolResolver.ResolveInCompilation("Demo.RecExtra.RecExtra", this._compilation, out IReadOnlyList<ISymbol>? ambiguous);

        // Assert
        symbol.Should().BeNull();
        ambiguous!.Select(s => s.GetDocumentationCommentId()).Should().BeEquivalentTo("M:Demo.RecExtra.#ctor(System.Int32)", "M:Demo.RecExtra.#ctor");
    }

    [Fact]
    public void SingleMember_WithNonMatchingParameterText_StillResolves()
    {
        // Act
        ISymbol? symbol = SymbolResolver.ResolveInCompilation("Demo.Foo.Single(string)", this._compilation, out IReadOnlyList<ISymbol>? ambiguous);

        // Assert
        ambiguous.Should().BeNull();
        symbol!.GetDocumentationCommentId().Should().Be("M:Demo.Foo.Single(System.Int32)");
    }
}
