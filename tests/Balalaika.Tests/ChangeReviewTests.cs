using FluentAssertions;
using Microsoft.CodeAnalysis;
using Balalaika.Index;
using Balalaika.Workspace;

namespace Balalaika.Tests;

/// <summary>Branch review over an in-memory solution: the base side comes from a fake provider, git never runs.</summary>
public sealed class ChangeReviewTests : IDisposable
{
    private const string BaseText = """
        using System;

        namespace Demo
        {
            public class Service
            {
                private int _calls;

                public void Run() { _calls++; }

                public int Count(string name) { return name.Length; }

                internal void Helper() { }

                public void Gone() { }

                public void OldName() { }

                private void Secret() { Console.WriteLine("a"); }

                public class Nested { }
            }

            public interface IRepo
            {
                void Save(int id);
            }

            public interface IMarker { }
        }
        """;

    private const string CurrentText = """
        using System;

        namespace Demo
        {
            public class Service
            {
                private int _calls;

                public void Run()
                {
                    // a comment does not count, the changed statement does
                    _calls += 1;
                }

                public int Count(string name, bool trimmed) { return name.Length; }

                public void Helper() { }

                public void NewName() { }

                private void Secret() { Console.WriteLine("b"); }

                public void Added(ReadOnlySpan<char> text, params int[] rest) { }

                /// <summary>Reformatted with a doc comment: not a change.</summary>
                public class Nested
                {
                }
            }

            [Obsolete]
            public interface IRepo : IMarker
            {
                void Save(int id);
            }

            public interface IMarker { }
        }
        """;

    private readonly string _dir = TestWorkspace.NewTempDir();
    private readonly ResultPager _pager = new();

    [Fact]
    public async Task AnalyzeAsync_ClassifiesEveryKindOfDeclarationChange()
    {
        // Arrange
        (LoadedSolution loaded, FakeGit git) = this.Solution(("Service.cs", CurrentText));
        git.Modified("Service.cs", BaseText);

        // Act
        (ReviewReport? report, string? error) = await ChangeReview.AnalyzeAsync(loaded, git, SourceFilter.None, "main", CancellationToken.None);

        // Assert
        error.Should().BeNull();
        ReviewedFile file = report!.Files.Should().ContainSingle().Which;
        file.Status.Should().Be(FileStatus.Modified);
        file.SurfaceChanged.Should().BeTrue();
        file.Changes.Select(c => (c.Kind, c.DocId)).Should().Equal(
            (DeclarationChangeKind.Body, "M:Demo.Service.Run"),
            (DeclarationChangeKind.Signature, "M:Demo.Service.Count(System.String,System.Boolean)"),
            (DeclarationChangeKind.Removed, "M:Demo.Service.Gone"),
            (DeclarationChangeKind.Signature, "M:Demo.Service.Helper"),
            (DeclarationChangeKind.Removed, "M:Demo.Service.OldName"),
            (DeclarationChangeKind.Added, "M:Demo.Service.NewName"),
            (DeclarationChangeKind.Body, "M:Demo.Service.Secret"),
            (DeclarationChangeKind.Added, "M:Demo.Service.Added(System.ReadOnlySpan{System.Char},System.Int32[])"),
            (DeclarationChangeKind.Header, "T:Demo.IRepo"));
        file.Changes.Single(c => c.DocId == "M:Demo.Service.Count(System.String,System.Boolean)").Before!.DocId.Should().Be("M:Demo.Service.Count(System.String)", "a parameter change pairs the old and the new overload");
        file.Changes.Single(c => c.DocId == "M:Demo.Service.Secret").IsPrivate.Should().BeTrue();
        file.Changes.Single(c => c.DocId == "M:Demo.Service.Secret").IsSurface.Should().BeFalse();
        report.Diagnostics.Should().BeEmpty();
        loaded.Solution.GetDocument(file.LiveDocument!.Id)!.GetTextAsync().Result.ToString().Should().Be(CurrentText, "the live solution is never touched");
    }

