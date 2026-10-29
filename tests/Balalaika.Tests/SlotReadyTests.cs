using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Balalaika.Index;
using Balalaika.Server;
using Balalaika.Watcher;
using Balalaika.Workspace;

namespace Balalaika.Tests;

/// <summary>Skips a test with a clear reason when no <c>git</c> executable can be started.</summary>
public sealed class GitFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(10_000);
            return p is { ExitCode: 0 };
        }
        catch (Exception)
        {
            return false;
        }
    });

    public GitFactAttribute()
    {
        if (!Available.Value)
        {
            this.Skip = "the git executable was not found on PATH; this test runs the real git";
        }
    }
}

/// <summary>
/// Branch switches in a linked git worktree, project reloads on git and overflow batches, the HEAD recorded per
/// solution and the readiness line of <c>status</c>. The hosted service, watcher and index are real; only the MSBuild
/// load is replaced (<see cref="WorkspaceManager.Loader"/>) by a project built from the <c>.cs</c> files on disk.
/// </summary>
public sealed class SlotReadyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // ---------------------------------------------------------------- worktree + real git

    [GitFact]
    public async Task Worktree_RecordsHeadAtStartup_AndFollowsABranchSwitchIntoTheIndex()
    {
        // Arrange: repo r (HEAD detached so the worktree can have main) and the linked worktree slot
        await using Host host = new(warmUp: true);
        host.CreateRepoWithWorktree();
        await host.StartAsync();

        // Assert: startup warm-up indexed the slot and recorded its HEAD
        string slotHead = host.Git(host.Slot, "rev-parse", "HEAD").Trim();
        (await WaitUntilAsync(() => host.IsReadyWithHead(host.SlotSolution, slotHead))).Should().BeTrue(host.Status());
        host.Git(host.Slot, "rev-parse", "--short=7", "HEAD").Trim().Should().Be(host.HeadShownFor(host.SlotSolution));

        // Act: b2 gets a new public type, then the slot goes main -> b2
        host.Git(host.Slot, "checkout", "-b", "b2");
        File.WriteAllText(Path.Combine(host.Slot, "B.cs"), "namespace Acme.Orders; public class OrderB { }");
        host.Git(host.Slot, "add", "B.cs");
        host.Git(host.Slot, "commit", "-m", "add B");
        string b2 = host.Git(host.Slot, "rev-parse", "HEAD").Trim();
        host.Git(host.Slot, "checkout", "main");
        host.Git(host.Slot, "checkout", "b2");

        // Assert
        (await WaitUntilAsync(() => host.IsReadyWithHead(host.SlotSolution, b2) && host.IndexHasType("OrderB", host.SlotSolution))).Should().BeTrue(host.Status());
        host.Status().Should().Contain("Ready: yes");
    }

    [GitFact]
    public async Task Worktree_ABranchSwitchThatOnlyChangesAReadme_StillUpdatesTheRecordedHead()
    {
        // Arrange
        await using Host host = new(warmUp: true);
        host.CreateRepoWithWorktree();
        await host.StartAsync();
        string main = host.Git(host.Slot, "rev-parse", "HEAD").Trim();
        (await WaitUntilAsync(() => host.IsReadyWithHead(host.SlotSolution, main))).Should().BeTrue(host.Status());

        // Act: a branch that differs from main in README.md only
        host.Git(host.Slot, "checkout", "-b", "docs");
        File.WriteAllText(Path.Combine(host.Slot, "README.md"), "docs");
        host.Git(host.Slot, "add", "README.md");
        host.Git(host.Slot, "commit", "-m", "readme");
        string docs = host.Git(host.Slot, "rev-parse", "HEAD").Trim();
        host.Git(host.Slot, "checkout", "main");
        (await WaitUntilAsync(() => host.IsReadyWithHead(host.SlotSolution, main))).Should().BeTrue(host.Status());
        host.Git(host.Slot, "checkout", "docs");

        // Assert: the index is fresh (no source file differs), the HEAD still moves
        (await WaitUntilAsync(() => host.IsReadyWithHead(host.SlotSolution, docs))).Should().BeTrue(host.Status());
    }

    [GitFact]
    public async Task Worktree_ABranchMovedFromTheMainCheckout_UpdatesTheRecordedHead_AlsoAfterPackRefs()
    {
        // Arrange: the slot has task/x checked out
        await using Host host = new(warmUp: true);
        host.CreateRepoWithWorktree();
        host.Git(host.Slot, "checkout", "-b", "task/x");
        await host.StartAsync();
        string old = host.Git(host.Slot, "rev-parse", "HEAD").Trim();
        (await WaitUntilAsync(() => host.IsReadyWithHead(host.SlotSolution, old))).Should().BeTrue(host.Status());
        string headFile = host.Git(host.Slot, "rev-parse", "--git-path", "HEAD").Trim();
        string headBefore = File.ReadAllText(Path.GetFullPath(headFile, host.Slot));

        // Act: the branch is moved from the main checkout (loose ref)
        string moved = host.CommitSameTree(host.Repo, old);
        host.Git(host.Repo, "update-ref", "refs/heads/task/x", moved, old);

        // Assert: the HEAD file is untouched, the recorded head follows
        File.ReadAllText(Path.GetFullPath(headFile, host.Slot)).Should().Be(headBefore);
        (await WaitUntilAsync(() => host.IsReadyWithHead(host.SlotSolution, moved))).Should().BeTrue(host.Status());

        // Act: the ref is packed (loose file deleted), then moved again
        host.Git(host.Repo, "pack-refs", "--all");
        string moved2 = host.CommitSameTree(host.Repo, moved);
        host.Git(host.Repo, "update-ref", "refs/heads/task/x", moved2, moved);

        // Assert
        (await WaitUntilAsync(() => host.IsReadyWithHead(host.SlotSolution, moved2))).Should().BeTrue(host.Status());
    }

    [GitFact]
    public async Task Worktree_ConcurrentRepoints_NeverLeaveMoreThanThreeWatchersPerRoot()
    {
        // Arrange
        await using Host host = new(warmUp: false);
        host.CreateRepoWithWorktree();
        host.Git(host.Slot, "checkout", "-b", "task/b");
        host.Git(host.Slot, "checkout", "-b", "task/a");
        using RepoWatcher watcher = new(_ => { }, TimeSpan.FromMilliseconds(100), NullLogger.Instance);
        watcher.WatchWorktreeHead(host.Slot).Should().BeTrue();

        // Act: after each branch switch many overlapping re-points race, as the HEAD and ref watchers do for one checkout
        for (int i = 0; i < 6; i++)
        {
            host.Git(host.Slot, "checkout", i % 2 == 0 ? "task/b" : "task/a");
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => watcher.RepointForTests(host.Slot))));

            // Assert: HEAD + loose ref + packed-refs at most
            watcher.WatcherCountForTests.Should().BeLessThanOrEqualTo(3);
        }

        // Act: a re-point after Dispose creates nothing
        watcher.Dispose();
        watcher.RepointForTests(host.Slot);

        // Assert
        watcher.WatcherCountForTests.Should().Be(0);
    }

    [GitFact]
    public async Task Worktree_UnwatchedHead_ReleasesAllItsWatchers_AndCanBeWatchedAgain_ButNotAfterDispose()
    {
        // Arrange
        await using Host host = new(warmUp: false);
        host.CreateRepoWithWorktree();
        using RepoWatcher watcher = new(_ => { }, TimeSpan.FromMilliseconds(100), NullLogger.Instance);
        watcher.WatchWorktreeHead(host.Slot).Should().BeTrue();
        watcher.WatcherCountForTests.Should().Be(3);

        // Act + Assert
        watcher.UnwatchWorktreeHead(host.Slot).Should().BeTrue();
        watcher.WatcherCountForTests.Should().Be(0);
        watcher.UnwatchWorktreeHead(host.Slot).Should().BeFalse("nothing is watched any more");
        watcher.WatchWorktreeHead(host.Slot).Should().BeTrue();
        watcher.WatcherCountForTests.Should().Be(3);

        // Act: a watch after Dispose creates nothing
        watcher.Dispose();
        watcher.WatchWorktreeHead(host.Slot).Should().BeFalse();
        watcher.WatcherCountForTests.Should().Be(0);
    }

    [GitFact]
    public async Task Worktree_GitDirectoryDeletedUnderTheLiveWatcher_ReleasesTheWatchers_WithoutAWarning()
    {
        // Arrange
        await using Host host = new(warmUp: false);
        host.CreateRepoWithWorktree();
        CapturingLogger logger = new();
        using RepoWatcher watcher = new(_ => { }, TimeSpan.FromMilliseconds(100), logger);
        watcher.WatchWorktreeHead(host.Slot).Should().BeTrue();
        watcher.WatcherCountForTests.Should().Be(3);

        // Act: what `git worktree remove` does to the directory the HEAD watcher sits on
        Directory.Delete(Path.Combine(host.Repo, ".git", "worktrees", "slot"), recursive: true);

        // Assert
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (watcher.WatcherCountForTests != 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        watcher.WatcherCountForTests.Should().Be(0);
        logger.Warnings.Should().BeEmpty();
    }

    [GitFact]
    public async Task Worktree_Removed_ReleasesItsWatchers_AndAReplacementAtTheSamePathIsWatched()
    {
        // Arrange
        await using Host host = new(warmUp: true);
        host.CreateRepoWithWorktree();
        await host.StartAsync();
        string old = host.Git(host.Slot, "rev-parse", "HEAD").Trim();
        (await WaitUntilAsync(() => host.IsReadyWithHead(host.SlotSolution, old))).Should().BeTrue(host.Status());
        int watching = host.WatcherCount;

        // Act: the worktree is removed
        host.Git(host.Repo, "worktree", "remove", "--force", host.Slot);

        // Assert: forgotten, and HEAD + loose ref + packed-refs watchers of the slot are gone
        (await WaitUntilAsync(() => !host.Status().Contains(host.SlotSolution, StringComparison.OrdinalIgnoreCase))).Should().BeTrue(host.Status());
        (await WaitUntilAsync(() => host.WatcherCount == watching - 3)).Should().BeTrue($"watchers: {host.WatcherCount}, before removal: {watching}");

        // Act: a worktree appears at the same path and moves to a new commit
        host.Git(host.Repo, "worktree", "add", host.Slot, "main");
        string moved = host.CommitSameTree(host.Repo, old);
        host.Git(host.Slot, "checkout", "--detach", moved);

        // Assert
        (await WaitUntilAsync(() => host.IsReadyWithHead(host.SlotSolution, moved))).Should().BeTrue(host.Status());
        host.WatcherCount.Should().Be(watching - 3 + 1, "the slot is detached again: only its HEAD watcher, no loose-ref or packed-refs watcher");
    }

    [GitFact]
    public async Task Worktree_UnwatchedAndWatchedAgainWhileTheWatcherIsBeingCreated_LeavesNoOrphanedWatcher()
    {
        // Arrange
        await using Host host = new(warmUp: false);
        host.CreateRepoWithWorktree();
        using RepoWatcher watcher = new(_ => { }, TimeSpan.FromMilliseconds(100), NullLogger.Instance);
        int baseline = watcher.WatcherCountForTests;
        watcher.WatchWorktreeHead(host.Slot).Should().BeTrue();
        int watching = watcher.WatcherCountForTests;
        watcher.UnwatchWorktreeHead(host.Slot).Should().BeTrue();
        watcher.WatcherCountForTests.Should().Be(baseline);

        // Act: an unwatch and a new watch of the same root complete while the first watch is creating its HEAD watcher
        watcher.BeforeHeadPublishForTests = root =>
        {
            watcher.BeforeHeadPublishForTests = null;
            watcher.UnwatchWorktreeHead(root);
            watcher.WatchWorktreeHead(root);
        };
        watcher.WatchWorktreeHead(host.Slot);

        // Assert: only the second registration's watchers are alive, and the unwatch releases all of them
        watcher.WatcherCountForTests.Should().Be(watching);
        watcher.UnwatchWorktreeHead(host.Slot).Should().BeTrue();
        watcher.WatcherCountForTests.Should().Be(baseline);
    }

    [GitFact]
    public async Task Worktree_RemovedAndAddedBackToBack_IsWatchedAgainAndFollowsItsNextCommit()
    {
        // Arrange
        await using Host host = new(warmUp: true);
        host.CreateRepoWithWorktree();
        await host.StartAsync();
        string old = host.Git(host.Slot, "rev-parse", "HEAD").Trim();
        (await WaitUntilAsync(() => host.IsReadyWithHead(host.SlotSolution, old))).Should().BeTrue(host.Status());
        int watching = host.WatcherCount;

        // Act: removed and re-created before the watcher batch is processed
        host.Git(host.Repo, "worktree", "remove", "--force", host.Slot);
        host.Git(host.Repo, "worktree", "add", host.Slot, "main");

        // Assert: its HEAD, loose-ref and packed-refs watchers are back
        (await WaitUntilAsync(() => host.WatcherCount == watching)).Should().BeTrue($"watchers: {host.WatcherCount}, before removal: {watching}");

        // Act: a commit in the re-created worktree
        File.WriteAllText(Path.Combine(host.Slot, "extra.txt"), "x");
        host.Git(host.Slot, "add", ".");
        host.Git(host.Slot, "commit", "-m", "next");
        string next = host.Git(host.Slot, "rev-parse", "HEAD").Trim();

        // Assert
        (await WaitUntilAsync(() => host.IsReadyWithHead(host.SlotSolution, next))).Should().BeTrue(host.Status());
    }

    [GitFact]
    public async Task NormalRepository_ABranchMovedByUpdateRef_UpdatesTheRecordedHead()
    {
        // Arrange: main checked out in the only checkout
        await using Host host = new(warmUp: true);
        host.CreateRepo();
        await host.StartAsync();
        string solution = Path.Combine(host.Repo, "Demo.slnx");
        string old = host.Git(host.Repo, "rev-parse", "HEAD").Trim();
        (await WaitUntilAsync(() => host.IsReadyWithHead(solution, old))).Should().BeTrue(host.Status());

        // Act: the branch moves without a checkout
        string moved = host.CommitSameTree(host.Repo, old);
        host.Git(host.Repo, "update-ref", "refs/heads/main", moved, old);

        // Assert
        (await WaitUntilAsync(() => host.IsReadyWithHead(solution, moved))).Should().BeTrue(host.Status());
    }

    [GitFact]
    public async Task Worktree_AfterABranchSwitch_TheNewBranchIsFollowed()
    {
        // Arrange
        await using Host host = new(warmUp: true);
        host.CreateRepoWithWorktree();
        await host.StartAsync();
        string old = host.Git(host.Slot, "rev-parse", "HEAD").Trim();
        (await WaitUntilAsync(() => host.IsReadyWithHead(host.SlotSolution, old))).Should().BeTrue(host.Status());

        // Act: HEAD now names "other" (the watchers are re-pointed), then "other" is moved from the main checkout
        host.Git(host.Slot, "checkout", "-b", "other");
        await Task.Delay(500);
        string moved = host.CommitSameTree(host.Repo, old);
        host.Git(host.Repo, "update-ref", "refs/heads/other", moved, old);

        // Assert
        (await WaitUntilAsync(() => host.IsReadyWithHead(host.SlotSolution, moved))).Should().BeTrue(host.Status());
    }

    [GitFact]
    public async Task Worktree_ABranchWhoseRefDirectoryDoesNotExistYet_IsFollowedOnceItAppears()
    {
        // Arrange: the slot is on task/y; packing the refs removes the loose file and with it the refs/heads/task directory
        await using Host host = new(warmUp: true);
        host.CreateRepoWithWorktree();
        host.Git(host.Slot, "checkout", "-b", "task/y");
        host.Git(host.Repo, "pack-refs", "--all");
        await host.StartAsync();
        string old = host.Git(host.Slot, "rev-parse", "HEAD").Trim();
        (await WaitUntilAsync(() => host.IsReadyWithHead(host.SlotSolution, old))).Should().BeTrue(host.Status());

        // Act
        string moved = host.CommitSameTree(host.Repo, old);
        host.Git(host.Repo, "update-ref", "refs/heads/task/y", moved, old);

        // Assert
        (await WaitUntilAsync(() => host.IsReadyWithHead(host.SlotSolution, moved))).Should().BeTrue(host.Status());
    }

    [GitFact]
    public async Task Worktree_GitEventForAnUnloadedSolution_RefreshesItAtOnceWithoutTheInterval()
    {
        // Arrange: no warm-up and an hour-long refresh interval, so only the git event can index the slot
        await using Host host = new(warmUp: false, unloadedRefreshSeconds: 3600);
        host.CreateRepoWithWorktree();
        await host.StartAsync();
        host.HeadShownFor(host.SlotSolution).Should().Be("-");
        host.Workspaces.TryGetLoaded(host.SlotSolution, out _).Should().BeFalse();

        // Act: HEAD of the worktree changes (a new branch at the same commit)
        string main = host.Git(host.Slot, "rev-parse", "HEAD").Trim();
        host.Git(host.Slot, "checkout", "-b", "other");

        // Assert
        (await WaitUntilAsync(() => host.IsReadyWithHead(host.SlotSolution, main))).Should().BeTrue(host.Status());
        host.Workspaces.TryGetLoaded(host.SlotSolution, out _).Should().BeFalse("the refresh evicts the solution again");
    }

    [Fact]
    public void WorktreeHead_ParsesAbsoluteAndRelativeGitdir_AndIgnoresANormalRepository()
    {
        // Arrange
        string dir = TestWorkspace.NewTempDir();
        string absolute = Path.Combine(dir, "abs");
        string relative = Path.Combine(dir, "rel");
        string normal = Path.Combine(dir, "normal");
        Directory.CreateDirectory(absolute);
        Directory.CreateDirectory(relative);
        Directory.CreateDirectory(Path.Combine(normal, ".git"));
        File.WriteAllText(Path.Combine(absolute, ".git"), "gitdir: " + Path.Combine(dir, "common", ".git", "worktrees", "abs").Replace('\\', '/') + "\n");
        File.WriteAllText(Path.Combine(relative, ".git"), "gitdir: ../common/.git/worktrees/rel\n");

        // Act + Assert
        WorktreeHead.Find(absolute).Should().Be(Path.Combine(dir, "common", ".git", "worktrees", "abs", "HEAD"));
        WorktreeHead.Find(relative).Should().Be(Path.Combine(dir, "common", ".git", "worktrees", "rel", "HEAD"));
        WorktreeHead.Find(normal).Should().BeNull();
        WorktreeHead.Find(Path.Combine(dir, "missing")).Should().BeNull();
    }

    [Fact]
    public void WorktreeHead_Resolve_ForARepositoryFolderNamedWorktrees_KeepsTheCommonDirInsideTheRepository()
    {
        // Arrange
        string root = Path.Combine(TestWorkspace.NewTempDir(), "worktrees");
        string gitDir = Path.Combine(root, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n");

        // Act
        GitHeadFiles? files = WorktreeHead.Resolve(root);

        // Assert
        files.Should().NotBeNull();
        files!.CommonDir.Should().Be(gitDir);
        files.LooseRef.Should().Be(Path.Combine(gitDir, "refs", "heads", "main"));
    }

    // ---------------------------------------------------------------- project reload

    [Fact]
    public async Task Overflow_ReloadsOnlyTheLoadedSolutionWhoseProjectFileChanged()
    {
        // Arrange
        await using Host host = new(warmUp: false);
        string a = host.CreatePlainSolution("SolA");
        string b = host.CreatePlainSolution("SolB");
        await host.StartAsync();
        await host.LoadAsync(a, b);
        host.LoadCount(a).Should().Be(1);

        // Act: a new Compile item in SolA's project, then the watcher loses events
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(a)!, "Demo.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"X.cs\" /></ItemGroup></Project>");
        await host.Jobs.Enqueue(new FullReconcileJob()).WaitAsync(Timeout);

        // Assert
        host.LoadCount(a).Should().Be(2, "its project file changed since the load");
        host.LoadCount(b).Should().Be(1, "nothing it is built from changed; it is only reconciled");
        host.Workspaces.TryGetLoaded(a, out _).Should().BeTrue();
    }

    [Fact]
    public async Task Overflow_ANewPropsFileBesideTheProject_ReloadsTheSolution()
    {
        // Arrange
        await using Host host = new(warmUp: false);
        string a = host.CreatePlainSolution("SolA");
        await host.StartAsync();
        await host.LoadAsync(a);

        // Act
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(a)!, "Extra.props"), "<Project />");
        await host.Jobs.Enqueue(new FullReconcileJob()).WaitAsync(Timeout);

        // Assert
        host.LoadCount(a).Should().Be(2);
    }

    [Fact]
    public async Task GitBatch_ThatAlsoCarriesAProjectFile_ReloadsTheSolution()
    {
        // Arrange
        await using Host host = new(warmUp: false);
        string a = host.CreatePlainSolution("SolA");
        string b = host.CreatePlainSolution("SolB");
        await host.StartAsync();
        await host.LoadAsync(a, b);
        string csproj = Path.Combine(Path.GetDirectoryName(a)!, "Demo.csproj");

        // The edit keeps size and timestamp, so the load-time comparison can not see it: only the batch's own project event can.
        FileInfo info = new(csproj);
        DateTime stamp = info.LastWriteTimeUtc;
        File.WriteAllText(csproj, File.ReadAllText(csproj).Replace("Microsoft", "Microsofx"));
        File.SetLastWriteTimeUtc(csproj, stamp);

        // Act
        string head = Path.Combine(Path.GetDirectoryName(a)!, ".git", "HEAD");
        await host.Jobs.Enqueue(new FileChangesJob([head, csproj])).WaitAsync(Timeout);

        // Assert
        host.LoadCount(a).Should().Be(2, "the project event of the git batch is not dropped");
        host.LoadCount(b).Should().Be(1);
    }

    // ---------------------------------------------------------------- readiness

    [Fact]
    public async Task Status_WhileTheWatcherDebouncesPaths_ShowsBufferedEventsThenReady()
    {
        // Arrange
        string root = Path.GetFullPath(TestWorkspace.NewTempDir());
        IndexActivity activity = new();
        using Fixture fixture = new(activity);
        using RepoWatcher watcher = new(_ => { }, TimeSpan.FromSeconds(1), NullLogger.Instance, null, activity);
        watcher.Watch(root);
        fixture.Query.Status().Should().Contain("Ready: yes");

        // Act
        File.WriteAllText(Path.Combine(root, "Held.cs"), "class Held { }");

        // Assert: held back first, released after the quiet period
        (await WaitUntilAsync(() => activity.Buffered >= 1)).Should().BeTrue();
        Regex.IsMatch(fixture.Query.Status(), @"Ready: no \(0 jobs, [1-9]\d* buffered\)").Should().BeTrue(fixture.Query.Status());
        (await WaitUntilAsync(() => activity.Buffered == 0)).Should().BeTrue();
        fixture.Query.Status().Should().Contain("Ready: yes");
    }

    [Fact]
    public void Status_WithQueuedJobsAndAnUnrefreshedSolution_CountsThemAsJobs()
    {
        // Arrange
        IndexActivity activity = new();
        using Fixture fixture = new(activity);
        fixture.Jobs.Enqueue(new ForgetMissingJob());
        activity.RefreshStarted();

        // Act
        string status = fixture.Query.Status();

        // Assert
        status.Should().Contain("Ready: no (2 jobs, 0 buffered)");
        status.Split('\n').ToList().IndexOf(status.Split('\n').First(l => l.StartsWith("Ready:")))
            .Should().Be(status.Split('\n').ToList().FindIndex(l => l.StartsWith("Jobs:")) + 1, "the Ready line follows the Jobs line");
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return condition();
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<string> _warnings = new();

        public IReadOnlyCollection<string> Warnings => this._warnings;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                this._warnings.Enqueue(formatter(state, exception));
            }
        }
    }

    /// <summary>Just enough of the server for <see cref="QueryService.Status"/>.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly IndexStore _index = new(Path.Combine(TestWorkspace.NewTempDir(), "index.db"));
        private readonly WorkspaceManager _workspaces;

        public Fixture(IndexActivity activity)
        {
            IOptions<BalalaikaOptions> options = Options.Create(new BalalaikaOptions());
            PendingRefreshes pending = new();
            this._workspaces = new WorkspaceManager(this._index, pending, options, NullLogger<WorkspaceManager>.Instance);
            this.Jobs = new IndexJobQueue();
            this.Query = new QueryService(
                this._workspaces,
                new SymbolResolver(this._workspaces, this._index, options),
                this._index,
                this.Jobs,
                new ResultPager(),
                new WarmUpTracker(),
                pending,
                options,
                new SourceFilter([]),
                new GitRepository(options, NullLogger<GitRepository>.Instance),
                NullLogger<QueryService>.Instance,
                activity);
        }

        public IndexJobQueue Jobs { get; }

        public QueryService Query { get; }

        public void Dispose()
        {
            this._workspaces.Dispose();
            this._index.Dispose();
        }
    }

    /// <summary>The real hosted service over a temp root, with the MSBuild load replaced by a project built from disk.</summary>
    private sealed class Host : IAsyncDisposable
    {
        private readonly string _state = Path.GetFullPath(TestWorkspace.NewTempDir());
        private readonly ConcurrentDictionary<string, int> _loads = new(StringComparer.OrdinalIgnoreCase);
        private readonly IndexStore _index;
        private readonly IndexingHostedService _service;
        private readonly QueryService _query;
        private readonly CancellationTokenSource _stop = new();

        public Host(bool warmUp, int unloadedRefreshSeconds = 0)
        {
            this.Root = Path.GetFullPath(TestWorkspace.NewTempDir());
            BalalaikaOptions opts = new()
            {
                Roots = [this.Root],
                WarmUp = warmUp,
                UnloadedRefreshSeconds = unloadedRefreshSeconds,
                DiscoverySweepSeconds = 0,
                DebounceMilliseconds = 100,
                RestoreOnProjectChange = false,
            };
            IOptions<BalalaikaOptions> options = Options.Create(opts);
            PendingRefreshes pending = new();
            IndexActivity activity = new();
            this._index = new IndexStore(Path.Combine(this._state, "index.db"));
            this.Workspaces = new WorkspaceManager(this._index, pending, options, NullLogger<WorkspaceManager>.Instance) { Loader = this.LoadFromDisk };
            this.Jobs = new IndexJobQueue();
            SourceFilter filter = new([]);
            GitRepository git = new(options, NullLogger<GitRepository>.Instance);
            WarmUpTracker warm = new();
            this._service = new IndexingHostedService(
                this.Workspaces,
                new SolutionIndexer(this._index, NullLogger<SolutionIndexer>.Instance),
                this._index,
                this.Jobs,
                warm,
                pending,
                activity,
                options,
                filter,
                git,
                NullLogger<IndexingHostedService>.Instance);
            this._query = new QueryService(
                this.Workspaces,
                new SymbolResolver(this.Workspaces, this._index, options),
                this._index,
                this.Jobs,
                new ResultPager(),
                warm,
                pending,
                options,
                filter,
                git,
                NullLogger<QueryService>.Instance,
                activity);
        }

        public string Root { get; }

        public string Repo => Path.Combine(this.Root, "r");

        public string Slot => Path.Combine(this.Root, "slot");

        public string SlotSolution => Path.Combine(this.Slot, "Demo.slnx");

        public WorkspaceManager Workspaces { get; }

        public IndexJobQueue Jobs { get; }

        public int WatcherCount => this._service.WatcherCountForTests;

        public int LoadCount(string solution) => this._loads.GetValueOrDefault(solution);

        public string Status() => this._query.Status();

        /// <summary>Starts the service and waits until it has discovered the solutions under the root (it does so after probing git).</summary>
        public async Task StartAsync()
        {
            await this._service.StartAsync(this._stop.Token);
            (await WaitUntilAsync(() => this.Workspaces.KnownSolutions.Count > 0)).Should().BeTrue("discovery registers the solutions");

            // The watchers are set up in the same synchronous stretch that registers the solutions; this lets that stretch finish.
            await Task.Delay(300);
        }

        /// <summary>Loads the solutions and waits until the index jobs the loads queued are done.</summary>
        public async Task LoadAsync(params string[] solutions)
        {
            foreach (string s in solutions)
            {
                await this.Workspaces.GetOrLoadAsync(s, CancellationToken.None);
            }

            (await WaitUntilAsync(() => this.Status().Contains("Ready: yes") && solutions.All(s => this._index.GetProjects().Any(p => p.SolutionPath.Equals(s, StringComparison.OrdinalIgnoreCase))))).Should().BeTrue(this.Status());
        }

        public string CreatePlainSolution(string name)
        {
            string dir = Path.Combine(this.Root, name);
            Directory.CreateDirectory(dir);
            WriteSolutionFiles(dir);
            return Path.Combine(dir, "Demo.slnx");
        }

        public void CreateRepoWithWorktree()
        {
            Directory.CreateDirectory(this.Repo);
            WriteSolutionFiles(this.Repo);
            this.Git(this.Repo, "init", "-b", "main");
            this.Git(this.Repo, "add", ".");
            this.Git(this.Repo, "commit", "-m", "initial");
            this.Git(this.Repo, "checkout", "--detach");
            this.Git(this.Repo, "worktree", "add", this.Slot, "main");
        }

        /// <summary>A normal repository with <c>main</c> checked out and no worktree.</summary>
        public void CreateRepo()
        {
            Directory.CreateDirectory(this.Repo);
            WriteSolutionFiles(this.Repo);
            this.Git(this.Repo, "init", "-b", "main");
            this.Git(this.Repo, "add", ".");
            this.Git(this.Repo, "commit", "-m", "initial");
        }

        /// <summary>Adds a commit with the tree of <paramref name="old"/> on top of it, as <c>git commit-tree</c> run from the main checkout does.</summary>
        public string CommitSameTree(string dir, string old)
        {
            string tree = this.Git(dir, "rev-parse", old + "^{tree}").Trim();
            return this.Git(dir, "commit-tree", tree, "-p", old, "-m", "m").Trim();
        }

        public string Git(string dir, params string[] args)
        {
            ProcessStartInfo psi = new("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string a in new[] { "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false" }.Concat(args))
            {
                psi.ArgumentList.Add(a);
            }

            using Process p = Process.Start(psi)!;
            Task<string> err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            p.ExitCode.Should().Be(0, "git {0} said: {1}", string.Join(' ', args), err.Result);
            return output;
        }

        public string HeadShownFor(string solution)
        {
            Match m = Regex.Match(this.Status(), @"^\s+\S+.*  head (\S+)  " + Regex.Escape(solution) + "\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : "<no line>";
        }

        public bool IsReadyWithHead(string solution, string fullHead)
        {
            return this.Status().Contains("Ready: yes") && this.HeadShownFor(solution) == fullHead[..7];
        }

        public bool IndexHasType(string name, string solution)
        {
            return this._index.FindSymbolsByName(name).Any(h => h.SolutionPath.Equals(solution, StringComparison.OrdinalIgnoreCase));
        }

        public async ValueTask DisposeAsync()
        {
            await this._stop.CancelAsync();
            try
            {
                await this._service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception)
            {
            }

            this._service.Dispose();
            this.Workspaces.Dispose();
            this._index.Dispose();
        }

        private static void WriteSolutionFiles(string dir)
        {
            File.WriteAllText(Path.Combine(dir, "Demo.slnx"), "<Solution><Project Path=\"Demo.csproj\" /></Solution>");
            File.WriteAllText(Path.Combine(dir, "Demo.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
            File.WriteAllText(Path.Combine(dir, "A.cs"), "namespace Acme.Orders; public class OrderA { }");
        }

        private Task<LoadedSolution> LoadFromDisk(string solutionPath, CancellationToken ct)
        {
            this._loads.AddOrUpdate(solutionPath, 1, (_, n) => n + 1);
            string dir = Path.GetDirectoryName(solutionPath)!;
            (string, string)[] documents = Directory.EnumerateFiles(dir, "*.cs").Select(f => (Path.GetFileName(f), File.ReadAllText(f))).ToArray();
            (AdhocWorkspace workspace, _) = TestWorkspace.CreateProject(dir, "Demo", documents);
            return Task.FromResult(new LoadedSolution(workspace, workspace.CurrentSolution, solutionPath, []));
        }
    }
}
