using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Balalaika.Index;
using Balalaika.Workspace;

namespace Balalaika.Tests;

/// <summary>The enclosing declaration recorded per reference, and the agreement between the live and the index path.</summary>
public sealed class EnclosingMemberTests : IDisposable
{
    private const string Source = """
        using System;

        namespace Demo
        {
            public interface IFoo { }

            public sealed class FooAttribute : Attribute
            {
                public FooAttribute(string name) { }
            }

            public class Foo : IFoo
            {
                public const string Name = "foo";
                private readonly Foo? _child = new Foo(null);

                public Foo(Foo? child)
                {
                    _child = child;
                }

                public Foo Prop
                {
                    get { return new Foo(null); }
                }

                public void Run()
                {
                    Foo local = new Foo(null);
                    Func<Foo> f = () => new Foo(null);
                    Foo Local() => new Foo(null);
                }

                [Foo(Foo.Name)]
                public void Attributed() { }

                public void Run(int times) { Foo again = new Foo(null); }
            }

            public class Bar : Foo
            {
                public Bar() : base(null) { }
            }
        }
        """;

    private const string TopLevel = """
        Demo.Foo x = new Demo.Foo(null);
        """;

    private readonly string _dir = TestWorkspace.NewTempDir();
    private readonly Document _document;
    private readonly Document _program;
    private readonly DocumentExtraction _extraction;
    private readonly IndexStore _store;

    public EnclosingMemberTests()
    {
        (_, Project project) = TestWorkspace.CreateProject(this._dir, "Demo", ("Demo.cs", Source), ("Program.cs", TopLevel));
        this._document = project.Documents.Single(d => d.Name == "Demo.cs");
        this._program = project.Documents.Single(d => d.Name == "Program.cs");
        this._extraction = DocumentExtractor.ExtractAsync(this._document, CancellationToken.None).GetAwaiter().GetResult();
        this._store = new IndexStore(Path.Combine(this._dir, "index.db"));
    }

    [Theory]
    [InlineData(15, "F:Demo.Foo._child")]            // field initializer
    [InlineData(19, "M:Demo.Foo.#ctor(Demo.Foo)")]    // constructor body
    [InlineData(24, "P:Demo.Foo.Prop")]              // property getter
    [InlineData(29, "M:Demo.Foo.Run")]               // method body
    [InlineData(30, "M:Demo.Foo.Run")]               // lambda reports its containing method
    [InlineData(31, "M:Demo.Foo.Run")]               // local function reports its containing method
    [InlineData(34, "M:Demo.Foo.Attributed")]        // attribute argument on a method
    [InlineData(40, "T:Demo.Bar")]                   // base list
    [InlineData(42, "M:Demo.Bar.#ctor")]             // base constructor initializer
    public void Extract_RecordsTheEnclosingDeclarationOfEachReference(int line, string expectedFromDocId)
    {
        // Act
        List<ReferenceRecord> onLine = this._extraction.References.Where(r => r.Line == line).ToList();

        // Assert
        onLine.Should().NotBeEmpty();
        onLine.Should().OnlyContain(r => r.FromDocId == expectedFromDocId, $"line {line}: {string.Join(", ", onLine.Select(r => r.TargetDocId + " from " + r.FromDocId))}");
    }

    [Fact]
    public void Extract_TypeLevelBaseListReportsTheType()
    {
        // Assert
        this._extraction.References.Should().Contain(r => r.TargetDocId == "T:Demo.IFoo" && r.Kind == ReferenceKinds.Base && r.FromDocId == "T:Demo.Foo");
    }

    [Fact]
    public async Task Extract_TopLevelStatementsReportTheSentinel()
    {
        // Act
        DocumentExtraction extraction = await DocumentExtractor.ExtractAsync(this._program, CancellationToken.None);

        // Assert
        extraction.References.Should().NotBeEmpty();
        extraction.References.Should().OnlyContain(r => r.FromDocId == EnclosingDisplay.TopLevel);
    }

    [Fact]
    public void Extract_RecordsTheEndLineOfEachDeclaration()
    {
        // Assert
        this._extraction.Symbols.Should().Contain(s => s.DocId == "T:Demo.Foo" && s.Line == 12 && s.EndLine == 38);
        this._extraction.Symbols.Should().Contain(s => s.DocId == "M:Demo.Foo.Run" && s.Line == 27 && s.EndLine == 32);
        this._extraction.Symbols.Should().Contain(s => s.DocId == "F:Demo.Foo._child" && s.EndLine == 15);
    }

