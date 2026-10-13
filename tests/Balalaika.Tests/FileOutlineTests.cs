using System.Text;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Balalaika.Index;
using Balalaika.Workspace;

namespace Balalaika.Tests;

public sealed class FileOutlineTests
{
    private const string Source = """
        using System;

        namespace Demo;

        /// <summary>Doc comment lines are not part of the span.</summary>
        public record UpdateOrderCommand(Guid OrderId, string Request) : IRequest<int>;

        public sealed class UpdateOrderCommandValidator
        {
            public UpdateOrderCommandValidator()
            {
                Rule = 1;
            }

            public int Rule { get; set; }
        }

        public sealed class UpdateOrderCommandHandler
        {
            private const string Route = "api/orders";
            private readonly int _count;

            [Obsolete]
            [Marker(Route)]
            public int Handle(UpdateOrderCommand request)
            {
                return _count;
            }

            [Marker("literal", 42)]
            [Marker(42)]
            private void Helper()
            {
            }

            public static class Nested
            {
                public static void Inner()
                {
                }
            }
        }

        public interface IRequest<T> { }

        public sealed class MarkerAttribute : Attribute
        {
            public MarkerAttribute(string name) { }
            public MarkerAttribute(string name, int n) { }
            public MarkerAttribute(int n) { }
        }
        """;

    private readonly Document _document;

    public FileOutlineTests()
    {
        (_, Project project) = TestWorkspace.CreateProject(TestWorkspace.NewTempDir(), "Demo", ("Orders.cs", Source));
        this._document = project.Documents.Single();
    }

    private async Task<List<OutlineEntry>> BuildAsync(int depth, bool includePrivate = true)
    {
        SyntaxNode root = (await this._document.GetSyntaxRootAsync())!;
        SemanticModel model = (await this._document.GetSemanticModelAsync())!;
        return FileOutline.Build(root, model, depth, includePrivate, CancellationToken.None);
    }

    [Fact]
    public async Task Build_ListsDeclarationsInSourceOrderWithSpansExcludingDocComments()
    {
        // Act
        List<OutlineEntry> entries = await BuildAsync(depth: 2);

        // Assert
        entries.Select(e => e.StartLine).Should().BeInAscendingOrder();
        entries[0].Should().BeEquivalentTo(new OutlineEntry(0, "record UpdateOrderCommand : IRequest<int>", 6, 6));
        entries[1].Should().BeEquivalentTo(new OutlineEntry(1, "property Guid OrderId { get; init; }", 6, 6));
        entries[2].Should().BeEquivalentTo(new OutlineEntry(1, "property string Request { get; init; }", 6, 6));
        entries[3].Should().BeEquivalentTo(new OutlineEntry(0, "class UpdateOrderCommandValidator", 8, 16));
        entries[4].Should().BeEquivalentTo(new OutlineEntry(1, "constructor UpdateOrderCommandValidator()", 10, 13));
        entries[5].Should().BeEquivalentTo(new OutlineEntry(1, "property int Rule { get; set; }", 15, 15));
        entries[6].Should().BeEquivalentTo(new OutlineEntry(0, "class UpdateOrderCommandHandler", 18, 42));
        entries.Should().Contain(e => e.Text == "private static field string Route" && e.StartLine == 20);
        entries.Should().Contain(e => e.Text == "private field int _count" && e.StartLine == 21);
        entries.Should().Contain(e => e.Text.StartsWith("method int Handle(UpdateOrderCommand request)") && e.StartLine == 23 && e.EndLine == 28);
        entries.Should().Contain(e => e.Text == "interface IRequest<T>" && e.StartLine == 44 && e.EndLine == 44);
        entries.Should().Contain(e => e.Text == "class MarkerAttribute : Attribute" && e.StartLine == 46 && e.EndLine == 51);
        entries[0].Format().Should().Be("  record UpdateOrderCommand : IRequest<int>  6");
        entries[3].Format().Should().Be("  class UpdateOrderCommandValidator  8-16");
        entries[4].Format().Should().Be("    constructor UpdateOrderCommandValidator()  10-13");
    }

    [Fact]
    public async Task Build_ShowsEveryAttributeByShortNameWithItsFirstStringArgument()
    {
        // Act
        List<OutlineEntry> entries = await BuildAsync(depth: 2);

        // Assert: Attribute suffix dropped, constant resolved, literal kept, non-string first argument dropped, several on one member all shown
        entries.Should().Contain(e => e.Text == "method int Handle(UpdateOrderCommand request)  [Obsolete] [Marker(\"api/orders\")]");
        entries.Should().Contain(e => e.Text == "private method void Helper()  [Marker(\"literal\")] [Marker]");
    }

