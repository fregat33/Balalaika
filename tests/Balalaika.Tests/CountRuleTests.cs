using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Balalaika.Index;
using Balalaika.Workspace;

namespace Balalaika.Tests;

public sealed class CountRuleTests
{
    private const string Source = """
        interface IShape { double Area(); }
        abstract class Base : IShape { public abstract double Area(); public string Describe() => Area().ToString(); }
        class Circle : Base { public override double Area() => 1; }
        class Square : Base { public override double Area() => 4; }
        class Use
        {
            double ViaInterface(IShape s) => s.Area();
            double Direct(Circle circle) => circle.Area();
        }
        """;

    private readonly Project _project;
    private readonly Compilation _compilation;

    public CountRuleTests()
    {
        (_, this._project) = TestWorkspace.CreateProject(TestWorkspace.NewTempDir(), "Demo", ("Demo.cs", Source));
        this._compilation = this._project.GetCompilationAsync().GetAwaiter().GetResult()!;
    }

    [Theory]
    [InlineData("IShape.Area", 1)]
    [InlineData("Base.Area", 1)]
    [InlineData("Circle.Area", 1)]
    [InlineData("Square.Area", 0)]
    public async Task References_CountOnlyUsagesThatBindToTheDeclaration(string spec, int expected)
    {
        // Arrange
        ISymbol symbol = SymbolResolver.ResolveInCompilation(spec, this._compilation)!;

        // Act
        IEnumerable<ReferencedSymbol> groups = await SymbolFinder.FindReferencesAsync(symbol, this._project.Solution, CancellationToken.None);
        int count = groups.Where(g => QueryService.BindsToDeclaration(g, symbol)).Sum(g => g.Locations.Count(l => !l.IsImplicit && l.Location.IsInSource));

        // Assert
        count.Should().Be(expected);
    }

    [Fact]
    public async Task FindCallersAsync_TagsDispatchThroughBaseAndInterfaceMembers()
    {
        // Arrange
        ISymbol circleArea = SymbolResolver.ResolveInCompilation("Circle.Area", this._compilation)!;

        // Act
        IReadOnlyList<CallSite> callers = await CallGraph.FindCallersAsync(circleArea, this._project.Solution, CancellationToken.None);

        // Assert
        callers.Should().HaveCount(3);
        callers.Where(c => c.IsDirect).Select(c => c.Symbol.Name).Should().BeEquivalentTo("Direct");
        callers.Single(c => c.Symbol.Name == "Direct").ViaMember.Should().BeNull();
        callers.Single(c => c.Symbol.Name == "Describe").ViaMember!.ToDisplayString(DisplayFormats.FullyQualifiedWithParameters).Should().Be("Base.Area()");
        callers.Single(c => c.Symbol.Name == "ViaInterface").ViaMember!.ToDisplayString(DisplayFormats.FullyQualifiedWithParameters).Should().Be("IShape.Area()");
    }

    [Fact]
    public async Task FormatCallers_CountsViaRowsApartAndTagsThem()
    {
        // Arrange
        ISymbol circleArea = SymbolResolver.ResolveInCompilation("Circle.Area", this._compilation)!;
        IReadOnlyList<CallSite> callers = await CallGraph.FindCallersAsync(circleArea, this._project.Solution, CancellationToken.None);
        List<(CallSite, string)> sites = callers.Select(c => (c, "Demo")).ToList();

        // Act
        (string header, List<string> lines) = QueryService.FormatCallers(sites, ReferenceFilter.None, directOnly: false);
        (string directHeader, List<string> directLines) = QueryService.FormatCallers(sites, ReferenceFilter.None, directOnly: true);

        // Assert
        header.Should().Be("Callers (3: 1 direct, 2 via base or interface members, live, loaded solutions only):");
        lines.Should().HaveCount(3);
        lines.Count(l => l.EndsWith("  via IShape.Area()")).Should().Be(1);
        lines.Count(l => l.EndsWith("  via Base.Area()")).Should().Be(1);
        directHeader.Should().Be("Callers (1, live, loaded solutions only):");
        directLines.Should().ContainSingle().Which.Should().NotContain(" via ");
    }
}