    [Fact]
    public async Task AnalyzeAsync_BodyOnlyEdit_LeavesTheSurfaceUnchanged()
    {
        // Arrange
        string current = BaseText.Replace("_calls++;", "_calls += 2;");
        (LoadedSolution loaded, FakeGit git) = this.Solution(("Service.cs", current));
        git.Modified("Service.cs", BaseText);

        // Act
        (ReviewReport? report, _) = await ChangeReview.AnalyzeAsync(loaded, git, SourceFilter.None, "main", CancellationToken.None);

        // Assert: the class itself is not reported, only the member whose tokens changed
        ReviewedFile file = report!.Files.Single();
        file.SurfaceChanged.Should().BeFalse();
        file.Changes.Select(c => (c.Kind, c.DocId)).Should().Equal((DeclarationChangeKind.Body, "M:Demo.Service.Run"));
    }

    [Fact]
    public async Task AnalyzeAsync_AddedAndDeletedFiles_ListTheirDeclarations()
    {
        // Arrange: New.cs exists live only; Old.cs is gone from the working tree and comes back from the base
        (LoadedSolution loaded, FakeGit git) = this.Solution(("New.cs", "namespace Demo { public class Fresh { public void Go() { } } }"));
        git.Added("New.cs");
        git.Deleted("Old.cs", "namespace Demo { public class Stale { public void Was() { } } }");

        // Act
        (ReviewReport? report, _) = await ChangeReview.AnalyzeAsync(loaded, git, SourceFilter.None, "main", CancellationToken.None);

        // Assert
        report!.Files.Should().HaveCount(2);
        ReviewedFile added = report.Files.Single(f => f.Path.EndsWith("New.cs"));
        added.Status.Should().Be(FileStatus.Added);
        added.DeclarationCount.Should().Be(2);
        added.Changes.Should().OnlyContain(c => c.Kind == DeclarationChangeKind.Added);
        ReviewedFile deleted = report.Files.Single(f => f.Path.EndsWith("Old.cs"));
        deleted.Status.Should().Be(FileStatus.Deleted);
        deleted.LiveDocument.Should().BeNull();
        deleted.BaseDocument.Should().NotBeNull();
        deleted.Changes.Select(c => (c.Kind, c.DocId)).Should().Equal((DeclarationChangeKind.Removed, "T:Demo.Stale"), (DeclarationChangeKind.Removed, "M:Demo.Stale.Was"));
    }

