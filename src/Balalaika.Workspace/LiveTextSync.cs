using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Balalaika.Workspace;

/// <summary>
/// Brings one document of a loaded solution in line with the file on disk on demand, ahead of the watcher. A tool
/// called right after a save (a post-edit hook, an agent that edits and asks in the same breath) would otherwise see
/// the text from before the save: the watcher applies a change only after its debounce and a trip through the job
/// queue. Only the in-memory solution is touched here. The index catches up when the watcher's job runs, reads the
/// same text, and reuses or extracts as usual; a deletion is likewise counted there through <c>RemoveDocument</c>,
/// which works from the stored tree rows and not from the live document.
/// </summary>
public static class LiveTextSync
{
    /// <summary>
    /// Compares the file at <paramref name="path"/> with the live document(s) at that path and applies the disk text
    /// when they differ. A file that is gone removes its documents; a file the solution does not know yet is added
    /// through <see cref="LoadedSolution.ApplyFileChange"/>, which also keeps excluded files out. Returns the ids of the
    /// documents changed, added or removed; empty when the live text already matched or the file could not be read.
    /// </summary>
    public static async Task<IReadOnlyList<DocumentId>> SyncFromDiskAsync(LoadedSolution loaded, string path, CancellationToken ct)
    {
        IReadOnlyList<Document> documents = loaded.FindDocuments(path);
        if (!File.Exists(path))
        {
            return documents.Count == 0 ? [] : loaded.ApplyFileChange(path, null);
        }

        SourceText? disk = await ReadTextAsync(path, ct).ConfigureAwait(false);
        if (disk is null)
        {
            return [];
        }

        if (documents.Count > 0)
        {
            bool differs = false;
            foreach (Document document in documents)
            {
                SourceText live = await document.GetTextAsync(ct).ConfigureAwait(false);
                if (!live.ContentEquals(disk))
                {
                    differs = true;
                    break;
                }
            }

            if (!differs)
            {
                return [];
            }
        }

        return loaded.ApplyFileChange(path, disk);
    }

    /// <summary>
    /// Reads a file as decoded <see cref="SourceText"/>, retrying briefly while an editor still holds it open for
    /// writing. Returns <c>null</c> when the file stays locked or disappears between attempts.
    /// </summary>
    public static async Task<SourceText?> ReadTextAsync(string path, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return SourceText.From(stream);
            }
            catch (IOException)
            {
                await Task.Delay(50 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }

        return null;
    }
}
