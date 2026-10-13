using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Balalaika.Index;
using Balalaika.Workspace;

namespace Balalaika.Tests;

public sealed class SymbolRankerTests
{
    [Theory]
    [InlineData("Job", "Job", MatchQuality.Exact)]
    [InlineData("job", "Job", MatchQuality.Exact)]
    [InlineData("Job", "CloseCompletedOrdersJob", MatchQuality.LastHump)]
    [InlineData("OrdersJob", "CloseCompletedOrdersJob", MatchQuality.LastHump)]
    [InlineData("Job", "JobTitle", MatchQuality.Prefix)]
    [InlineData("Upload", "UploadFile", MatchQuality.Prefix)]
    [InlineData("UOC", "UpdateOrderCommand", MatchQuality.CamelHump)]
    [InlineData("UpOrCo", "UpdateOrderCommand", MatchQuality.CamelHump)]
    [InlineData("Retry", "CreateRetryOptions", MatchQuality.CamelHump)]
    [InlineData("retry", "CreateRetryOptions", MatchQuality.CamelHump)]
    [InlineData("validator", "_validator", MatchQuality.LastHump)]
    [InlineData("valid", "_validator", MatchQuality.CamelHump)]
    [InlineData("Client", "HTTPClientFactory", MatchQuality.CamelHump)]
    [InlineData("ample", "Sample", MatchQuality.Substring)]
    [InlineData("rsJo", "CloseCompletedOrdersJob", MatchQuality.Substring)]
    [InlineData("uoc", "UpdateOrderCommand", null)]
    [InlineData("Jobs", "Job", null)]
    [InlineData("UOX", "UpdateOrderCommand", null)]
    public void Match_ClassifiesByRule(string pattern, string name, MatchQuality? expected)
    {
        // Act
        MatchQuality? actual = SymbolRanker.Match(pattern, name);

        // Assert
        actual.Should().Be(expected);
    }

    [Fact]
    public void HumpStarts_SplitsAcronymsDigitsAndUnderscores()
    {
        // Act / Assert
        SymbolRanker.HumpStarts("HTTPClientFactory_v2").Should().Equal(0, 4, 10, 18, 19);
        SymbolRanker.HumpStarts("_validator").Should().Equal(1);
        SymbolRanker.HumpStarts("Job").Should().Equal(0);
        SymbolRanker.HumpStarts("UOC").Should().Equal(0);
        SymbolRanker.PatternHumpStarts("UOC").Should().Equal(0, 1, 2);
    }

    [Fact]
    public void Score_OrdersByTestThenMatchThenKindThenLength()
    {
        // Arrange
        SymbolScore jobClass = SymbolRanker.Score("Job", "CloseCompletedOrdersJob", "class", "Acme.Orders", @"C:\a\Job.cs")!.Value;
        SymbolScore jobTitle = SymbolRanker.Score("Job", "JobTitle", "property", "Acme.Orders", @"C:\a\Order.cs")!.Value;
        SymbolScore uploadFile = SymbolRanker.Score("Upload", "UploadFile", "method", "Acme.Orders", @"C:\a\Repo.cs")!.Value;
        SymbolScore uploaded = SymbolRanker.Score("Upload", "Uploaded", "enum_member", "Acme.Orders", @"C:\a\Status.cs")!.Value;
        SymbolScore production = SymbolRanker.Score("Send", "Send", "method", "Acme.Orders", @"C:\a\S.cs")!.Value;
        SymbolScore test = SymbolRanker.Score("Send", "Send", "method", "Acme.Orders.Tests", @"C:\a\T.cs")!.Value;
        SymbolScore retryPolicy = SymbolRanker.Score("Retry", "RetryPolicy", "class", "Acme.Orders", @"C:\a\R.cs")!.Value;
        SymbolScore createRetry = SymbolRanker.Score("Retry", "CreateRetryOptions", "method", "Acme.Orders", @"C:\a\R.cs")!.Value;

        // Assert
        jobClass.Should().BeLessThan(jobTitle, "a class ending in Job beats a JobTitle property");
        uploadFile.Should().BeLessThan(uploaded, "a method beats an enum member on the same match quality");
        production.Should().BeLessThan(test, "a non-test member beats a test member of the same name");
        SymbolScore testLastHump = SymbolRanker.Score("Upload", "Handle_ShouldTruncateAndUpload", "method", "Acme.Orders.Tests", @"C:\a\T.cs")!.Value;
        uploaded.Should().BeLessThan(testLastHump, "any production hit beats a test method that merely ends in the word");
        retryPolicy.Should().BeLessThan(createRetry, "a prefix beats a hump match");
        SymbolRanker.Score("Nope", "Job", "class", "A", "").Should().BeNull();
    }

