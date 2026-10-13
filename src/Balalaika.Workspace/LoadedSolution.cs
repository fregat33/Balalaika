using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace Balalaika.Workspace;

/// <summary>
/// A solution held in memory. The live <see cref="Solution"/> snapshot is forked on every file change with
/// <c>WithDocumentText</c> / <c>AddDocument</c> / <c>RemoveDocument</c>. The underlying MSBuildWorkspace is kept only
/// for disposal: applying changes through it would write files back to disk, which is never wanted here.
/// </summary>
public sealed class LoadedSolution : IDisposable
{
    private readonly Microsoft.CodeAnalysis.Workspace? _workspace;
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<string>> _excludedByProject;
    private readonly HashSet<string> _excluded;
    private Solution _solution;

    public string SolutionPath { get; }
    public string Name => SolutionDiscovery.SolutionName(this.SolutionPath);
    public DateTime LoadedAtUtc { get; }
    public DateTime LastUsedUtc { get; private set; }
    public TimeSpan LoadDuration { get; init; }
    public IReadOnlyList<string> LoadDiagnostics { get; }

    public Solution Solution
    {
        get
        {
            lock (this._gate)
            {
                this.LastUsedUtc = DateTime.UtcNow;
                return this._solution;
            }
        }
    }

    public LoadedSolution(Microsoft.CodeAnalysis.Workspace? workspace, Solution solution, string solutionPath, IReadOnlyList<string> loadDiagnostics)
    {
        this._workspace = workspace;
        this._solution = solution;
        this.SolutionPath = solutionPath;
        this.LoadDiagnostics = loadDiagnostics;
        this.LoadedAtUtc = DateTime.UtcNow;
        this.LastUsedUtc = this.LoadedAtUtc;
        (this._excludedByProject, this._excluded) = ComputeExcludedSourceFiles(solution);
    }

    /// <summary>
    /// The <c>.cs</c> files under a project's directory that the project did not compile when it was loaded, typically
    /// because of a <c>Compile Remove</c> glob. A reconcile must not treat them as newly created files, and they must
    /// never be added to the live compilation.
    /// </summary>
    public IReadOnlyCollection<string> ExcludedSourceFilesOf(string projectFilePath)
    {
        return this._excludedByProject.TryGetValue(projectFilePath, out HashSet<string>? set) ? set : [];
    }

    public bool IsExcludedSourceFile(string path)
    {
        return this._excluded.Contains(path);
    }

    /// <summary>Files on disk under each project directory minus the documents of every project in the solution, measured once at load.</summary>
    private static (Dictionary<string, HashSet<string>> ByProject, HashSet<string> All) ComputeExcludedSourceFiles(Solution solution)
    {
        HashSet<string> documents = new(StringComparer.OrdinalIgnoreCase);
        foreach (Project project in solution.Projects)
        {
            foreach (Document document in project.Documents)
            {
                if (document.FilePath is not null)
                {
                    documents.Add(document.FilePath);
                }
            }
        }

        Dictionary<string, HashSet<string>> byProject = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> all = new(StringComparer.OrdinalIgnoreCase);
        foreach (Project project in solution.Projects)
        {
            string? dir = project.FilePath is null ? null : Path.GetDirectoryName(project.FilePath);
            if (project.FilePath is null || dir is null || !Directory.Exists(dir))
            {
                continue;
            }

            HashSet<string> excluded = new(StringComparer.OrdinalIgnoreCase);
            foreach (string file in IndexFreshness.EnumerateSourceFiles(dir))
            {
                if (!documents.Contains(file))
                {
                    excluded.Add(file);
                    all.Add(file);
                }
            }

            byProject[project.FilePath] = excluded;
        }

        return (byProject, all);
    }

    public static async Task<LoadedSolution> LoadAsync(string solutionPath, ILogger logger, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        MSBuildWorkspace workspace = MSBuildWorkspace.Create();
        workspace.LoadMetadataForReferencedProjects = false;
        List<string> failures = [];
        workspace.WorkspaceFailed += (_, e) =>
        {
            string line = $"{e.Diagnostic.Kind}: {e.Diagnostic.Message}";
            failures.Add(line);
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure && !IsNuGetAuditNoise(e.Diagnostic.Message))
            {
                logger.LogWarning("Workspace failure loading {Solution}: {Message}", solutionPath, e.Diagnostic.Message);
            }
        };

        Solution solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new LoadedSolution(workspace, solution, solutionPath, failures)
        {
            LoadDuration = DateTime.UtcNow - started,
        };
    }

    /// <summary>NuGet audit and source-mapping warnings surface as design-time build failures but do not affect loading.</summary>
    private static bool IsNuGetAuditNoise(string message)
    {
        return message.Contains("has a known", StringComparison.OrdinalIgnoreCase) && message.Contains("vulnerability", StringComparison.OrdinalIgnoreCase)
               || message.Contains("package source mapping", StringComparison.OrdinalIgnoreCase)
               || message.Contains("will not be pruned", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Document> FindDocuments(string filePath)
    {
        Solution solution = this.Solution;
        return solution.GetDocumentIdsWithFilePath(filePath)
            .Select(solution.GetDocument)
            .Where(d => d is not null)
            .Select(d => d!)
            .ToList();
    }

    /// <summary>Project whose directory is the longest prefix of the path. Used to home a newly created file.</summary>
    public Project? FindProjectForNewFile(string filePath)
    {
        Project? best = null;
        int bestLength = -1;
        foreach (Project project in this.Solution.Projects)
        {
            string? dir = Path.GetDirectoryName(project.FilePath);
            if (dir is null)
            {
                continue;
            }

            if (filePath.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && dir.Length > bestLength)
            {
                best = project;
                bestLength = dir.Length;
            }
        }

        return best;
    }

    /// <summary>
    /// Applies a file change to the in-memory solution. Pass <c>null</c> text for a deleted file.
    /// Returns the ids of the documents that were changed, added or removed.
    /// </summary>
    public IReadOnlyList<DocumentId> ApplyFileChange(string filePath, SourceText? text)
    {
        lock (this._gate)
        {
            this.LastUsedUtc = DateTime.UtcNow;
            List<DocumentId> ids = this._solution.GetDocumentIdsWithFilePath(filePath).ToList();

            if (text is null)
            {
                foreach (DocumentId id in ids)
                {
                    this._solution = this._solution.RemoveDocument(id);
                }

                return ids;
            }

            if (ids.Count == 0)
            {
                // The project deliberately leaves this file out of its compilation; adding it would make live
                // diagnostics and index rows disagree with a real build.
                if (this._excluded.Contains(filePath))
                {
                    return [];
                }

                Project? project = this.FindProjectForNewFile(filePath);
                if (project is null)
                {
                    return [];
                }

                DocumentId id = DocumentId.CreateNewId(project.Id, filePath);
                this._solution = this._solution.AddDocument(id, Path.GetFileName(filePath), text, filePath: filePath);
                return [id];
            }

            foreach (DocumentId id in ids)
            {
                this._solution = this._solution.WithDocumentText(id, text, PreservationMode.PreserveValue);
            }

            return ids;
        }
    }

    public void Dispose()
    {
        this._workspace?.Dispose();
    }
}
