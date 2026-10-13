using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Balalaika.Index;

namespace Balalaika.Workspace;

public sealed record IndexReport(int ProjectsVisited, int DocumentsVisited, int DocumentsExtracted, int DocumentsReused, int TreeEntriesRemoved, TimeSpan Elapsed);

/// <summary>Outcome of indexing a batch of changed documents.</summary>
/// <param name="DocumentsExtracted">Documents whose content had no blob yet and were extracted.</param>
/// <param name="SurfaceChangedProjects">
/// Projects in which at least one changed document now declares something different from what the previous content
/// at the same path declared (see <see cref="DeclarationSurface"/>). Reference rows stored for other documents of
/// these projects and of their dependents may be stale.
/// </param>
public sealed record IncrementalIndexReport(int DocumentsExtracted, IReadOnlySet<ProjectId> SurfaceChangedProjects);

/// <summary>
/// Brings the index in line with a loaded solution. Per document: compute the content hash; if a blob for that hash
/// already exists the document is just re-pointed in the tree, otherwise it is extracted once.
/// </summary>
public sealed class SolutionIndexer(IndexStore index, ILogger<SolutionIndexer> logger, SourceFilter? filter = null)
{
    private readonly SourceFilter _filter = filter ?? SourceFilter.None;

    public async Task<IndexReport> IndexSolutionAsync(LoadedSolution loaded, bool force, CancellationToken cancellationToken)
    {
        DateTime started = DateTime.UtcNow;
        Solution solution = loaded.Solution;
        int projects = 0, visited = 0, extracted = 0, reused = 0, removed = 0;

        foreach (Project project in solution.Projects)
        {
            if (project.FilePath is null)
            {
                continue;
            }

            projects++;
            ProjectIndexCounts counts = await this.IndexProjectAsync(loaded, project, force, cancellationToken).ConfigureAwait(false);
            visited += counts.Visited;
            extracted += counts.Extracted;
            reused += counts.Reused;
            removed += counts.Removed;
        }

        IndexReport report = new(projects, visited, extracted, reused, removed, DateTime.UtcNow - started);
        logger.LogInformation(
            "Indexed {Solution}: {Projects} projects, {Visited} documents, {Extracted} extracted, {Reused} reused, {Removed} removed in {Elapsed:N1}s",
            loaded.Name, projects, visited, extracted, reused, removed, report.Elapsed.TotalSeconds);
        return report;
    }

    /// <summary>
    /// Re-indexes specific documents after an incremental text change and reports the projects whose declared surface
    /// changed in the process. The surface of the previous content at each path is compared with the surface of the
    /// new content; both are read from stored blobs, so a reverted file that reuses an old blob is compared too.
    /// </summary>
    public async Task<IncrementalIndexReport> IndexDocumentsAsync(LoadedSolution loaded, IEnumerable<DocumentId> documentIds, CancellationToken cancellationToken)
    {
        Solution solution = loaded.Solution;
        int extracted = 0;
        HashSet<ProjectId> surfaceChanged = [];
        foreach (DocumentId id in documentIds)
        {
            Document? document = solution.GetDocument(id);
            if (document?.FilePath is null || document.Project.FilePath is null || this._filter.IsIgnored(document.FilePath))
            {
                continue;
            }

            int projectId = index.EnsureProject(document.Project.FilePath, document.Project.Name, document.Project.AssemblyName, loaded.SolutionPath);
            string? previousHash = index.GetTreeEntry(projectId, document.FilePath)?.ContentHash;
            (long mtime, long size) = Stat(document.FilePath);
            (bool didExtract, string hash) = await this.IndexDocumentAsync(projectId, document, mtime, size, force: false, cancellationToken).ConfigureAwait(false);
            if (didExtract)
            {
                extracted++;
            }

            if (!string.Equals(previousHash, hash, StringComparison.Ordinal) && this.SurfaceDiffers(projectId, previousHash, hash))
            {
                surfaceChanged.Add(document.Project.Id);
            }
        }

        return new IncrementalIndexReport(extracted, surfaceChanged);
    }

    /// <summary>
    /// Drops a deleted file from the tree of every project that listed it. Returns the projects of
    /// <paramref name="loaded"/> whose declared surface shrank because the removed content declared something.
    /// </summary>
    public IReadOnlyCollection<ProjectId> RemoveDocument(LoadedSolution loaded, string path)
    {
        HashSet<ProjectId> surfaceChanged = [];
        IReadOnlyList<(ProjectRecord Project, TreeEntry Entry)> entries = index.FindTreeEntries(path);
        if (entries.Count == 0)
        {
            return surfaceChanged;
        }

        Solution solution = loaded.Solution;
        foreach ((ProjectRecord record, TreeEntry entry) in entries)
        {
            string? surface = index.GetSurfaceHash(record.Id, entry.ContentHash);
            if (surface is null || surface == DeclarationSurface.EmptyHash)
            {
                continue;
            }

            Project? project = solution.Projects.FirstOrDefault(p => string.Equals(p.FilePath, record.Path, StringComparison.OrdinalIgnoreCase));
            if (project is not null)
            {
                surfaceChanged.Add(project.Id);
            }
        }

        index.RemoveTreeEntriesByPath(path);
        return surfaceChanged;
    }

