using FluentAssertions;
using Balalaika.Index;

namespace Balalaika.Tests;

public sealed class IndexStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(TestWorkspace.NewTempDir(), "index.db");
    private readonly IndexStore _store;

    public IndexStoreTests()
    {
        this._store = new IndexStore(this._dbPath);
    }

    [Fact]
    public void FindReferences_ReturnsOnlyHitsPointedToByCurrentTree()
    {
        // Arrange
        int project = this._store.EnsureProject(@"C:\x\A\A.csproj", "A", "A", @"C:\x\A.sln");
        string file = @"C:\x\A\F.cs";
        DocumentExtraction withReference = new([], [new ReferenceRecord("T:Foo", "A", ReferenceKinds.Usage, 3, 5)], []);
        this._store.WriteBlob(project, "hash-v1", withReference);
        this._store.SetTreeEntry(project, file, "hash-v1", 1, 10);

        // Act / Assert: v1 is current
        this._store.FindReferences("T:Foo").Should().ContainSingle().Which.Should().BeEquivalentTo(new { FilePath = file, Line = 3, Column = 5, ProjectName = "A" });

        // Switch to v2 content without the reference
        this._store.WriteBlob(project, "hash-v2", DocumentExtraction.Empty);
        this._store.SetTreeEntry(project, file, "hash-v2", 2, 20);
        this._store.FindReferences("T:Foo").Should().BeEmpty();

        // Switch back: the old blob is reused by re-pointing the tree, nothing is re-written
        this._store.HasBlob(project, "hash-v1").Should().BeTrue();
        this._store.SetTreeEntry(project, file, "hash-v1", 1, 10);
        this._store.FindReferences("T:Foo").Should().ContainSingle();
        this._store.GetStats().Blobs.Should().Be(2);
    }

    [Fact]
    public void WriteBlob_WithReplace_OverwritesExistingContent()
    {
        // Arrange
        int project = this._store.EnsureProject(@"C:\x\A\A.csproj", "A", "A", @"C:\x\A.sln");
        this._store.WriteBlob(project, "h", new DocumentExtraction([], [new ReferenceRecord("T:Old", "A", ReferenceKinds.Usage, 1, 1)], []));
        this._store.SetTreeEntry(project, @"C:\x\A\F.cs", "h", 1, 1);

        // Act
        this._store.WriteBlob(project, "h", new DocumentExtraction([], [new ReferenceRecord("T:New", "A", ReferenceKinds.Usage, 1, 1)], []), replace: true);

        // Assert
        this._store.FindReferences("T:Old").Should().BeEmpty();
        this._store.FindReferences("T:New").Should().ContainSingle();
        this._store.GetStats().Blobs.Should().Be(1);
    }

    [Fact]
    public void CollectGarbage_DeletesUnreferencedBlobsOlderThanRetention()
    {
        // Arrange
        int project = this._store.EnsureProject(@"C:\x\A\A.csproj", "A", "A", @"C:\x\A.sln");
        this._store.WriteBlob(project, "orphan", new DocumentExtraction([new SymbolRecord("T:Gone", "class", "Gone", "Gone", "class Gone", "public", null, 1, 1)], [], []));
        this._store.WriteBlob(project, "live", DocumentExtraction.Empty);
        this._store.SetTreeEntry(project, @"C:\x\A\F.cs", "live", 1, 1);

        // Act: negative retention makes "now" older than the cutoff
        int removed = this._store.CollectGarbage(TimeSpan.FromSeconds(-5));

        // Assert
        removed.Should().Be(1);
        this._store.HasBlob(project, "orphan").Should().BeFalse();
        this._store.HasBlob(project, "live").Should().BeTrue();
        this._store.GetStats().Symbols.Should().Be(0);
    }

    [Fact]
    public void SearchSymbols_MatchesPrefixHumpsAndSubstringsRankedByMatchQuality()
    {
        // Arrange
        int project = this._store.EnsureProject(@"C:\x\A\A.csproj", "A", "A", @"C:\x\A.sln");
        this._store.WriteBlob(project, "h", new DocumentExtraction(
            [
                new SymbolRecord("T:App.reUpdateOrderCommand", "record", "UpdateOrderCommand", "App.UpdateOrderCommand", "record UpdateOrderCommand", "public", null, 1, 1),
                new SymbolRecord("T:App.OrdersController", "class", "OrdersController", "App.OrdersController", "class OrdersController", "internal", null, 5, 1),
            ],
            [], []));
        this._store.SetTreeEntry(project, @"C:\x\A\F.cs", "h", 1, 1);

        // Act / Assert
        this._store.SearchSymbols("Update").Should().ContainSingle().Which.Name.Should().Be("UpdateOrderCommand");
        this._store.SearchSymbols("App.Order").Select(h => h.Name).Should().Equal("OrdersController", "UpdateOrderCommand");
        this._store.SearchSymbols("UOC").Should().ContainSingle().Which.Name.Should().Be("UpdateOrderCommand");
        this._store.SearchSymbols("Controller").Should().ContainSingle().Which.Name.Should().Be("OrdersController");
        this._store.SearchSymbols("Order", kind: "class").Should().ContainSingle().Which.Name.Should().Be("OrdersController");
    }

    [Fact]
    public void SearchSymbols_RanksProductionAboveTestAndRunsSubstringNextToPrefix()
    {
        // Arrange
        int app = this._store.EnsureProject(@"C:\x\App\App.csproj", "App", "App", @"C:\x\A.sln");
        int tests = this._store.EnsureProject(@"C:\x\App.Tests\App.Tests.csproj", "App.Tests", "App.Tests", @"C:\x\A.sln");
        this._store.WriteBlob(app, "h1", new DocumentExtraction(
            [
                new SymbolRecord("M:App.Sender.Send", "method", "Send", "App.Sender.Send", "void Send()", "public", "T:App.Sender", 3, 1),
                new SymbolRecord("T:App.RetryPolicy", "class", "RetryPolicy", "App.RetryPolicy", "class RetryPolicy", "public", null, 5, 1),
                new SymbolRecord("M:App.Factory.CreateRetryOptions", "method", "CreateRetryOptions", "App.Factory.CreateRetryOptions", "int CreateRetryOptions()", "public", "T:App.Factory", 7, 1),
            ],
            [], []));
        this._store.SetTreeEntry(app, @"C:\x\App\F.cs", "h1", 1, 1);
        this._store.WriteBlob(tests, "h2", new DocumentExtraction(
            [new SymbolRecord("M:App.Tests.SenderTests.Send", "method", "Send", "App.Tests.SenderTests.Send", "void Send()", "public", "T:App.Tests.SenderTests", 3, 1)],
            [], []));
        this._store.SetTreeEntry(tests, @"C:\x\App.Tests\T.cs", "h2", 1, 1);

        // Act / Assert
        this._store.SearchSymbols("Send").Select(h => h.ProjectName).Should().Equal("App", "App.Tests");
        this._store.SearchSymbols("Retry").Select(h => h.Name).Should().Equal("RetryPolicy", "CreateRetryOptions");
        this._store.SearchSymbols("Retry", limit: 1).Select(h => h.Name).Should().Equal("RetryPolicy");
        this._store.SearchSymbols("App.Retry").Select(h => h.Name).Should().Equal("RetryPolicy", "CreateRetryOptions");
        this._store.SearchSymbols("Tests.Send").Select(h => h.ProjectName).Should().Equal("App.Tests");
    }

    [Fact]
    public void FindRelations_ReturnsImplementorsWithDeclarationLocation()
    {
        // Arrange
        int project = this._store.EnsureProject(@"C:\x\A\A.csproj", "A", "A", @"C:\x\A.sln");
        this._store.WriteBlob(project, "h", new DocumentExtraction(
            [new SymbolRecord("T:Foo", "class", "Foo", "Foo", "public class Foo", "public", null, 2, 14)],
            [],
            [new RelationRecord("T:Foo", "T:IFoo", "A", RelationKinds.Implements)]));
        this._store.SetTreeEntry(project, @"C:\x\A\Foo.cs", "h", 1, 1);

        // Act
        IReadOnlyList<RelationHit> hits = this._store.FindRelations("T:IFoo", [RelationKinds.Implements, RelationKinds.Inherits]);

        // Assert
        hits.Should().ContainSingle().Which.Should().BeEquivalentTo(new { FromDocId = "T:Foo", Kind = "implements", FilePath = @"C:\x\A\Foo.cs", Line = 2, Signature = "public class Foo" });
    }

    [Fact]
    public void RemoveTreeEntriesByPath_HidesBlobFromQueriesWithoutDeletingIt()
    {
        // Arrange
        int project = this._store.EnsureProject(@"C:\x\A\A.csproj", "A", "A", @"C:\x\A.sln");
        this._store.WriteBlob(project, "h", new DocumentExtraction([], [new ReferenceRecord("T:Foo", "A", ReferenceKinds.Usage, 1, 1)], []));
        this._store.SetTreeEntry(project, @"C:\x\A\F.cs", "h", 1, 1);

        // Act
        this._store.RemoveTreeEntriesByPath(@"c:\X\a\f.CS");

        // Assert
        this._store.FindReferences("T:Foo").Should().BeEmpty();
        this._store.HasBlob(project, "h").Should().BeTrue();
    }

    [Fact]
    public void FindPackageDrift_ReportsConsumersOnAnotherVersionThanTheSource()
    {
        // Arrange: the source project declares 3.4, one consumer is pinned at 3.1, another is current, a third source has no version
        int source = this._store.EnsureProject(@"C:\x\Acme\Contracts\Acme.Contracts.csproj", "Acme.Contracts", "Acme.Contracts", @"C:\x\Acme\Acme.sln");
        this._store.SetProjectPackages(source, "3.4.0", []);
        int behind = this._store.EnsureProject(@"C:\x\Shop\App\Shop.App.csproj", "Shop.App", "Shop.App", @"C:\x\Shop\Shop.sln");
        this._store.SetProjectPackages(behind, null, [new PackageRefRecord("Acme.Contracts", "Acme.Contracts.Package", "3.1.0"), new PackageRefRecord("Newtonsoft.Json", "newtonsoft.json", "13.0.3")]);
        int current = this._store.EnsureProject(@"C:\x\Shop\Api\Shop.Api.csproj", "Shop.Api", "Shop.Api", @"C:\x\Shop\Shop.sln");
        this._store.SetProjectPackages(current, null, [new PackageRefRecord("Acme.Contracts", "acme.contracts", "3.4.0")]);
        int unversioned = this._store.EnsureProject(@"C:\x\Tools\Tools.csproj", "Tools", "Newtonsoft.Json", @"C:\x\Tools\Tools.sln");
        this._store.SetProjectPackages(unversioned, null, []);

        // Act
        IReadOnlyList<PackageDriftRow> drift = this._store.FindPackageDrift();

        // Assert
        drift.Should().ContainSingle();
        drift[0].Consumer.Name.Should().Be("Shop.App");
        drift[0].ConsumedVersion.Should().Be("3.1.0");
        drift[0].Source.Name.Should().Be("Acme.Contracts");
        drift[0].Source.Version.Should().Be("3.4.0");
        this._store.GetPackageConsumers("Acme.Contracts.Package").Should().BeEmpty("consumers are keyed by assembly name, not package id");
        this._store.GetPackageConsumers("acme.contracts").Should().HaveCount(2, "assembly names compare case-insensitively");
    }

    [Fact]
    public void SetProjectPackages_ReplacesPreviousSet()
    {
        // Arrange
        int project = this._store.EnsureProject(@"C:\x\A\A.csproj", "A", "A", @"C:\x\A.sln");
        this._store.SetProjectPackages(project, "1.2.3", [new PackageRefRecord("Old", "old", "1.0.0")]);

        // Act
        this._store.SetProjectPackages(project, null, [new PackageRefRecord("New", "new", "2.0.0")]);

        // Assert
        this._store.GetPackageConsumers("Old").Should().BeEmpty();
        this._store.GetPackageConsumers("New").Should().ContainSingle();
        this._store.GetProjects().Single().Version.Should().BeNull();
    }

    [Fact]
    public void FindReferences_JoinsTheEnclosingDeclarationFromTheSameBlob()
    {
        // Arrange
        int project = this._store.EnsureProject(@"C:\x\A\A.csproj", "A", "A", @"C:\x\A.sln");
        this._store.WriteBlob(project, "h", new DocumentExtraction(
            [
                new SymbolRecord("T:Foo", "class", "Foo", "Foo", "public class Foo", "public", null, 1, 14, 20),
                new SymbolRecord("M:Foo.Run", "method", "Run", "Foo.Run", "public void Run()", "public", "T:Foo", 3, 17, 5),
                new SymbolRecord("M:Foo.#ctor", "constructor", ".ctor", "Foo.Foo", "public Foo()", "public", "T:Foo", 7, 12, 9),
            ],
            [
                new ReferenceRecord("T:Bar", "A", ReferenceKinds.Creation, 4, 9, "M:Foo.Run"),
                new ReferenceRecord("T:Bar", "A", ReferenceKinds.Base, 1, 20, "T:Foo"),
                new ReferenceRecord("T:Bar", "A", ReferenceKinds.Usage, 8, 9, "M:Foo.#ctor"),
                new ReferenceRecord("T:Bar", "A", ReferenceKinds.Usage, 12, 1, EnclosingDisplay.TopLevel),
                new ReferenceRecord("T:Bar", "A", ReferenceKinds.Usage, 13, 1),
            ],
            []));
        this._store.SetTreeEntry(project, @"C:\x\A\F.cs", "h", 1, 1);

        // Act
        IReadOnlyList<ReferenceHit> hits = this._store.FindReferences("T:Bar");

        // Assert
        hits.Select(h => h.Enclosing).Should().Equal("Foo", "Foo.Run", "Foo.ctor", EnclosingDisplay.TopLevel, null);
    }

    [Fact]
    public void FindReferences_WithKinds_KeepsOnlyThoseKinds()
    {
        // Arrange
        int project = this._store.EnsureProject(@"C:\x\A\A.csproj", "A", "A", @"C:\x\A.sln");
        this._store.WriteBlob(project, "h", new DocumentExtraction(
            [],
            [
                new ReferenceRecord("T:Bar", "A", ReferenceKinds.Base, 1, 1),
                new ReferenceRecord("T:Bar", "A", ReferenceKinds.Creation, 2, 1),
                new ReferenceRecord("T:Bar", "A", ReferenceKinds.Usage, 3, 1),
                new ReferenceRecord("T:Bar", "A", ReferenceKinds.Invocation, 4, 1),
                new ReferenceRecord("T:Bar", "A", ReferenceKinds.Attribute, 5, 1),
            ],
            []));
        this._store.SetTreeEntry(project, @"C:\x\A\F.cs", "h", 1, 1);
        ReferenceFilter.TryParse("creation", "all", out ReferenceFilter one).Should().BeNull();
        ReferenceFilter.TryParse("base,attribute", "all", out ReferenceFilter two).Should().BeNull();

        // Act / Assert
        this._store.FindReferences("T:Bar", filter: one).Select(h => h.Line).Should().Equal(2);
        this._store.FindReferences("T:Bar", filter: two).Select(h => h.Line).Should().Equal(1, 5);
        this._store.FindReferences("T:Bar", filter: ReferenceFilter.None).Should().HaveCount(5);
        this._store.FindReferences("T:Bar", limit: 1, filter: two).Select(h => h.Line).Should().Equal(1);
    }

    [Fact]
    public void FindReferences_WithScope_SplitsTestProjectsByNameAndPath_AndCountsPerSolution()
    {
        // Arrange: a production project, a test project by name, and a production-named project under a tests folder
        int app = this._store.EnsureProject(@"C:\x\src\App\App.csproj", "App", "App", @"C:\x\A.sln");
        int unit = this._store.EnsureProject(@"C:\x\src\App.UnitTests\App.UnitTests.csproj", "App.UnitTests", "App.UnitTests", @"C:\x\A.sln");
        int other = this._store.EnsureProject(@"C:\y\tests\Other\Other.csproj", "Other", "Other", @"C:\y\B.sln");
        this._store.WriteBlob(app, "h1", new DocumentExtraction([], [new ReferenceRecord("T:Bar", "A", ReferenceKinds.Creation, 1, 1)], []));
        this._store.WriteBlob(unit, "h2", new DocumentExtraction([], [new ReferenceRecord("T:Bar", "A", ReferenceKinds.Creation, 2, 1), new ReferenceRecord("T:Bar", "A", ReferenceKinds.Usage, 3, 1)], []));
        this._store.WriteBlob(other, "h3", new DocumentExtraction([], [new ReferenceRecord("T:Bar", "A", ReferenceKinds.Creation, 4, 1)], []));
        this._store.SetTreeEntry(app, @"C:\x\src\App\F.cs", "h1", 1, 1);
        this._store.SetTreeEntry(unit, @"C:\x\src\App.UnitTests\F.cs", "h2", 1, 1);
        this._store.SetTreeEntry(other, @"C:\y\tests\Other\F.cs", "h3", 1, 1);
        ReferenceFilter.TryParse(null, "production", out ReferenceFilter production).Should().BeNull();
        ReferenceFilter.TryParse("creation", "tests", out ReferenceFilter testCreations).Should().BeNull();

        // Act / Assert
        this._store.FindReferences("T:Bar", filter: production).Select(h => h.ProjectName).Should().Equal("App");
        this._store.FindReferences("T:Bar", filter: testCreations).Select(h => h.Line).Should().Equal(2, 4);
        this._store.FindReferences("T:Bar", limit: 1, filter: testCreations).Select(h => h.Line).Should().Equal(2);
        this._store.CountReferencesBySolution("T:Bar").Should().BeEquivalentTo(new Dictionary<string, int> { [@"C:\x\A.sln"] = 3, [@"C:\y\B.sln"] = 1 });
        this._store.CountReferencesBySolution("T:Missing").Should().BeEmpty();
    }

    [Fact]
    public void GetFileSymbols_ReturnsTheCurrentTreesDeclarationsWithSpans()
    {
        // Arrange
        int project = this._store.EnsureProject(@"C:\x\A\A.csproj", "A", "A", @"C:\x\A.sln");
        string file = @"C:\x\A\F.cs";
        this._store.WriteBlob(project, "v1", new DocumentExtraction([new SymbolRecord("T:Old", "class", "Old", "Old", "public class Old", "public", null, 1, 14, 3)], [], []));
        this._store.WriteBlob(project, "v2", new DocumentExtraction([new SymbolRecord("T:New", "class", "New", "New", "public class New", "public", null, 2, 14, 9)], [], []));
        this._store.SetTreeEntry(project, file, "v2", 1, 1);

        // Act
        IReadOnlyList<FileSymbolHit> hits = this._store.GetFileSymbols(file);

        // Assert
        hits.Should().ContainSingle().Which.Should().BeEquivalentTo(new { ProjectName = "A", FilePath = file, ContentHash = "v2", Symbol = new { DocId = "T:New", Line = 2, EndLine = 9 } });
        this._store.GetFileSymbols(@"C:\x\A\Missing.cs").Should().BeEmpty();
    }

    public void Dispose()
    {
        this._store.Dispose();
        try
        {
            Directory.Delete(Path.GetDirectoryName(this._dbPath)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
