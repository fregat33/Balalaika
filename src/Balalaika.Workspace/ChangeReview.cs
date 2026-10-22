using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Balalaika.Index;

namespace Balalaika.Workspace;

public enum DeclarationChangeKind
{
    Added,
    Removed,

    /// <summary>Same declaration, different signature, accessibility or parameter list.</summary>
    Signature,

    /// <summary>A type whose header changed: attributes, base list, primary constructor parameters or constraints. Its members are reported on their own.</summary>
    Header,

    /// <summary>Same signature, different tokens in the declaration.</summary>
    Body,
}

/// <summary>
/// One declaration that differs between the merge base and the working tree. <paramref name="Before"/> is the record
/// on the base side, <paramref name="After"/> on the current side; one of them is null for an addition or a removal.
/// <paramref name="HeaderDelta"/> lists what a type header gained or lost (<c>+ implements Ns.IMarker</c>,
/// <c>- [Obsolete]</c>); empty when the change is elsewhere in the header (primary constructor, constraints).
/// </summary>
public sealed record DeclarationChange(DeclarationChangeKind Kind, SymbolRecord? Before, SymbolRecord? After, IReadOnlyList<string>? HeaderDelta = null)
{
    public SymbolRecord Record => this.After ?? this.Before!;

    public string DocId => this.Record.DocId;

    public bool IsPrivate => DeclarationSurface.IsPrivate(this.Record.Accessibility);

    /// <summary>A surface change is one other code can bind differently to: anything but a body edit, on a non-private declaration.</summary>
    public bool IsSurface => !this.IsPrivate && this.Kind != DeclarationChangeKind.Body;
}

public enum ReviewedFileKind
{
    /// <summary>A <c>.cs</c> document of the solution, analysed.</summary>
    Source,

    /// <summary>A project, props, targets or solution file; listed, not analysed.</summary>
    Project,

    /// <summary>Anything else under the solution directory; listed by name.</summary>
    Other,
}

/// <summary>
/// A changed file of the review. <paramref name="BaseDocument"/> is the document of the fork that holds the base text
/// (null for an added file); <paramref name="LiveDocument"/> the document of the live solution (null for a deleted
/// file). <paramref name="DeclarationCount"/> is the number of declarations of an added or deleted file.
/// </summary>
public sealed record ReviewedFile(
    string Path,
    FileStatus Status,
    ReviewedFileKind Kind,
    bool IsTest,
    IReadOnlyList<DeclarationChange> Changes,
    bool SurfaceChanged,
    int DeclarationCount,
    Document? BaseDocument,
    Document? LiveDocument);

/// <summary>What <see cref="ChangeReview.AnalyzeAsync"/> found. <paramref name="BaseSolution"/> is the in-memory fork that holds the base side; it is never written anywhere.</summary>
public sealed record ReviewReport(
    string SolutionName,
    string BaseRef,
    string MergeBase,
    Solution BaseSolution,
    IReadOnlyList<ReviewedFile> Files,
    IReadOnlyList<Diagnostic> Diagnostics,
    int OutsideSolution);

/// <summary>
/// The branch review behind <c>review_changes</c>: the changed files since the merge base, a fork of the live
/// <see cref="Solution"/> with the base text in place of each changed document, the declarations that differ between
/// the two sides, and the compiler diagnostics of the changed files. The fork shares every unchanged tree with the live
/// snapshot and is dropped with the report; nothing is written to disk or to the index.
/// </summary>
public static class ChangeReview
{
    /// <summary>Added or deleted files list their members only up to this many declarations; larger files print a count and are left to <c>get_outline</c>.</summary>
    public const int ListedDeclarationsOfNewFile = 10;