    /// <summary>
    /// Re-extracts every document of the given projects against the current compilation, replacing the blobs their
    /// content points at. Used after a dependency's declared surface changed: the stored reference rows may point at a
    /// symbol that no longer exists or at an overload that a new declaration now captures. Returns the document count.
    /// </summary>
    public async Task<int> ReextractProjectsAsync(LoadedSolution loaded, IEnumerable<ProjectId> projectIds, CancellationToken cancellationToken)
    {
        Solution solution = loaded.Solution;
        int extracted = 0;
        foreach (ProjectId id in projectIds)
        {
            Project? project = solution.GetProject(id);
            if (project?.FilePath is null)
            {
                continue;
            }

            ProjectIndexCounts counts = await this.IndexProjectAsync(loaded, project, force: true, cancellationToken).ConfigureAwait(false);
            extracted += counts.Extracted;
            logger.LogDebug("Re-extracted {Project}: {Count} documents", project.Name, counts.Extracted);
        }

        return extracted;
    }

    private readonly record struct ProjectIndexCounts(int Visited, int Extracted, int Reused, int Removed);

    /// <summary>Walks one project: stat fast path, blob reuse or extraction per document, then drops tree entries for documents the project no longer has.</summary>
    private async Task<ProjectIndexCounts> IndexProjectAsync(LoadedSolution loaded, Project project, bool force, CancellationToken cancellationToken)
    {
        int visited = 0, extracted = 0, reused = 0, removed = 0;
        int projectId = index.EnsureProject(project.FilePath!, project.Name, project.AssemblyName, loaded.SolutionPath);

        // Blobs written under an older schema lack columns a newer extractor fills (enclosing member, end line); the
        // content hash alone would reuse them forever, so a project stamped with an older version is re-extracted once.
        int indexedSchema = index.GetIndexedSchema(projectId);
        if (!force && indexedSchema > 0 && indexedSchema < IndexStore.SchemaVersion)
        {
            logger.LogInformation("{Project} was indexed with schema {Old}; re-extracting every document for schema {New}", project.Name, indexedSchema, IndexStore.SchemaVersion);
            force = true;
        }

        Compilation? compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        index.SetExcludedFiles(projectId, loaded.ExcludedSourceFilesOf(project.FilePath!));
        IReadOnlyDictionary<string, TreeEntry> tree = index.GetTree(projectId);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (Document document in project.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document.FilePath is null || !document.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || this._filter.IsIgnored(document.FilePath))
            {
                continue;
            }

            visited++;
            seen.Add(document.FilePath);

            (long mtime, long size) = Stat(document.FilePath);
            if (!force && tree.TryGetValue(document.FilePath, out TreeEntry? entry) && entry.Mtime == mtime && entry.Size == size && mtime != 0)
            {
                reused++;
                continue;
            }

            (bool didExtract, _) = await this.IndexDocumentAsync(projectId, document, mtime, size, force, cancellationToken).ConfigureAwait(false);
            if (didExtract)
            {
                extracted++;
            }
            else
            {
                reused++;
            }
        }

        List<string> stale = tree.Keys.Where(k => !seen.Contains(k)).ToList();
        if (stale.Count > 0)
        {
            index.RemoveTreeEntries(projectId, stale);
            removed = stale.Count;
        }

        // Stamped last, so a pass cancelled halfway leaves the project marked with the old schema and it is walked again.
        index.SetProjectPackages(projectId, compilation is null ? null : PackageReferences.ReadVersion(compilation), PackageReferences.Collect(project));
        return new ProjectIndexCounts(visited, extracted, reused, removed);
    }

    /// <summary>Returns whether the document was extracted (false when an existing blob was reused) and its content hash.</summary>
    private async Task<(bool Extracted, string Hash)> IndexDocumentAsync(int projectId, Document document, long mtime, long size, bool force, CancellationToken cancellationToken)
    {
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        string hash = ContentHasher.HashText(text.ToString());
        bool extracted = false;

        if (force || !index.HasBlob(projectId, hash))
        {
            DocumentExtraction extraction;
            try
            {
                extraction = await DocumentExtractor.ExtractAsync(document, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Extraction failed for {Path}", document.FilePath);
                extraction = DocumentExtraction.Empty;
            }

            index.WriteBlob(projectId, hash, extraction, replace: force);
            extracted = true;
        }

        index.SetTreeEntry(projectId, document.FilePath!, hash, mtime, size);
        return (extracted, hash);
    }

    /// <summary>Compares the declared surface of the blob the path pointed at before with the one it points at now. A missing blob counts as declaring nothing.</summary>
    private bool SurfaceDiffers(int projectId, string? previousHash, string currentHash)
    {
        string before = previousHash is null ? DeclarationSurface.EmptyHash : index.GetSurfaceHash(projectId, previousHash) ?? DeclarationSurface.EmptyHash;
        string after = index.GetSurfaceHash(projectId, currentHash) ?? DeclarationSurface.EmptyHash;
        return !string.Equals(before, after, StringComparison.Ordinal);
    }

    private static (long Mtime, long Size) Stat(string path)
    {
        try
        {
            FileInfo info = new(path);
            if (!info.Exists)
            {
                return (0, 0);
            }

            return (info.LastWriteTimeUtc.Ticks, info.Length);
        }
        catch (IOException)
        {
            return (0, 0);
        }
    }
}
