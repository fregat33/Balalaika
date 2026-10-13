using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using Balalaika.Index;
using Balalaika.Workspace;

namespace Balalaika.Tests;

public sealed class PackageReferencesTests : IDisposable
{
    private readonly string _dir = TestWorkspace.NewTempDir();

    [Theory]
    [InlineData(@"C:\Users\me\.nuget\packages\acme.contracts\3.1.0\lib\net8.0\Acme.Contracts.dll", "acme.contracts", "3.1.0")]
    [InlineData(@"C:\Users\me\.nuget\packages\acme.contracts\3.2.0-beta.1\ref\netstandard2.0\Acme.Contracts.dll", "acme.contracts", "3.2.0-beta.1")]
    [InlineData("/home/me/.nuget/packages/newtonsoft.json/13.0.3/lib/net6.0/Newtonsoft.Json.dll", "newtonsoft.json", "13.0.3")]
    public void TryParseNuGetPath_ForPackagesFolderLayout_ReturnsIdAndVersion(string path, string expectedId, string expectedVersion)
    {
        // Act
        bool parsed = PackageReferences.TryParseNuGetPath(path, out string id, out string version);

        // Assert
        parsed.Should().BeTrue();
        id.Should().Be(expectedId);
        version.Should().Be(expectedVersion);
    }

    [Fact]
    public void TryParseNuGetPath_ForBuildOutput_ReturnsFalse()
    {
        // Act / Assert
        PackageReferences.TryParseNuGetPath(@"C:\src\Acme\Lib\bin\Debug\net8.0\Acme.Lib.dll", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseNuGetPath_ForFrameworkPack_ParsesPackAsPackage()
    {
        // The targeting pack shares the <id>/<version>/ref layout. It parses, and drift stays silent because no
        // indexed source project has a framework assembly name.
        PackageReferences.TryParseNuGetPath(@"C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\10.0.0\ref\net10.0\System.Runtime.dll", out string id, out string version).Should().BeTrue();
        id.Should().Be("Microsoft.NETCore.App.Ref");
        version.Should().Be("10.0.0");
    }

    [Fact]
    public async Task ReadVersion_FromInformationalVersionAttribute_StripsCommitSuffix()
    {
        // Arrange
        (AdhocWorkspace workspace, Project project) = TestWorkspace.CreateProject(this._dir, "Lib",
            ("AssemblyInfo.cs", "[assembly: System.Reflection.AssemblyInformationalVersion(\"0.1.6+abc123\")]"),
            ("A.cs", "public class A { }"));
        using AdhocWorkspace _ = workspace;

        // Act
        string? version = PackageReferences.ReadVersion((await project.GetCompilationAsync())!);

        // Assert
        version.Should().Be("0.1.6");
    }

    [Fact]
    public async Task ReadVersion_WithoutDeclaredVersion_IsNull()
    {
        // Arrange
        (AdhocWorkspace workspace, Project project) = TestWorkspace.CreateProject(this._dir, "Lib", ("A.cs", "public class A { }"));
        using AdhocWorkspace _ = workspace;

        // Act / Assert: the SDK default 1.0.0 means "set elsewhere", not a comparable version
        PackageReferences.ReadVersion((await project.GetCompilationAsync())!).Should().BeNull();
    }

    [Fact]
    public async Task Collect_FindsPackageReferencesByPathAndIgnoresFrameworkReferences()
    {
        // Arrange: emit a DLL into a packages-folder layout and reference it like a NuGet package
        string dll = Path.Combine(this._dir, "packages", "acme.contracts", "3.1.0", "lib", "net10.0", "Acme.Contracts.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        (AdhocWorkspace libWorkspace, Project lib) = TestWorkspace.CreateProject(Path.Combine(this._dir, "Lib"), "Acme.Contracts", ("C.cs", "public class C { }"));
        using AdhocWorkspace _ = libWorkspace;
        EmitResult emitted = (await lib.GetCompilationAsync())!.Emit(dll);
        emitted.Success.Should().BeTrue();

        List<MetadataReference> references = [.. TestWorkspace.DefaultReferences, MetadataReference.CreateFromFile(dll)];
        (AdhocWorkspace appWorkspace, Project app) = TestWorkspace.CreateProject(Path.Combine(this._dir, "App"), "App", references, ("A.cs", "public class A { C c = new(); }"));
        using AdhocWorkspace __ = appWorkspace;

        // Act
        IReadOnlyList<PackageRefRecord> packages = PackageReferences.Collect(app);

        // Assert
        packages.Should().ContainSingle().Which.Should().Be(new PackageRefRecord("Acme.Contracts", "acme.contracts", "3.1.0"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this._dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
