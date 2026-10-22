using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Options;
using Balalaika.Index;
using Balalaika.Watcher;
using Balalaika.Workspace;

namespace Balalaika.Server;

/// <summary>
/// Startup orchestration and the single consumer of <see cref="IndexJobQueue"/>. All index mutations go through here,
/// so there is never more than one writer racing on the tree table.
/// </summary>
public sealed class IndexingHostedService(
    WorkspaceManager workspaces,
    SolutionIndexer indexer,
    IndexStore index,
    IndexJobQueue jobs,
    WarmUpTracker warmUp,
    PendingRefreshes pendingRefreshes,
    IndexActivity activity,
    IOptions<BalalaikaOptions> options,
    SourceFilter filter,
    IBaseContentProvider git,
    ILogger<IndexingHostedService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, Task> _loadIndexing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<string> _immediateRefreshes = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HashSet<string> _immediateQueued = new(StringComparer.OrdinalIgnoreCase);
    private RepoWatcher? _watcher;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BalalaikaOptions opts = options.Value;

        // Probe git once so a server without it says so at startup and in status, not on the first review_changes call.
        string? gitProblem = await git.ProbeAsync(stoppingToken).ConfigureAwait(false);
        if (gitProblem is not null)
        {
            logger.LogWarning("review_changes is unavailable: {Problem}", gitProblem);
        }
        else
        {
            logger.LogInformation("Git: {Version}", git.Describe());
        }

        IReadOnlyList<string> solutions = SolutionDiscovery.FindSolutions(opts.Roots);
        workspaces.RegisterSolutions(solutions);
        logger.LogInformation("Discovered {Count} solutions under {Roots}", solutions.Count, string.Join(", ", opts.Roots));

        int collected = index.CollectGarbage(TimeSpan.FromDays(Math.Max(1, opts.BlobRetentionDays)));
        if (collected > 0)
        {
            logger.LogInformation("Garbage-collected {Count} unreferenced blobs", collected);
        }

        // Every load indexes the solution once. The job's completion is kept per solution so a caller that needs the
        // index (the warm-up) awaits this job instead of queueing a second, redundant walk behind it.
        workspaces.SolutionLoaded += loaded => this._loadIndexing[loaded.SolutionPath] = jobs.Enqueue(new IndexSolutionJob(loaded.SolutionPath, Force: false));
        workspaces.SolutionForgotten += forgotten => this._loadIndexing.TryRemove(forgotten.Path, out _);

        this._watcher = new RepoWatcher(
            batch => jobs.Enqueue(batch.Overflowed ? new FullReconcileJob() : new FileChangesJob(batch.Paths, batch.CreatedPaths, batch.CreatedDirectories)),
            TimeSpan.FromMilliseconds(Math.Max(100, opts.DebounceMilliseconds)),
            logger,
            opts.IgnoredFolders,
            activity);
        foreach (string root in opts.Roots)
        {
            this._watcher.Watch(Path.GetFullPath(Environment.ExpandEnvironmentVariables(root)));
        }

        this.WatchWorktreeHeads();
        Task immediateRefreshTask = this.RunImmediateRefreshAsync(stoppingToken);

        List<Task> preloads = [];
        HashSet<string> preloaded = new(StringComparer.OrdinalIgnoreCase);
        foreach (string preload in opts.Preload)
        {
            string? path = workspaces.ResolveSolution(preload);
            if (path is null)
            {
                logger.LogWarning("Preload solution not found: {Name}", preload);
                continue;
            }

            workspaces.Pin(path);
            preloaded.Add(path);
            preloads.Add(workspaces.GetOrLoadAsync(path, stoppingToken).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted));
        }

        Task eviction = opts.IdleEvictionMinutes > 0 ? this.RunEvictionAsync(TimeSpan.FromMinutes(opts.IdleEvictionMinutes), stoppingToken) : Task.CompletedTask;
        Task warmUpTask = opts.WarmUp
            ? this.RunWarmUpAsync(preloads, solutions.Where(s => !preloaded.Contains(s)).ToList(), stoppingToken)
            : Task.CompletedTask;
        Task refreshTask = opts.UnloadedRefreshSeconds > 0
            ? this.RunUnloadedRefreshAsync(TimeSpan.FromSeconds(opts.UnloadedRefreshSeconds), TimeSpan.FromSeconds(Math.Max(opts.UnloadedRefreshSeconds, opts.UnloadedRefreshMaxWaitSeconds)), stoppingToken)
            : Task.CompletedTask;
        Task sweepTask = opts.DiscoverySweepSeconds > 0
            ? this.RunDiscoverySweepAsync(TimeSpan.FromSeconds(opts.DiscoverySweepSeconds), stoppingToken)
            : Task.CompletedTask;

        await foreach (QueuedJob queued in jobs.ReadAllAsync(stoppingToken))
        {
            try
            {
                await this.ProcessAsync(queued.Job, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Index job failed: {Job}", queued.Job);
            }
            finally
            {
                jobs.MarkCompleted(queued);
            }
        }

        await Task.WhenAll(eviction, warmUpTask, refreshTask, sweepTask, immediateRefreshTask).ConfigureAwait(false);
    }

    /// <summary>
    /// A linked worktree keeps its <c>HEAD</c> outside the working tree, so no root watcher sees a checkout in it, and
    /// a branch moved from outside (<c>git update-ref</c>) changes only a ref file. Watches <c>HEAD</c>, the current
    /// branch's ref and <c>packed-refs</c> for the repository root of every known solution. Safe to call again: a HEAD
    /// already watched is skipped.
    /// </summary>
    private void WatchWorktreeHeads()
    {
        if (this._watcher is null)
        {
            return;
        }

        foreach (string root in workspaces.KnownSolutions.Select(s => git.FindRepositoryRoot(s)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            this._watcher.WatchWorktreeHead(root);
        }
    }

    /// <summary>The commit HEAD points at for the repository a solution lives in, or null (no repository, no git).</summary>
    private async Task<string?> ReadHeadAsync(string solutionPath, CancellationToken ct)
    {
        string? root = git.FindRepositoryRoot(solutionPath);
        return root is null ? null : await git.GetHeadAsync(root, ct).ConfigureAwait(false);
    }

    private void RecordHead(string solutionPath, string? head)
    {
        if (head is not null)
        {
            workspaces.RecordIndexedHead(solutionPath, head);
        }
    }

    /// <summary>Queues the refresh of unloaded solutions after a git change, to run now instead of at the next interval.</summary>
    private void RefreshUnloadedNow(IEnumerable<string> solutionPaths)
    {
        foreach (string solution in solutionPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (workspaces.TryGetLoaded(solution, out _))
            {
                continue;
            }

            lock (this._immediateQueued)
            {
                if (!this._immediateQueued.Add(solution))
                {
                    continue;
                }
            }

            activity.RefreshStarted();
            this._immediateRefreshes.Writer.TryWrite(solution);
        }
    }

    private async Task RunImmediateRefreshAsync(CancellationToken ct)
    {
        try
        {
            await foreach (string path in this._immediateRefreshes.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // Taken off the queued set before the work starts, so a git change that lands meanwhile queues another pass.
                lock (this._immediateQueued)
                {
                    this._immediateQueued.Remove(path);
                }

                try
                {
                    await this.WarmSolutionAsync(path, "Git refresh", ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Git refresh of {Solution} failed", SolutionDiscovery.SolutionName(path));
                }
                finally
                {
                    activity.RefreshFinished();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private enum WarmOutcome
    {
        AlreadyLoaded,
        Fresh,
        Indexed,
        Forgotten,
    }

    /// <summary>
    /// Looks for known solutions whose file is gone (one <c>File.Exists</c> per known solution) and queues the forgetting
    /// for the consumer, so it can not race a running index job.
    /// </summary>
    private async Task RunDiscoverySweepAsync(TimeSpan every, CancellationToken ct)
    {
        using PeriodicTimer timer = new(every);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (workspaces.HasMissingSolutions())
                {
                    _ = jobs.Enqueue(new ForgetMissingJob());
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Brings an unloaded solution's index up to date: skip if it is loaded (its own load indexes it) or if the index
    /// already matches the disk; otherwise load it, await the <see cref="IndexSolutionJob"/> the load queued, and evict it again.
    /// Shared by the startup warm-up and the watcher-driven refresh.
    /// </summary>
    private async Task<WarmOutcome> WarmSolutionAsync(string path, string reason, CancellationToken ct)
    {
        string name = SolutionDiscovery.SolutionName(path);
        if (!workspaces.IsKnown(path))
        {
            return WarmOutcome.Forgotten;
        }

        if (workspaces.TryGetLoaded(path, out _))
        {
            return WarmOutcome.AlreadyLoaded;
        }

        // Read before the freshness check: a checkout that lands meanwhile moves HEAD again and raises its own git event.
        string? head = await this.ReadHeadAsync(path, ct).ConfigureAwait(false);
        if (IndexFreshness.IsFresh(index, path, filter))
        {
            this.RecordHead(path, head);
            logger.LogInformation("{Reason}: {Solution} index is fresh, not loading", reason, name);
            return WarmOutcome.Fresh;
        }

        DateTime started = DateTime.UtcNow;
        LoadedSolution loaded;
        try
        {
            loaded = await workspaces.GetOrLoadAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested && !workspaces.IsKnown(path))
        {
            return WarmOutcome.Forgotten;
        }

        Task indexed = this._loadIndexing.TryGetValue(loaded.SolutionPath, out Task? queuedByLoad)
            ? queuedByLoad
            : jobs.Enqueue(new IndexSolutionJob(loaded.SolutionPath, Force: false));
        await indexed.WaitAsync(ct).ConfigureAwait(false);
        if (!workspaces.IsKnown(path))
        {
            return WarmOutcome.Forgotten;
        }

        bool evicted = workspaces.Evict(path);
        logger.LogInformation(
            "{Reason}: {Solution} indexed ({Projects} projects) in {Seconds:N1}s{Kept}",
            reason, name, loaded.Solution.ProjectIds.Count, (DateTime.UtcNow - started).TotalSeconds, evicted ? "" : ", kept loaded (pinned)");
        return WarmOutcome.Indexed;
    }

    /// <summary>
    /// Refreshes unloaded solutions whose files changed, once they have been quiet for <paramref name="quiet"/> or
    /// changing for <paramref name="maxWait"/>. Runs one solution at a time; a solution somebody loaded meanwhile is
    /// dropped, because its own load and the normal file-change path cover it.
    /// </summary>
    private async Task RunUnloadedRefreshAsync(TimeSpan quiet, TimeSpan maxWait, CancellationToken ct)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(Math.Clamp(quiet.TotalSeconds / 4, 1, 15)));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                foreach (DueRefresh due in pendingRefreshes.TakeDue(quiet, maxWait, DateTime.UtcNow))
                {
                    string name = SolutionDiscovery.SolutionName(due.SolutionPath);
                    try
                    {
                        logger.LogInformation("Refresh: {Solution} had {Files} files change while unloaded", name, due.Files);
                        WarmOutcome outcome = await this.WarmSolutionAsync(due.SolutionPath, "Refresh", ct).ConfigureAwait(false);
                        if (outcome == WarmOutcome.AlreadyLoaded)
                        {
                            logger.LogInformation("Refresh: {Solution} is loaded now; its own load covered the changes", name);
                        }

                        pendingRefreshes.MarkCompleted();
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Refresh of {Solution} failed", name);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Marks the unloaded solutions a change belongs to for a later refresh. Loaded solutions are handled directly by the caller.</summary>
    private void RecordUnloadedChanges(IEnumerable<string> solutionPaths, IEnumerable<string> changedPaths)
    {
        DateTime now = DateTime.UtcNow;
        List<string> paths = changedPaths.ToList();
        foreach (string solution in solutionPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!workspaces.TryGetLoaded(solution, out _))
            {
                pendingRefreshes.Record(solution, paths, now);
            }
        }
    }

    /// <summary>Known solutions that live inside the repository whose <c>.git/HEAD</c> changed.</summary>
    private IEnumerable<string> SolutionsUnderRepository(string headPath)
    {
        string? gitDir = Path.GetDirectoryName(headPath);
        string? repoRoot = gitDir is null ? null : Path.GetDirectoryName(gitDir);
        if (repoRoot is null)
        {
            return [];
        }

        string prefix = repoRoot + Path.DirectorySeparatorChar;
        return workspaces.KnownSolutions.Where(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Once the preloaded solutions are up, loads every other discovered solution one at a time, waits for its index
    /// job, and evicts it again, so index results exist for solutions nobody has opened. A solution whose index already
    /// matches the disk is skipped without loading. Memory therefore peaks at the preloads plus one extra solution.
    /// </summary>
    private async Task RunWarmUpAsync(IReadOnlyList<Task> preloads, IReadOnlyList<string> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(preloads).ConfigureAwait(false);
        }
        catch
        {
            // Preload failures are logged by WorkspaceManager; warm-up still runs.
        }

        warmUp.Start(candidates.Count);
        logger.LogInformation("Warm-up: {Count} solutions to check", candidates.Count);
        foreach (string path in candidates)
        {
            ct.ThrowIfCancellationRequested();
            string name = SolutionDiscovery.SolutionName(path);
            try
            {
                warmUp.Begin(name);
                WarmOutcome outcome = await this.WarmSolutionAsync(path, "Warm-up", ct).ConfigureAwait(false);
                if (outcome == WarmOutcome.Indexed)
                {
                    warmUp.MarkWarmed(name);
                }
                else
                {
                    warmUp.MarkSkipped(name);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Warm-up of {Solution} failed", name);
                warmUp.MarkFailed(name);
            }
        }

        warmUp.Finish();
        logger.LogInformation("Warm-up finished: {Summary}", warmUp.Describe());
    }

    private async Task RunEvictionAsync(TimeSpan idle, CancellationToken ct)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                workspaces.EvictIdle(idle);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ProcessAsync(IndexJob job, CancellationToken ct)
    {
        switch (job)
        {
            case IndexSolutionJob indexJob:
                if (workspaces.TryGetLoaded(indexJob.SolutionPath, out LoadedSolution loaded))
                {
                    string? head = await this.ReadHeadAsync(indexJob.SolutionPath, ct).ConfigureAwait(false);
                    await indexer.IndexSolutionAsync(loaded, indexJob.Force, ct).ConfigureAwait(false);
                    this.DropRowsIfForgotten(indexJob.SolutionPath);
                    this.RecordHead(indexJob.SolutionPath, head);
                }

                break;

            case FileChangesJob changes:
                await this.ProcessFileChangesAsync(changes.Paths, changes.CreatedPaths, changes.CreatedDirectories, ct).ConfigureAwait(false);
                break;

            case ForgetSolutionJob forget:
                try
                {
                    forget.Result.TrySetResult(workspaces.Forget(forget.Hint));
                }
                catch (Exception ex)
                {
                    forget.Result.TrySetException(ex);
                    throw;
                }

                break;

            case ForgetMissingJob:
                workspaces.ForgetMissing();
                break;

            case FullReconcileJob:
                // The watcher lost events: vanished solutions are forgotten and new ones (a copied repository creates
                // many files at once, which is what overflows) are found by a fresh scan of the roots.
                workspaces.ForgetMissing();
                workspaces.Rediscover(options.Value.Roots);
                this.WatchWorktreeHeads();
                _ = await this.ReconcileOrReloadLoadedAsync(ct).ConfigureAwait(false);

                // The watcher lost events; anything unloaded may have changed too. The freshness check makes an unneeded refresh cheap.
                this.RecordUnloadedChanges(workspaces.KnownSolutions, []);
                break;

            case ReloadSolutionJob reload:
                await this.ReloadAsync(reload, ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// For a batch that may have changed anything (a git change, lost watcher events): a loaded solution whose project
    /// files differ from what they were at its load is reloaded, the others are reconciled with the disk.
    /// Returns the solutions that were reloaded.
    /// </summary>
    private async Task<HashSet<string>> ReconcileOrReloadLoadedAsync(CancellationToken ct)
    {
        HashSet<string> reloaded = new(StringComparer.OrdinalIgnoreCase);
        foreach (LoadedSolution l in workspaces.Loaded)
        {
            IReadOnlyList<string> changedProjectFiles = l.ChangedProjectFiles();
            if (changedProjectFiles.Count > 0)
            {
                reloaded.Add(l.SolutionPath);
                await this.ReloadAsync(new ReloadSolutionJob(l.SolutionPath, changedProjectFiles), ct).ConfigureAwait(false);
            }
            else
            {
                await this.ReconcileAsync(l, ct).ConfigureAwait(false);
            }
        }

        return reloaded;
    }

    /// <summary>
    /// The consumer runs every forget, so a solution forgotten while its index job was running (or queued behind it)
    /// would otherwise get its rows back from that job. Called after each job that writes rows for one solution.
    /// </summary>
    private void DropRowsIfForgotten(string solutionPath)
    {
        if (!workspaces.IsKnown(solutionPath))
        {
            index.DeleteSolution(solutionPath);
        }
    }

    private async Task ProcessFileChangesAsync(IReadOnlyCollection<string> paths, IReadOnlyCollection<string>? createdPaths, IReadOnlyCollection<string>? createdDirectories, CancellationToken ct)
    {
        // A deleted folder may arrive as a single event or none; any batch is a chance to notice vanished solution files.
        workspaces.ForgetMissing();

        List<(string Path, ChangeKind Kind)> classified = paths
            .Select(p => (p, ChangeClassifier.Classify(p, options.Value.IgnoredFolders)))
            .Where(x => x.Item2 != ChangeKind.Ignored)
            .ToList();

        // New solutions are registered only: no load, no warm-up, and their own file is not a change to refresh.
        List<string> appeared = workspaces.RegisterAppearedIn(createdDirectories ?? [], options.Value.Roots).ToList();
        appeared.AddRange(workspaces.RegisterAppeared(
            classified.Where(c => c.Kind == ChangeKind.SolutionFile).Select(c => c.Path), createdPaths, options.Value.Roots));
        if (appeared.Count > 0)
        {
            this.WatchWorktreeHeads();
            classified = classified.Where(c => !appeared.Contains(c.Path, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        if (classified.Count == 0)
        {
            return;
        }

        // A checkout touches HEAD and many files at once: one reconcile (or a reload, where project files changed) covers
        // all of it for the loaded solutions. The rest of the batch is still looked at below for what the checkout did not cover.
        List<string> heads = classified.Where(c => c.Kind == ChangeKind.Git).Select(c => c.Path).ToList();
        HashSet<string> reloaded = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> covered = new(StringComparer.OrdinalIgnoreCase);
        if (heads.Count > 0)
        {
            logger.LogInformation("Git HEAD changed; reconciling {Count} loaded solutions", workspaces.Loaded.Count);
            covered.UnionWith(workspaces.Loaded.Select(l => l.SolutionPath));
            reloaded = await this.ReconcileOrReloadLoadedAsync(ct).ConfigureAwait(false);

            foreach (string head in heads)
            {
                List<string> inRepository = this.SolutionsUnderRepository(head).ToList();
                this.RecordUnloadedChanges(inRepository, [head]);
                this.RefreshUnloadedNow(inRepository);
            }
        }

        HashSet<string> reloadedByGit = new(reloaded, StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string?, (string Path, ChangeKind Kind)> group in classified
                     .Where(c => c.Kind is ChangeKind.ProjectFile or ChangeKind.SolutionFile)
                     .GroupBy(c => workspaces.FindSolutionForPath(c.Path), StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key is null || reloadedByGit.Contains(group.Key))
            {
                continue;
            }

            if (!workspaces.TryGetLoaded(group.Key, out _))
            {
                this.RecordUnloadedChanges([group.Key], group.Select(g => g.Path));
                continue;
            }

            reloaded.Add(group.Key);
            await this.ReloadAsync(new ReloadSolutionJob(group.Key, group.Select(g => g.Path).ToList()), ct).ConfigureAwait(false);
        }

        foreach (IGrouping<string?, (string Path, ChangeKind Kind)> group in classified
                     .Where(c => c.Kind == ChangeKind.Source)
                     .GroupBy(c => workspaces.FindSolutionForPath(c.Path), StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key is null || reloaded.Contains(group.Key) || covered.Contains(group.Key))
            {
                continue;
            }

            if (!workspaces.TryGetLoaded(group.Key, out LoadedSolution loaded))
            {
                this.RecordUnloadedChanges([group.Key], group.Select(g => g.Path));
                continue;
            }

            List<DocumentId> changedIds = [];
            HashSet<ProjectId> surfaceChanged = [];
            foreach ((string path, _) in group)
            {
                changedIds.AddRange(await this.ApplyFromDiskAsync(loaded, path, surfaceChanged, ct).ConfigureAwait(false));
            }

            IncrementalIndexReport report = await indexer.IndexDocumentsAsync(loaded, changedIds, ct).ConfigureAwait(false);
            surfaceChanged.UnionWith(report.SurfaceChangedProjects);
            logger.LogInformation("{Solution}: applied {Files} file changes, {Extracted} documents re-extracted", loaded.Name, group.Count(), report.DocumentsExtracted);
            await this.PropagateSurfaceChangesAsync(loaded, surfaceChanged, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Re-extracts every project whose stored reference rows may bind differently now that the declared surface of
    /// <paramref name="changed"/> is different: those projects, their transitive dependents in the same solution, and
    /// in every other loaded solution the projects that consume one of them (by assembly name or csproj path) plus
    /// their dependents. Unloaded solutions keep their rows from the last load; they are marked stale at query time.
    /// </summary>
    private async Task PropagateSurfaceChangesAsync(LoadedSolution origin, IReadOnlySet<ProjectId> changed, CancellationToken ct)
    {
        if (changed.Count == 0 || !options.Value.ReextractDependents)
        {
            return;
        }

        Solution originSolution = origin.Solution;
        List<Project> changedProjects = changed.Select(originSolution.GetProject).Where(p => p is not null).Select(p => p!).ToList();
        List<string> assemblyNames = changedProjects.Select(p => p.AssemblyName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        List<string> projectPaths = changedProjects.Where(p => p.FilePath is not null).Select(p => p.FilePath!).ToList();

        foreach (LoadedSolution loaded in workspaces.Loaded)
        {
            DateTime started = DateTime.UtcNow;
            Solution solution = loaded.Solution;
            IEnumerable<ProjectId> seeds = string.Equals(loaded.SolutionPath, origin.SolutionPath, StringComparison.OrdinalIgnoreCase)
                ? changed
                : DependentProjects.ReferencingAssemblies(solution, assemblyNames, projectPaths);
            IReadOnlySet<ProjectId> targets = DependentProjects.WithTransitiveDependents(solution, seeds);
            if (targets.Count == 0)
            {
                continue;
            }

            int extracted = await indexer.ReextractProjectsAsync(loaded, targets, ct).ConfigureAwait(false);
            logger.LogInformation(
                "{Solution}: declared surface of {Changed} changed; re-extracted {Projects} dependent projects, {Documents} documents in {Seconds:N1}s",
                loaded.Name, string.Join(", ", changedProjects.Select(p => p.Name)), targets.Count, extracted, (DateTime.UtcNow - started).TotalSeconds);
        }
    }

    private async Task ReloadAsync(ReloadSolutionJob reload, CancellationToken ct)
    {
        if (!workspaces.IsKnown(reload.SolutionPath))
        {
            return;
        }

        if (options.Value.RestoreOnProjectChange)
        {
            foreach (string projectFile in reload.ChangedProjectFiles.Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && File.Exists(p)))
            {
                logger.LogInformation("Restoring {Project}", projectFile);
                await WorkspaceManager.RunDotnetAsync($"restore \"{projectFile}\" --nologo", TimeSpan.FromMinutes(5), logger).ConfigureAwait(false);
            }
        }

        logger.LogInformation("Reloading {Solution} after project file changes", SolutionDiscovery.SolutionName(reload.SolutionPath));
        string? head = await this.ReadHeadAsync(reload.SolutionPath, ct).ConfigureAwait(false);
        await workspaces.ReloadAsync(reload.SolutionPath, ct).ConfigureAwait(false);
        this.RecordHead(reload.SolutionPath, head);
    }

    /// <summary>Compares every document of a loaded solution with the disk and applies the differences, then indexes.</summary>
    private async Task ReconcileAsync(LoadedSolution loaded, CancellationToken ct)
    {
        DateTime started = DateTime.UtcNow;
        string? head = await this.ReadHeadAsync(loaded.SolutionPath, ct).ConfigureAwait(false);
        Solution solution = loaded.Solution;
        List<DocumentId> changed = [];
        HashSet<ProjectId> surfaceChanged = [];
        int added = 0, removed = 0, updated = 0;

        foreach (Project project in solution.Projects)
        {
            if (project.FilePath is null)
            {
                continue;
            }

            int projectId = index.EnsureProject(project.FilePath, project.Name, project.AssemblyName, loaded.SolutionPath);
            IReadOnlyDictionary<string, TreeEntry> tree = index.GetTree(projectId);
            HashSet<string> known = new(StringComparer.OrdinalIgnoreCase);

            foreach (Document document in project.Documents)
            {
                if (document.FilePath is null || !document.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || filter.IsIgnored(document.FilePath))
                {
                    continue;
                }

                known.Add(document.FilePath);
                FileInfo info = new(document.FilePath);
                if (!info.Exists)
                {
                    surfaceChanged.UnionWith(indexer.RemoveDocument(loaded, document.FilePath));
                    loaded.ApplyFileChange(document.FilePath, null);
                    removed++;
                    continue;
                }

                bool unchanged = tree.TryGetValue(document.FilePath, out TreeEntry? entry)
                                 && entry.Mtime == info.LastWriteTimeUtc.Ticks && entry.Size == info.Length;
                if (!unchanged)
                {
                    changed.AddRange(await this.ApplyFromDiskAsync(loaded, document.FilePath, surfaceChanged, ct).ConfigureAwait(false));
                    updated++;
                }
            }

            string? projectDir = Path.GetDirectoryName(project.FilePath);
            if (projectDir is null)
            {
                continue;
            }

            foreach (string file in filter.EnumerateSourceFiles(projectDir))
            {
                // Files the project's compilation left out at load time (Compile Remove globs) are not new files.
                if (known.Contains(file) || loaded.IsExcludedSourceFile(file) || ChangeClassifier.Classify(file, options.Value.IgnoredFolders) != ChangeKind.Source)
                {
                    continue;
                }

                // Only add files that are not claimed by a more specific (nested) project directory.
                if (loaded.FindProjectForNewFile(file)?.Id != project.Id)
                {
                    continue;
                }

                changed.AddRange(await this.ApplyFromDiskAsync(loaded, file, surfaceChanged, ct).ConfigureAwait(false));
                added++;
            }
        }

        IncrementalIndexReport report = await indexer.IndexDocumentsAsync(loaded, changed, ct).ConfigureAwait(false);
        surfaceChanged.UnionWith(report.SurfaceChangedProjects);
        await indexer.IndexSolutionAsync(loaded, force: false, ct).ConfigureAwait(false);
        this.DropRowsIfForgotten(loaded.SolutionPath);
        this.RecordHead(loaded.SolutionPath, head);
        logger.LogInformation(
            "Reconciled {Solution}: {Updated} updated, {Added} added, {Removed} removed, {Extracted} extracted in {Seconds:N1}s",
            loaded.Name, updated, added, removed, report.DocumentsExtracted, (DateTime.UtcNow - started).TotalSeconds);
        await this.PropagateSurfaceChangesAsync(loaded, surfaceChanged, ct).ConfigureAwait(false);
    }

    /// <summary>Pushes the on-disk text of a file into the in-memory solution. A deleted file is dropped from the tree first, recording the projects whose surface it contributed to.</summary>
    private async Task<IReadOnlyList<DocumentId>> ApplyFromDiskAsync(LoadedSolution loaded, string path, ISet<ProjectId> surfaceChanged, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            surfaceChanged.UnionWith(indexer.RemoveDocument(loaded, path));
            return loaded.ApplyFileChange(path, null);
        }

        SourceText? text = await LiveTextSync.ReadTextAsync(path, ct).ConfigureAwait(false);
        return text is null ? [] : loaded.ApplyFileChange(path, text);
    }

    public override void Dispose()
    {
        this._watcher?.Dispose();
        base.Dispose();
    }
}