    [Fact]
    public async Task LiveAndIndexPaths_PrintTheSameEnclosingNameAndUsageKind()
    {
        // Arrange: the index path
        int project = this._store.EnsureProject(Path.Combine(this._dir, "Demo.csproj"), "Demo", "Demo", Path.Combine(this._dir, "Demo.sln"));
        this._store.WriteBlob(project, "h1", this._extraction);
        this._store.SetTreeEntry(project, this._document.FilePath!, "h1", 1, 1);
        IReadOnlyList<ReferenceHit> hits = this._store.FindReferences("T:Demo.Foo");
        hits.Should().HaveCountGreaterThan(8);

        // Arrange: the live path
        SyntaxNode root = (await this._document.GetSyntaxRootAsync())!;
        SemanticModel model = (await this._document.GetSemanticModelAsync())!;
        SourceText text = await this._document.GetTextAsync();

        foreach (ReferenceHit hit in hits)
        {
            // Act
            int position = text.Lines[hit.Line - 1].Start + hit.Column - 1;
            SyntaxNode node = root.FindToken(position).Parent!;
            (string? docId, string? display) = EnclosingMember.Describe(model, node, CancellationToken.None);

            // Assert
            hit.Enclosing.Should().NotBeNull($"hit at {hit.Line}:{hit.Column}");
            display.Should().Be(hit.Enclosing, $"hit at {hit.Line}:{hit.Column}");
            docId.Should().Be(hit.FromDocId);
            DocumentExtractor.UsageKindOf(node).Should().Be(hit.Kind, $"hit at {hit.Line}:{hit.Column}");
        }

        hits.Select(h => h.Enclosing).Should().Contain(["Foo._child", "Foo.ctor", "Foo.Prop", "Foo.Run", "Foo.Attributed", "Bar"]);
    }

    [Fact]
    public void FindReferences_FromBlobWithoutEnclosingData_PrintsNoEnclosingName()
    {
        // Arrange: a blob extracted before from_doc_id existed
        int project = this._store.EnsureProject(Path.Combine(this._dir, "Old.csproj"), "Old", "Old", Path.Combine(this._dir, "Old.sln"));
        this._store.WriteBlob(project, "old", new DocumentExtraction(
            [new SymbolRecord("T:Foo", "class", "Foo", "Foo", "class Foo", "public", null, 1, 1)],
            [new ReferenceRecord("T:Bar", "Old", ReferenceKinds.Usage, 3, 5)],
            []));
        this._store.SetTreeEntry(project, Path.Combine(this._dir, "Old.cs"), "old", 1, 1);

        // Act
        IReadOnlyList<ReferenceHit> hits = this._store.FindReferences("T:Bar");

        // Assert
        hits.Should().ContainSingle().Which.Should().BeEquivalentTo(new { FromDocId = (string?)null, Enclosing = (string?)null });
    }

    [Fact]
    public void Disambiguate_AddsParameterCountOnlyWhenTwoOverloadsAppear()
    {
        // Arrange
        int project = this._store.EnsureProject(Path.Combine(this._dir, "Demo.csproj"), "Demo", "Demo", Path.Combine(this._dir, "Demo.sln"));
        this._store.WriteBlob(project, "h1", this._extraction);
        this._store.SetTreeEntry(project, this._document.FilePath!, "h1", 1, 1);
        IReadOnlyList<ReferenceHit> hits = this._store.FindReferences("T:Demo.Foo");

        // Act: both Run overloads create a Foo
        IReadOnlyDictionary<string, string> names = EnclosingDisplay.Disambiguate(hits.Where(h => h.FromDocId is not null).Select(h => (h.FromDocId!, h.Enclosing!)));

        // Assert
        names["M:Demo.Foo.Run"].Should().Be("Foo.Run(0 params)");
        names["M:Demo.Foo.Run(System.Int32)"].Should().Be("Foo.Run(1 params)");
        names["M:Demo.Foo.Attributed"].Should().Be("Foo.Attributed");
        names["T:Demo.Bar"].Should().Be("Bar");
    }

    public void Dispose()
    {
        this._store.Dispose();
    }
}
