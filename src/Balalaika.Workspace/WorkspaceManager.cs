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

public sealed record SolutionStatus(string Path, string Name, SolutionState State, int Projects, int Documents, DateTime? LoadedAtUtc, DateTime? LastUsedUtc, TimeSpan? LoadDuration, string? Error, bool Pinned);

/// <summary>
/// Owns one <see cref="LoadedSolution"/> per solution path. Loads are lazy and deduplicated; a solution is loaded at
/// most once concurrently. Preloaded solutions are pinned and never evicted.
/// </summary>
public sealed class WorkspaceManager(IndexStore index, IOptions<BalalaikaOptions> options, ILogger<WorkspaceManager> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<LoadedSolution>>> _loads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _known = [];
    private readonly HashSet<string> _pinned = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public event Action<LoadedSolution>? SolutionLoaded;

    public IReadOnlyList<string> KnownSolutions
    {
        get
        {
            lock (this._gate)
            {
                return this._known.ToList();
            }
        }
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

        string candidate = nameOrPath.Trim();
        string? byPath = known.FirstOrDefault(k => k.Equals(candidate, StringComparison.OrdinalIgnoreCase));
        if (byPath is not null)
        {
            return byPath;
        }

        string? byName = known.FirstOrDefault(k => SolutionDiscovery.SolutionName(k).Equals(candidate, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            return byName;
        }

        return known.FirstOrDefault(k => SolutionDiscovery.SolutionName(k).Contains(candidate, StringComparison.OrdinalIgnoreCase));
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

    public IReadOnlyList<LoadedSolution> Loaded
    {
        get
        {
            return this._loads.Values
                .Where(l => l.IsValueCreated && l.Value.IsCompletedSuccessfully)
                .Select(l => l.Value.Result)
                .ToList();
        }
    }

    public Task<LoadedSolution> GetOrLoadAsync(string solutionPath, CancellationToken cancellationToken)
    {
        string full = Path.GetFullPath(solutionPath);
        this.RegisterSolutions([full]);
        Lazy<Task<LoadedSolution>> lazy = this._loads.GetOrAdd(full, p => new Lazy<Task<LoadedSolution>>(() => this.LoadCoreAsync(p)));
        return lazy.Value.WaitAsync(cancellationToken);
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

    private async Task<LoadedSolution> LoadCoreAsync(string solutionPath)
    {
        try
        {
            logger.LogInformation("Loading {Solution}", solutionPath);
            LoadedSolution loaded = await LoadedSolution.LoadAsync(solutionPath, logger, CancellationToken.None).ConfigureAwait(false);

            if (NeedsRestore(loaded))
            {
                logger.LogInformation("{Solution} has projects without references; running dotnet restore and reloading", loaded.Name);
                loaded.Dispose();
                await RunDotnetAsync($"restore \"{solutionPath}\" --nologo", TimeSpan.FromMinutes(5), logger).ConfigureAwait(false);
                loaded = await LoadedSolution.LoadAsync(solutionPath, logger, CancellationToken.None).ConfigureAwait(false);
            }

            logger.LogInformation(
                "Loaded {Solution}: {Projects} projects in {Seconds:N1}s ({Failures} workspace diagnostics)",
                loaded.Name, loaded.Solution.ProjectIds.Count, loaded.LoadDuration.TotalSeconds, loaded.LoadDiagnostics.Count);

            this._failures.TryRemove(solutionPath, out _);
            this.SolutionLoaded?.Invoke(loaded);
            return loaded;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load {Solution}", solutionPath);
            this._failures[solutionPath] = ex.Message;
            this._loads.TryRemove(solutionPath, out _);
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
            if (this._loads.TryGetValue(path, out Lazy<Task<LoadedSolution>>? lazy) && lazy.IsValueCreated)
            {
                if (lazy.Value.IsCompletedSuccessfully)
                {
                    LoadedSolution l = lazy.Value.Result;
                    Solution s = l.Solution;
                    list.Add(new SolutionStatus(path, name, SolutionState.Loaded, s.ProjectIds.Count, s.Projects.Sum(p => p.DocumentIds.Count), l.LoadedAtUtc, l.LastUsedUtc, l.LoadDuration, null, pinned.Contains(path)));
                    continue;
                }

                if (!lazy.Value.IsCompleted)
                {
                    list.Add(new SolutionStatus(path, name, SolutionState.Loading, 0, 0, null, null, null, null, pinned.Contains(path)));
                    continue;
                }
            }

            this._failures.TryGetValue(path, out string? error);
            list.Add(new SolutionStatus(path, name, error is null ? SolutionState.Unloaded : SolutionState.Failed, 0, 0, null, null, null, error, pinned.Contains(path)));
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
