namespace Balalaika.Index;

/// <summary>
/// The part of a document's extraction that other documents can bind against: every non-private declaration
/// (doc id, signature, accessibility) and every inherits / implements / overrides edge. Two contents with the same
/// surface hash declare the same things, so replacing one with the other cannot change how any other document binds.
/// Body-only edits, comments, usings and private members therefore do not count as a surface change.
/// </summary>
public static class DeclarationSurface
{
    /// <summary>Surface hash of a content that declares nothing (or only private members).</summary>
    public static readonly string EmptyHash = Hash([], []);

    public static string Hash(DocumentExtraction extraction)
    {
        return Hash(extraction.Symbols, extraction.Relations);
    }

    public static string Hash(IEnumerable<SymbolRecord> symbols, IEnumerable<RelationRecord> relations)
    {
        List<string> lines = [];
        foreach (SymbolRecord s in symbols)
        {
            if (IsPrivate(s.Accessibility))
            {
                continue;
            }

            lines.Add($"S\t{s.DocId}\t{s.Signature}\t{s.Accessibility}");
        }

        foreach (RelationRecord r in relations)
        {
            lines.Add($"R\t{r.FromDocId}\t{r.ToDocId}\t{r.ToAssembly}\t{r.Kind}");
        }

        lines.Sort(StringComparer.Ordinal);
        return ContentHasher.HashText(string.Join('\n', lines));
    }

    public static bool IsPrivate(string accessibility)
    {
        return accessibility.Equals("private", StringComparison.OrdinalIgnoreCase);
    }
}
