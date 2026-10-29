using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Balalaika.Index;
using Balalaika.Watcher;
using Balalaika.Workspace;

namespace Balalaika.Tests;

/// <summary>Solutions that appear and disappear under a root while the server runs.</summary>
public sealed class SolutionDiscoveryTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(TestWorkspace.NewTempDir());
    private readonly IndexStore _index;
    private readonly PendingRefreshes _pending = new();
    private readonly WorkspaceManager _workspaces;

    public SolutionDiscoveryTests()
    {
        this._index = new IndexStore(Path.Combine(TestWorkspace.NewTempDir(), "index.db"));
        this._workspaces = new WorkspaceManager(this._index, this._pending, Options.Create(new BalalaikaOptions { Roots = [this._root] }), NullLogger<WorkspaceManager>.Instance);
    }

    public void Dispose()
    {
        this._workspaces.Dispose();
        this._index.Dispose();
    }

    // ---------------------------------------------------------------- discovery helper

    [Fact]
    public void QualifiesForDiscovery_AtDepthSixYesAtDepthSevenNo()
    {
        // Arrange
        string six = Path.Combine(this._root, "a", "b", "c", "d", "e", "f", "X.sln");
        string seven = Path.Combine(this._root, "a", "b", "c", "d", "e", "f", "g", "X.sln");

        // Act / Assert
        SolutionDiscovery.QualifiesForDiscovery(Path.Combine(this._root, "X.sln"), [this._root]).Should().BeTrue();
        SolutionDiscovery.QualifiesForDiscovery(six, [this._root]).Should().BeTrue();
        SolutionDiscovery.QualifiesForDiscovery(seven, [this._root]).Should().BeFalse();
    }

    [Theory]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData("node_modules")]
    [InlineData(".git")]
    public void QualifiesForDiscovery_InsideSkippedFolder_IsFalse(string folder)
    {
        SolutionDiscovery.QualifiesForDiscovery(Path.Combine(this._root, "repo", folder, "X.sln"), [this._root]).Should().BeFalse();
    }

    [Fact]
    public void QualifiesForDiscovery_OutsideRootsOrWrongExtension_IsFalse()
    {
        SolutionDiscovery.QualifiesForDiscovery(Path.Combine(Path.GetTempPath(), "elsewhere", "X.sln"), [this._root]).Should().BeFalse();
        SolutionDiscovery.QualifiesForDiscovery(Path.Combine(this._root, "X.csproj"), [this._root]).Should().BeFalse();
    }

    [Fact]
    public void QualifiesForDiscovery_SlnxNextToSameNameSln_IsFalse()
    {
        // Arrange
        string dir = Path.Combine(this._root, "repo");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Acme.Orders.sln"), "");
        File.WriteAllText(Path.Combine(dir, "Acme.Orders.slnx"), "");
        File.WriteAllText(Path.Combine(dir, "Other.slnx"), "");

        // Act / Assert
        SolutionDiscovery.QualifiesForDiscovery(Path.Combine(dir, "Acme.Orders.slnx"), [this._root]).Should().BeFalse();
        SolutionDiscovery.QualifiesForDiscovery(Path.Combine(dir, "Acme.Orders.sln"), [this._root]).Should().BeTrue();
        SolutionDiscovery.QualifiesForDiscovery(Path.Combine(dir, "Other.slnx"), [this._root]).Should().BeTrue();
    }

    // ---------------------------------------------------------------- WorkspaceManager.ResolveSolution

    [Fact]
    public void ResolveSolution_MatchesAFullPath_WithEitherSeparator()
    {
        // Arrange
        string a = this.MakeSolution("A");
        this._workspaces.RegisterSolutions([a]);
        string forward = a.Replace('\\', '/');
        int lastSeparator = a.LastIndexOf(Path.DirectorySeparatorChar);
        string mixed = a[..lastSeparator] + "/" + a[(lastSeparator + 1)..];
        string unknown = Path.Combine(this._root, "Missing", "Missing.sln");

        // Act / Assert
        this._workspaces.ResolveSolution(a).Should().Be(a);
        this._workspaces.ResolveSolution(forward).Should().Be(a);
        this._workspaces.ResolveSolution(mixed).Should().Be(a);
        this._workspaces.ResolveSolution(a + "/").Should().Be(a);
        this._workspaces.ResolveSolution(a + Path.DirectorySeparatorChar).Should().Be(a);
        this._workspaces.ResolveSolution(unknown).Should().BeNull();
    }

    // ---------------------------------------------------------------- WorkspaceManager.Forget

    [Fact]
    public void Forget_ByFullPath_RemovesOnlyThatSolutionFromKnownAndStatus()
    {
        // Arrange
        string a = this.MakeSolution("A");
        string b = this.MakeSolution("B");
        this._workspaces.RegisterSolutions([a, b]);

        // Act
        ForgottenSolution? forgotten = this._workspaces.Forget(a);

        // Assert
        forgotten.Should().NotBeNull();
        forgotten!.Path.Should().Be(a);
        this._workspaces.KnownSolutions.Should().BeEquivalentTo([b]);
        this._workspaces.GetStatus().Select(s => s.Path).Should().BeEquivalentTo([b]);
    }

    [Fact]
    public void Forget_ByName_Works_AndUnknownOrBlankHintReturnsNull()
    {
        // Arrange
        string a = this.MakeSolution("Acme.Orders");
        this._workspaces.RegisterSolutions([a]);

        // Act / Assert
        this._workspaces.Forget("nothing-like-it").Should().BeNull();
        this._workspaces.Forget("").Should().BeNull();
        this._workspaces.Forget(null).Should().BeNull();
        this._workspaces.KnownSolutions.Should().HaveCount(1);
        this._workspaces.Forget("acme.orders")!.Name.Should().Be("Acme.Orders");
        this._workspaces.KnownSolutions.Should().BeEmpty();
    }

    [Fact]
    public void Forget_PinnedLoadedSolution_DisposesItAndDropsPendingRefresh()
    {
        // Arrange
        string dir = Path.Combine(this._root, "Pinned");
        Directory.CreateDirectory(dir);
        string sln = Path.Combine(dir, "Pinned.sln");
        File.WriteAllText(sln, "");
        (AdhocWorkspace workspace, _) = TestWorkspace.CreateProject(dir, "P", ("A.cs", "public class A { }"));
        LoadedSolution loaded = new(workspace, workspace.CurrentSolution, sln, []);
        this._workspaces.Adopt(loaded);
        this._workspaces.Pin(sln);
        this._pending.Record(sln, [Path.Combine(dir, "A.cs")], DateTime.UtcNow);
        this._workspaces.TryGetLoaded(sln, out _).Should().BeTrue();

        // Act
        this._workspaces.Forget("Pinned").Should().NotBeNull();

        // Assert
        this._workspaces.TryGetLoaded(sln, out _).Should().BeFalse();
        this._workspaces.Loaded.Should().BeEmpty();
        workspace.CurrentSolution.ProjectIds.Should().BeEmpty("a disposed workspace drops its solution");
        this._pending.Count.Should().Be(0);
        this._workspaces.GetStatus().Should().BeEmpty();
    }

    [Fact]
    public async Task GetOrLoadAsync_AfterForget_DoesNotBringTheSolutionBack()
    {
        // Arrange
        string a = this.MakeSolution("A");
        this._workspaces.RegisterSolutions([a]);
        this._workspaces.Forget(a);

        // Act
        Func<Task> act = () => this._workspaces.GetOrLoadAsync(a, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        this._workspaces.KnownSolutions.Should().BeEmpty();
    }

    [Fact]
    public void Forget_RemovesIndexRowsOfThatSolution()
    {
        // Arrange
        string a = this.MakeSolution("A");
        this._workspaces.RegisterSolutions([a]);
        this._index.EnsureProject(Path.Combine(this._root, "A", "A.csproj"), "A", "A", a);
        this._index.EnsureProject(Path.Combine(this._root, "A", "A2.csproj"), "A2", "A2", a);

        // Act
        ForgottenSolution? forgotten = this._workspaces.Forget("A");

        // Assert
        forgotten!.ProjectsRemoved.Should().Be(2);
        this._index.GetProjects().Should().BeEmpty();
    }

    [Fact]
    public void ForgetMissing_ForgetsOnlySolutionsWhoseFileIsGone()
    {
        // Arrange
        string a = this.MakeSolution("A");
        string b = this.MakeSolution("B");
        this._workspaces.RegisterSolutions([a, b]);
        File.Delete(a);

        // Act
        IReadOnlyList<ForgottenSolution> forgotten = this._workspaces.ForgetMissing();

        // Assert
        forgotten.Select(f => f.Path).Should().BeEquivalentTo([a]);
        this._workspaces.KnownSolutions.Should().BeEquivalentTo([b]);
    }

    [Fact]
    public void RegisterAppeared_AfterForgetOfExistingFile_NeedsACreateEvent()
    {
        // Arrange
        string a = this.MakeSolution("A");
        this._workspaces.RegisterSolutions([a]);
        this._workspaces.Forget(a);

        // Act / Assert: a modification does not bring it back, a create event does
        this._workspaces.RegisterAppeared([a], [], [this._root]).Should().BeEmpty();
        this._workspaces.IsKnown(a).Should().BeFalse();
        this._workspaces.RegisterAppeared([a], [a], [this._root]).Should().BeEquivalentTo([a]);
        this._workspaces.IsKnown(a).Should().BeTrue();
    }

    // ---------------------------------------------------------------- load_solution with a path / forget_solution text

    [Fact]
    public void RegisterExistingSolution_FullPathOfExistingUnknownSolution_RegistersIt_EvenOutsideTheRoots()
    {
        // Arrange
        string outside = Path.GetFullPath(TestWorkspace.NewTempDir());
        string sln = Path.Combine(outside, "Outside.slnx");
        File.WriteAllText(sln, "");

        // Act
        string? registered = this._workspaces.RegisterExistingSolution(sln);

        // Assert
        registered.Should().Be(sln);
        this._workspaces.KnownSolutions.Should().Contain(sln);
        this._workspaces.RegisterExistingSolution(Path.Combine(outside, "Missing.sln")).Should().BeNull();
        this._workspaces.RegisterExistingSolution(Path.Combine(outside, "Outside.csproj")).Should().BeNull();
        this._workspaces.RegisterExistingSolution("Outside").Should().BeNull();
    }

    [Fact]
    public async Task LoadSolutionAsync_MissingPath_AnswersUnknownSolution()
    {
        // Arrange
        QueryService query = this.CreateQuery();
        string missing = Path.Combine(this._root, "Nope", "Nope.sln");

        // Act
        string answer = await query.LoadSolutionAsync(missing, CancellationToken.None);

        // Assert
        answer.Should().StartWith("Unknown solution: " + missing);
        this._workspaces.KnownSolutions.Should().BeEmpty();
    }

    [Fact]
    public async Task ForgetSolutionAsync_RunsOnTheJobConsumer_AndAnswersWithNamePathAndRemovedProjectCount()
    {
        // Arrange: the test plays the hosted service's consumer for ForgetSolutionJob
        string a = this.MakeSolution("Acme.Orders");
        this._workspaces.RegisterSolutions([a]);
        this._index.EnsureProject(Path.Combine(this._root, "Acme.Orders", "Acme.Orders.csproj"), "Acme.Orders", "Acme.Orders", a);
        IndexJobQueue jobs = new();
        QueryService query = this.CreateQuery(jobs);
        using CancellationTokenSource stop = new();
        List<IndexJob> seen = [];
        Task consumer = Task.Run(async () =>
        {
            await foreach (QueuedJob queued in jobs.ReadAllAsync(stop.Token))
            {
                seen.Add(queued.Job);
                if (queued.Job is ForgetSolutionJob forget)
                {
                    forget.Result.TrySetResult(this._workspaces.Forget(forget.Hint));
                }

                jobs.MarkCompleted(queued);
            }
        });

        // Act
        string first = await query.ForgetSolutionAsync("Acme.Orders", CancellationToken.None);
        string second = await query.ForgetSolutionAsync("Acme.Orders", CancellationToken.None);
        stop.Cancel();

        // Assert
        first.Should().Be($"Forgot Acme.Orders ({a}): 1 projects removed from the index.");
        second.Should().StartWith("Unknown solution: Acme.Orders");
        seen.Should().AllBeOfType<ForgetSolutionJob>().And.HaveCount(2);
        await consumer.Awaiting(c => c).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ForgetSolutionAsync_AFragmentMatchingSeveralSolutions_ForgetsNothing_AndAnswersWithTheCandidates()
    {
        // Arrange
        string orders = this.MakeSolution("Acme.Orders");
        string billing = this.MakeSolution("Acme.Billing");
        this._workspaces.RegisterSolutions([orders, billing]);
        IndexJobQueue jobs = new();
        QueryService query = this.CreateQuery(jobs);

        // Act
        string answer = await query.ForgetSolutionAsync("Acme", CancellationToken.None);

        // Assert
        IReadOnlyList<string> known = this._workspaces.KnownSolutions;
        answer.Should().StartWith("Ambiguous solution: Acme matches ").And.EndWith(". Use the full path or the exact name.");
        answer.Should().Contain("Acme.Orders (" + orders + ")").And.Contain("Acme.Billing (" + billing + ")");
        jobs.Pending.Should().Be(0);
        known.Should().BeEquivalentTo([orders, billing]);
    }

    [Fact]
    public async Task ForgetSolutionAsync_AFragmentMatchingOneSolution_AndAnExactNameOfDifferentCase_ForgetOnlyThatSolution()
    {
        // Arrange
        string orders = this.MakeSolution("Acme.Orders");
        string tests = this.MakeSolution("Acme.Orders.Tests");
        string billing = this.MakeSolution("Acme.Billing");
        this._workspaces.RegisterSolutions([orders, tests, billing]);
        IndexJobQueue jobs = new();
        QueryService query = this.CreateQuery(jobs);
        using CancellationTokenSource stop = new();
        Task consumer = Task.Run(async () =>
        {
            await foreach (QueuedJob queued in jobs.ReadAllAsync(stop.Token))
            {
                if (queued.Job is ForgetSolutionJob forget)
                {
                    forget.Result.TrySetResult(this._workspaces.Forget(forget.Hint));
                }

                jobs.MarkCompleted(queued);
            }
        });

        // Act
        string byFragment = await query.ForgetSolutionAsync("Billing", CancellationToken.None);
        string byExactName = await query.ForgetSolutionAsync("acme.orders", CancellationToken.None);
        stop.Cancel();

        // Assert
        byFragment.Should().Be($"Forgot Acme.Billing ({billing}): 0 projects removed from the index.");
        byExactName.Should().Be($"Forgot Acme.Orders ({orders}): 0 projects removed from the index.");
        this._workspaces.KnownSolutions.Should().BeEquivalentTo([tests]);
        await consumer.Awaiting(c => c).Should().ThrowAsync<OperationCanceledException>();
    }

    // ---------------------------------------------------------------- re-discovery, .sln wins

    [Fact]
    public void Rediscover_RegistersNewSolutionsWithoutAWatcher_ButNotOnesForgottenOnPurpose()
    {
        // Arrange
        string fresh = this.MakeSolution("Fresh");
        string kept = this.MakeSolution("Kept");
        this._workspaces.RegisterSolutions([kept]);
        this._workspaces.Forget(kept).Should().NotBeNull();

        // Act
        IReadOnlyList<string> added = this._workspaces.Rediscover([this._root]);

        // Assert
        added.Should().BeEquivalentTo([fresh]);
        this._workspaces.KnownSolutions.Should().BeEquivalentTo([fresh]);
        this._workspaces.Rediscover([this._root]).Should().BeEmpty();
    }

    [Fact]
    public void RegisterAppeared_SlnNextToKnownSameNameSlnx_ReplacesTheSlnx()
    {
        // Arrange
        string dir = Path.Combine(this._root, "repo");
        Directory.CreateDirectory(dir);
        string slnx = Path.Combine(dir, "Acme.Orders.slnx");
        string sln = Path.Combine(dir, "Acme.Orders.sln");
        File.WriteAllText(slnx, "");
        this._workspaces.RegisterSolutions([slnx]);
        this._index.EnsureProject(Path.Combine(dir, "Acme.Orders.csproj"), "Acme.Orders", "Acme.Orders", slnx);
        File.WriteAllText(sln, "");

        // Act
        IReadOnlyList<string> added = this._workspaces.RegisterAppeared([sln], [sln], [this._root]);

        // Assert
        added.Should().BeEquivalentTo([sln]);
        this._workspaces.KnownSolutions.Should().BeEquivalentTo([sln]);
        this._index.GetProjects().Should().BeEmpty("the forgotten .slnx lost its index rows");
        this._workspaces.Rediscover([this._root]).Should().BeEmpty("the .slnx stays out while the .sln exists");
    }

    [Fact]
    public void RegisterAppearedIn_DirectoryWithSolutions_HonoursDepthAndSkippedFolders()
    {
        // Arrange
        string dir = Path.Combine(this._root, "copy");
        string top = Path.Combine(dir, "A.sln");
        string deep = Path.Combine(dir, "a", "b", "c", "d", "e", "B.sln");
        string tooDeep = Path.Combine(dir, "a", "b", "c", "d", "e", "f", "C.sln");
        string inBin = Path.Combine(dir, "bin", "D.sln");
        foreach (string p in new[] { top, deep, tooDeep, inBin })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "");
        }

        // Act: the copy dir is one level below the root, B.sln at depth 6 (kept), C.sln at 7 (dropped)
        IReadOnlyList<string> added = this._workspaces.RegisterAppearedIn([dir, Path.Combine(dir, "a")], [this._root]);

        // Assert
        added.Should().BeEquivalentTo([top, deep]);
        this._workspaces.RegisterAppearedIn([Path.Combine(this._root, "..", "elsewhere")], [this._root]).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- real watcher

    [Fact]
    public async Task Watcher_NewSolutionFolderBecomesKnown_AndDeletingItForgetsIt()
    {
        // Arrange: the same calls the hosted service makes per batch
        string[] roots = [this._root];
        using RepoWatcher watcher = new(
            batch =>
            {
                this._workspaces.ForgetMissing();
                this._workspaces.RegisterAppearedIn(batch.CreatedDirectories ?? [], roots);
                this._workspaces.RegisterAppeared(batch.Paths, batch.CreatedPaths, roots);
            },
            TimeSpan.FromMilliseconds(100),
            NullLogger.Instance);
        watcher.Watch(this._root);

        // Act: a task worktree appears
        string dir = Path.Combine(this._root, "worktree-1");
        Directory.CreateDirectory(dir);
        string sln = Path.Combine(dir, "Acme.Orders.sln");
        File.WriteAllText(sln, "");

        // Assert
        (await WaitUntilAsync(() => this._workspaces.IsKnown(sln))).Should().BeTrue("the new solution is registered from the watcher batch");

        // Act: the worktree is deleted; the folder delete may raise no per-file event, so the next batch (or the sweep) notices
        Directory.Delete(dir, recursive: true);
        string trigger = Path.Combine(this._root, "poke.sln");
        File.WriteAllText(trigger, "");
        File.Delete(trigger);

        // Assert
        (await WaitUntilAsync(() => !this._workspaces.IsKnown(sln))).Should().BeTrue("the vanished solution is forgotten");
    }

    [Fact]
    public async Task Watcher_FolderMovedIntoTheRoot_IsScannedAndItsSolutionBecomesKnown()
    {
        // Arrange: a repository copy built outside the root
        string[] roots = [this._root];
        string outside = Path.GetFullPath(TestWorkspace.NewTempDir());
        string built = Path.Combine(outside, "built");
        Directory.CreateDirectory(Path.Combine(built, "src"));
        File.WriteAllText(Path.Combine(built, "src", "Moved.sln"), "");
        using RepoWatcher watcher = new(
            batch => this._workspaces.RegisterAppearedIn(batch.CreatedDirectories ?? [], roots),
            TimeSpan.FromMilliseconds(100),
            NullLogger.Instance);
        watcher.Watch(this._root);

        // Act: the move raises a directory event only
        string target = Path.Combine(this._root, "worktree-2");
        Directory.Move(built, target);

        // Assert
        string sln = Path.Combine(target, "src", "Moved.sln");
        (await WaitUntilAsync(() => this._workspaces.IsKnown(sln))).Should().BeTrue("the moved folder is scanned for solutions");
    }

    // ---------------------------------------------------------------- helpers

    private string MakeSolution(string name)
    {
        string dir = Path.Combine(this._root, name);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name + ".sln");
        File.WriteAllText(path, "");
        return path;
    }

    private QueryService CreateQuery(IndexJobQueue? jobs = null)
    {
        IOptions<BalalaikaOptions> options = Options.Create(new BalalaikaOptions { Roots = [this._root] });
        SourceFilter filter = new([]);
        return new QueryService(
            this._workspaces,
            new SymbolResolver(this._workspaces, this._index, options),
            this._index,
            jobs ?? new IndexJobQueue(),
            new ResultPager(),
            new WarmUpTracker(),
            this._pending,
            options,
            filter,
            new GitRepository(options, NullLogger<GitRepository>.Instance),
            NullLogger<QueryService>.Instance);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }
}