    public static async Task<(ReviewReport? Report, string? Error)> AnalyzeAsync(
        LoadedSolution loaded,
        IBaseContentProvider git,
        SourceFilter filter,
        string? baseRef,
        CancellationToken cancellationToken)
    {
        string? unavailable = await git.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (unavailable is not null)
        {
            return (null, unavailable);
        }

        string? root = git.FindRepositoryRoot(loaded.SolutionPath);
        if (root is null)
        {
            return (null, $"{loaded.SolutionPath} is not inside a git repository.");
        }

        if (string.IsNullOrWhiteSpace(baseRef))
        {
            baseRef = await git.FindDefaultBaseAsync(root, cancellationToken).ConfigureAwait(false);
            if (baseRef is null)
            {
                return (null, $"No default branch found in {root}: origin/HEAD is not set and none of {string.Join(", ", IBaseContentProvider.DefaultBaseCandidates)} exists. Pass a branch, tag or commit as base.");
            }
        }

        (ChangeSet? changes, string? error) = await git.GetChangesAsync(root, baseRef, cancellationToken).ConfigureAwait(false);
        if (changes is null)
        {
            return (null, error ?? "git failed");
        }

        Solution live = loaded.Solution;
        Dictionary<string, List<Document>> documentsByPath = new(StringComparer.OrdinalIgnoreCase);
        foreach (Project project in live.Projects)
        {
            foreach (Document document in project.Documents)
            {
                if (document.FilePath is not null)
                {
                    if (!documentsByPath.TryGetValue(document.FilePath, out List<Document>? list))
                    {
                        list = [];
                        documentsByPath[document.FilePath] = list;
                    }

                    list.Add(document);
                }
            }
        }

        string solutionDir = Path.GetDirectoryName(loaded.SolutionPath) ?? "";
        HashSet<string> projectFiles = new(live.Projects.Select(p => p.FilePath).Where(p => p is not null).Select(p => p!), StringComparer.OrdinalIgnoreCase);

        // Pass 1: classify the files and build the fork.
        Solution fork = live;
        List<(ChangedFile File, ReviewedFileKind Kind, Document? Live, DocumentId? BaseId, Project? Project)> kept = [];
        int outside = 0;
        foreach (ChangedFile change in changes.Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            bool underSolution = change.Path.StartsWith(solutionDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (IsSourceFile(change.Path))
            {
                if (filter.IsIgnored(change.Path) || loaded.IsExcludedSourceFile(change.Path))
                {
                    continue;
                }

                documentsByPath.TryGetValue(change.Path, out List<Document>? liveDocs);
                Document? liveDoc = liveDocs?.FirstOrDefault();
                if (liveDoc is not null)
                {
                    FileStatus status = change.Status == FileStatus.Deleted ? FileStatus.Modified : change.Status;
                    DocumentId? baseId = null;
                    if (status == FileStatus.Added)
                    {
                        foreach (Document d in liveDocs!)
                        {
                            fork = fork.RemoveDocument(d.Id);
                        }
                    }
                    else
                    {
                        string? baseText = await git.GetBaseTextAsync(root, changes.MergeBase, change.Path, cancellationToken).ConfigureAwait(false);
                        if (baseText is null)
                        {
                            status = FileStatus.Added;
                            foreach (Document d in liveDocs!)
                            {
                                fork = fork.RemoveDocument(d.Id);
                            }
                        }
                        else
                        {
                            foreach (Document d in liveDocs!)
                            {
                                fork = fork.WithDocumentText(d.Id, SourceText.From(baseText), PreservationMode.PreserveValue);
                            }

                            baseId = liveDoc.Id;
                        }
                    }

                    kept.Add((change with { Status = status }, ReviewedFileKind.Source, liveDoc, baseId, liveDoc.Project));
                    continue;
                }

                Project? home = change.Status == FileStatus.Deleted ? loaded.FindProjectForNewFile(change.Path) : null;
                if (home is not null)
                {
                    string? baseText = await git.GetBaseTextAsync(root, changes.MergeBase, change.Path, cancellationToken).ConfigureAwait(false);
                    if (baseText is not null)
                    {
                        DocumentId id = DocumentId.CreateNewId(home.Id, change.Path);
                        fork = fork.AddDocument(id, Path.GetFileName(change.Path), SourceText.From(baseText), filePath: change.Path);
                        kept.Add((change, ReviewedFileKind.Source, null, id, home));
                        continue;
                    }
                }

                if (underSolution)
                {
                    kept.Add((change, ReviewedFileKind.Other, null, null, null));
                }
                else
                {
                    outside++;
                }

                continue;
            }

            if (IsProjectFile(change.Path) && (projectFiles.Contains(change.Path) || underSolution))
            {
                kept.Add((change, ReviewedFileKind.Project, null, null, null));
            }
            else if (underSolution)
            {
                kept.Add((change, ReviewedFileKind.Other, null, null, null));
            }
            else
            {
                outside++;
            }
        }

        // Pass 2: declarations on both sides, diagnostics on the current side.
        List<ReviewedFile> files = [];
        List<Diagnostic> diagnostics = [];
        foreach ((ChangedFile file, ReviewedFileKind kind, Document? liveDoc, DocumentId? baseId, Project? project) in kept)
        {
            bool isTest = project is not null && ProjectKinds.IsTestProject(project.Name, file.Path);
            if (kind != ReviewedFileKind.Source)
            {
                files.Add(new ReviewedFile(file.Path, file.Status, kind, isTest, [], false, 0, null, null));
                continue;
            }

            Document? baseDoc = baseId is null ? null : fork.GetDocument(baseId);
            (DocumentExtraction before, IReadOnlyDictionary<string, DeclarationText> beforeTexts) = await ExtractSideAsync(baseDoc, cancellationToken).ConfigureAwait(false);
            (DocumentExtraction after, IReadOnlyDictionary<string, DeclarationText> afterTexts) = await ExtractSideAsync(liveDoc, cancellationToken).ConfigureAwait(false);
            List<DeclarationChange> changesInFile = Compare(before, beforeTexts, after, afterTexts);
            bool surfaceChanged = DeclarationSurface.Hash(before) != DeclarationSurface.Hash(after);
            int declarationCount = file.Status == FileStatus.Deleted ? before.Symbols.Count : after.Symbols.Count;
            files.Add(new ReviewedFile(file.Path, file.Status, kind, isTest, changesInFile, surfaceChanged, declarationCount, baseDoc, liveDoc));

            if (liveDoc is not null)
            {
                SemanticModel? model = await liveDoc.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (model is not null)
                {
                    diagnostics.AddRange(model.GetDiagnostics(cancellationToken: cancellationToken).Where(d => !d.IsSuppressed && d.Severity >= DiagnosticSeverity.Warning));
                }
            }
        }

        List<Diagnostic> ordered = diagnostics
            .DistinctBy(d => (d.Id, d.Location.GetLineSpan().Path, d.Location.GetLineSpan().StartLinePosition, d.GetMessage()))
            .OrderByDescending(d => d.Severity)
            .ThenBy(d => d.Location.GetLineSpan().Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Location.GetLineSpan().StartLinePosition.Line)
            .ToList();

        return (new ReviewReport(loaded.Name, baseRef, changes.MergeBase, fork, files, ordered, outside), null);
    }

    /// <summary>
    /// Matches the two sides by doc id: present on one side only is an addition or a removal; the same id with a
    /// different signature or accessibility is a signature change; the same signature with different declaration
    /// tokens is a body change. Listed in current source order, removed declarations by their base line.
    /// </summary>
    public static List<DeclarationChange> Compare(
        DocumentExtraction before,
        IReadOnlyDictionary<string, DeclarationText> beforeTexts,
        DocumentExtraction after,
        IReadOnlyDictionary<string, DeclarationText> afterTexts)
    {
        Dictionary<string, SymbolRecord> beforeById = before.Symbols.GroupBy(s => s.DocId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        List<DeclarationChange> changes = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (SymbolRecord current in after.Symbols)
        {
            if (!seen.Add(current.DocId))
            {
                continue;
            }

            if (!beforeById.TryGetValue(current.DocId, out SymbolRecord? previous))
            {
                changes.Add(new DeclarationChange(DeclarationChangeKind.Added, null, current));
            }
            else if (previous.Signature != current.Signature || previous.Accessibility != current.Accessibility)
            {
                changes.Add(new DeclarationChange(DeclarationChangeKind.Signature, previous, current));
            }
            else if (beforeTexts.GetValueOrDefault(current.DocId)?.Text != afterTexts.GetValueOrDefault(current.DocId)?.Text)
            {
                bool isType = current.DocId.StartsWith("T:", StringComparison.Ordinal);
                changes.Add(isType
                    ? new DeclarationChange(DeclarationChangeKind.Header, previous, current, HeaderDeltaOf(current.DocId, before, beforeTexts, after, afterTexts))
                    : new DeclarationChange(DeclarationChangeKind.Body, previous, current));
            }
        }

        HashSet<string> removedIds = new(StringComparer.Ordinal);
        foreach (SymbolRecord previous in before.Symbols)
        {
            if (!seen.Contains(previous.DocId) && removedIds.Add(previous.DocId))
            {
                changes.Add(new DeclarationChange(DeclarationChangeKind.Removed, previous, null));
            }
        }

        // A parameter change gives the member a new doc id, so it arrives as one removal and one addition. A removed
        // and an added member of the same type, name and kind are one signature change; a rename stays - and +.
        foreach (DeclarationChange removed in changes.Where(c => c.Kind == DeclarationChangeKind.Removed).ToList())
        {
            DeclarationChange? added = changes.FirstOrDefault(c =>
                c.Kind == DeclarationChangeKind.Added
                && c.Record.Kind == removed.Record.Kind
                && c.Record.Name == removed.Record.Name
                && c.Record.ContainingDocId == removed.Record.ContainingDocId
                && c.Record.FqName == removed.Record.FqName);
            if (added is not null)
            {
                changes.Remove(removed);
                changes.Remove(added);
                changes.Add(new DeclarationChange(DeclarationChangeKind.Signature, removed.Before, added.After));
            }
        }

        changes.Sort((a, b) =>
        {
            int c = a.Record.Line.CompareTo(b.Record.Line);
            if (c == 0)
            {
                c = a.Record.Column.CompareTo(b.Record.Column);
            }

            return c != 0 ? c : string.CompareOrdinal(a.DocId, b.DocId);
        });
        return changes;
    }

    /// <summary>
    /// What a type header gained or lost: base-list edges from the relations of both extractions (<c>+ implements
    /// Ns.IMarker</c>, <c>- inherits Ns.Base</c>) and attributes as written (<c>+ [Obsolete]</c>). Empty when the
    /// change is in the primary constructor parameters or the constraints, which neither side records.
    /// </summary>
    public static List<string> HeaderDeltaOf(
        string docId,
        DocumentExtraction before,
        IReadOnlyDictionary<string, DeclarationText> beforeTexts,
        DocumentExtraction after,
        IReadOnlyDictionary<string, DeclarationText> afterTexts)
    {
        List<string> delta = [];
        HashSet<(string Kind, string To)> beforeEdges = before.Relations.Where(r => r.FromDocId == docId).Select(r => (r.Kind, r.ToDocId)).ToHashSet();
        HashSet<(string Kind, string To)> afterEdges = after.Relations.Where(r => r.FromDocId == docId).Select(r => (r.Kind, r.ToDocId)).ToHashSet();
        foreach ((string kind, string to) in afterEdges.Where(e => !beforeEdges.Contains(e)).OrderBy(e => e.To, StringComparer.Ordinal))
        {
            delta.Add($"+ {kind} {EnclosingDisplay.QualifiedNameOf(to)}");
        }

        foreach ((string kind, string to) in beforeEdges.Where(e => !afterEdges.Contains(e)).OrderBy(e => e.To, StringComparer.Ordinal))
        {
            delta.Add($"- {kind} {EnclosingDisplay.QualifiedNameOf(to)}");
        }

        IReadOnlyList<string> beforeAttributes = beforeTexts.GetValueOrDefault(docId)?.Attributes ?? [];
        IReadOnlyList<string> afterAttributes = afterTexts.GetValueOrDefault(docId)?.Attributes ?? [];
        delta.AddRange(afterAttributes.Except(beforeAttributes, StringComparer.Ordinal).Select(a => $"+ [{a}]"));
        delta.AddRange(beforeAttributes.Except(afterAttributes, StringComparer.Ordinal).Select(a => $"- [{a}]"));
        return delta;
    }

    /// <summary>
    /// Prints the report. <paramref name="impact"/> supplies the indented line under a declaration (reference counts,
    /// callers), null for none; it is computed by the caller because it needs the index and the other loaded solutions.
    /// Declaration, file and diagnostic lines go through the pager; the header and the total do not.
    /// </summary>
    public static string Format(ReviewReport report, bool includeTests, Func<ReviewedFile, DeclarationChange, string?> impact, ResultPager pager, int limit)
    {
        List<ReviewedFile> shown = includeTests ? report.Files.ToList() : report.Files.Where(f => !f.IsTest).ToList();
        int hiddenTests = report.Files.Count - shown.Count;
        int source = shown.Count(f => f.Kind == ReviewedFileKind.Source);
        int project = shown.Count(f => f.Kind == ReviewedFileKind.Project);
        int other = shown.Count(f => f.Kind == ReviewedFileKind.Other);

        StringBuilder sb = new();
        sb.Append("Review: ").Append(report.SolutionName).Append("  working tree vs ").Append(report.BaseRef)
          .Append(" (merge-base ").Append(report.MergeBase.Length > 7 ? report.MergeBase[..7] : report.MergeBase).Append(")  ")
          .Append(shown.Count).Append(" files changed: ").Append(source).Append(" source, ").Append(project).Append(" project, ").Append(other).Append(" other");
        if (hiddenTests > 0)
        {
            sb.Append("; ").Append(hiddenTests).Append(" in test projects hidden (includeTests=true shows them)");
        }

        if (report.OutsideSolution > 0)
        {
            sb.Append("; ").Append(report.OutsideSolution).Append(" outside the solution ignored");
        }

        sb.AppendLine();

        List<string> lines = [];
        int surface = 0;
        int bodyOnly = 0;
        int declarations = 0;
        foreach (ReviewedFile file in shown)
        {
            string status = file.Status.ToString().ToLowerInvariant();
            switch (file.Kind)
            {
                case ReviewedFileKind.Project:
                    lines.Add($"{file.Path}  {status}  (project file, not analysed; run reindex force=true if a package version changed)");
                    continue;
                case ReviewedFileKind.Other:
                    lines.Add($"{file.Path}  {status}  (not a source file of the solution)");
                    continue;
            }

            bool wholeFile = file.Status != FileStatus.Modified;
            if (wholeFile && file.DeclarationCount > ListedDeclarationsOfNewFile)
            {
                lines.Add($"{file.Path}  {status}  ({file.DeclarationCount} declarations; get_outline lists them)");
                declarations += file.Changes.Count;
                surface += file.Changes.Count(c => c.IsSurface);
                bodyOnly += file.Changes.Count(c => !c.IsSurface);
                continue;
            }

            string label = file.Status == FileStatus.Modified ? (file.SurfaceChanged ? "  surface" : "  body-only") : "";
            lines.Add($"{file.Path}  {status}{label}");
            foreach (DeclarationChange change in file.Changes)
            {
                declarations++;
                if (change.IsSurface)
                {
                    surface++;
                }
                else
                {
                    bodyOnly++;
                }

                lines.Add("  " + DescribeChange(change));
                string? extra = impact(file, change);
                if (!string.IsNullOrEmpty(extra))
                {
                    lines.Add("      " + extra);
                }
            }
        }

        int errors = report.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warnings = report.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        if (report.Diagnostics.Count > 0)
        {
            lines.Add($"Diagnostics on changed files: {errors} errors, {warnings} warnings (compiler only; analyzers are not run)");
            foreach (Diagnostic d in report.Diagnostics)
            {
                FileLinePositionSpan span = d.Location.GetLineSpan();
                string where = span.Path.Length > 0 ? $"{span.Path}:{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1}  " : "";
                lines.Add($"  {where}{d.Severity.ToString().ToLowerInvariant()} {d.Id}: {d.GetMessage()}");
            }
        }

        pager.AppendPage(sb, lines, limit, $"review_changes {report.SolutionName} vs {report.BaseRef}");
        sb.Append("Total: ").Append(source).Append(" source files, ").Append(declarations).Append(" changed declarations (")
          .Append(surface).Append(" surface, ").Append(bodyOnly).Append(" body-only or private), ")
          .Append(errors).Append(" errors, ").Append(warnings).Append(" warnings");
        return sb.ToString();
    }

    /// <summary>
    /// One declaration line: prefix, kind, a spec the other tools accept (<c>Ns.Type.Member(paramTypes)</c>),
    /// accessibility, and the line span. A signature change also prints the old and the new signature.
    /// </summary>
    public static string DescribeChange(DeclarationChange change)
    {
        SymbolRecord r = change.Record;
        string span = r.EndLine > 0 && r.EndLine != r.Line ? $"{r.Line}-{r.EndLine}" : r.Line.ToString();
        return change.Kind switch
        {
            DeclarationChangeKind.Added => $"+ {r.Kind}  {Spec(r)}  {r.Accessibility}  {span}",
            DeclarationChangeKind.Removed => $"- {r.Kind}  {Spec(r)}  {r.Accessibility}  was {span}",
            DeclarationChangeKind.Signature => $"~ signature  {Spec(r)}  {SignatureDelta(change.Before!, r)}  {span}",
            DeclarationChangeKind.Header => $"~ header  {Spec(r)}  {r.Accessibility}  {span}  {(change.HeaderDelta is { Count: > 0 } delta ? string.Join("; ", delta) : "(primary constructor or constraints)")}",
            _ => $"~ body  {Spec(r)}  {r.Accessibility}  {span}",
        };
    }

    /// <summary>
    /// <c>old -> new</c> for a signature change. When the two signatures print the same (a parameter type of the same
    /// short name moved to another namespace, or the doc id changed for another reason the display hides), the fully
    /// qualified parameter lists of the doc ids are printed instead, so the line still shows what differs.
    /// </summary>
    public static string SignatureDelta(SymbolRecord before, SymbolRecord after)
    {
        if (before.Signature != after.Signature)
        {
            return before.Signature + " -> " + after.Signature;
        }

        return "parameter types " + DocIdParameters(before.DocId) + " -> " + DocIdParameters(after.DocId);
    }

    private static string DocIdParameters(string docId)
    {
        int open = docId.IndexOf('(');
        if (open < 0 || !docId.EndsWith(')'))
        {
            return "()";
        }

        return "(" + docId[(open + 1)..^1].Replace('{', '<').Replace('}', '>') + ")";
    }

    /// <summary>A count with its production and test halves when any of it is in a test project: <c>25 (2 production, 23 tests)</c>, else just the number.</summary>
    public static string SplitCount(int total, int tests)
    {
        return tests > 0 ? $"{total} ({total - tests} production, {tests} tests)" : total.ToString();
    }

    /// <summary>
    /// A caller count with its halves: <c>3 (1 direct, 2 via base or interface members)</c>, <c>25 (2 production, 23 tests)</c>,
    /// both joined by <c>; </c> when both apply, else just the number.
    /// </summary>
    public static string SplitCallers(int total, int via, int tests)
    {
        List<string> parts = [];
        if (via > 0)
        {
            parts.Add($"{total - via} direct, {via} via base or interface members");
        }

        if (tests > 0)
        {
            parts.Add($"{total - tests} production, {tests} tests");
        }

        return parts.Count == 0 ? total.ToString() : $"{total} ({string.Join("; ", parts)})";
    }

    /// <summary>Fully qualified name plus the parameter types for members that have a parameter list, the form <c>SymbolResolver</c> picks an overload by.</summary>
    public static string Spec(SymbolRecord record)
    {
        string types = ParameterTypes(record.Signature);
        return types is null ? record.FqName : record.FqName + "(" + types + ")";
    }

    /// <summary>The parameter types of a signature like <c>public void Run(int times, string? name = null)</c>: <c>int, string?</c>. Null when the signature has no parameter list.</summary>
    public static string? ParameterTypes(string signature)
    {
        int open = signature.IndexOf('(');
        if (open < 0)
        {
            return null;
        }

        int depth = 0;
        int close = -1;
        for (int i = open; i < signature.Length; i++)
        {
            if (signature[i] == '(')
            {
                depth++;
            }
            else if (signature[i] == ')' && --depth == 0)
            {
                close = i;
                break;
            }
        }

        if (close < 0)
        {
            return null;
        }

        string inner = signature[(open + 1)..close];
        if (inner.Trim().Length == 0)
        {
            return "";
        }

        List<string> types = [];
        foreach (string part in SplitTopLevel(inner))
        {
            string p = part;
            int eq = p.IndexOf('=');
            if (eq >= 0)
            {
                p = p[..eq];
            }

            p = p.Trim();
            foreach (string modifier in new[] { "params ", "ref readonly ", "ref ", "out ", "in ", "this ", "scoped " })
            {
                if (p.StartsWith(modifier, StringComparison.Ordinal))
                {
                    p = p[modifier.Length..].TrimStart();
                }
            }

            int lastSpace = p.LastIndexOf(' ');
            types.Add(lastSpace > 0 ? p[..lastSpace].Trim() : p);
        }

        return string.Join(", ", types);
    }

    private static IEnumerable<string> SplitTopLevel(string text)
    {
        int depth = 0;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<' or '(' or '[':
                    depth++;
                    break;
                case '>' or ')' or ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return text[start..i];
                    start = i + 1;
                    break;
            }
        }

        yield return text[start..];
    }

    private static async Task<(DocumentExtraction Extraction, IReadOnlyDictionary<string, DeclarationText> Texts)> ExtractSideAsync(Document? document, CancellationToken cancellationToken)
    {
        if (document is null)
        {
            return (DocumentExtraction.Empty, new Dictionary<string, DeclarationText>(StringComparer.Ordinal));
        }

        SemanticModel? model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (model is null || root is null)
        {
            return (DocumentExtraction.Empty, new Dictionary<string, DeclarationText>(StringComparer.Ordinal));
        }

        return (DocumentExtractor.Extract(model, root, cancellationToken), DocumentExtractor.DeclarationTexts(model, root, cancellationToken));
    }

    private static bool IsSourceFile(string path)
    {
        return path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProjectFile(string path)
    {
        string name = Path.GetFileName(path);
        return name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
    }
}