    [Fact]
    public async Task AnalyzeAsync_RemovedMemberStillUsed_ShowsAsAnErrorOnTheChangedFiles()
    {
        // Arrange
        const string caller = "namespace Demo { public class Caller { public void Go(Service s) => s.Gone(); } }";
        (LoadedSolution loaded, FakeGit git) = this.Solution(("Service.cs", CurrentText), ("Caller.cs", caller));
        git.Modified("Service.cs", BaseText);
        git.Modified("Caller.cs", caller.Replace("s.Gone()", "s.Run()"));

        // Act
        (ReviewReport? report, _) = await ChangeReview.AnalyzeAsync(loaded, git, SourceFilter.None, "main", CancellationToken.None);

        // Assert
        report!.Diagnostics.Should().ContainSingle(d => d.Id == "CS1061" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task AnalyzeAsync_ProjectAndOtherFiles_AreListedNotAnalysed_OutsideFilesAreCounted()
    {
        // Arrange
        (LoadedSolution loaded, FakeGit git) = this.Solution(("Service.cs", CurrentText));
        git.Files.Add(new ChangedFile(Path.Combine(this._dir, "Demo.csproj"), FileStatus.Modified));
        git.Files.Add(new ChangedFile(Path.Combine(this._dir, "README.md"), FileStatus.Added));
        git.Files.Add(new ChangedFile(Path.Combine(Path.GetDirectoryName(this._dir)!, "elsewhere", "Other.cs"), FileStatus.Modified));

        // Act
        (ReviewReport? report, _) = await ChangeReview.AnalyzeAsync(loaded, git, SourceFilter.None, "main", CancellationToken.None);

        // Assert
        report!.Files.Select(f => (Path.GetFileName(f.Path), f.Kind)).Should().BeEquivalentTo([("Demo.csproj", ReviewedFileKind.Project), ("README.md", ReviewedFileKind.Other)]);
        report.OutsideSolution.Should().Be(1);
    }

    [Fact]
    public async Task AnalyzeAsync_WhenGitIsMissingOrTheBaseIsUnknown_ReturnsTheErrorAndNothingElse()
    {
        // Arrange
        (LoadedSolution loaded, FakeGit git) = this.Solution(("Service.cs", CurrentText));
        git.ProbeError = "'git' could not be started";

        // Act / Assert
        (await ChangeReview.AnalyzeAsync(loaded, git, SourceFilter.None, "main", CancellationToken.None)).Should().Be((null, "'git' could not be started"));

        git.ProbeError = null;
        git.ChangesError = "git merge-base nope HEAD failed: fatal: Not a valid object name nope";
        (await ChangeReview.AnalyzeAsync(loaded, git, SourceFilter.None, "nope", CancellationToken.None)).Error.Should().StartWith("git merge-base nope HEAD failed");

        git.ChangesError = null;
        git.Root = null;
        (await ChangeReview.AnalyzeAsync(loaded, git, SourceFilter.None, "main", CancellationToken.None)).Error.Should().EndWith("is not inside a git repository.");
    }

    [Fact]
    public async Task AnalyzeAsync_WhenNoBaseIsGiven_UsesTheRepositoryDefaultBranch_OrExplainsThatNoneExists()
    {
        // Arrange
        (LoadedSolution loaded, FakeGit git) = this.Solution(("Service.cs", CurrentText));
        git.Modified("Service.cs", BaseText);
        git.DefaultBase = "dev";

        // Act
        (ReviewReport? report, string? error) = await ChangeReview.AnalyzeAsync(loaded, git, SourceFilter.None, null, CancellationToken.None);

        // Assert
        error.Should().BeNull();
        report!.BaseRef.Should().Be("dev");
        git.LastBaseRef.Should().Be("dev");

        // Act: a repository with neither origin/HEAD nor any of the usual branch names
        git.DefaultBase = null;
        (report, error) = await ChangeReview.AnalyzeAsync(loaded, git, SourceFilter.None, "  ", CancellationToken.None);

        // Assert
        report.Should().BeNull();
        error.Should().StartWith("No default branch found").And.Contain("main, master, develop, dev").And.EndWith("as base.");
    }

    [Fact]
    public async Task Format_PrintsFilesDeclarationsImpactAndTotals()
    {
        // Arrange
        (LoadedSolution loaded, FakeGit git) = this.Solution(("Service.cs", CurrentText));
        git.Modified("Service.cs", BaseText);
        git.Files.Add(new ChangedFile(Path.Combine(this._dir, "Demo.csproj"), FileStatus.Modified));
        (ReviewReport? report, _) = await ChangeReview.AnalyzeAsync(loaded, git, SourceFilter.None, "main", CancellationToken.None);

        // Act
        string text = ChangeReview.Format(report!, includeTests: true, (_, c) => c.DocId == "M:Demo.Service.Gone" ? "references: Live Demo 1 before removal" : null, this._pager, 200);
        string[] lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        // Assert
        lines[0].Should().Be("Review: Demo  working tree vs main (merge-base abc1234)  2 files changed: 1 source, 1 project, 0 other");
        lines[1].Should().Be(Path.Combine(this._dir, "Demo.csproj") + "  modified  (project file, not analysed; run reindex force=true if a package version changed)");
        lines[2].Should().Be(Path.Combine(this._dir, "Service.cs") + "  modified  surface");
        lines.Should().Contain("  ~ body  Demo.Service.Run()  public  9-13");
        lines.Should().Contain("  ~ signature  Demo.Service.Count(string, bool)  public int Count(string name) -> public int Count(string name, bool trimmed)  15");
        lines.Should().Contain("  - method  Demo.Service.Gone()  public  was 15");
        lines.Should().Contain("      references: Live Demo 1 before removal");
        lines.Should().Contain("  ~ body  Demo.Service.Secret()  private  21");
        lines.Should().Contain("  + method  Demo.Service.Added(ReadOnlySpan<char>, int[])  public  23");
        lines.Should().NotContain(l => l.Contains("Nested"), "a reformatted type header with a new doc comment is not a change");
        lines.Should().Contain("  ~ header  Demo.IRepo  public  32-35  + implements Demo.IMarker; + [Obsolete]");
        lines[^1].Should().Be("Total: 1 source files, 9 changed declarations (7 surface, 2 body-only or private), 0 errors, 0 warnings");
    }

    [Fact]
    public async Task Format_PagesDeclarationLines_AndHidesTestProjectsOnRequest()
    {
        // Arrange: a second, test project with one changed file
        (LoadedSolution loaded, FakeGit git) = this.Solution(("Service.cs", CurrentText));
        git.Modified("Service.cs", BaseText);
        (ReviewReport? report, _) = await ChangeReview.AnalyzeAsync(loaded, git, SourceFilter.None, "main", CancellationToken.None);
        ReviewedFile testFile = report!.Files[0] with { Path = Path.Combine(this._dir, "ServiceTests.cs"), IsTest = true };
        ReviewReport withTests = report with { Files = [report.Files[0], testFile] };

        // Act
        string paged = ChangeReview.Format(withTests, includeTests: true, (_, _) => null, this._pager, 3);
        string hidden = ChangeReview.Format(withTests, includeTests: false, (_, _) => null, this._pager, 200);

        // Assert
        paged.Should().Contain("more (next_page cursor=");
        paged.Split('\n').Should().HaveCount(6, "header, three paged lines, the cursor line and the total");
        hidden.Should().Contain("1 in test projects hidden (includeTests=true shows them)");
        hidden.Should().NotContain("ServiceTests.cs");
    }

    [Fact]
    public void DescribeChange_HeaderWithoutRecordedDelta_SaysWhereElseTheChangeCanBe()
    {
        // Arrange: a primary constructor gained a parameter; neither relations nor attributes record that
        SymbolRecord type = new("T:App.Handler", "class", "Handler", "App.Handler", "public class Handler", "public", null, 10, 14, 40);

        // Act / Assert
        ChangeReview.DescribeChange(new DeclarationChange(DeclarationChangeKind.Header, type, type, []))
            .Should().Be("~ header  App.Handler  public  10-40  (primary constructor or constraints)");
    }

    [Fact]
    public void DescribeChange_WhenSignaturesPrintTheSame_ShowsTheQualifiedParameterTypes()
    {
        // Arrange: DocumentMessage moved to another namespace; the short signature does not change
        SymbolRecord before = new("M:App.Consumer.Consume(MassTransit.ConsumeContext{App.DTO.DocumentMessage})", "method", "Consume", "App.Consumer.Consume", "public Task Consume(ConsumeContext<DocumentMessage> context)", "public", "T:App.Consumer", 28, 17, 93);
        SymbolRecord after = before with { DocId = "M:App.Consumer.Consume(MassTransit.ConsumeContext{Contracts.DocumentMessage})", Line = 31, EndLine = 116 };

        // Act
        string line = ChangeReview.DescribeChange(new DeclarationChange(DeclarationChangeKind.Signature, before, after));

        // Assert
        line.Should().Be("~ signature  App.Consumer.Consume(ConsumeContext<DocumentMessage>)  parameter types (MassTransit.ConsumeContext<App.DTO.DocumentMessage>) -> (MassTransit.ConsumeContext<Contracts.DocumentMessage>)  31-116");
    }

    [Theory]
    [InlineData(25, 23, "25 (2 production, 23 tests)")]
    [InlineData(7, 0, "7")]
    [InlineData(0, 0, "0")]
    [InlineData(3, 3, "3 (0 production, 3 tests)")]
    public void SplitCount_ShowsTheHalvesOnlyWhenTestsAreInvolved(int total, int tests, string expected)
    {
        // Act / Assert
        ChangeReview.SplitCount(total, tests).Should().Be(expected);
    }

    [Theory]
    [InlineData(3, 2, 0, "3 (1 direct, 2 via base or interface members)")]
    [InlineData(7, 4, 5, "7 (3 direct, 4 via base or interface members; 2 production, 5 tests)")]
    [InlineData(25, 0, 23, "25 (2 production, 23 tests)")]
    [InlineData(3, 0, 0, "3")]
    public void SplitCallers_SplitsViaAndTestsJoinedBySemicolon(int total, int via, int tests, string expected)
    {
        // Act / Assert
        ChangeReview.SplitCallers(total, via, tests).Should().Be(expected);
    }

    [Theory]
    [InlineData("public void Run()", "")]
    [InlineData("public int Count(string name)", "string")]
    [InlineData("public void Go(ref int a, out string b, params int[] rest)", "int, string, int[]")]
    [InlineData("public void Go(Dictionary<string, List<int>> map, int x = 5)", "Dictionary<string, List<int>>, int")]
    [InlineData("public string Name { get; set; }", null)]
    [InlineData("public Service(Func<int, bool> pick)", "Func<int, bool>")]
    public void ParameterTypes_StripsNamesModifiersAndDefaults(string signature, string? expected)
    {
        // Act / Assert
        ChangeReview.ParameterTypes(signature).Should().Be(expected);
    }

    private (LoadedSolution Loaded, FakeGit Git) Solution(params (string FileName, string Text)[] documents)
    {
        (AdhocWorkspace workspace, _) = TestWorkspace.CreateProject(this._dir, "Demo", documents);
        LoadedSolution loaded = new(workspace, workspace.CurrentSolution, Path.Combine(this._dir, "Demo.sln"), []);
        return (loaded, new FakeGit(this._dir));
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

    private sealed class FakeGit(string root) : IBaseContentProvider
    {
        public string? Root { get; set; } = root;

        public string? ProbeError { get; set; }

        public string? ChangesError { get; set; }

        public string? DefaultBase { get; set; } = "main";

        public string? LastBaseRef { get; private set; }

        public List<ChangedFile> Files { get; } = [];

        public Dictionary<string, string> BaseTexts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Modified(string fileName, string baseText)
        {
            string path = Path.Combine(root, fileName);
            this.Files.Add(new ChangedFile(path, FileStatus.Modified));
            this.BaseTexts[path] = baseText;
        }

        public void Added(string fileName)
        {
            this.Files.Add(new ChangedFile(Path.Combine(root, fileName), FileStatus.Added));
        }

        public void Deleted(string fileName, string baseText)
        {
            string path = Path.Combine(root, fileName);
            this.Files.Add(new ChangedFile(path, FileStatus.Deleted));
            this.BaseTexts[path] = baseText;
        }

        public Task<string?> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(this.ProbeError);

        public string Describe() => "fake";

        public string? FindRepositoryRoot(string path) => this.Root;

        public Task<string?> FindDefaultBaseAsync(string repositoryRoot, CancellationToken cancellationToken) => Task.FromResult(this.DefaultBase);

        public Task<(ChangeSet? Changes, string? Error)> GetChangesAsync(string repositoryRoot, string baseRef, CancellationToken cancellationToken)
        {
            this.LastBaseRef = baseRef;
            return Task.FromResult(this.ChangesError is null ? (new ChangeSet(repositoryRoot, "abc1234def", this.Files), null) : ((ChangeSet?)null, this.ChangesError));
        }

        public Task<string?> GetBaseTextAsync(string repositoryRoot, string mergeBase, string path, CancellationToken cancellationToken)
        {
            return Task.FromResult(this.BaseTexts.GetValueOrDefault(path));
        }
    }
}
