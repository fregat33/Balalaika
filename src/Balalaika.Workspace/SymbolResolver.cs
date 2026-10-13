using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Options;
using Balalaika.Index;

namespace Balalaika.Workspace;

public sealed record ResolvedSymbol(ISymbol Symbol, Project Project, LoadedSolution Solution, bool IsSource);

/// <summary>
/// Turns a user-supplied symbol spec into an <see cref="ISymbol"/> in a live compilation. Accepted forms:
/// documentation comment ids (<c>T:Ns.Type</c>, <c>M:Ns.Type.Method</c>), fully qualified names
/// (<c>Ns.Type</c>, <c>Ns.Type.Member</c>, <c>Ns.Outer.Inner</c>, <c>Ns.Type&lt;T&gt;</c>) and bare names.
/// Loaded solutions are searched first; the index is consulted to find and load the defining solution otherwise.
/// </summary>
public sealed class SymbolResolver(WorkspaceManager workspaces, IndexStore index, IOptions<BalalaikaOptions> options)
{
    public async Task<ResolvedSymbol?> ResolveAsync(string spec, string? solutionHint, CancellationToken cancellationToken)
    {
        spec = spec.Trim();
        if (spec.Length == 0)
        {
            return null;
        }

        TimeSpan wait = TimeSpan.FromSeconds(options.Value.LoadWaitSeconds);
        if (TryParsePosition(spec, out string positionPath, out int positionLine, out int? positionColumn))
        {
            return await this.ResolveAtPositionAsync(positionPath, positionLine, positionColumn, solutionHint, wait, cancellationToken).ConfigureAwait(false);
        }

        List<LoadedSolution> candidates = [];
        HashSet<string> tried = new(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(solutionHint))
        {
            string? path = workspaces.ResolveSolution(solutionHint);
            if (path is not null)
            {
                LoadedSolution? loaded = await workspaces.TryGetOrLoadWithinAsync(path, wait, cancellationToken).ConfigureAwait(false);
                if (loaded is not null)
                {
                    candidates.Add(loaded);
                }
            }
        }
        else
        {
            candidates.AddRange(workspaces.Loaded);
        }

        ResolvedSymbol? metadataFallback = null;
        foreach (LoadedSolution loaded in candidates)
        {
            tried.Add(loaded.SolutionPath);
            ResolvedSymbol? resolved = await ResolveInSolutionAsync(spec, loaded, cancellationToken).ConfigureAwait(false);
            if (resolved is null)
            {
                continue;
            }

            if (resolved.IsSource)
            {
                return resolved;
            }

            metadataFallback ??= resolved;
        }

        if (metadataFallback is not null && !string.IsNullOrWhiteSpace(solutionHint))
        {
            return metadataFallback;
        }

        // Ask the index which solution declares the symbol, then load that one.
        IReadOnlyList<SymbolHit> hits = LooksLikeDocId(spec) ? index.FindSymbolsByDocId(spec) : index.FindSymbolsByName(StripSignature(spec));
        foreach (string solutionPath in hits.Select(h => h.SolutionPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!tried.Add(solutionPath))
            {
                continue;
            }

            LoadedSolution? loaded = await workspaces.TryGetOrLoadWithinAsync(solutionPath, wait, cancellationToken).ConfigureAwait(false);
            if (loaded is null)
            {
                continue;
            }

            ResolvedSymbol? resolved = await ResolveInSolutionAsync(spec, loaded, cancellationToken).ConfigureAwait(false);
            if (resolved?.IsSource == true)
            {
                return resolved;
            }

            metadataFallback ??= resolved;
        }

        if (metadataFallback is null && candidates.Count == 0)
        {
            string? fallbackPath = workspaces.ResolveSolution(null);
            if (fallbackPath is not null && tried.Add(fallbackPath))
            {
                LoadedSolution? loaded = await workspaces.TryGetOrLoadWithinAsync(fallbackPath, wait, cancellationToken).ConfigureAwait(false);
                if (loaded is not null)
                {
                    metadataFallback = await ResolveInSolutionAsync(spec, loaded, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return metadataFallback;
    }

    public static async Task<ResolvedSymbol?> ResolveInSolutionAsync(string spec, LoadedSolution loaded, CancellationToken cancellationToken)
    {
        ResolvedSymbol? metadata = null;
        foreach (Project project in loaded.Solution.Projects)
        {
            Compilation? compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            ISymbol? symbol = ResolveInCompilation(spec, compilation);
            if (symbol is null)
            {
                continue;
            }

            bool isSource = symbol.Locations.Any(l => l.IsInSource);
            ResolvedSymbol resolved = new(symbol, project, loaded, isSource);
            if (isSource)
            {
                return resolved;
            }

            metadata ??= resolved;
        }

        return metadata;
    }

    /// <summary>
    /// Finds the same symbol (by doc id) inside another solution, source or metadata. <paramref name="assemblyName"/>
    /// is compared in its <see cref="SymbolIdentity"/> form, so a framework type matches whichever reference assembly
    /// the other solution binds it from.
    /// </summary>
    public static async Task<ISymbol?> FindTwinAsync(string docId, string? assemblyName, LoadedSolution other, CancellationToken cancellationToken)
    {
        string? wanted = SymbolIdentity.Canonical(assemblyName);
        ISymbol? fallback = null;
        foreach (Project project in other.Solution.Projects)
        {
            Compilation? compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            ISymbol? symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(docId, compilation);
            if (symbol is null)
            {
                continue;
            }

            if (wanted is null || string.Equals(SymbolIdentity.AssemblyOf(symbol), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return symbol;
            }

            fallback ??= symbol;
        }

        return fallback;
    }

    public static bool LooksLikeDocId(string spec)
    {
        return spec.Length > 2 && spec[1] == ':' && "TMPFEN".Contains(spec[0]);
    }

    // ------------------------------------------------------------------ positional specs (path:line[:col])

    private static readonly System.Text.RegularExpressions.Regex PositionPattern = new(
        @"^(?<path>.+?\.cs):(?<line>\d+)(?::(?<col>\d+))?$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Recognises the <c>path:line</c> and <c>path:line:col</c> forms that every tool prints for a hit. The drive
    /// colon in a Windows path is followed by a separator, not a digit, so it never matches the line group.
    /// </summary>
    public static bool TryParsePosition(string spec, out string path, out int line, out int? column)
    {
        path = "";
        line = 0;
        column = null;
        System.Text.RegularExpressions.Match m = PositionPattern.Match(spec.Trim());
        if (!m.Success)
        {
            return false;
        }

        path = m.Groups["path"].Value;
        line = int.Parse(m.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (m.Groups["col"].Success)
        {
            column = int.Parse(m.Groups["col"].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        return line > 0 && column is null or > 0;
    }

    private async Task<ResolvedSymbol?> ResolveAtPositionAsync(string path, int line, int? column, string? solutionHint, TimeSpan wait, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path);
        string? solutionPath = workspaces.FindSolutionForPath(path) ?? workspaces.ResolveSolution(solutionHint);
        if (solutionPath is null)
        {
            return null;
        }

        LoadedSolution? loaded = await workspaces.TryGetOrLoadWithinAsync(solutionPath, wait, cancellationToken).ConfigureAwait(false);
        if (loaded is null)
        {
            return null;
        }

        foreach (Document document in loaded.FindDocuments(path))
        {
            ISymbol? symbol = await ResolveAtPositionAsync(document, line, column, cancellationToken).ConfigureAwait(false);
            if (symbol is not null)
            {
                return new ResolvedSymbol(symbol, document.Project, loaded, symbol.Locations.Any(l => l.IsInSource));
            }
        }

        return null;
    }

    /// <summary>
    /// The symbol at a 1-based line and column. Without a column, the first identifier on the line that declares
    /// a member or type wins, then any other declaration, then the first identifier that references a symbol.
    /// </summary>
    public static async Task<ISymbol?> ResolveAtPositionAsync(Document document, int line, int? column, CancellationToken cancellationToken)
    {
        SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        SemanticModel? model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return null;
        }

        Microsoft.CodeAnalysis.Text.SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (line < 1 || line > text.Lines.Count)
        {
            return null;
        }

        Microsoft.CodeAnalysis.Text.TextLine textLine = text.Lines[line - 1];
        if (column is int col)
        {
            int position = Math.Min(textLine.Start + col - 1, Math.Max(textLine.Start, textLine.End - 1));
            SyntaxToken token = root.FindToken(position);
            ISymbol? direct = SymbolForToken(token, model, out _);
            if (direct is not null || position <= textLine.Start)
            {
                return direct;
            }

            // A column just past an identifier (on the '(' of a call, say) still means that identifier.
            return SymbolForToken(root.FindToken(position - 1), model, out _);
        }

        ISymbol? bestDeclaration = null;
        int bestRank = int.MaxValue;
        ISymbol? firstReference = null;
        foreach (SyntaxToken token in root.DescendantTokens(textLine.Span).Where(t => textLine.Span.Contains(t.SpanStart)))
        {
            ISymbol? symbol = SymbolForToken(token, model, out bool declared);
            if (symbol is null)
            {
                continue;
            }

            if (declared)
            {
                int rank = symbol switch
                {
                    INamedTypeSymbol => 0,
                    IMethodSymbol or IPropertySymbol or IEventSymbol => 1,
                    IFieldSymbol => 2,
                    IParameterSymbol or ILocalSymbol => 4,
                    _ => 3,
                };
                if (rank < bestRank)
                {
                    bestRank = rank;
                    bestDeclaration = symbol;
                }
            }
            else
            {
                firstReference ??= symbol;
            }
        }

        if (bestDeclaration is not null && bestRank <= 3)
        {
            return bestDeclaration;
        }

        return firstReference ?? bestDeclaration;
    }

    private static ISymbol? SymbolForToken(SyntaxToken token, SemanticModel model, out bool declared)
    {
        declared = false;
        if (!token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.IdentifierToken) || token.Parent is null)
        {
            return null;
        }

        SyntaxNode node = token.Parent;
        ISymbol? symbol = model.GetDeclaredSymbol(node);
        if (symbol is not null)
        {
            declared = true;
        }
        else
        {
            SymbolInfo info = model.GetSymbolInfo(node);
            symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        }

        return symbol is null ? null : Normalize(symbol);
    }

    /// <summary>Strips constructed generics, reduced extension forms and aliases so the result has a doc id.</summary>
    private static ISymbol Normalize(ISymbol symbol)
    {
        if (symbol is IAliasSymbol alias)
        {
            symbol = alias.Target;
        }

        if (symbol is IMethodSymbol { ReducedFrom: { } reduced })
        {
            symbol = reduced;
        }

        return symbol.OriginalDefinition;
    }

    private static string StripSignature(string spec)
    {
        int paren = spec.IndexOf('(');
        if (paren > 0)
        {
            spec = spec[..paren];
        }

        int angle = spec.IndexOf('<');
        if (angle > 0)
        {
            spec = spec[..angle];
        }

        return spec;
    }

    public static ISymbol? ResolveInCompilation(string spec, Compilation compilation)
    {
        if (LooksLikeDocId(spec))
        {
            return DocumentationCommentId.GetFirstSymbolForDeclarationId(spec, compilation);
        }

        string? parameterText = null;
        int paren = spec.IndexOf('(');
        if (paren > 0)
        {
            parameterText = spec[(paren + 1)..].TrimEnd(')').Trim();
            spec = spec[..paren];
        }

        spec = NormalizeGenerics(spec);

        INamedTypeSymbol? type = FindType(spec, compilation);
        if (type is not null)
        {
            return type;
        }

        int dot = spec.LastIndexOf('.');
        if (dot > 0)
        {
            string typeName = spec[..dot];
            string memberName = spec[(dot + 1)..];
            INamedTypeSymbol? container = FindType(typeName, compilation);
            if (container is not null)
            {
                ISymbol? member = PickMember(container, memberName, parameterText);
                if (member is not null)
                {
                    return member;
                }
            }
        }

        if (!spec.Contains('.'))
        {
            string bare = spec;
            int backtick = bare.IndexOf('`');
            if (backtick > 0)
            {
                bare = bare[..backtick];
            }

            // Source first, as written and with the attribute suffix ([Authorize] is AuthorizeAttribute); then the
            // referenced assemblies, which GetSymbolsWithName never sees, so AuthorizeAttribute or HttpGet resolve bare.
            return FindSourceSymbolByName(bare, compilation)
                ?? FindSourceSymbolByName(bare + "Attribute", compilation)
                ?? FindMetadataType(bare, compilation)
                ?? FindMetadataType(bare + "Attribute", compilation);
        }

        return null;
    }

    private static ISymbol? FindSourceSymbolByName(string name, Compilation compilation)
    {
        return compilation.GetSymbolsWithName(n => n.Equals(name, StringComparison.Ordinal), SymbolFilter.TypeAndMember)
            .OrderBy(s => s is INamedTypeSymbol ? 0 : 1)
            .ThenBy(s => s.ToDisplayString().Length)
            .FirstOrDefault();
    }

    /// <summary>
    /// A type of that name in any namespace of the compilation, including the referenced assemblies; the shortest
    /// fully qualified name wins when several namespaces declare one. Walks the merged namespace tree, which is in
    /// memory already, and runs only after the source lookups missed.
    /// </summary>
    private static INamedTypeSymbol? FindMetadataType(string name, Compilation compilation)
    {
        INamedTypeSymbol? best = null;
        Stack<INamespaceSymbol> pending = new();
        pending.Push(compilation.GlobalNamespace);
        while (pending.Count > 0)
        {
            INamespaceSymbol ns = pending.Pop();
            foreach (INamedTypeSymbol type in ns.GetTypeMembers(name))
            {
                if (type.DeclaredAccessibility == Accessibility.Public && (best is null || type.ToDisplayString().Length < best.ToDisplayString().Length))
                {
                    best = type;
                }
            }

            foreach (INamespaceSymbol child in ns.GetNamespaceMembers())
            {
                pending.Push(child);
            }
        }

        return best;
    }

    private static ISymbol? PickMember(INamedTypeSymbol container, string memberName, string? parameterText)
    {
        List<ISymbol> members = memberName == container.Name || memberName == ".ctor" || memberName == "ctor"
            ? container.InstanceConstructors.Cast<ISymbol>().ToList()
            : container.GetMembers(memberName).Where(m => !m.IsImplicitlyDeclared).ToList();

        if (members.Count == 0)
        {
            foreach (INamedTypeSymbol baseType in BaseTypes(container))
            {
                members = baseType.GetMembers(memberName).Where(m => !m.IsImplicitlyDeclared).ToList();
                if (members.Count > 0)
                {
                    break;
                }
            }
        }

        if (members.Count == 0)
        {
            return null;
        }

        if (parameterText is not null && members.Count > 1)
        {
            string wanted = parameterText.Replace(" ", "");
            foreach (ISymbol m in members)
            {
                if (m is IMethodSymbol method)
                {
                    string actual = string.Join(",", method.Parameters.Select(p => p.Type.ToDisplayString(DisplayFormats.Compact))).Replace(" ", "");
                    if (actual.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        return m;
                    }
                }
            }
        }

        return members[0];
    }

    private static IEnumerable<INamedTypeSymbol> BaseTypes(INamedTypeSymbol type)
    {
        INamedTypeSymbol? current = type.BaseType;
        while (current is not null)
        {
            yield return current;
            current = current.BaseType;
        }
    }

    private static INamedTypeSymbol? FindType(string name, Compilation compilation)
    {
        INamedTypeSymbol? type = compilation.GetTypeByMetadataName(name);
        if (type is not null)
        {
            return type;
        }

        // Nested types use '+' in metadata names: Ns.Outer.Inner -> Ns.Outer+Inner -> Ns+Outer+Inner.
        string candidate = name;
        int dot;
        while ((dot = candidate.LastIndexOf('.')) > 0)
        {
            candidate = candidate[..dot] + "+" + candidate[(dot + 1)..];
            type = compilation.GetTypeByMetadataName(candidate);
            if (type is not null)
            {
                return type;
            }
        }

        return null;
    }

    /// <summary>Foo&lt;T, U&gt; -> Foo`2 for every generic segment.</summary>
    private static string NormalizeGenerics(string spec)
    {
        if (!spec.Contains('<'))
        {
            return spec;
        }

        System.Text.StringBuilder sb = new();
        int depth = 0;
        int arity = 0;
        foreach (char c in spec)
        {
            switch (c)
            {
                case '<':
                    if (depth == 0)
                    {
                        arity = 1;
                    }

                    depth++;
                    break;
                case '>':
                    depth--;
                    if (depth == 0)
                    {
                        sb.Append('`').Append(arity);
                    }

                    break;
                case ',' when depth == 1:
                    arity++;
                    break;
                default:
                    if (depth == 0)
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }
}
