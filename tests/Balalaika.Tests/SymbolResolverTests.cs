using FluentAssertions;
using Microsoft.CodeAnalysis;
using Balalaika.Workspace;

namespace Balalaika.Tests;

public sealed class SymbolResolverTests
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
                public int Count { get; set; }
            }

            public class Outer
            {
                public class Inner { }
            }

            public class Box<T>
            {
                public T Value { get; set; } = default!;
            }
        }
        """;

    private const string PositionSource = """
        using System.Collections.Generic;

        namespace Demo
        {
            public class Widget
            {
                public List<int> Items { get; } = new List<int>();

                public void Add(int value)
                {
                    var count = Items.Count;
                    Items.Add(value + count);
                }
            }
        }
        """;

    private readonly Compilation _compilation;
    private readonly Document _positionDocument;

    public SymbolResolverTests()
    {
        (_, Project project) = TestWorkspace.CreateProject(TestWorkspace.NewTempDir(), "Demo", ("Demo.cs", Source), ("Widget.cs", PositionSource));
        this._compilation = project.GetCompilationAsync().GetAwaiter().GetResult()!;
        this._positionDocument = project.Documents.Single(d => d.Name == "Widget.cs");
    }

    [Theory]
    [InlineData(@"C:\dev\Foo.cs:12:5", @"C:\dev\Foo.cs", 12, 5)]
    [InlineData(@"C:\dev\Foo.cs:12", @"C:\dev\Foo.cs", 12, null)]
    [InlineData("/home/x/Foo.CS:3:1", "/home/x/Foo.CS", 3, null)]
    public void TryParsePosition_AcceptsPathLineAndOptionalColumn(string spec, string expectedPath, int expectedLine, int? expectedColumn)
    {
        // Act
        bool ok = SymbolResolver.TryParsePosition(spec, out string path, out int line, out int? column);

        // Assert
        ok.Should().BeTrue();
        path.Should().Be(expectedPath);
        line.Should().Be(expectedLine);
        if (expectedColumn is not null)
        {
            column.Should().Be(expectedColumn);
        }
    }

    [Theory]
    [InlineData("Demo.Foo")]
    [InlineData("T:Demo.Foo")]
    [InlineData(@"C:\dev\Foo.cs")]
    [InlineData(@"C:\dev\Foo.cs:0")]
    [InlineData("Foo.cs:12:0")]
    public void TryParsePosition_RejectsOtherSpecs(string spec)
    {
        // Act
        bool ok = SymbolResolver.TryParsePosition(spec, out _, out _, out _);

        // Assert
        ok.Should().BeFalse();
    }

    [Theory]
    [InlineData(5, 18, "T:Demo.Widget")]                       // 'Widget' in the class declaration
    [InlineData(7, 26, "P:Demo.Widget.Items")]                 // 'Items' property declaration
    [InlineData(7, 16, "T:System.Collections.Generic.List`1")] // 'List' in the property type: constructed generic normalised
    [InlineData(9, 21, "M:Demo.Widget.Add(System.Int32)")]     // 'Add' method declaration
    [InlineData(9, 24, "M:Demo.Widget.Add(System.Int32)")]     // '(' right after the name still means the method
    [InlineData(12, 19, "M:System.Collections.Generic.List`1.Add(`0)")] // 'Add' in Items.Add(...)
    [InlineData(12, 13, "P:Demo.Widget.Items")]                // 'Items' reference
    public void ResolveAtPositionAsync_WithColumn_ReturnsSymbolUnderCursor(int line, int column, string expectedDocId)
    {
        // Act
        ISymbol? symbol = SymbolResolver.ResolveAtPositionAsync(this._positionDocument, line, column, CancellationToken.None).GetAwaiter().GetResult();

        // Assert
        symbol.Should().NotBeNull();
        symbol!.GetDocumentationCommentId().Should().Be(expectedDocId);
    }

    [Theory]
    [InlineData(5, "T:Demo.Widget")]                 // declaration line: the type wins
    [InlineData(7, "P:Demo.Widget.Items")]           // property declaration beats the List<int> reference
    [InlineData(9, "M:Demo.Widget.Add(System.Int32)")] // method beats its parameter
    [InlineData(12, "P:Demo.Widget.Items")]          // no declaration: first referenced symbol
    public void ResolveAtPositionAsync_WithoutColumn_PrefersDeclarationsThenFirstReference(int line, string expectedDocId)
    {
        // Act
        ISymbol? symbol = SymbolResolver.ResolveAtPositionAsync(this._positionDocument, line, null, CancellationToken.None).GetAwaiter().GetResult();

        // Assert
        symbol.Should().NotBeNull();
        symbol!.GetDocumentationCommentId().Should().Be(expectedDocId);
    }

    [Theory]
    [InlineData(2, 1)]   // blank line
    [InlineData(99, 1)]  // past the end
    [InlineData(5, 5)]   // 'public' keyword
    public void ResolveAtPositionAsync_WhenNothingAtPosition_ReturnsNull(int line, int column)
    {
        // Act
        ISymbol? symbol = SymbolResolver.ResolveAtPositionAsync(this._positionDocument, line, column, CancellationToken.None).GetAwaiter().GetResult();

        // Assert
        symbol.Should().BeNull();
    }

    [Theory]
    [InlineData("Demo.Foo", "T:Demo.Foo")]
    [InlineData("T:Demo.Foo", "T:Demo.Foo")]
    [InlineData("Foo", "T:Demo.Foo")]
    [InlineData("Demo.Foo.Run", "M:Demo.Foo.Run")]
    [InlineData("Demo.Foo.Run()", "M:Demo.Foo.Run")]
    [InlineData("Demo.Foo.Run(int)", "M:Demo.Foo.Run(System.Int32)")]
    [InlineData("M:Demo.Foo.Run(System.Int32)", "M:Demo.Foo.Run(System.Int32)")]
    [InlineData("Demo.Foo.Foo(int)", "M:Demo.Foo.#ctor(System.Int32)")]
    [InlineData("Demo.Foo.Count", "P:Demo.Foo.Count")]
    [InlineData("Demo.Outer.Inner", "T:Demo.Outer.Inner")]
    [InlineData("Demo.Box<T>", "T:Demo.Box`1")]
    [InlineData("Demo.Box<T>.Value", "P:Demo.Box`1.Value")]
    [InlineData("Count", "P:Demo.Foo.Count")]
    [InlineData("Console", "T:System.Console")]
    [InlineData("ObsoleteAttribute", "T:System.ObsoleteAttribute")]
    [InlineData("Obsolete", "T:System.ObsoleteAttribute")]
    public void ResolveInCompilation_AcceptsCommonSpecForms(string spec, string expectedDocId)
    {
        // Act
        ISymbol? symbol = SymbolResolver.ResolveInCompilation(spec, this._compilation);

        // Assert
        symbol.Should().NotBeNull();
        symbol!.GetDocumentationCommentId().Should().Be(expectedDocId);
    }

    [Theory]
    [InlineData("Demo.Missing")]
    [InlineData("Nope")]
    [InlineData("Demo.Foo.Missing")]
    public void ResolveInCompilation_ReturnsNullForUnknownSymbols(string spec)
    {
        // Act
        ISymbol? symbol = SymbolResolver.ResolveInCompilation(spec, this._compilation);

        // Assert
        symbol.Should().BeNull();
    }
}
