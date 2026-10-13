using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Balalaika.Index;

namespace Balalaika.Workspace;

/// <summary>
/// Implements every MCP tool as a method returning compact text. Live Roslyn results come first; results from the
/// SQLite index are appended for solutions that are not loaded and are marked as such.
/// </summary>
public sealed class QueryService(
    WorkspaceManager workspaces,
    SymbolResolver resolver,
    IndexStore index,
    IndexJobQueue jobs,
    ResultPager pager,
    WarmUpTracker warmUp,
    PendingRefreshes pendingRefreshes,
    IOptions<BalalaikaOptions> options,
    SourceFilter filter,
    IBaseContentProvider git,
    ILogger<QueryService> logger)
{
    private TimeSpan Wait => TimeSpan.FromSeconds(options.Value.LoadWaitSeconds);

    // ------------------------------------------------------------------ find_references

    public async Task<string> FindReferencesAsync(string spec, string? solution, bool allSolutions, string? kind, string? scope, int limit, CancellationToken ct)
    {
        string? badFilter = ReferenceFilter.TryParse(kind, scope, out ReferenceFilter criteria);
        if (badFilter is not null)
        {
            return "find_references: " + badFilter;
        }

        ResolvedSymbol? r = await resolver.ResolveAsync(spec, solution, ct).ConfigureAwait(false);
        if (r is null)
        {
            return this.NotFoundWithIndexFallback(spec, limit);
        }

        StringBuilder sb = new();
        this.AppendHeader(sb, r);
        string docId = r.Symbol.GetDocumentationCommentId() ?? "";
        string? assembly = SymbolIdentity.AssemblyOf(r.Symbol);
        HashSet<string> liveSolutions = new(StringComparer.OrdinalIgnoreCase);
        int total = 0;

        total += await this.AppendLiveReferencesAsync(sb, r.Symbol, r.Solution, criteria, limit, ct).ConfigureAwait(false);
        liveSolutions.Add(r.Solution.SolutionPath);

        if (allSolutions)
        {
            foreach (LoadedSolution other in workspaces.Loaded)
            {
                if (liveSolutions.Contains(other.SolutionPath) || docId.Length == 0)
                {
                    continue;
                }

                ISymbol? twin = await SymbolResolver.FindTwinAsync(docId, assembly, other, ct).ConfigureAwait(false);
                liveSolutions.Add(other.SolutionPath);
                if (twin is not null)
                {
                    total += await this.AppendLiveReferencesAsync(sb, twin, other, criteria, limit, ct).ConfigureAwait(false);
                }
            }

            if (docId.Length > 0)
            {
                total += this.AppendIndexReferences(sb, docId, assembly, liveSolutions, criteria, limit);
            }
        }

        sb.Append("Total: ").Append(total).Append(" references");
        if (criteria.IsActive)
        {
            sb.Append(" (").Append(criteria.Describe()).Append(')');
        }

        if (!allSolutions)
        {
            sb.Append(" (defining solution only; pass allSolutions=true to include other repositories)");
        }

        return sb.ToString();
    }

    private async Task<int> AppendLiveReferencesAsync(StringBuilder sb, ISymbol symbol, LoadedSolution loaded, ReferenceFilter criteria, int limit, CancellationToken ct)
    {
        IEnumerable<ReferencedSymbol> referenced = await SymbolFinder.FindReferencesAsync(symbol, loaded.Solution, ct).ConfigureAwait(false);
        List<(string Path, int Line, int Col, string Project, string Kind, string? EnclosingDocId, string? Enclosing)> hits = [];
        HashSet<(string, int, int)> seen = [];
        Dictionary<DocumentId, (SyntaxNode Root, SemanticModel Model)?> trees = [];
        int unfiltered = 0;
        foreach (ReferencedSymbol rs in referenced)
        {
            foreach (ReferenceLocation loc in rs.Locations)
            {
                if (loc.IsImplicit || !loc.Location.IsInSource)
                {
                    continue;
                }

                FileLinePositionSpan span = loc.Location.GetLineSpan();
                string path = span.Path;
                if (filter.IsIgnored(path))
                {
                    continue;
                }

                int line = span.StartLinePosition.Line + 1;
                int col = span.StartLinePosition.Character + 1;
                if (!seen.Add((path, line, col)))
                {
                    continue;
                }

                // The scope is known before classification, which needs the semantic model; skip the work for hits outside it.
                unfiltered++;
                string project = loc.Document.Project.Name;
                if (!criteria.MatchesScope(project, path))
                {
                    continue;
                }

                (string kind, string? enclosingDocId, string? enclosing) = await DescribeUsageAsync(loc, trees, ct).ConfigureAwait(false);
                if (criteria.MatchesKind(kind))
                {
                    hits.Add((path, line, col, project, kind, enclosingDocId, enclosing));
                }
            }
        }

        hits.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase) is var c && c != 0 ? c : a.Line.CompareTo(b.Line));
        sb.Append("Live ").Append(loaded.Name).Append(": ").Append(CountLabel(hits.Count, unfiltered, criteria)).Append(" references")
          .Append(criteria.IsActive ? " (" + criteria.Describe() + ")" : "")
          .Append(this.DriftTag(SymbolIdentity.AssemblyOf(symbol), loaded.SolutionPath, unfiltered == 0)).AppendLine();
        IReadOnlyDictionary<string, string> names = EnclosingDisplay.Disambiguate(hits.Where(h => h.EnclosingDocId is not null && h.Enclosing is not null).Select(h => (h.EnclosingDocId!, h.Enclosing!)));
        List<string> lines = hits.Select(h => $"  {h.Path}:{h.Line}:{h.Col}  {h.Project}  {h.Kind}{EnclosingSuffix(h.EnclosingDocId, names)}").ToList();
        pager.AppendPage(sb, lines, limit, $"find_references {symbol.Name} in {loaded.Name}");
        return hits.Count;
    }

    /// <summary>
    /// Usage kind and enclosing declaration of one live hit, from the node the location's token belongs to. Syntax
    /// roots and semantic models are cached per document for the duration of one call; hits cluster in few files.
    /// </summary>
    private static async Task<(string Kind, string? EnclosingDocId, string? Enclosing)> DescribeUsageAsync(ReferenceLocation loc, Dictionary<DocumentId, (SyntaxNode Root, SemanticModel Model)?> trees, CancellationToken ct)
    {
        if (!trees.TryGetValue(loc.Document.Id, out (SyntaxNode Root, SemanticModel Model)? tree))
        {
            SyntaxNode? root = await loc.Document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            SemanticModel? model = await loc.Document.GetSemanticModelAsync(ct).ConfigureAwait(false);
            tree = root is null || model is null ? null : (root, model);
            trees[loc.Document.Id] = tree;
        }

        if (tree is null)
        {
            return (ReferenceKinds.Usage, null, null);
        }

        SyntaxNode node = tree.Value.Root.FindToken(loc.Location.SourceSpan.Start).Parent ?? tree.Value.Root;
        (string? docId, string? display) = EnclosingMember.Describe(tree.Value.Model, node, ct);
        return (DocumentExtractor.UsageKindOf(node), docId, display);
    }

    private static string EnclosingSuffix(string? enclosingDocId, IReadOnlyDictionary<string, string> names)
    {
        return enclosingDocId is not null && names.TryGetValue(enclosingDocId, out string? name) ? "  in " + name : "";
    }

    /// <summary>Hit count of one group: <c>N</c>, or <c>N of M</c> when a filter cut it down.</summary>
    private static string CountLabel(int kept, int unfiltered, ReferenceFilter criteria)
    {
        return criteria.IsActive ? $"{kept} of {unfiltered}" : kept.ToString();
    }

    /// <summary>The scope test for a call-graph edge: the project of the declaration on the other end; a metadata callee has none and counts as production.</summary>
    private static bool InScope(CallSite site, ReferenceFilter criteria)
    {
        return criteria.MatchesScope(site.Document?.Project.Name ?? "", site.Document?.FilePath ?? "");
    }

    private int AppendIndexReferences(StringBuilder sb, string docId, string? assembly, HashSet<string> excludeSolutions, ReferenceFilter criteria, int limit)
    {
        // Fetch generously: the group count and the pager's "N more" must reflect every indexed hit, not a multiple of the page size.
        IReadOnlyList<ReferenceHit> hits = index.FindReferences(docId, assembly, Math.Max(limit * 4, 500), criteria);
        IReadOnlyDictionary<string, int> unfilteredCounts = criteria.IsActive ? index.CountReferencesBySolution(docId, assembly) : new Dictionary<string, int>();
        Dictionary<string, string> staleCache = new(StringComparer.OrdinalIgnoreCase);
        int total = 0;

        // With a filter, a solution whose every hit was cut still gets its group line, so "0 of 12" is visible.
        List<string> solutionPaths = hits.Select(h => h.SolutionPath).Concat(unfilteredCounts.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Where(s => !excludeSolutions.Contains(s)).ToList();
        foreach (string solutionPath in solutionPaths)
        {
            List<ReferenceHit> list = hits.Where(h => h.SolutionPath.Equals(solutionPath, StringComparison.OrdinalIgnoreCase)).ToList();
            total += list.Count;
            string count = CountLabel(list.Count, unfilteredCounts.GetValueOrDefault(solutionPath, list.Count), criteria);
            sb.Append("Index ").Append(SolutionDiscovery.SolutionName(solutionPath)).Append(": ").Append(count).Append(" references (")
              .Append(criteria.IsActive ? criteria.Describe() + "; " : "").Append("solution not loaded; from index)")
              .Append(this.DriftTag(assembly, solutionPath, noHits: false)).AppendLine();
            IReadOnlyDictionary<string, string> names = EnclosingDisplay.Disambiguate(list.Where(h => h.FromDocId is not null && h.Enclosing is not null).Select(h => (h.FromDocId!, h.Enclosing!)));
            List<string> lines = [];
            foreach (ReferenceHit h in list)
            {
                string stale = this.IsStale(h.FilePath, h.ContentHash, staleCache) ? "  [stale: file changed since indexed]" : "";
                lines.Add($"  {h.FilePath}:{h.Line}:{h.Column}  {h.ProjectName}  {h.Kind}{EnclosingSuffix(h.FromDocId, names)}{stale}");
            }

            pager.AppendPage(sb, lines, limit, $"find_references {docId} in {SolutionDiscovery.SolutionName(solutionPath)} (index)");
        }

        return total;
    }

    private string NotFoundWithIndexFallback(string spec, int limit)
    {
        StringBuilder sb = new();
        if (SymbolResolver.TryParsePosition(spec, out string positionPath, out _, out _))
        {
            sb.Append("No symbol at ").Append(spec).AppendLine();
            string? owner = workspaces.FindSolutionForPath(Path.GetFullPath(positionPath));
            if (owner is null)
            {
                sb.AppendLine("The file is not under any known solution or indexed project directory.");
            }
            else if (!workspaces.TryGetLoaded(owner, out _))
            {
                sb.Append(SolutionDiscovery.SolutionName(owner)).AppendLine(" is not loaded yet; call load_solution or retry.");
            }
            else
            {
                sb.AppendLine("Point the column at an identifier, or omit the column to take the first declaration or reference on that line.");
            }

            return sb.ToString();
        }

        sb.Append("Symbol not found in any loaded solution: ").Append(spec).AppendLine();
        IReadOnlyList<SymbolHit> hits = SymbolResolver.LooksLikeDocId(spec) ? index.FindSymbolsByDocId(spec) : index.SearchSymbols(spec, null, 10);
        if (hits.Count > 0)
        {
            sb.AppendLine("Index knows these candidates (pass the doc id or fully qualified name):");
            foreach (SymbolHit h in hits.Take(10))
            {
                sb.Append("  ").Append(h.DocId).Append("  ").Append(h.Kind).Append("  ").Append(h.FilePath).Append(':').Append(h.Line).Append("  (").Append(SolutionDiscovery.SolutionName(h.SolutionPath)).Append(')').AppendLine();
            }
        }
        else
        {
            sb.AppendLine("Tip: use search_symbols to find the exact name, or load_solution if the owning solution is not loaded yet.");
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ find_implementations / find_derived_types

    public async Task<string> FindImplementationsAsync(string spec, string? solution, bool typesOnly, int limit, CancellationToken ct)
    {
        ResolvedSymbol? r = await resolver.ResolveAsync(spec, solution, ct).ConfigureAwait(false);
        if (r is null)
        {
            return this.NotFoundWithIndexFallback(spec, limit);
        }

        if (typesOnly && r.Symbol is not INamedTypeSymbol)
        {
            return $"{spec} resolved to a {DisplayFormats.KindOf(r.Symbol)}, not a type. Use find_implementations for members.";
        }

        StringBuilder sb = new();
        this.AppendHeader(sb, r);
        string docId = r.Symbol.GetDocumentationCommentId() ?? "";
        string? assembly = SymbolIdentity.AssemblyOf(r.Symbol);
        HashSet<string> liveSolutions = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seen = new(StringComparer.Ordinal);
        int total = 0;

        total += await this.AppendLiveImplementationsAsync(sb, r.Symbol, r.Solution, seen, limit, ct).ConfigureAwait(false);
        liveSolutions.Add(r.Solution.SolutionPath);

        foreach (LoadedSolution other in workspaces.Loaded)
        {
            if (liveSolutions.Contains(other.SolutionPath) || docId.Length == 0)
            {
                continue;
            }

            ISymbol? twin = await SymbolResolver.FindTwinAsync(docId, assembly, other, ct).ConfigureAwait(false);
            liveSolutions.Add(other.SolutionPath);
            if (twin is not null)
            {
                total += await this.AppendLiveImplementationsAsync(sb, twin, other, seen, limit, ct).ConfigureAwait(false);
            }
        }

        if (docId.Length > 0)
        {
            IReadOnlyList<RelationHit> hits = index.FindRelations(docId, [RelationKinds.Implements, RelationKinds.Inherits, RelationKinds.Overrides], Math.Max(limit * 4, 500));
            Dictionary<string, string> staleCache = new(StringComparer.OrdinalIgnoreCase);
            foreach (IGrouping<string, RelationHit> group in hits.Where(h => !liveSolutions.Contains(h.SolutionPath)).GroupBy(h => h.SolutionPath, StringComparer.OrdinalIgnoreCase))
            {
                List<RelationHit> list = group.ToList();
                total += list.Count;
                sb.Append("Index ").Append(SolutionDiscovery.SolutionName(group.Key)).Append(": ").Append(list.Count).Append(" (solution not loaded; from index)")
                  .Append(this.DriftTag(assembly, group.Key, noHits: false)).AppendLine();
                List<string> lines = [];
                foreach (RelationHit h in list)
                {
                    string stale = h.FilePath is not null && this.IsStale(h.FilePath, h.ContentHash, staleCache) ? "  [stale]" : "";
                    lines.Add($"  {h.Kind}  {h.Signature ?? h.FromDocId}  {h.FilePath}:{h.Line}{stale}");
                }

                pager.AppendPage(sb, lines, limit, $"find_implementations {docId} in {SolutionDiscovery.SolutionName(group.Key)} (index)");
            }
        }

        sb.Append("Total: ").Append(total);
        return sb.ToString();
    }

    private async Task<int> AppendLiveImplementationsAsync(StringBuilder sb, ISymbol symbol, LoadedSolution loaded, HashSet<string> seen, int limit, CancellationToken ct)
    {
        List<(string Kind, ISymbol Symbol)> found = await FindLiveImplementationsAsync(symbol, loaded.Solution, ct).ConfigureAwait(false);
        List<(string Kind, ISymbol Symbol)> fresh = found
            .Where(f => !this.IsDeclaredOnlyInIgnoredFiles(f.Symbol))
            .Where(f => seen.Add(f.Symbol.GetDocumentationCommentId() ?? f.Symbol.ToDisplayString()))
            .ToList();
        sb.Append("Live ").Append(loaded.Name).Append(": ").Append(fresh.Count)
          .Append(this.DriftTag(SymbolIdentity.AssemblyOf(symbol), loaded.SolutionPath, fresh.Count == 0)).AppendLine();
        List<string> lines = fresh
            .OrderBy(f => f.Symbol.ToDisplayString(DisplayFormats.FullyQualified))
            .Select(f => $"  {f.Kind}  {DisplayFormats.KindOf(f.Symbol)} {f.Symbol.ToDisplayString(DisplayFormats.FullyQualified)}  {FirstLocation(f.Symbol)}")
            .ToList();
        pager.AppendPage(sb, lines, limit, $"find_implementations {symbol.Name} in {loaded.Name}");
        return fresh.Count;
    }

    /// <summary>Implementations, derived types or overrides of a symbol in one solution, with the relation kind of each; the live half of <c>find_implementations</c>.</summary>
    private static async Task<List<(string Kind, ISymbol Symbol)>> FindLiveImplementationsAsync(ISymbol symbol, Solution solution, CancellationToken ct)
    {
        List<(string Kind, ISymbol Symbol)> found = [];

        if (symbol is INamedTypeSymbol type)
        {
            if (type.TypeKind == TypeKind.Interface)
            {
                foreach (INamedTypeSymbol impl in await SymbolFinder.FindImplementationsAsync(type, solution, transitive: true, cancellationToken: ct).ConfigureAwait(false))
                {
                    found.Add((RelationKinds.Implements, impl));
                }

                foreach (INamedTypeSymbol derived in await SymbolFinder.FindDerivedInterfacesAsync(type, solution, transitive: true, cancellationToken: ct).ConfigureAwait(false))
                {
                    found.Add((RelationKinds.Inherits, derived));
                }
            }
            else
            {
                foreach (INamedTypeSymbol derived in await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, cancellationToken: ct).ConfigureAwait(false))
                {
                    found.Add((RelationKinds.Inherits, derived));
                }
            }
        }
        else
        {
            foreach (ISymbol impl in await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: ct).ConfigureAwait(false))
            {
                found.Add((RelationKinds.Implements, impl));
            }

            foreach (ISymbol over in await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: ct).ConfigureAwait(false))
            {
                found.Add((RelationKinds.Overrides, over));
            }
        }

        return found;
    }

    // ------------------------------------------------------------------ get_call_graph

    public async Task<string> GetCallGraphAsync(string spec, string? solution, string direction, bool includeExternal, string? scope, int limit, CancellationToken ct)
    {
        string? badFilter = ReferenceFilter.TryParse(kind: null, scope, out ReferenceFilter criteria);
        if (badFilter is not null)
        {
            return "get_call_graph: " + badFilter;
        }

        ResolvedSymbol? r = await resolver.ResolveAsync(spec, solution, ct).ConfigureAwait(false);
        if (r is null)
        {
            return this.NotFoundWithIndexFallback(spec, 10);
        }

        if (r.Symbol is INamedTypeSymbol or INamespaceSymbol)
        {
            return $"{spec} resolved to {DisplayFormats.KindOf(r.Symbol)} {r.Symbol.ToDisplayString(DisplayFormats.FullyQualified)}. get_call_graph needs a method, constructor, property or event; use find_references for types.";
        }

        bool wantCallers = !direction.Equals("callees", StringComparison.OrdinalIgnoreCase);
        bool wantCallees = !direction.Equals("callers", StringComparison.OrdinalIgnoreCase);

        StringBuilder sb = new();
        this.AppendHeader(sb, r);
        string docId = r.Symbol.GetDocumentationCommentId() ?? "";
        string? assembly = SymbolIdentity.AssemblyOf(r.Symbol);

        if (wantCallers)
        {
            List<CallSite> callers = (await CallGraph.FindCallersAsync(r.Symbol, r.Solution.Solution, ct).ConfigureAwait(false)).ToList();
            List<(CallSite Site, string Solution)> all = callers.Select(c => (c, r.Solution.Name)).ToList();
            foreach (LoadedSolution other in workspaces.Loaded)
            {
                if (other.SolutionPath.Equals(r.Solution.SolutionPath, StringComparison.OrdinalIgnoreCase) || docId.Length == 0)
                {
                    continue;
                }

                ISymbol? twin = await SymbolResolver.FindTwinAsync(docId, assembly, other, ct).ConfigureAwait(false);
                if (twin is not null)
                {
                    all.AddRange((await CallGraph.FindCallersAsync(twin, other.Solution, ct).ConfigureAwait(false)).Select(c => (c, other.Name)));
                }
            }

            List<(CallSite Site, string Solution)> kept = all.Where(e => InScope(e.Site, criteria)).ToList();
            sb.Append("Callers (").Append(CountLabel(kept.Count, all.Count, criteria)).Append(criteria.IsActive ? ", " + criteria.Describe() : "").Append(", live, loaded solutions only):").AppendLine();
            List<string> lines = kept.Select(e => $"  {e.Site.Symbol.ToDisplayString(DisplayFormats.FullyQualifiedWithParameters)}  {Format(e.Site.Location)}  {e.Solution}").ToList();
            pager.AppendPage(sb, lines, limit, $"get_call_graph callers of {r.Symbol.Name}");
        }

        if (wantCallees)
        {
            IReadOnlyList<CallSite> callees = await CallGraph.FindCalleesAsync(r.Symbol, r.Solution.Solution, includeExternal, ct).ConfigureAwait(false);
            List<CallSite> kept = callees.Where(c => InScope(c, criteria)).ToList();
            sb.Append("Callees (").Append(CountLabel(kept.Count, callees.Count, criteria)).Append(criteria.IsActive ? ", " + criteria.Describe() : "")
              .Append(includeExternal ? "):" : ", source only; includeExternal=true adds framework and NuGet calls):").AppendLine();
            if (!r.IsSource)
            {
                sb.AppendLine("  (metadata symbol; no source body to walk)");
            }

            List<string> lines = kept.Select(c => $"  {c.Symbol.ToDisplayString(DisplayFormats.FullyQualifiedWithParameters)}  {FirstLocation(c.Symbol)}  called at {Format(c.Location)}").ToList();
            pager.AppendPage(sb, lines, limit, $"get_call_graph callees of {r.Symbol.Name}");
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ review_changes

    public async Task<string> ReviewChangesAsync(string? solution, string? baseRef, bool includeTests, int limit, CancellationToken ct)
    {
        string? solutionPath = workspaces.ResolveSolution(solution);
        if (solutionPath is null)
        {
            return $"Unknown solution: {solution}. Known: {string.Join(", ", workspaces.KnownSolutions.Select(SolutionDiscovery.SolutionName))}";
        }

        LoadedSolution? loaded = await workspaces.TryGetOrLoadWithinAsync(solutionPath, this.Wait, ct).ConfigureAwait(false);
        if (loaded is null)
        {
            return $"{SolutionDiscovery.SolutionName(solutionPath)} is still loading; retry shortly.";
        }

        (ReviewReport? report, string? error) = await ChangeReview.AnalyzeAsync(loaded, git, filter, baseRef?.Trim(), ct).ConfigureAwait(false);
        if (report is null)
        {
            return "review_changes: " + error;
        }

        // Impact is computed up front because the formatter is synchronous; only non-private declarations have any.
        Dictionary<(string Path, string DocId), string> impacts = [];
        foreach (ReviewedFile file in report.Files)
        {
            if (file.Kind != ReviewedFileKind.Source || (!includeTests && file.IsTest))
            {
                continue;
            }

            foreach (DeclarationChange change in file.Changes)
            {
                string? impact = await this.DescribeImpactAsync(loaded, report, file, change, ct).ConfigureAwait(false);
                if (impact is not null)
                {
                    impacts[(file.Path, change.DocId)] = impact;
                }
            }
        }

        return ChangeReview.Format(report, includeTests, (file, change) => impacts.GetValueOrDefault((file.Path, change.DocId)), pager, limit);
    }

    /// <summary>
    /// The line under a changed declaration: references per solution for a removed or signature-changed non-private
    /// declaration (live for loaded solutions, index for the rest, drift tag as <c>find_references</c> prints it), the
    /// caller count for a body-only change of a member, nothing for private declarations and additions.
    /// </summary>
    private async Task<string?> DescribeImpactAsync(LoadedSolution loaded, ReviewReport report, ReviewedFile file, DeclarationChange change, CancellationToken ct)
    {
        if (change.IsPrivate)
        {
            return null;
        }

        if (change.Kind == DeclarationChangeKind.Added)
        {
            // Nothing binds to a new declaration yet, with one exception: a new interface, abstract type or interface
            // member has implementers to list, and a member added to a public interface breaks implementers elsewhere.
            ISymbol? added = await ResolveInDocumentAsync(file.LiveDocument, change.DocId, ct).ConfigureAwait(false);
            return added is null ? null : await this.DescribeImplementersAsync(added, loaded, ct).ConfigureAwait(false);
        }

        if (change.Kind == DeclarationChangeKind.Removed)
        {
            // The symbol exists on the base side only: references it had are counted on the fork, references other
            // repositories still hold come from the index; usages left in this solution show up as diagnostics.
            ISymbol? gone = await ResolveInDocumentAsync(file.BaseDocument, change.DocId, ct).ConfigureAwait(false);
            if (gone is null)
            {
                return null;
            }

            (int before, int beforeTests) = await CountLiveReferencesAsync(gone, report.BaseSolution, ct).ConfigureAwait(false);
            StringBuilder sb = new();
            sb.Append("references: Live ").Append(loaded.Name).Append(' ').Append(ChangeReview.SplitCount(before, beforeTests)).Append(" before removal (usages left show as errors below)");
            await this.AppendOtherSolutionCountsAsync(sb, gone, loaded, remaining: true, ct).ConfigureAwait(false);
            return sb.ToString();
        }

        ISymbol? symbol = await ResolveInDocumentAsync(file.LiveDocument, change.DocId, ct).ConfigureAwait(false);
        if (symbol is null)
        {
            return null;
        }

        if (change.Kind == DeclarationChangeKind.Signature)
        {
            (int own, int ownTests) = await CountLiveReferencesAsync(symbol, loaded.Solution, ct).ConfigureAwait(false);
            StringBuilder sb = new();
            sb.Append("references: Live ").Append(loaded.Name).Append(' ').Append(ChangeReview.SplitCount(own, ownTests))
              .Append(this.DriftTag(SymbolIdentity.AssemblyOf(symbol), loaded.SolutionPath, own == 0));
            await this.AppendOtherSolutionCountsAsync(sb, symbol, loaded, remaining: false, ct).ConfigureAwait(false);
            if (change.Before is not null && change.Before.DocId != change.DocId)
            {
                // A parameter change is a new doc id: callers of the old overload either moved to the new one (counted
                // above) or still bind to the old one, which no longer exists and shows as an error below.
                ISymbol? old = await ResolveInDocumentAsync(file.BaseDocument, change.Before.DocId, ct).ConfigureAwait(false);
                if (old is not null)
                {
                    (int wasBound, int wasBoundTests) = await CountLiveReferencesAsync(old, report.BaseSolution, ct).ConfigureAwait(false);
                    sb.Append("; the old signature had ").Append(ChangeReview.SplitCount(wasBound, wasBoundTests)).Append(" in ").Append(loaded.Name);
                }
            }

            return sb.ToString();
        }

        // A body edit in a test project has no production caller by construction, and a test runner's reflection
        // is invisible to Roslyn, so "callers: 0" there would only be noise.
        if (symbol is IMethodSymbol or IPropertySymbol or IEventSymbol && !file.IsTest)
        {
            IReadOnlyList<CallSite> callers = await CallGraph.FindCallersAsync(symbol, loaded.Solution, ct).ConfigureAwait(false);
            int testCallers = callers.Count(c => c.Document is not null && ProjectKinds.IsTestProject(c.Document.Project.Name, c.Document.FilePath ?? ""));
            return "callers: " + ChangeReview.SplitCount(callers.Count, testCallers);
        }

        return null;
    }

    /// <summary>
    /// <c>implementations: Live X N, Index Y M</c> for an added interface or interface member, <c>derived types:</c>
    /// for an added abstract class, <c>overrides:</c> for an added abstract member; null for anything else. Counts
    /// come from the same lookups as <c>find_implementations</c>: live for loaded solutions, relations from the index
    /// for the rest. A member added to a public interface prints its count even when it is zero, because every
    /// implementer in another repository must add it.
    /// </summary>
    private async Task<string?> DescribeImplementersAsync(ISymbol symbol, LoadedSolution own, CancellationToken ct)
    {
        string label;
        string[] kinds;
        switch (symbol)
        {
            case INamedTypeSymbol { TypeKind: TypeKind.Interface }:
                label = "implementations";
                kinds = [RelationKinds.Implements, RelationKinds.Inherits];
                break;
            case INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: true }:
                label = "derived types";
                kinds = [RelationKinds.Inherits];
                break;
            case IMethodSymbol or IPropertySymbol or IEventSymbol when symbol.ContainingType?.TypeKind == TypeKind.Interface:
                label = "implementations";
                kinds = [RelationKinds.Implements];
                break;
            case IMethodSymbol or IPropertySymbol or IEventSymbol when symbol.IsAbstract:
                label = "overrides";
                kinds = [RelationKinds.Overrides];
                break;
            default:
                return null;
        }

        string docId = symbol.GetDocumentationCommentId() ?? "";
        string? assembly = SymbolIdentity.AssemblyOf(symbol);
        HashSet<string> seen = new(StringComparer.Ordinal);
        HashSet<string> covered = new(StringComparer.OrdinalIgnoreCase);
        StringBuilder sb = new();
        sb.Append(label).Append(": ");
        foreach (LoadedSolution loaded in new[] { own }.Concat(workspaces.Loaded.Where(l => !l.SolutionPath.Equals(own.SolutionPath, StringComparison.OrdinalIgnoreCase))))
        {
            ISymbol? target = ReferenceEquals(loaded, own) ? symbol : docId.Length == 0 ? null : await SymbolResolver.FindTwinAsync(docId, assembly, loaded, ct).ConfigureAwait(false);
            if (target is null)
            {
                continue;
            }

            covered.Add(loaded.SolutionPath);
            List<(string Kind, ISymbol Symbol)> found = await FindLiveImplementationsAsync(target, loaded.Solution, ct).ConfigureAwait(false);
            int count = found.Count(f => !this.IsDeclaredOnlyInIgnoredFiles(f.Symbol) && seen.Add(f.Symbol.GetDocumentationCommentId() ?? f.Symbol.ToDisplayString()));
            if (count > 0 || ReferenceEquals(loaded, own))
            {
                sb.Append(covered.Count > 1 ? ", Live " : "Live ").Append(loaded.Name).Append(' ').Append(count).Append(this.DriftTag(assembly, loaded.SolutionPath, count == 0));
            }
        }

        if (docId.Length > 0)
        {
            foreach (IGrouping<string, RelationHit> group in index.FindRelations(docId, kinds, 500).Where(h => !covered.Contains(h.SolutionPath)).GroupBy(h => h.SolutionPath, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(", Index ").Append(SolutionDiscovery.SolutionName(group.Key)).Append(' ').Append(group.Count()).Append(this.DriftTag(assembly, group.Key, noHits: false));
            }
        }

        return sb.ToString();
    }

    private static async Task<ISymbol?> ResolveInDocumentAsync(Document? document, string docId, CancellationToken ct)
    {
        Compilation? compilation = document is null ? null : await document.Project.GetCompilationAsync(ct).ConfigureAwait(false);
        return compilation is null ? null : DocumentationCommentId.GetFirstSymbolForDeclarationId(docId, compilation);
    }

    /// <summary>
    /// Source locations that bind to this declaration itself. <c>SymbolFinder</c> also returns the groups of related
    /// symbols (the interface member an implementation satisfies, overridden members), which <c>find_references</c>
    /// lists but an impact count must not: a consumer's <c>Consume</c> would otherwise count every consumer's callers.
    /// </summary>
    private static async Task<(int Total, int Tests)> CountLiveReferencesAsync(ISymbol symbol, Solution solution, CancellationToken ct)
    {
        int total = 0;
        int tests = 0;
        foreach (ReferencedSymbol rs in await SymbolFinder.FindReferencesAsync(symbol, solution, ct).ConfigureAwait(false))
        {
            if (!SymbolEqualityComparer.Default.Equals(rs.Definition.OriginalDefinition, symbol.OriginalDefinition))
            {
                continue;
            }

            foreach (ReferenceLocation location in rs.Locations)
            {
                if (location.IsImplicit || !location.Location.IsInSource)
                {
                    continue;
                }

                total++;
                if (ProjectKinds.IsTestProject(location.Document.Project.Name, location.Document.FilePath ?? ""))
                {
                    tests++;
                }
            }
        }

        return (total, tests);
    }

    /// <summary>Appends <c>, Live Other N</c> for every other loaded solution where the symbol's twin resolves and <c>, Index Other N</c> for the rest, with the drift tag.</summary>
    private async Task AppendOtherSolutionCountsAsync(StringBuilder sb, ISymbol symbol, LoadedSolution own, bool remaining, CancellationToken ct)
    {
        string docId = symbol.GetDocumentationCommentId() ?? "";
        string? assembly = SymbolIdentity.AssemblyOf(symbol);
        HashSet<string> covered = new(StringComparer.OrdinalIgnoreCase) { own.SolutionPath };
        string suffix = remaining ? " remaining" : "";
        foreach (LoadedSolution other in workspaces.Loaded)
        {
            if (covered.Contains(other.SolutionPath) || docId.Length == 0)
            {
                continue;
            }

            covered.Add(other.SolutionPath);
            ISymbol? twin = await SymbolResolver.FindTwinAsync(docId, assembly, other, ct).ConfigureAwait(false);
            if (twin is not null)
            {
                (int count, int tests) = await CountLiveReferencesAsync(twin, other.Solution, ct).ConfigureAwait(false);
                sb.Append(", Live ").Append(other.Name).Append(' ').Append(ChangeReview.SplitCount(count, tests)).Append(suffix).Append(this.DriftTag(assembly, other.SolutionPath, count == 0));
            }
        }

        if (docId.Length == 0)
        {
            return;
        }

        Dictionary<string, int> indexedTests = index.FindReferences(docId, assembly, int.MaxValue, new ReferenceFilter(null, ReferenceScope.Tests))
            .GroupBy(h => h.SolutionPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach ((string solutionPath, int count) in index.CountReferencesBySolution(docId, assembly).OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!covered.Contains(solutionPath))
            {
                sb.Append(", Index ").Append(SolutionDiscovery.SolutionName(solutionPath)).Append(' ')
                  .Append(ChangeReview.SplitCount(count, indexedTests.GetValueOrDefault(solutionPath))).Append(suffix).Append(this.DriftTag(assembly, solutionPath, noHits: false));
            }
        }
    }

    // ------------------------------------------------------------------ next_page

    public string NextPage(string cursor, int? limit = null)
    {
        ResultPage? page = pager.Take(cursor, limit);
        if (page is null)
        {
            return "Unknown or expired cursor. Cursors live in memory for 30 minutes; re-run the original query.";
        }

        StringBuilder sb = new();
        sb.Append("Continued: ").Append(page.Label).AppendLine();
        foreach (string line in page.Lines)
        {
            sb.AppendLine(line);
        }

        if (page.NextCursor is not null)
        {
            sb.Append("  ... ").Append(page.Remaining).Append(" more (next_page cursor=").Append(page.NextCursor).Append(')').AppendLine();
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ go_to_definition

    public async Task<string> GoToDefinitionAsync(string spec, string? solution, int maxLines, CancellationToken ct)
    {
        ResolvedSymbol? r = await resolver.ResolveAsync(spec, solution, ct).ConfigureAwait(false);
        if (r is null)
        {
            return this.NotFoundWithIndexFallback(spec, 10);
        }

        StringBuilder sb = new();
        this.AppendHeader(sb, r);
        List<Location> sourceLocations = r.Symbol.Locations.Where(l => l.IsInSource).ToList();
        if (sourceLocations.Count > 0)
        {
            sb.AppendLine("Declared in source:");
            foreach (Location loc in sourceLocations)
            {
                sb.Append("  ").Append(Format(loc)).AppendLine();
            }

            return sb.ToString();
        }

        Compilation? compilation = await r.Project.GetCompilationAsync(ct).ConfigureAwait(false);
        if (compilation is null)
        {
            return sb.AppendLine("No compilation available.").ToString();
        }

        string? code;
        string? assemblyPath;
        try
        {
            code = MetadataDecompiler.DecompileContainingType(r.Symbol, compilation, maxLines, out assemblyPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Decompilation failed for {Symbol}", spec);
            return sb.Append("Metadata symbol; decompilation failed: ").Append(ex.Message).ToString();
        }

        if (code is null)
        {
            return sb.AppendLine("Metadata symbol with no resolvable assembly file.").ToString();
        }

        sb.Append("Decompiled from ").Append(assemblyPath).AppendLine(":");
        sb.AppendLine(code);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ get_symbol_info

    public async Task<string> GetSymbolInfoAsync(string spec, string? solution, CancellationToken ct)
    {
        ResolvedSymbol? r = await resolver.ResolveAsync(spec, solution, ct).ConfigureAwait(false);
        if (r is null)
        {
            return this.NotFoundWithIndexFallback(spec, 10);
        }

        ISymbol s = r.Symbol;
        StringBuilder sb = new();
        sb.Append("Signature: ").Append(s.ToDisplayString(DisplayFormats.Signature)).AppendLine();
        sb.Append("Kind: ").Append(DisplayFormats.KindOf(s)).AppendLine();
        sb.Append("DocId: ").Append(s.GetDocumentationCommentId()).AppendLine();
        sb.Append("Accessibility: ").Append(s.DeclaredAccessibility.ToString().ToLowerInvariant());
        List<string> modifiers = [];
        if (s.IsStatic) { modifiers.Add("static"); }
        if (s.IsAbstract) { modifiers.Add("abstract"); }
        if (s.IsVirtual) { modifiers.Add("virtual"); }
        if (s.IsOverride) { modifiers.Add("override"); }
        if (s.IsSealed) { modifiers.Add("sealed"); }
        if (s.IsExtern) { modifiers.Add("extern"); }
        if (s is IMethodSymbol { IsAsync: true }) { modifiers.Add("async"); }
        if (s is IMethodSymbol { IsExtensionMethod: true }) { modifiers.Add("extension"); }
        if (modifiers.Count > 0)
        {
            sb.Append("  ").Append(string.Join(' ', modifiers));
        }

        sb.AppendLine();
        sb.Append("Namespace: ").Append(s.ContainingNamespace?.ToDisplayString()).AppendLine();
        if (s.ContainingType is not null)
        {
            sb.Append("Containing type: ").Append(s.ContainingType.ToDisplayString(DisplayFormats.FullyQualified)).AppendLine();
        }

        sb.Append("Assembly: ").Append(s.ContainingAssembly?.Name).Append(r.IsSource ? " (source)" : " (metadata)").AppendLine();
        sb.Append("Resolved in: ").Append(r.Project.Name).Append(" / ").Append(r.Solution.Name).AppendLine();
        foreach (Location loc in s.Locations.Where(l => l.IsInSource))
        {
            sb.Append("Declared at: ").Append(Format(loc)).AppendLine();
        }

        if (s is INamedTypeSymbol type)
        {
            if (type.BaseType is not null && type.BaseType.SpecialType != SpecialType.System_Object)
            {
                sb.Append("Base type: ").Append(type.BaseType.ToDisplayString(DisplayFormats.FullyQualified)).AppendLine();
            }

            if (type.Interfaces.Length > 0)
            {
                sb.Append("Interfaces: ").Append(string.Join(", ", type.Interfaces.Select(i => i.ToDisplayString(DisplayFormats.FullyQualified)))).AppendLine();
            }

            if (type.TypeParameters.Length > 0)
            {
                sb.Append("Type parameters: ").Append(string.Join(", ", type.TypeParameters.Select(t => t.Name))).AppendLine();
            }
        }

        if (s is IMethodSymbol method)
        {
            sb.Append("Returns: ").Append(method.ReturnType.ToDisplayString(DisplayFormats.FullyQualified)).AppendLine();
            foreach (IParameterSymbol p in method.Parameters)
            {
                sb.Append("Parameter: ").Append(p.Type.ToDisplayString(DisplayFormats.FullyQualified)).Append(' ').Append(p.Name);
                if (p.HasExplicitDefaultValue)
                {
                    sb.Append(" = ").Append(p.ExplicitDefaultValue ?? "null");
                }

                sb.AppendLine();
            }
        }

        if (s is IPropertySymbol property)
        {
            sb.Append("Type: ").Append(property.Type.ToDisplayString(DisplayFormats.FullyQualified)).AppendLine();
            sb.Append("Accessors: ").Append(property.GetMethod is not null ? "get " : "").Append(property.SetMethod is { IsInitOnly: true } ? "init" : property.SetMethod is not null ? "set" : "").AppendLine();
        }

        if (s is IFieldSymbol field)
        {
            sb.Append("Type: ").Append(field.Type.ToDisplayString(DisplayFormats.FullyQualified)).AppendLine();
            if (field.HasConstantValue)
            {
                sb.Append("Constant: ").Append(field.ConstantValue).AppendLine();
            }
        }

        System.Collections.Immutable.ImmutableArray<AttributeData> attributes = s.GetAttributes();
        if (attributes.Length > 0)
        {
            sb.Append("Attributes: ").Append(string.Join(", ", attributes.Select(a => a.AttributeClass?.Name ?? "?"))).AppendLine();
        }

        string? xml = s.GetDocumentationCommentXml(expandIncludes: true, cancellationToken: ct);
        string docs = FormatDocComment(xml);
        if (docs.Length > 0)
        {
            sb.AppendLine("Docs:");
            sb.AppendLine(docs);
        }

        if (s is INamedTypeSymbol membersOf)
        {
            List<ISymbol> members = VisibleMembers(membersOf, includeInherited: false).ToList();
            sb.Append("Members (").Append(members.Count).Append("):").AppendLine();
            foreach (ISymbol m in members.Take(60))
            {
                sb.Append("  ").Append(CompactMember(m)).AppendLine();
            }

            if (members.Count > 60)
            {
                sb.AppendLine("  ... use list_members for the full list");
            }
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ list_members

    public async Task<string> ListMembersAsync(string spec, string? solution, string? filter, bool includeInherited, int limit, CancellationToken ct)
    {
        ResolvedSymbol? r = await resolver.ResolveAsync(spec, solution, ct).ConfigureAwait(false);
        if (r is null)
        {
            return this.NotFoundWithIndexFallback(spec, 10);
        }

        INamedTypeSymbol? type = r.Symbol as INamedTypeSymbol ?? r.Symbol.ContainingType;
        if (type is null)
        {
            return $"{spec} is not a type.";
        }

        StringBuilder sb = new();
        sb.Append(DisplayFormats.KindOf(type)).Append(' ').Append(type.ToDisplayString(DisplayFormats.FullyQualified)).Append("  ").Append(FirstLocation(type)).AppendLine();
        List<ISymbol> members = VisibleMembers(type, includeInherited)
            .Where(m => filter is null || m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        sb.Append(members.Count).Append(" members").AppendLine();
        List<string> lines = members
            .Select(m => SymbolEqualityComparer.Default.Equals(m.ContainingType, type)
                ? $"  {CompactMember(m)}"
                : $"  {CompactMember(m)}  [from {m.ContainingType.ToDisplayString(DisplayFormats.FullyQualified)}]")
            .ToList();
        pager.AppendPage(sb, lines, limit, $"list_members {type.Name}");
        return sb.ToString();
    }

    private static IEnumerable<ISymbol> VisibleMembers(INamedTypeSymbol type, bool includeInherited)
    {
        IEnumerable<INamedTypeSymbol> chain = includeInherited ? Chain(type) : [type];
        return chain
            .SelectMany(t => t.GetMembers())
            .Where(m => !m.IsImplicitlyDeclared)
            .Where(m => m is not IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise })
            .Where(m => m.ContainingType.SpecialType != SpecialType.System_Object)
            .OrderBy(m => m.DeclaredAccessibility == Accessibility.Public ? 0 : 1)
            .ThenBy(m => m switch { IFieldSymbol => 0, IPropertySymbol => 1, IEventSymbol => 2, IMethodSymbol { MethodKind: MethodKind.Constructor } => 3, IMethodSymbol => 4, _ => 5 })
            .ThenBy(m => m.Name, StringComparer.Ordinal);

        static IEnumerable<INamedTypeSymbol> Chain(INamedTypeSymbol t)
        {
            INamedTypeSymbol? current = t;
            while (current is not null)
            {
                yield return current;
                current = current.BaseType;
            }
        }
    }

    private static string CompactMember(ISymbol m)
    {
        return $"{m.DeclaredAccessibility.ToString().ToLowerInvariant()} {(m.IsStatic ? "static " : "")}{DisplayFormats.KindOf(m)} {m.ToDisplayString(DisplayFormats.Compact)}";
    }

    // ------------------------------------------------------------------ get_outline

    /// <summary>
    /// One line per declaration of a file or a type, in source order with line spans, so the caller reads only the
    /// range it needs. A <c>.cs</c> target is outlined from the live syntax when its solution is loaded and from the
    /// index otherwise; any other target is a symbol spec that must resolve to a type, whose partial parts are all listed.
    /// </summary>
    public async Task<string> GetOutlineAsync(string target, string? solution, int depth, bool includePrivate, int limit, CancellationToken ct)
    {
        target = target.Trim();
        depth = Math.Clamp(depth, 1, 3);
        if (target.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            string? path = this.ResolveFilePath(target, solution);
            if (path is null)
            {
                return $"File not found: {target}. Pass an absolute path, or a path relative to a solution directory or a configured root.";
            }

            return await this.OutlineFileAsync(path, solution, depth, includePrivate, limit, loadIfUnindexed: true, ct).ConfigureAwait(false);
        }

        ResolvedSymbol? r = await resolver.ResolveAsync(target, solution, ct).ConfigureAwait(false);
        if (r is null)
        {
            return this.NotFoundWithIndexFallback(target, 10);
        }

        INamedTypeSymbol? type = r.Symbol as INamedTypeSymbol ?? r.Symbol.ContainingType;
        if (type is null)
        {
            return $"{target} resolved to {DisplayFormats.KindOf(r.Symbol)} {r.Symbol.ToDisplayString(DisplayFormats.FullyQualified)}, which is not inside a type. Pass a type or a .cs path.";
        }

        if (!r.IsSource || type.DeclaringSyntaxReferences.Length == 0)
        {
            return $"{type.ToDisplayString(DisplayFormats.FullyQualified)} is a metadata type; use go_to_definition for its decompiled source or list_members for its members.";
        }

        StringBuilder sb = new();
        sb.Append("Outline: ").Append(DisplayFormats.KindOf(type)).Append(' ').Append(type.ToDisplayString(DisplayFormats.FullyQualified))
          .Append("  (Live ").Append(r.Solution.Name).Append(')').AppendLine();
        List<string> lines = [];
        int total = 0;
        foreach (IGrouping<SyntaxTree, SyntaxReference> part in type.DeclaringSyntaxReferences.GroupBy(d => d.SyntaxTree))
        {
            Document? document = r.Solution.Solution.GetDocument(part.Key) ?? r.Solution.FindDocuments(part.Key.FilePath).FirstOrDefault();
            SemanticModel? model = document is null ? null : await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
            if (document is null || model is null)
            {
                continue;
            }

            List<OutlineEntry> entries = FileOutline.BuildForTypes(part.Select(p => p.GetSyntax(ct)), model, depth, includePrivate, ct);
            lines.Add($"  {part.Key.FilePath}  ({document.Project.Name})");
            lines.AddRange(entries.Select(e => e.Format(extraIndent: 1)));
            total += entries.Count;
        }

        pager.AppendPage(sb, lines, limit, $"get_outline {target}");
        sb.Append("Total: ").Append(total).Append(" declarations");
        return sb.ToString();
    }

    private async Task<string> OutlineFileAsync(string path, string? solutionHint, int depth, bool includePrivate, int limit, bool loadIfUnindexed, CancellationToken ct)
    {
        string? owner = workspaces.FindSolutionForPath(path) ?? workspaces.ResolveSolution(solutionHint);
        if (owner is null)
        {
            return $"{path} is not under any known solution or indexed project directory.";
        }

        StringBuilder sb = new();
        if (workspaces.TryGetLoaded(owner, out LoadedSolution loaded))
        {
            IReadOnlyList<Document> documents = loaded.FindDocuments(path);
            if (documents.Count == 0)
            {
                return $"{path} is not part of {loaded.Name}. New files are picked up on save; otherwise check the project globs.";
            }

            Document document = documents[0];
            SyntaxNode? root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
            SemanticModel? model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
            if (root is null || model is null)
            {
                return $"No syntax tree for {path}.";
            }

            List<OutlineEntry> entries = FileOutline.Build(root, model, depth, includePrivate, ct);
            sb.Append("Outline: ").Append(path).Append("  (Live ").Append(loaded.Name).Append(", ").Append(string.Join(", ", documents.Select(d => d.Project.Name))).Append(')').AppendLine();
            pager.AppendPage(sb, entries.Select(e => e.Format()).ToList(), limit, $"get_outline {path}");
            sb.Append("Total: ").Append(entries.Count).Append(" declarations");
            return sb.ToString();
        }

        // Not loaded: the index knows the declarations and their spans, but not the attributes.
        IReadOnlyList<FileSymbolHit> rows = index.GetFileSymbols(path);
        if (rows.Count == 0)
        {
            if (!loadIfUnindexed)
            {
                return $"{path} is not indexed in {SolutionDiscovery.SolutionName(owner)}.";
            }

            LoadedSolution? now = await workspaces.TryGetOrLoadWithinAsync(owner, this.Wait, ct).ConfigureAwait(false);
            if (now is null)
            {
                return $"{path} is not indexed yet and {SolutionDiscovery.SolutionName(owner)} is still loading; retry shortly.";
            }

            return await this.OutlineFileAsync(path, solutionHint, depth, includePrivate, limit, loadIfUnindexed: false, ct).ConfigureAwait(false);
        }

        IGrouping<string, FileSymbolHit> group = rows.GroupBy(h => h.ProjectName, StringComparer.OrdinalIgnoreCase).First();
        FileSymbolHit first = group.First();
        List<OutlineEntry> indexed = FileOutline.FromIndex(group.Select(h => h.Symbol).ToList(), depth, includePrivate);
        string stale = this.IsStale(path, first.ContentHash, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)) ? "; [stale: file changed since indexed]" : "";
        sb.Append("Outline: ").Append(path).Append("  (Index ").Append(SolutionDiscovery.SolutionName(first.SolutionPath)).Append(", ").Append(first.ProjectName)
          .Append("; solution not loaded, attributes not shown, load_solution for the live view").Append(stale).Append(')').AppendLine();
        pager.AppendPage(sb, indexed.Select(e => e.Format()).ToList(), limit, $"get_outline {path} (index)");
        sb.Append("Total: ").Append(indexed.Count).Append(" declarations");
        return sb.ToString();
    }

    /// <summary>An absolute path as given, or a relative one tried against the hinted, loaded and known solution directories and the configured roots.</summary>
    private string? ResolveFilePath(string target, string? solutionHint)
    {
        if (Path.IsPathRooted(target))
        {
            return File.Exists(target) ? Path.GetFullPath(target) : null;
        }

        List<string> directories = [];
        if (workspaces.ResolveSolution(solutionHint) is { } hinted && Path.GetDirectoryName(hinted) is { } hintedDir)
        {
            directories.Add(hintedDir);
        }

        directories.AddRange(workspaces.Loaded.Select(l => Path.GetDirectoryName(l.SolutionPath)).OfType<string>());
        directories.AddRange(workspaces.KnownSolutions.Select(Path.GetDirectoryName).OfType<string>());
        directories.AddRange(options.Value.Roots);
        directories.Add(Directory.GetCurrentDirectory());
        foreach (string directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string candidate = Path.GetFullPath(Path.Combine(directory, target));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ get_diagnostics

    public async Task<string> GetDiagnosticsAsync(string target, string? solution, string minSeverity, int limit, CancellationToken ct)
    {
        DiagnosticSeverity min = minSeverity.ToLowerInvariant() switch
        {
            "error" => DiagnosticSeverity.Error,
            "info" or "information" => DiagnosticSeverity.Info,
            _ => DiagnosticSeverity.Warning,
        };

        List<Diagnostic> diagnostics = [];
        string scopeDescription;

        if (target.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            string path = Path.GetFullPath(target);
            string? solutionPath = workspaces.FindSolutionForPath(path) ?? workspaces.ResolveSolution(solution);
            if (solutionPath is null)
            {
                return "No known solution contains " + path;
            }

            LoadedSolution? loaded = await workspaces.TryGetOrLoadWithinAsync(solutionPath, this.Wait, ct).ConfigureAwait(false);
            if (loaded is null)
            {
                return $"{SolutionDiscovery.SolutionName(solutionPath)} is still loading; retry shortly.";
            }

            // A call right after a save (a post-edit hook) must see the saved text; the watcher applies it only after
            // its debounce and the job queue. The index catches up when that job runs.
            IReadOnlyList<DocumentId> synced = await LiveTextSync.SyncFromDiskAsync(loaded, path, ct).ConfigureAwait(false);
            if (synced.Count > 0)
            {
                logger.LogDebug("{Solution}: applied {Path} from disk ahead of the watcher for get_diagnostics", loaded.Name, path);
            }

            IReadOnlyList<Document> documents = loaded.FindDocuments(path);
            if (documents.Count == 0)
            {
                return $"{path} is not part of {loaded.Name}. New files are picked up on save; otherwise check the project globs.";
            }

            foreach (Document document in documents)
            {
                SemanticModel? model = await document.GetSemanticModelAsync(ct).ConfigureAwait(false);
                if (model is not null)
                {
                    diagnostics.AddRange(model.GetDiagnostics(cancellationToken: ct));
                }
            }

            scopeDescription = path;
        }
        else
        {
            string? solutionPath = workspaces.ResolveSolution(solution);
            if (solutionPath is null)
            {
                return "No solution configured.";
            }

            LoadedSolution? loaded = await workspaces.TryGetOrLoadWithinAsync(solutionPath, this.Wait, ct).ConfigureAwait(false);
            if (loaded is null)
            {
                return $"{SolutionDiscovery.SolutionName(solutionPath)} is still loading; retry shortly.";
            }

            List<Project> projects = loaded.Solution.Projects
                .Where(p => p.Name.Equals(target, StringComparison.OrdinalIgnoreCase)
                            || p.Name.StartsWith(target + " (", StringComparison.OrdinalIgnoreCase)
                            || (p.FilePath is not null && p.FilePath.Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (projects.Count == 0)
            {
                return $"No project named {target} in {loaded.Name}. Projects: {string.Join(", ", loaded.Solution.Projects.Select(p => p.Name))}";
            }

            foreach (Project project in projects)
            {
                Compilation? compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
                if (compilation is not null)
                {
                    diagnostics.AddRange(compilation.GetDiagnostics(ct));
                }
            }

            scopeDescription = string.Join(", ", projects.Select(p => p.Name));
        }

        List<Diagnostic> filtered = diagnostics
            .Where(d => !d.IsSuppressed && d.Severity >= min)
            .DistinctBy(d => (d.Id, d.Location.GetLineSpan().Path, d.Location.GetLineSpan().StartLinePosition, d.GetMessage()))
            .OrderByDescending(d => d.Severity)
            .ThenBy(d => d.Location.GetLineSpan().Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Location.GetLineSpan().StartLinePosition.Line)
            .ToList();

        StringBuilder sb = new();
        sb.Append("Diagnostics for ").Append(scopeDescription).Append(": ")
          .Append(filtered.Count(d => d.Severity == DiagnosticSeverity.Error)).Append(" errors, ")
          .Append(filtered.Count(d => d.Severity == DiagnosticSeverity.Warning)).Append(" warnings")
          .AppendLine(" (compiler only; analyzers are not run)");
        List<string> lines = [];
        foreach (Diagnostic d in filtered)
        {
            FileLinePositionSpan span = d.Location.GetLineSpan();
            string where = span.Path.Length > 0 ? $"{span.Path}:{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1}  " : "";
            lines.Add($"  {where}{d.Severity.ToString().ToLowerInvariant()} {d.Id}: {d.GetMessage()}");
        }

        pager.AppendPage(sb, lines, limit, $"get_diagnostics {scopeDescription}");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ search_symbols

    public async Task<string> SearchSymbolsAsync(string query, string? kind, int limit, CancellationToken ct)
    {
        StringBuilder sb = new();
        HashSet<string> liveSolutions = new(StringComparer.OrdinalIgnoreCase);
        int total = 0;

        string namePart = SymbolRanker.NamePart(query);
        IReadOnlyList<string> containers = SymbolRanker.ContainerParts(query);

        foreach (LoadedSolution loaded in workspaces.Loaded)
        {
            liveSolutions.Add(loaded.SolutionPath);

            // Roslyn's matcher (prefix, camel humps, container patterns) and a substring pass over declaration names,
            // so a pattern like Retry still reaches CreateRetryOptions. Both go through the one ranking function.
            IEnumerable<ISymbol> byPattern = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(loaded.Solution, query, SymbolFilter.TypeAndMember, ct).ConfigureAwait(false);
            IEnumerable<ISymbol> bySubstring = namePart.Length == 0
                ? []
                : await SymbolFinder.FindSourceDeclarationsAsync(loaded.Solution, n => n.Contains(namePart, StringComparison.OrdinalIgnoreCase), SymbolFilter.TypeAndMember, ct).ConfigureAwait(false);

            Dictionary<string, (ISymbol Symbol, SymbolScore Score)> ranked = new(StringComparer.Ordinal);
            foreach ((ISymbol symbol, bool fromPattern) in byPattern.Select(s => (s, true)).Concat(bySubstring.Select(s => (s, false))))
            {
                if (symbol.IsImplicitlyDeclared)
                {
                    continue;
                }

                string symbolKind = DisplayFormats.KindOf(symbol);
                if (kind is not null && !symbolKind.Equals(kind, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string fqName = symbol.ToDisplayString(DisplayFormats.FullyQualified);
                if (!fromPattern && !containers.All(c => fqName.Contains(c, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                string id = symbol.GetDocumentationCommentId() ?? fqName;
                if (ranked.ContainsKey(id))
                {
                    continue;
                }

                string projectName = loaded.Solution.GetProject(symbol.ContainingAssembly)?.Name ?? symbol.ContainingAssembly?.Name ?? "";
                string filePath = symbol.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath ?? "";
                if (this.IsDeclaredOnlyInIgnoredFiles(symbol))
                {
                    continue;
                }

                SymbolScore? score = SymbolRanker.Score(namePart, symbol.Name, symbolKind, projectName, filePath, fromPattern ? MatchQuality.Fuzzy : null);
                if (score is not null)
                {
                    ranked[id] = (symbol, score.Value);
                }
            }

            List<ISymbol> list = ranked.Values
                .OrderBy(x => x.Score)
                .ThenBy(x => x.Symbol.ToDisplayString(DisplayFormats.FullyQualified), StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Symbol)
                .ToList();
            total += list.Count;
            if (list.Count == 0)
            {
                continue;
            }

            sb.Append("Live ").Append(loaded.Name).Append(" (").Append(list.Count).Append("):").AppendLine();
            List<string> lines = list
                .Select(s => $"  {DisplayFormats.KindOf(s)} {s.ToDisplayString(DisplayFormats.FullyQualified)}  {FirstLocation(s)}")
                .ToList();
            pager.AppendPage(sb, lines, limit, $"search_symbols {query} in {loaded.Name}");
        }

        IReadOnlyList<SymbolHit> hits = index.SearchSymbols(query, kind, Math.Max(limit * 4, 300));
        foreach (IGrouping<string, SymbolHit> group in hits.Where(h => !liveSolutions.Contains(h.SolutionPath)).GroupBy(h => h.SolutionPath, StringComparer.OrdinalIgnoreCase))
        {
            List<SymbolHit> list = group.DistinctBy(h => h.DocId).ToList();
            total += list.Count;
            sb.Append("Index ").Append(SolutionDiscovery.SolutionName(group.Key)).Append(" (not loaded, ").Append(list.Count).Append("):").AppendLine();
            // The printed position is the spec to pass on; the doc id repeated the name and cost a third of each line.
            List<string> lines = list.Select(h => $"  {h.Kind} {h.FqName}  {h.FilePath}:{h.Line}:{h.Column}").ToList();
            pager.AppendPage(sb, lines, limit, $"search_symbols {query} in {SolutionDiscovery.SolutionName(group.Key)} (index)");
        }

        if (total == 0)
        {
            sb.Append("No symbols match ").Append(query).Append(". Patterns support camel-case humps (e.g. UOC for UpdateOrderCommand).");
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ package drift

    /// <summary>
    /// Every indexed consumer whose package version of a sibling repository's assembly differs from the version the
    /// source project declares today, grouped by source assembly.
    /// </summary>
    public string PackageDrift(int limit)
    {
        IReadOnlyList<PackageDriftRow> rows = index.FindPackageDrift();
        if (rows.Count == 0)
        {
            return "No package version drift: every indexed consumer uses the version its source project currently declares. Only solutions indexed at least once are compared.";
        }

        StringBuilder sb = new();
        foreach (IGrouping<(string Assembly, string Version, string SolutionPath), PackageDriftRow> group in rows
                     .GroupBy(r => (Assembly: r.Source.AssemblyName, Version: r.Source.Version ?? "", SolutionPath: r.Source.SolutionPath))
                     .OrderBy(g => g.Key.Assembly, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(group.Key.Assembly).Append("  source ").Append(group.Key.Version).Append(" (").Append(SolutionDiscovery.SolutionName(group.Key.SolutionPath)).Append(')').AppendLine();
            List<string> lines = group
                .OrderBy(r => r.Consumer.SolutionPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Consumer.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => $"  {SolutionDiscovery.SolutionName(r.Consumer.SolutionPath)}  {r.Consumer.Name}  {r.ConsumedVersion}")
                .ToList();
            pager.AppendPage(sb, lines, limit, $"package_drift {group.Key.Assembly}");
        }

        sb.Append("Total: ").Append(rows.Count).Append(" consumer projects on a version other than the source. Only solutions indexed at least once are compared.");
        return sb.ToString();
    }

    /// <summary>Versions declared by indexed source projects with this assembly name (several when a library is duplicated across repositories).</summary>
    private List<string> SourceVersions(string? assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName) || assemblyName == SymbolIdentity.FrameworkAssembly)
        {
            return [];
        }

        return index.GetProjects()
            .Where(p => p.Version is not null && p.AssemblyName.Equals(assemblyName, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Version!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// A suffix for a per-solution group line when projects of that solution consume the symbol's assembly as a package
    /// at a version other than the one the source project declares. Empty when there is no package, no drift, or no
    /// declared source version to compare with.
    /// </summary>
    private string DriftTag(string? assemblyName, string solutionPath, bool noHits)
    {
        if (string.IsNullOrEmpty(assemblyName) || assemblyName == SymbolIdentity.FrameworkAssembly)
        {
            return "";
        }

        List<string> consumed = index.GetPackageConsumers(assemblyName)
            .Where(c => c.Consumer.SolutionPath.Equals(solutionPath, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Package.Version)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (consumed.Count == 0)
        {
            return "";
        }

        List<string> source = this.SourceVersions(assemblyName);
        if (source.Count == 0 || (consumed.Count == 1 && source.Count == 1 && consumed[0].Equals(source[0], StringComparison.OrdinalIgnoreCase)))
        {
            return "";
        }

        string tag = $"  [package {string.Join("/", consumed)}, source is {string.Join("/", source)}";
        if (noHits)
        {
            tag += "; the symbol may not exist in that version";
        }

        return tag + "]";
    }

    // ------------------------------------------------------------------ reindex / load_solution / status

    public string Reindex(string? solution, bool force)
    {
        if (string.Equals(solution, "all", StringComparison.OrdinalIgnoreCase))
        {
            int count = 0;
            foreach (LoadedSolution loaded in workspaces.Loaded)
            {
                jobs.Enqueue(new IndexSolutionJob(loaded.SolutionPath, force));
                count++;
            }

            return $"Queued reindex of {count} loaded solutions (force={force}).";
        }

        string? path = workspaces.ResolveSolution(solution);
        if (path is null)
        {
            return "Unknown solution: " + solution;
        }

        if (!workspaces.TryGetLoaded(path, out _))
        {
            _ = workspaces.GetOrLoadAsync(path, CancellationToken.None).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return $"{SolutionDiscovery.SolutionName(path)} was not loaded; load started, indexing follows automatically.";
        }

        jobs.Enqueue(new IndexSolutionJob(path, force));
        return $"Queued reindex of {SolutionDiscovery.SolutionName(path)} (force={force}).";
    }

    public async Task<string> LoadSolutionAsync(string solution, CancellationToken ct)
    {
        string? path = workspaces.ResolveSolution(solution);
        if (path is null)
        {
            return $"Unknown solution: {solution}. Known: {string.Join(", ", workspaces.KnownSolutions.Select(SolutionDiscovery.SolutionName))}";
        }

        LoadedSolution? loaded = await workspaces.TryGetOrLoadWithinAsync(path, this.Wait, ct).ConfigureAwait(false);
        if (loaded is null)
        {
            return $"{SolutionDiscovery.SolutionName(path)} is loading in the background; check status.";
        }

        return $"{loaded.Name}: {loaded.Solution.ProjectIds.Count} projects loaded in {loaded.LoadDuration.TotalSeconds:N1}s, {loaded.LoadDiagnostics.Count} workspace diagnostics.";
    }

    public string Status()
    {
        StringBuilder sb = new();
        sb.Append("Process: ").Append(Environment.WorkingSet / (1024 * 1024)).Append(" MB working set, GC heap ")
          .Append(GC.GetTotalMemory(false) / (1024 * 1024)).Append(" MB").AppendLine();
        sb.Append("Index: ").Append(index.DatabasePath).AppendLine();
        IndexStats stats = index.GetStats();
        sb.Append("  ").Append(stats.Projects).Append(" projects, ").Append(stats.TreeEntries).Append(" tracked files, ")
          .Append(stats.Blobs).Append(" blobs, ").Append(stats.Symbols).Append(" symbols, ").Append(stats.References).Append(" references, ")
          .Append(stats.Relations).Append(" relations").AppendLine();
        sb.Append("Jobs: ").Append(jobs.Pending).Append(" pending");
        if (jobs.LastCompletedUtc is { } last)
        {
            sb.Append(", last completed ").Append(last.ToLocalTime().ToString("HH:mm:ss")).Append(' ').Append(jobs.LastJobDescription);
        }

        sb.AppendLine();
        sb.Append("Git: ").AppendLine(git.Describe());
        if (warmUp.Describe() is { } warmUpLine)
        {
            sb.Append("Warm-up: ").AppendLine(warmUpLine);
        }

        if (pendingRefreshes.Describe(DateTime.UtcNow) is { } refreshLine)
        {
            sb.Append("Unloaded refresh: ").AppendLine(refreshLine);
        }

        sb.AppendLine("Solutions:");
        foreach (SolutionStatus s in workspaces.GetStatus())
        {
            sb.Append("  ").Append(s.Name).Append("  ").Append(s.State.ToString().ToLowerInvariant());
            if (s.Pinned)
            {
                sb.Append(" pinned");
            }

            if (s.State == SolutionState.Loaded)
            {
                sb.Append("  ").Append(s.Projects).Append(" projects, ").Append(s.Documents).Append(" documents, loaded in ")
                  .Append(s.LoadDuration?.TotalSeconds.ToString("N1")).Append("s, last used ").Append(s.LastUsedUtc?.ToLocalTime().ToString("HH:mm:ss"));
            }

            if (s.Error is not null)
            {
                sb.Append("  error: ").Append(s.Error);
            }

            sb.Append("  ").Append(s.Path).AppendLine();
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ helpers

    private void AppendHeader(StringBuilder sb, ResolvedSymbol r)
    {
        sb.Append("Symbol: ").Append(r.Symbol.ToDisplayString(DisplayFormats.Signature))
          .Append("  [").Append(DisplayFormats.KindOf(r.Symbol)).Append("]  ")
          .Append(r.Symbol.GetDocumentationCommentId()).AppendLine();
        sb.Append("Assembly: ").Append(r.Symbol.ContainingAssembly?.Name).Append(r.IsSource ? " (source, " : " (metadata, ").Append(r.Solution.Name);
        if (r.IsSource && this.SourceVersions(SymbolIdentity.AssemblyOf(r.Symbol)) is { Count: > 0 } versions)
        {
            sb.Append(", version ").Append(string.Join("/", versions));
        }

        sb.Append(')').AppendLine();
        Location? first = r.Symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (first is not null)
        {
            sb.Append("Defined: ").Append(Format(first)).AppendLine();
        }
    }

    /// <summary>
    /// A symbol whose every source declaration sits under an ignored folder is left out of live lists, so Live and
    /// Index halves agree. A partial type with one part outside the folder stays.
    /// </summary>
    private bool IsDeclaredOnlyInIgnoredFiles(ISymbol symbol)
    {
        bool anySource = false;
        foreach (Location location in symbol.Locations)
        {
            if (!location.IsInSource || location.SourceTree is null)
            {
                continue;
            }

            anySource = true;
            if (!filter.IsIgnored(location.SourceTree.FilePath))
            {
                return false;
            }
        }

        return anySource;
    }

    private static string Format(Location location)
    {
        FileLinePositionSpan span = location.GetLineSpan();
        return $"{span.Path}:{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1}";
    }

    private static string FirstLocation(ISymbol symbol)
    {
        Location? loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        return loc is null ? "(metadata " + symbol.ContainingAssembly?.Name + ")" : Format(loc);
    }

    private bool IsStale(string path, string indexedHash, Dictionary<string, string> cache)
    {
        if (!cache.TryGetValue(path, out string? current))
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                current = ContentHasher.HashText(SourceText.From(stream).ToString());
            }
            catch (IOException)
            {
                current = "";
            }
            catch (UnauthorizedAccessException)
            {
                current = "";
            }

            cache[path] = current;
        }

        return !string.Equals(current, indexedHash, StringComparison.Ordinal);
    }

    private static string FormatDocComment(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return "";
        }

        XElement root;
        try
        {
            root = XElement.Parse(xml.Trim());
        }
        catch
        {
            return xml.Trim();
        }

        foreach (XElement see in root.Descendants().Where(e => e.Name.LocalName is "see" or "seealso" or "paramref" or "typeparamref").ToList())
        {
            string? cref = see.Attribute("cref")?.Value ?? see.Attribute("name")?.Value ?? see.Attribute("langword")?.Value;
            if (cref is not null)
            {
                int colon = cref.IndexOf(':');
                string shortName = colon == 1 ? cref[2..] : cref;
                see.ReplaceWith(new XText(shortName));
            }
        }

        StringBuilder sb = new();
        foreach (XElement e in root.Elements())
        {
            string text = Collapse(e.Value);
            if (text.Length == 0)
            {
                continue;
            }

            string label = e.Name.LocalName switch
            {
                "param" => "param " + e.Attribute("name")?.Value,
                "typeparam" => "typeparam " + e.Attribute("name")?.Value,
                "exception" => "throws " + (e.Attribute("cref")?.Value is { } c && c.Length > 2 ? c[2..] : ""),
                var n => n,
            };
            sb.Append("  ").Append(label).Append(": ").Append(text).AppendLine();
        }

        return sb.ToString().TrimEnd();

        static string Collapse(string s)
        {
            return string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