    [Fact]
    public async Task Build_DepthCutsNestedTypesAndTheirMembers()
    {
        // Act
        List<OutlineEntry> typesOnly = await BuildAsync(depth: 1);
        List<OutlineEntry> withMembers = await BuildAsync(depth: 2);
        List<OutlineEntry> everything = await BuildAsync(depth: 3);

        // Assert
        typesOnly.Should().OnlyContain(e => e.Indent == 0);
        typesOnly.Select(e => e.Text).Should().Equal(
            "record UpdateOrderCommand : IRequest<int>",
            "class UpdateOrderCommandValidator",
            "class UpdateOrderCommandHandler",
            "interface IRequest<T>",
            "class MarkerAttribute : Attribute");
        withMembers.Should().Contain(e => e.Text == "static class Nested" && e.Indent == 1);
        withMembers.Should().NotContain(e => e.Text.Contains("Inner"));
        everything.Should().Contain(e => e.Text == "static method void Inner()" && e.Indent == 2 && e.StartLine == 38 && e.EndLine == 40);
    }

    [Fact]
    public async Task Build_WithoutPrivate_HidesPrivateMembers()
    {
        // Act
        List<OutlineEntry> entries = await BuildAsync(depth: 3, includePrivate: false);

        // Assert
        entries.Should().NotContain(e => e.Text.Contains("_count") || e.Text.Contains("Helper") || e.Text.Contains("Route"));
        entries.Should().Contain(e => e.Text.StartsWith("method int Handle"));
    }

    [Fact]
    public async Task BuildForTypes_PartialType_ListsEveryPart()
    {
        // Arrange
        (_, Project project) = TestWorkspace.CreateProject(
            TestWorkspace.NewTempDir(),
            "Partial",
            ("P1.cs", "public partial class P { public void A() { } }"),
            ("P2.cs", "public partial class P { public void B() { } }"));
        Compilation compilation = (await project.GetCompilationAsync())!;
        INamedTypeSymbol type = compilation.GetTypeByMetadataName("P")!;

        // Act
        List<string> texts = [];
        foreach (IGrouping<SyntaxTree, SyntaxReference> part in type.DeclaringSyntaxReferences.GroupBy(d => d.SyntaxTree))
        {
            SemanticModel model = compilation.GetSemanticModel(part.Key);
            texts.AddRange(FileOutline.BuildForTypes(part.Select(p => p.GetSyntax()), model, 2, true, CancellationToken.None).Select(e => e.Text));
        }

        // Assert
        texts.Should().Equal("class P", "method void A()", "class P", "method void B()");
    }

    [Fact]
    public void FromIndex_NestsByContainingDocIdAndAppliesDepthAndPrivacy()
    {
        // Arrange
        List<SymbolRecord> rows =
        [
            new("T:A", "class", "A", "A", "public class A", "public", null, 1, 14, 20),
            new("M:A.Run", "method", "Run", "A.Run", "public void Run()", "public", "T:A", 3, 17, 5),
            new("T:A.Inner", "class", "Inner", "A.Inner", "private class Inner", "private", "T:A", 7, 19, 15),
            new("M:A.Inner.X", "method", "X", "A.Inner.X", "public void X()", "public", "T:A.Inner", 9, 21, 11),
        ];

        // Act / Assert
        FileOutline.FromIndex(rows, 3, includePrivate: true).Select(e => (e.Indent, e.Text, e.StartLine, e.EndLine)).Should().Equal(
            (0, "public class A", 1, 20),
            (1, "public void Run()", 3, 5),
            (1, "private class Inner", 7, 15),
            (2, "public void X()", 9, 11));
        FileOutline.FromIndex(rows, 2, includePrivate: true).Should().HaveCount(3);
        FileOutline.FromIndex(rows, 3, includePrivate: false).Select(e => e.Text).Should().Equal("public class A", "public void Run()");
    }

    [Fact]
    public void FromIndex_WithoutEndLine_PrintsTheStartLineOnly()
    {
        // Arrange: a row from a blob written before end_line existed
        List<SymbolRecord> rows = [new("T:A", "class", "A", "A", "public class A", "public", null, 4, 14)];

        // Act / Assert
        FileOutline.FromIndex(rows, 1, true).Single().Format().Should().Be("  public class A  4");
    }

    [Fact]
    public async Task Build_OverTheLimit_EndsWithANextPageCursor()
    {
        // Arrange
        List<OutlineEntry> entries = await BuildAsync(depth: 3);
        ResultPager pager = new();
        StringBuilder sb = new();

        // Act
        int remaining = pager.AppendPage(sb, entries.Select(e => e.Format()).ToList(), 5, "get_outline Orders.cs");

        // Assert
        remaining.Should().Be(entries.Count - 5);
        sb.ToString().Should().Contain("more (next_page cursor=");
    }
}
