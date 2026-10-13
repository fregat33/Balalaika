using FluentAssertions;
using Microsoft.CodeAnalysis;
using Balalaika.Workspace;

namespace Balalaika.Tests;

public sealed class CallGraphTests
{
    private const string Source = """
        using System;

        namespace Demo
        {
            public class Service
            {
                private readonly Helper _helper = new Helper();

                public void Run()
                {
                    _helper.Prepare();
                    Helper.Log("run");
                    Console.WriteLine("done");
                    var other = new Helper();
                }

                public void RunTwice()
                {
                    Run();
                    Run();
                }
            }

            public class Helper
            {
                public void Prepare() { }
                public static void Log(string message) { }
            }

            public class Caller
            {
                public void Go(Service s) => s.Run();
            }
        }
        """;

    private readonly Project _project;
    private readonly Compilation _compilation;

    public CallGraphTests()
    {
        (_, this._project) = TestWorkspace.CreateProject(TestWorkspace.NewTempDir(), "Demo", ("Demo.cs", Source));
        this._compilation = this._project.GetCompilationAsync().GetAwaiter().GetResult()!;
    }

    [Fact]
    public async Task FindCallersAsync_ListsEveryCallSite()
    {
        // Arrange
        ISymbol run = SymbolResolver.ResolveInCompilation("Demo.Service.Run", this._compilation)!;

        // Act
        IReadOnlyList<CallSite> callers = await CallGraph.FindCallersAsync(run, this._project.Solution, CancellationToken.None);

        // Assert
        callers.Should().HaveCount(3);
        callers.Select(c => c.Symbol.ToDisplayString(DisplayFormats.FullyQualifiedWithParameters)).Distinct()
            .Should().BeEquivalentTo("Demo.Service.RunTwice()", "Demo.Caller.Go(Demo.Service)");
        callers.Select(c => c.Location.GetLineSpan().StartLinePosition.Line + 1).Should().BeEquivalentTo([19, 20, 32]);
    }

    [Fact]
    public async Task FindCalleesAsync_WithoutExternal_ListsSourceMethodsAndConstructorsOnce()
    {
        // Arrange
        ISymbol run = SymbolResolver.ResolveInCompilation("Demo.Service.Run", this._compilation)!;

        // Act
        IReadOnlyList<CallSite> callees = await CallGraph.FindCalleesAsync(run, this._project.Solution, includeExternal: false, CancellationToken.None);

        // Assert
        callees.Select(c => c.Symbol.GetDocumentationCommentId())
            .Should().BeEquivalentTo("M:Demo.Helper.Prepare", "M:Demo.Helper.Log(System.String)", "M:Demo.Helper.#ctor");
    }

    [Fact]
    public async Task FindCalleesAsync_WithExternal_IncludesFrameworkCalls()
    {
        // Arrange
        ISymbol run = SymbolResolver.ResolveInCompilation("Demo.Service.Run", this._compilation)!;

        // Act
        IReadOnlyList<CallSite> callees = await CallGraph.FindCalleesAsync(run, this._project.Solution, includeExternal: true, CancellationToken.None);

        // Assert
        callees.Select(c => c.Symbol.GetDocumentationCommentId()).Should().Contain("M:System.Console.WriteLine(System.String)");
    }

    [Fact]
    public async Task CallSites_CarryTheDocumentOfTheOtherEnd_NullForMetadata()
    {
        // Arrange
        ISymbol run = SymbolResolver.ResolveInCompilation("Demo.Service.Run", this._compilation)!;

        // Act
        IReadOnlyList<CallSite> callers = await CallGraph.FindCallersAsync(run, this._project.Solution, CancellationToken.None);
        IReadOnlyList<CallSite> callees = await CallGraph.FindCalleesAsync(run, this._project.Solution, includeExternal: true, CancellationToken.None);

        // Assert
        callers.Should().OnlyContain(c => c.Document != null && c.Document.Project.Name == "Demo");
        callees.Where(c => c.Symbol.Locations.Any(l => l.IsInSource)).Should().OnlyContain(c => c.Document != null && c.Document.Project.Name == "Demo");
        callees.Single(c => c.Symbol.GetDocumentationCommentId() == "M:System.Console.WriteLine(System.String)").Document.Should().BeNull();
    }
}
