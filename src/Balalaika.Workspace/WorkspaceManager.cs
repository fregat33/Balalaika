using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Balalaika.Index;

namespace Balalaika.Workspace;

public enum SolutionState
{
    Unloaded,
    Loading,
    Loaded,
    Failed,
}

public sealed record ForgottenSolution(string Path, string Name, int ProjectsRemoved);

public sealed record SolutionStatus(string Path, string Name, SolutionState State, int Projects, int Documents, DateTime? LoadedAtUtc, DateTime? LastUsedUtc, TimeSpan? LoadDuration, string? Error, bool Pinned, string? IndexedHead = null);

/// <summary>
/// Owns one <see cref="LoadedSolution"/> per solution path. Loads are lazy and deduplicated; a solution is loaded at
/// most once concurrently. Preloaded solutions are pinned and never evicted.
/// </summary>
public sealed class WorkspaceManager(IndexStore index, PendingRefreshes pendingRefreshes, IOptions<BalalaikaOptions> options, ILogger<WorkspaceManager> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<LoadedSolution>>> _loads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _heads = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _known = [];
    private readonly HashSet<string> _pinned = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _suppressed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public event Action<LoadedSolution>? SolutionLoaded;

    public event Action<ForgottenSolution>? SolutionForgotten;

    /// <summary>Replaces the MSBuild load of a solution (and so every reload). For tests and embedding.</summary>
    public Func<string, CancellationToken, Task<LoadedSolution>>? Loader { get; set; }

    /// <summary>Remembers the git commit (full id) the index of a solution was last brought in line with.</summary>
    public void RecordIndexedHead(string solutionPath, string head)
    {
        string full = Path.GetFullPath(solutionPath);
        if (this.IsKnown(full))
        {
            this._heads[full] = head;
        }
    }

    public string? GetIndexedHead(string solutionPath)
    {
        return this._heads.TryGetValue(Path.GetFullPath(solutionPath), out string? head) ? head : null;
    }

    private readonly ConcurrentDictionary<string, RepositoryInfo> _repositories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The known solutions the current request may see (see <see cref="ViewContext"/>), its parts first; every one outside a request.</summary>
    public IReadOnlyList<string> KnownSolutions
    {
        get
        {
            List<string> all = this.AllKnown;
            if (ViewContext.Current is not { } view)
            {
                return all;
            }

            return all
                .Where(s => !view.Hidden.Contains(s))
                .OrderBy(s => this.RepositoryOf(s) is { IsLinkedWorktree: true } ? 0 : 1)
                .ToList();
        }
    }

    private List<string> AllKnown
    {
        get
        {
            lock (this._gate)
            {
                return this._known.ToList();
            }
        }
    }

    /// <summary>The git placement of a solution, cached once found.</summary>
    public RepositoryInfo? RepositoryOf(string solutionPath)
    {
        string full = Path.GetFullPath(solutionPath);
        if (this._repositories.TryGetValue(full, out RepositoryInfo? cached))
        {
            return cached;
        }

        RepositoryInfo? found = Path.GetDirectoryName(full) is { } dir ? RepositoryInfo.Find(dir) : null;
        if (found is not null)
        {
            this._repositories[full] = found;
        }

        return found;
    }

    /// <summary>
    /// Builds the view of a request that asked for the worktrees <paramref name="parts"/>: private worktree solutions
    /// are hidden unless they are in a part, and the base checkout of a repository that has a part is hidden. No parts
    /// gives the base checkouts only. <paramref name="error"/> says why a request is refused.
    /// </summary>
    public ViewContext? CreateView(IEnumerable<string> parts, out string? error)
    {
        error = null;
        List<string> known = this.AllKnown;
        List<string> requested = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
        Dictionary<string, string> commonByPart = new(StringComparer.OrdinalIgnoreCase);
        List<(string Given, string Root, string Common)> accepted = [];
        foreach (string given in requested)
        {
            string root = RepositoryInfo.Normalize(given);
            string? common = known.Select(this.RepositoryOf).FirstOrDefault(r => r is { IsLinkedWorktree: true } && r.WorktreeRoot.Equals(root, StringComparison.OrdinalIgnoreCase))?.CommonDir;
            if (common is null)
            {
                error = "Unknown part: " + given;
                return null;
            }

            if (accepted.All(a => !a.Root.Equals(root, StringComparison.OrdinalIgnoreCase)))
            {
                accepted.Add((given, root, common));
            }
        }

        for (int i = 0; i < accepted.Count; i++)
        {
            for (int j = i + 1; j < accepted.Count; j++)
            {
                if (accepted[i].Common.Equals(accepted[j].Common, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"Two parts of one repository: {accepted[i].Given}, {accepted[j].Given}";
                    return null;
                }
            }
        }

        HashSet<string> roots = new(accepted.Select(a => a.Root), StringComparer.OrdinalIgnoreCase);
        HashSet<string> replaced = new(accepted.Select(a => a.Common), StringComparer.OrdinalIgnoreCase);
        HashSet<string> hidden = new(StringComparer.OrdinalIgnoreCase);
        foreach (string solution in known)
        {
            if (IsHiddenBy(this.RepositoryOf(solution), roots, replaced))
            {
                hidden.Add(solution);
            }
        }

        return new ViewContext(hidden, roots, replaced);
    }

    private static bool IsHiddenBy(RepositoryInfo? repository, IReadOnlySet<string> parts, IReadOnlySet<string> replacedRepositories)
    {
        if (repository is null)
        {
            return false;
        }

        return repository.IsLinkedWorktree
            ? !parts.Contains(repository.WorktreeRoot)
            : replacedRepositories.Contains(repository.CommonDir);
    }

    public void RegisterSolutions(IEnumerable<string> solutionPaths)
    {
        lock (this._gate)
        {
            foreach (string p in solutionPaths)
            {
                string full = Path.GetFullPath(p);
                if (!this._known.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    this._known.Add(full);
                }

                this._suppressed.Remove(full);
            }
        }
    }

    public void Pin(string solutionPath)
    {
        lock (this._gate)
        {
            this._pinned.Add(Path.GetFullPath(solutionPath));
        }
    }

    public bool IsKnown(string solutionPath)
    {
        string full = Path.GetFullPath(solutionPath);
        lock (this._gate)
        {
            return this._known.Contains(full, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Registers an existing *.sln / *.slnx by full path, inside the roots or not, and returns its normalized path.
    /// Null when <paramref name="candidate"/> is not a rooted path to an existing solution file.
    /// </summary>
    public string? RegisterExistingSolution(string candidate)
    {
        string trimmed = candidate.Trim();
        string ext = Path.GetExtension(trimmed);
        if (!Path.IsPathRooted(trimmed) || !(ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) || ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        string full = Path.GetFullPath(trimmed);
        if (!File.Exists(full))
        {
            return null;
        }

        // A request never registers, and so never loads, a solution its own view hides.
        if (ViewContext.Current is { } view
            && (view.Hidden.Contains(full)
                || (!this.IsKnown(full) && IsHiddenBy(Path.GetDirectoryName(full) is { } dir ? RepositoryInfo.Find(dir) : null, view.Parts, view.ReplacedRepositories))))
        {
            return null;
        }

        this.RegisterSolutions([full]);
        lock (this._gate)
        {
            return this._known.First(k => k.Equals(full, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Registers the solution files among <paramref name="changedPaths"/> that appeared under <paramref name="roots"/>
    /// (see <see cref="SolutionDiscovery.QualifiesForDiscovery"/>) and still exist. A solution that was forgotten while
    /// its file still exists is only registered again when its path is in <paramref name="createdPaths"/> (a create or
    /// rename event), not on a plain modification. Registered solutions are neither loaded nor warmed.
    /// </summary>
    public IReadOnlyList<string> RegisterAppeared(IEnumerable<string> changedPaths, IReadOnlyCollection<string>? createdPaths, IEnumerable<string> roots)
    {
        List<string> rootList = roots.ToList();
        HashSet<string> created = new(createdPaths ?? [], StringComparer.OrdinalIgnoreCase);
        List<string> added = [];
        foreach (string path in changedPaths)
        {
            string full = Path.GetFullPath(path);
            if (File.Exists(full) && SolutionDiscovery.QualifiesForDiscovery(full, rootList) && this.TryRegisterDiscovered(full, created.Contains(full)))
            {
                added.Add(full);
            }
        }

        return added;
    }

    /// <summary>
    /// Registers the solutions below directories that were created or moved into a root (a directory event carries no
    /// file events for what it contains). Applies the discovery rules, limited by each directory's depth below its root.
    /// A forgotten solution at such a path is registered again: the folder is new, so the file is too.
    /// </summary>
    public IReadOnlyList<string> RegisterAppearedIn(IEnumerable<string> directories, IEnumerable<string> roots)
    {
        List<string> rootList = roots.ToList();
        List<string> dirs = directories.Select(d => Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // A scan of a directory covers its subdirectories; a tree copy reports every folder in it.
        List<string> tops = dirs
            .Where(d => !dirs.Any(o => o.Length < d.Length && d.StartsWith(o + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        List<string> added = [];
        foreach (string dir in tops)
        {
            foreach (string found in SolutionDiscovery.FindSolutionsIn(dir, rootList))
            {
                if (this.TryRegisterDiscovered(Path.GetFullPath(found), overrideSuppression: true))
                {
                    added.Add(found);
                }
            }
        }

        return added;
    }

    /// <summary>
    /// Scans <paramref name="roots"/> again and registers every solution that is not known and was not forgotten on
    /// purpose. The recovery for a watcher overflow, which loses the create events a plain batch relies on.
    /// </summary>
    public IReadOnlyList<string> Rediscover(IEnumerable<string> roots)
    {
        List<string> added = [];
        foreach (string found in SolutionDiscovery.FindSolutions(roots))
        {
            if (this.TryRegisterDiscovered(Path.GetFullPath(found), overrideSuppression: false))
            {
                added.Add(found);
            }
        }

        return added;
    }

    private bool TryRegisterDiscovered(string full, bool overrideSuppression)
    {
        if (this.IsKnown(full))
        {
            return false;
        }

        lock (this._gate)
        {
            if (this._suppressed.Contains(full) && !overrideSuppression)
            {
                return false;
            }
        }

        // Discovery keeps the .sln of a same-name pair, so a .sln that appears replaces a known .slnx.
        if (Path.GetExtension(full).Equals(".sln", StringComparison.OrdinalIgnoreCase))
        {
            string slnx = Path.ChangeExtension(full, ".slnx");
            if (this.IsKnown(slnx))
            {
                this.ForgetPath(slnx);
            }
        }

        this.RegisterSolutions([full]);
        logger.LogInformation("Discovered new solution {Solution}", full);
        return true;
    }

    /// <summary>True when at least one known solution's file no longer exists.</summary>
    public bool HasMissingSolutions()
    {
        return this.AllKnown.Any(p => !File.Exists(p));
    }

    /// <summary>Forgets every known solution whose file no longer exists. Returns what was forgotten.</summary>
    public IReadOnlyList<ForgottenSolution> ForgetMissing()
    {
        List<ForgottenSolution> forgotten = [];
        foreach (string path in this.AllKnown.Where(p => !File.Exists(p)))
        {
            if (this.ForgetPath(path) is { } result)
            {
                logger.LogInformation("Solution file gone, forgot {Solution}: {Projects} projects removed from the index", result.Name, result.ProjectsRemoved);
                forgotten.Add(result);
            }
        }

        return forgotten;
    }

    /// <summary>
    /// Forgets a solution by full path or name (resolved like <see cref="ResolveSolution"/>, but a blank hint never falls
    /// back to the default): removes it from the known and pinned sets, drops its load state and pending refresh,
    /// disposes it even if pinned, and deletes its index rows. Null when the hint matches nothing.
    /// A load that is still running is not awaited: it is left to finish and then discards its result (see
    /// <see cref="LoadCoreAsync"/>), so forgetting never blocks or deadlocks on a slow load.
    /// </summary>
    public ForgottenSolution? Forget(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            return null;
        }

        string? path = Match(this.KnownSolutions, hint.Trim());
        return path is null ? null : this.ForgetPath(path);
    }

    private ForgottenSolution? ForgetPath(string path)
    {
        bool removed;
        lock (this._gate)
        {
            removed = this._known.RemoveAll(k => k.Equals(path, StringComparison.OrdinalIgnoreCase)) > 0;
            this._pinned.Remove(path);
            if (removed && File.Exists(path))
            {
                this._suppressed.Add(path);
            }
        }

        if (!removed)
        {
            return null;
        }

        this._failures.TryRemove(path, out _);
        this._heads.TryRemove(path, out _);
        if (this._loads.TryRemove(path, out Lazy<Task<LoadedSolution>>? lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
        {
            lazy.Value.Result.Dispose();
        }

        pendingRefreshes.Discard(path);
        int projects = index.DeleteSolution(path);
        logger.LogInformation("Forgot {Solution}: {Projects} projects removed from the index", SolutionDiscovery.SolutionName(path), projects);
        ForgottenSolution result = new(path, SolutionDiscovery.SolutionName(path), projects);
        this.SolutionForgotten?.Invoke(result);
        return result;
    }

    /// <summary>Resolves a user-supplied solution name or path. Null falls back to the configured default.</summary>
    public string? ResolveSolution(string? nameOrPath)
    {
        IReadOnlyList<string> known = this.KnownSolutions;
        if (string.IsNullOrWhiteSpace(nameOrPath))
        {
            nameOrPath = options.Value.DefaultSolution;
            if (string.IsNullOrWhiteSpace(nameOrPath))
            {
                nameOrPath = options.Value.Preload.FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(nameOrPath))
            {
                return known.FirstOrDefault();
            }
        }

        return Match(known, nameOrPath.Trim());
    }

    /// <summary>
    /// Every solution a hint can mean, among the solutions this request sees: the one exact full-path or exact-name match
    /// when there is one, else all solutions whose name contains the hint. Blank hint: none (no default fallback).
    /// </summary>
    public IReadOnlyList<string> ResolveForForget(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            return [];
        }

        IReadOnlyList<string> known = this.KnownSolutions;
        string candidate = hint.Trim();
        string? exact = MatchExact(known, candidate);
        if (exact is not null)
        {
            return [exact];
        }

        return known.Where(k => SolutionDiscovery.SolutionName(k).Contains(candidate, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static string? Match(IReadOnlyList<string> known, string candidate)
    {
        return MatchExact(known, candidate)
            ?? known.FirstOrDefault(k => SolutionDiscovery.SolutionName(k).Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static string? MatchExact(IReadOnlyList<string> known, string candidate)
    {
        string? byPath = known.FirstOrDefault(k => k.Equals(candidate, StringComparison.OrdinalIgnoreCase));
        if (byPath is not null)
        {
            return byPath;
        }

        if (Path.IsPathRooted(candidate))
        {
            string? normalized = null;
            try
            {
                normalized = Path.GetFullPath(candidate).TrimEnd('/', '\\');
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Not a usable path: fall through to the name matches.
            }

            if (normalized is not null)
            {
                byPath = known.FirstOrDefault(k => k.TrimEnd('/', '\\').Equals(normalized, StringComparison.OrdinalIgnoreCase));
                if (byPath is not null)
                {
                    return byPath;
                }
            }
        }

        return known.FirstOrDefault(k => SolutionDiscovery.SolutionName(k).Equals(candidate, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Finds the solution that owns a file, by project directory when known to the index, else by solution directory.</summary>
    public string? FindSolutionForPath(string filePath)
    {
        string? best = null;
        int bestLength = -1;

        foreach (ProjectRecord project in index.GetProjects())
        {
            string? dir = Path.GetDirectoryName(project.Path);
            if (dir is not null && filePath.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && dir.Length > bestLength)
            {
                best = project.SolutionPath;
                bestLength = dir.Length;
            }
        }

        if (best is not null)
        {
            return best;
        }

        foreach (string solution in this.KnownSolutions)
        {
            string? dir = Path.GetDirectoryName(solution);
            if (dir is not null && filePath.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && dir.Length > bestLength)
            {
                best = solution;
                bestLength = dir.Length;
            }
        }

        return best;
    }

    public bool TryGetLoaded(string solutionPath, out LoadedSolution loaded)
    {
        if (this._loads.TryGetValue(Path.GetFullPath(solutionPath), out Lazy<Task<LoadedSolution>>? lazy)
            && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
        {
            loaded = lazy.Value.Result;
            return true;
        }

        loaded = null!;
        return false;
    }

    /// <summary>Registers an already built solution as loaded, bypassing MSBuild. For tests and embedding.</summary>
    public void Adopt(LoadedSolution loaded)
    {
        this.RegisterSolutions([loaded.SolutionPath]);
        Lazy<Task<LoadedSolution>> lazy = new(() => Task.FromResult(loaded));
        _ = lazy.Value;
        this._loads[Path.GetFullPath(loaded.SolutionPath)] = lazy;
    }

    public IReadOnlyList<LoadedSolution> Loaded
    {
        get
        {
            return this._loads.Values
                .Where(l => l.IsValueCreated && l.Value.IsCompletedSuccessfully)
                .Select(l => l.Value.Result)
                .Where(l => !ViewContext.IsHidden(l.SolutionPath))
                .ToList();
        }
    }

    public Task<LoadedSolution> GetOrLoadAsync(string solutionPath, CancellationToken cancellationToken)
    {
        // Does not register: a queued job for a forgotten solution must fail here instead of bringing it back.
        string full = Path.GetFullPath(solutionPath);
        if (!this.IsKnown(full))
        {
            return Task.FromException<LoadedSolution>(new InvalidOperationException("Unknown solution: " + full));
        }

        Lazy<Task<LoadedSolution>> lazy = this._loads.GetOrAdd(full, this.NewLoad);
        return lazy.Value.WaitAsync(cancellationToken);
    }

    // The load needs its own entry to tell, when it ends, whether it is still the one in _loads (the solution may have
    // been forgotten and registered again meanwhile, which starts a new load under the same key).
    private Lazy<Task<LoadedSolution>> NewLoad(string path)
    {
        Lazy<Task<LoadedSolution>>? self = null;
        self = new Lazy<Task<LoadedSolution>>(() => this.LoadCoreAsync(path, self!));
        return self;
    }

    private bool IsCurrentLoad(string path, Lazy<Task<LoadedSolution>> load)
    {
        return this._loads.TryGetValue(path, out Lazy<Task<LoadedSolution>>? current) && ReferenceEquals(current, load);
    }

    /// <summary>Starts (or joins) a load and waits up to <paramref name="wait"/>. Returns null when still loading.</summary>
    public async Task<LoadedSolution?> TryGetOrLoadWithinAsync(string solutionPath, TimeSpan wait, CancellationToken cancellationToken)
    {
        Task<LoadedSolution> task = this.GetOrLoadAsync(solutionPath, CancellationToken.None);
        try
        {
            return await task.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private async Task<LoadedSolution> LoadCoreAsync(string solutionPath, Lazy<Task<LoadedSolution>> self)
    {
        // A load started by a request outlives it and is shared with every other request: it runs without that request's view.
        ViewContext.Current = null;
        try
        {
            logger.LogInformation("Loading {Solution}", solutionPath);
            LoadedSolution loaded = this.Loader is { } loader
                ? await loader(solutionPath, CancellationToken.None).ConfigureAwait(false)
                : await LoadedSolution.LoadAsync(solutionPath, logger, CancellationToken.None).ConfigureAwait(false);

            if (this.Loader is null && NeedsRestore(loaded))
            {
                logger.LogInformation("{Solution} has projects without references; running dotnet restore and reloading", loaded.Name);
                loaded.Dispose();
                await RunDotnetAsync($"restore \"{solutionPath}\" --nologo", TimeSpan.FromMinutes(5), logger).ConfigureAwait(false);
                loaded = await LoadedSolution.LoadAsync(solutionPath, logger, CancellationToken.None).ConfigureAwait(false);
            }

            // Forgotten while loading: the load is not awaited by Forget, so it ends here instead.
            if (!this.IsKnown(solutionPath) || !this.IsCurrentLoad(solutionPath, self))
            {
                loaded.Dispose();
                logger.LogInformation("{Solution} was forgotten while loading; load discarded", loaded.Name);
                throw new OperationCanceledException("Solution forgotten while loading: " + solutionPath);
            }

            logger.LogInformation(
                "Loaded {Solution}: {Projects} projects in {Seconds:N1}s ({Failures} workspace diagnostics)",
                loaded.Name, loaded.Solution.ProjectIds.Count, loaded.LoadDuration.TotalSeconds, loaded.LoadDiagnostics.Count);

            this._failures.TryRemove(solutionPath, out _);
            this.SolutionLoaded?.Invoke(loaded);
            return loaded;
        }
        catch (OperationCanceledException) when (!this.IsKnown(solutionPath) || !this.IsCurrentLoad(solutionPath, self))
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load {Solution}", solutionPath);
            if (this.IsCurrentLoad(solutionPath, self))
            {
                this._failures[solutionPath] = ex.Message;
                this._loads.TryRemove(new KeyValuePair<string, Lazy<Task<LoadedSolution>>>(solutionPath, self));
            }

            throw;
        }
    }

    private static bool NeedsRestore(LoadedSolution loaded)
    {
        Solution solution = loaded.Solution;
        return solution.Projects.Any(p => p.FilePath is not null && p.MetadataReferences.Count == 0 && p.DocumentIds.Count > 0);
    }

    public static async Task<string> RunDotnetAsync(string arguments, TimeSpan timeout, ILogger logger)
    {
        ProcessStartInfo psi = new("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(psi)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource cts = new(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            logger.LogWarning("dotnet {Arguments} timed out after {Timeout}", arguments, timeout);
        }

        string output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
        if (process.HasExited && process.ExitCode != 0)
        {
            logger.LogWarning("dotnet {Arguments} exited with {Code}: {Tail}", arguments, process.ExitCode, Tail(output, 2000));
        }

        return output;
    }

    private static string Tail(string s, int max)
    {
        return s.Length <= max ? s : s[^max..];
    }

    public async Task ReloadAsync(string solutionPath, CancellationToken cancellationToken)
    {
        string full = Path.GetFullPath(solutionPath);
        if (this._loads.TryRemove(full, out Lazy<Task<LoadedSolution>>? old) && old.IsValueCreated)
        {
            try
            {
                (await old.Value.ConfigureAwait(false)).Dispose();
            }
            catch
            {
            }
        }

        await this.GetOrLoadAsync(full, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Unloads one solution unless it is pinned. Returns false when it was pinned or not loaded.</summary>
    public bool Evict(string solutionPath)
    {
        string full = Path.GetFullPath(solutionPath);
        lock (this._gate)
        {
            if (this._pinned.Contains(full))
            {
                return false;
            }
        }

        if (!this._loads.TryGetValue(full, out Lazy<Task<LoadedSolution>>? lazy) || !lazy.IsValueCreated || !lazy.Value.IsCompletedSuccessfully)
        {
            return false;
        }

        if (!this._loads.TryRemove(full, out _))
        {
            return false;
        }

        LoadedSolution loaded = lazy.Value.Result;
        loaded.Dispose();
        logger.LogInformation("Evicted {Solution}", loaded.Name);
        GC.Collect();
        return true;
    }

    public int EvictIdle(TimeSpan idle)
    {
        int evicted = 0;
        DateTime cutoff = DateTime.UtcNow - idle;
        foreach ((string path, Lazy<Task<LoadedSolution>> lazy) in this._loads.ToArray())
        {
            if (!lazy.IsValueCreated || !lazy.Value.IsCompletedSuccessfully)
            {
                continue;
            }

            lock (this._gate)
            {
                if (this._pinned.Contains(path))
                {
                    continue;
                }
            }

            LoadedSolution loaded = lazy.Value.Result;
            if (loaded.LastUsedUtc < cutoff && this._loads.TryRemove(path, out _))
            {
                loaded.Dispose();
                evicted++;
                logger.LogInformation("Evicted idle solution {Solution}", loaded.Name);
            }
        }

        if (evicted > 0)
        {
            GC.Collect();
        }

        return evicted;
    }

    public IReadOnlyList<SolutionStatus> GetStatus()
    {
        List<SolutionStatus> list = [];
        HashSet<string> pinned;
        lock (this._gate)
        {
            pinned = new HashSet<string>(this._pinned, StringComparer.OrdinalIgnoreCase);
        }

        foreach (string path in this.KnownSolutions)
        {
            string name = SolutionDiscovery.SolutionName(path);
            this._heads.TryGetValue(path, out string? head);
            if (this._loads.TryGetValue(path, out Lazy<Task<LoadedSolution>>? lazy) && lazy.IsValueCreated)
            {
                if (lazy.Value.IsCompletedSuccessfully)
                {
                    LoadedSolution l = lazy.Value.Result;
                    Solution s = l.Solution;
                    list.Add(new SolutionStatus(path, name, SolutionState.Loaded, s.ProjectIds.Count, s.Projects.Sum(p => p.DocumentIds.Count), l.LoadedAtUtc, l.LastUsedUtc, l.LoadDuration, null, pinned.Contains(path), head));
                    continue;
                }

                if (!lazy.Value.IsCompleted)
                {
                    list.Add(new SolutionStatus(path, name, SolutionState.Loading, 0, 0, null, null, null, null, pinned.Contains(path), head));
                    continue;
                }
            }

            this._failures.TryGetValue(path, out string? error);
            list.Add(new SolutionStatus(path, name, error is null ? SolutionState.Unloaded : SolutionState.Failed, 0, 0, null, null, null, error, pinned.Contains(path), head));
        }

        return list;
    }

    public void Dispose()
    {
        foreach (Lazy<Task<LoadedSolution>> lazy in this._loads.Values)
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
            {
                lazy.Value.Result.Dispose();
            }
        }
    }
}