    [Fact]
    public void Score_WhenNoRuleMatches_UsesFallbackQuality()
    {
        // Act
        SymbolScore? score = SymbolRanker.Score("xyz", "Job", "class", "A", "", MatchQuality.Fuzzy);

        // Assert
        score.Should().NotBeNull();
        score!.Value.Match.Should().Be(MatchQuality.Fuzzy);
    }

    [Fact]
    public void ContainerParts_SplitTheLeadingTermsOffThePattern()
    {
        // Act / Assert
        SymbolRanker.NamePart("App.Order").Should().Be("Order");
        SymbolRanker.ContainerParts("App.Order").Should().Equal("App");
        SymbolRanker.ContainerParts("Order").Should().BeEmpty();
        SymbolRanker.NamePart("   ").Should().Be("");
    }

    [Fact]
    public async Task LiveAndIndexPaths_ReturnTheSameOrderForTheSameFixture()
    {
        // Arrange: one project with the symbols from the task 3 verification list, extracted into a store
        const string source = """
            namespace Demo
            {
                public enum FileStatus { Uploaded, Pending }
                public sealed class CloseCompletedOrdersJob { public string JobTitle { get; set; } = ""; }
                public interface IFileRepository { void UploadFile(string name); string UploadUri { get; } }
                public sealed class RetryPolicy { public static int CreateRetryOptions() => 0; }
                public sealed class Order { public string JobTitle { get; set; } = ""; public System.DateTime UploadDate { get; set; } }
            }
            """;
        string dir = TestWorkspace.NewTempDir();
        (AdhocWorkspace workspace, Project project) = TestWorkspace.CreateProject(dir, "Demo", ("Demo.cs", source));
        using IndexStore store = new(Path.Combine(dir, "index.db"));
        int projectId = store.EnsureProject(project.FilePath!, project.Name, project.AssemblyName, Path.Combine(dir, "Demo.sln"));
        DocumentExtraction extraction = await DocumentExtractor.ExtractAsync(project.Documents.Single(), CancellationToken.None);
        store.WriteBlob(projectId, "h", extraction);
        store.SetTreeEntry(projectId, Path.Combine(dir, "Demo.cs"), "h", 1, 1);

        foreach (string pattern in new[] { "Job", "Upload", "Retry", "CCOJ" })
        {
            // Act
            List<string> fromIndex = store.SearchSymbols(pattern).Select(h => h.DocId).ToList();
            IEnumerable<ISymbol> byPattern = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(workspace.CurrentSolution, pattern, SymbolFilter.TypeAndMember, CancellationToken.None);
            IEnumerable<ISymbol> bySubstring = await SymbolFinder.FindSourceDeclarationsAsync(workspace.CurrentSolution, n => n.Contains(pattern, StringComparison.OrdinalIgnoreCase), SymbolFilter.TypeAndMember, CancellationToken.None);
            List<string> fromLive = byPattern.Concat(bySubstring)
                .Where(s => !s.IsImplicitlyDeclared)
                .DistinctBy(s => s.GetDocumentationCommentId())
                .Select(s => (Id: s.GetDocumentationCommentId()!, Fq: s.ToDisplayString(DisplayFormats.FullyQualified), Score: SymbolRanker.Score(pattern, s.Name, DisplayFormats.KindOf(s), "Demo", "", MatchQuality.Fuzzy)!.Value))
                .OrderBy(x => x.Score).ThenBy(x => x.Fq, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Id)
                .ToList();

            // Assert
            fromIndex.Should().Equal(fromLive, "pattern {0}", pattern);
        }

        store.SearchSymbols("Job").Select(h => h.Name).Should().StartWith(["CloseCompletedOrdersJob", "JobTitle", "JobTitle"]);
        store.SearchSymbols("Upload").Select(h => h.Name).Should().StartWith(["UploadFile", "UploadUri", "UploadDate", "Uploaded"]);
        store.SearchSymbols("Retry").Select(h => h.Name).Should().Equal("RetryPolicy", "CreateRetryOptions");
        store.SearchSymbols("CCOJ").Select(h => h.Name).Should().Equal("CloseCompletedOrdersJob");
    }
}
