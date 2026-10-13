using FluentAssertions;
using Microsoft.CodeAnalysis;
using Balalaika.Index;
using Balalaika.Workspace;

namespace Balalaika.Tests;

public sealed class DocumentExtractorTests
{
    private const string Source = """
        namespace Demo
        {
            public interface IFoo
            {
                void Run();
            }

            public class Foo : IFoo
            {
                public void Run()
                {
                }
            }

            public abstract class Base
            {
                public abstract int Value { get; }
            }

            public sealed class Derived : Base
            {
                public override int Value => 1;
            }

            public record Person(string Name);

            public class Bar
            {
                public void M()
                {
                    var foo = new Foo();
                    foo.Run();
                    Person p = new("x");
                    System.Console.WriteLine(p.Name);
                }
            }
        }
        """;

    private readonly DocumentExtraction _extraction;

    public DocumentExtractorTests()
    {
        (_, Project project) = TestWorkspace.CreateProject(TestWorkspace.NewTempDir(), "Demo", ("Demo.cs", Source));
        this._extraction = DocumentExtractor.ExtractAsync(project.Documents.Single(), CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
    public void Extract_RecordsDeclarationsWithKindsAndPositions()
    {
        // Assert
        this._extraction.Symbols.Should().Contain(s => s.DocId == "T:Demo.IFoo" && s.Kind == "interface" && s.Line == 3);
        this._extraction.Symbols.Should().Contain(s => s.DocId == "M:Demo.Foo.Run" && s.Kind == "method" && s.ContainingDocId == "T:Demo.Foo");
        this._extraction.Symbols.Should().Contain(s => s.DocId == "T:Demo.Person" && s.Kind == "record");
        this._extraction.Symbols.Should().Contain(s => s.DocId == "P:Demo.Person.Name" && s.Kind == "property");
        this._extraction.Symbols.Should().Contain(s => s.DocId == "P:Demo.Derived.Value" && s.Accessibility == "public");
    }

    [Fact]
    public void Extract_RecordsImplementsInheritsAndOverridesRelations()
    {
        // Assert
        this._extraction.Relations.Should().Contain(r => r.FromDocId == "T:Demo.Foo" && r.ToDocId == "T:Demo.IFoo" && r.Kind == RelationKinds.Implements);
        this._extraction.Relations.Should().Contain(r => r.FromDocId == "M:Demo.Foo.Run" && r.ToDocId == "M:Demo.IFoo.Run" && r.Kind == RelationKinds.Implements);
        this._extraction.Relations.Should().Contain(r => r.FromDocId == "T:Demo.Derived" && r.ToDocId == "T:Demo.Base" && r.Kind == RelationKinds.Inherits);
        this._extraction.Relations.Should().Contain(r => r.FromDocId == "P:Demo.Derived.Value" && r.ToDocId == "P:Demo.Base.Value" && r.Kind == RelationKinds.Overrides);
    }

    [Fact]
    public void Extract_RecordsUsagesWithKinds()
    {
        // Assert
        this._extraction.References.Should().Contain(r => r.TargetDocId == "M:Demo.Foo.#ctor" && r.Kind == ReferenceKinds.Creation);
        this._extraction.References.Should().Contain(r => r.TargetDocId == "M:Demo.Foo.Run" && r.Kind == ReferenceKinds.Invocation);
        this._extraction.References.Should().Contain(r => r.TargetDocId == "M:Demo.Person.#ctor(System.String)" && r.Kind == ReferenceKinds.Creation);
        this._extraction.References.Should().Contain(r => r.TargetDocId == "P:Demo.Person.Name" && r.Kind == ReferenceKinds.Usage);
        this._extraction.References.Should().Contain(r => r.TargetDocId == "T:Demo.IFoo" && r.Kind == ReferenceKinds.Base);
        this._extraction.References.Should().Contain(r => r.TargetDocId == "M:System.Console.WriteLine(System.String)" && r.TargetAssembly == SymbolIdentity.FrameworkAssembly);
    }

    [Fact]
    public void Extract_SkipsVarAndLocals()
    {
        // Assert: `var foo = new Foo()` must yield exactly one usage of the type (the explicit one), not a second via `var`
        this._extraction.References.Count(r => r.TargetDocId == "T:Demo.Foo").Should().Be(1);
        this._extraction.References.Should().NotContain(r => r.TargetDocId.Contains("foo", StringComparison.Ordinal));
    }
}
