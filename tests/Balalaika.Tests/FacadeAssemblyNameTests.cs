using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Balalaika.Index;
using Balalaika.Workspace;

namespace Balalaika.Tests;

/// <summary>
/// Framework types live in whichever reference assembly a compilation binds against: <c>netstandard</c> for
/// netstandard2.0, <c>System.Runtime</c> for net8.0 and later, <c>mscorlib</c> for net48. Each of those reference
/// assemblies defines the type itself (no type forwarding), so <c>ISymbol.ContainingAssembly.Name</c> differs per
/// target framework even though it is the same <c>System.String</c>. Symbol identity in the index is doc id plus
/// assembly name, so references extracted from two solutions on different frameworks must still agree on the
/// assembly part or they never merge in <c>find_references</c>.
/// </summary>
public sealed class FacadeAssemblyNameTests
{
    /// <summary>A minimal core library, compiled twice under different assembly names to stand in for two reference packs.</summary>
    private const string CoreLibrarySource = """
        namespace System
        {
            public class Object { }
            public abstract class ValueType { }
            public struct Void { }
            public sealed class String { }
            public struct Int32 { }
            public struct Boolean { }
            public abstract class Attribute { }
            public enum AttributeTargets { All = 32767 }
            public abstract class Enum : ValueType { }

            [AttributeUsage(AttributeTargets.All)]
            public sealed class AttributeUsageAttribute : Attribute
            {
                public AttributeUsageAttribute(AttributeTargets validOn) { }
                public bool AllowMultiple { get; set; }
                public bool Inherited { get; set; }
            }
        }
        """;

    private const string ConsumerSource = """
        namespace Demo
        {
            public class Holder
            {
                public System.String Name;
            }
        }
        """;

    [Fact]
    public async Task Extract_WhenSameTypeComesFromDifferentReferenceAssemblies_RecordsOneAssemblyName()
    {
        // Arrange
        MetadataReference netstandard = CompileCoreLibrary("netstandard");
        MetadataReference systemRuntime = CompileCoreLibrary("System.Runtime");
        (_, Project onNetStandard) = TestWorkspace.CreateProject(TestWorkspace.NewTempDir(), "OnNetStandard", [netstandard], ("Holder.cs", ConsumerSource));
        (_, Project onNet8) = TestWorkspace.CreateProject(TestWorkspace.NewTempDir(), "OnNet8", [systemRuntime], ("Holder.cs", ConsumerSource));

        // Sanity: the two compilations really do see System.String in differently named assemblies.
        (await ContainingAssemblyOfStringAsync(onNetStandard)).Should().Be("netstandard");
        (await ContainingAssemblyOfStringAsync(onNet8)).Should().Be("System.Runtime");

        // Act
        DocumentExtraction fromNetStandard = await DocumentExtractor.ExtractAsync(onNetStandard.Documents.Single(), CancellationToken.None);
        DocumentExtraction fromNet8 = await DocumentExtractor.ExtractAsync(onNet8.Documents.Single(), CancellationToken.None);

        // Assert
        ReferenceRecord a = fromNetStandard.References.Should().ContainSingle(r => r.TargetDocId == "T:System.String").Subject;
        ReferenceRecord b = fromNet8.References.Should().ContainSingle(r => r.TargetDocId == "T:System.String").Subject;
        b.TargetAssembly.Should().Be(a.TargetAssembly, "the same framework type must get one assembly name regardless of which reference assembly the project binds against");
        a.TargetAssembly.Should().Be(SymbolIdentity.FrameworkAssembly);
    }

    [Theory]
    [InlineData("netstandard", SymbolIdentity.FrameworkAssembly)]
    [InlineData("mscorlib", SymbolIdentity.FrameworkAssembly)]
    [InlineData("System.Private.CoreLib", SymbolIdentity.FrameworkAssembly)]
    [InlineData("System.Runtime", SymbolIdentity.FrameworkAssembly)]
    [InlineData("System.Core", SymbolIdentity.FrameworkAssembly)]
    [InlineData("System", SymbolIdentity.FrameworkAssembly)]
    [InlineData("System.Collections", SymbolIdentity.FrameworkAssembly)]
    [InlineData("system.console", SymbolIdentity.FrameworkAssembly)]
    [InlineData("Acme.Contracts", "Acme.Contracts")]
    [InlineData("SystemX.Tools", "SystemX.Tools")]
    [InlineData("", "")]
    public void Canonical_MapsFrameworkAssembliesToOneTokenAndLeavesOthersAlone(string input, string expected)
    {
        // Act
        string? actual = SymbolIdentity.Canonical(input);

        // Assert
        actual.Should().Be(expected);
    }

    [Fact]
    public void Canonical_WhenNull_ReturnsNull()
    {
        // Act & Assert
        SymbolIdentity.Canonical(null).Should().BeNull();
    }

    [Fact]
    public async Task Extract_WhenSameNamedTypesLiveInDifferentFrameworkNamespaces_KeepsThemDistinct()
    {
        // Arrange: two framework-named assemblies each define a Widget, in different namespaces.
        MetadataReference core = CompileCoreLibrary("System.Runtime");
        MetadataReference fooLib = CompileLibrary("System.Foo", "namespace System.Foo { public class Widget { } }", core);
        MetadataReference barLib = CompileLibrary("System.Bar", "namespace System.Bar { public class Widget { } }", core);
        const string consumer = """
            namespace Demo
            {
                public class Holder
                {
                    public System.Foo.Widget Foo;
                    public System.Bar.Widget Bar;
                }
            }
            """;
        (_, Project project) = TestWorkspace.CreateProject(TestWorkspace.NewTempDir(), "Consumer", [core, fooLib, barLib], ("Holder.cs", consumer));

        // Act
        DocumentExtraction extraction = await DocumentExtractor.ExtractAsync(project.Documents.Single(), CancellationToken.None);

        // Assert: both collapse to the framework token on the assembly half, but the doc ids keep them apart.
        ReferenceRecord foo = extraction.References.Should().ContainSingle(r => r.TargetDocId == "T:System.Foo.Widget").Subject;
        ReferenceRecord bar = extraction.References.Should().ContainSingle(r => r.TargetDocId == "T:System.Bar.Widget").Subject;
        foo.TargetAssembly.Should().Be(SymbolIdentity.FrameworkAssembly);
        bar.TargetAssembly.Should().Be(SymbolIdentity.FrameworkAssembly);
        foo.TargetDocId.Should().NotBe(bar.TargetDocId);
    }

    private static MetadataReference CompileLibrary(string assemblyName, string source, MetadataReference coreLibrary)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            references: [coreLibrary],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return Emit(compilation);
    }

    private static MetadataReference CompileCoreLibrary(string assemblyName)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(CoreLibrarySource)],
            references: [],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return Emit(compilation);
    }

    private static MetadataReference Emit(CSharpCompilation compilation)
    {
        using MemoryStream stream = new();
        Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(stream, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(runtimeMetadataVersion: "v4.0.30319"));
        result.Success.Should().BeTrue(string.Join(Environment.NewLine, result.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private static async Task<string?> ContainingAssemblyOfStringAsync(Project project)
    {
        Compilation compilation = (await project.GetCompilationAsync())!;
        return compilation.GetTypeByMetadataName("System.String")?.ContainingAssembly?.Name;
    }
}
