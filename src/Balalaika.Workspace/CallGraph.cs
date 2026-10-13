using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Balalaika.Workspace;

/// <summary>
/// One edge of the call graph: the symbol on the other end, where the call is written, and the document that declares
/// the other end (the calling member for a caller, the callee for a callee; null for a metadata callee). The document
/// names the project the <c>scope</c> filter is applied to.
/// </summary>
public sealed record CallSite(ISymbol Symbol, Location Location, Document? Document);

/// <summary>
/// One level of callers (via <see cref="SymbolFinder.FindCallersAsync(ISymbol, Solution, CancellationToken)"/>) and
/// callees (invocations and object creations inside the declaring syntax) of a member.
/// </summary>
public static class CallGraph
{
    public static async Task<IReadOnlyList<CallSite>> FindCallersAsync(ISymbol symbol, Solution solution, CancellationToken cancellationToken)
    {
        List<CallSite> result = [];
        foreach (SymbolCallerInfo caller in await SymbolFinder.FindCallersAsync(symbol, solution, cancellationToken).ConfigureAwait(false))
        {
            foreach (Location location in caller.Locations)
            {
                if (location.IsInSource)
                {
                    result.Add(new CallSite(caller.CallingSymbol, location, solution.GetDocument(location.SourceTree)));
                }
            }
        }

        result.Sort(CompareByLocation);
        return result;
    }

    /// <summary>
    /// Methods and constructors invoked from the declaring syntax of <paramref name="symbol"/>. Each callee is listed
    /// once, at its first call site. Metadata callees are included only when <paramref name="includeExternal"/> is set.
    /// </summary>
    public static async Task<IReadOnlyList<CallSite>> FindCalleesAsync(ISymbol symbol, Solution solution, bool includeExternal, CancellationToken cancellationToken)
    {
        List<CallSite> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
        {
            SyntaxNode declaration = await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
            Document? document = solution.GetDocument(declaration.SyntaxTree);
            SemanticModel? model = document is null ? null : await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (model is null)
            {
                continue;
            }

            foreach (SyntaxNode call in declaration.DescendantNodes().Where(IsCall))
            {
                SymbolInfo info = model.GetSymbolInfo(call, cancellationToken);
                if ((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is not IMethodSymbol method)
                {
                    continue;
                }

                method = (method.ReducedFrom ?? method).OriginalDefinition;
                bool inSource = method.Locations.Any(l => l.IsInSource);
                if (!inSource && !includeExternal)
                {
                    continue;
                }

                string key = method.GetDocumentationCommentId() ?? method.ToDisplayString();
                if (seen.Add(key))
                {
                    Location? declared = method.Locations.FirstOrDefault(l => l.IsInSource);
                    result.Add(new CallSite(method, call.GetLocation(), declared is null ? null : solution.GetDocument(declared.SourceTree)));
                }
            }
        }

        result.Sort(CompareByLocation);
        return result;
    }

    private static bool IsCall(SyntaxNode node)
    {
        return node is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax;
    }

    private static int CompareByLocation(CallSite a, CallSite b)
    {
        FileLinePositionSpan sa = a.Location.GetLineSpan();
        FileLinePositionSpan sb = b.Location.GetLineSpan();
        int c = string.Compare(sa.Path, sb.Path, StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : sa.StartLinePosition.CompareTo(sb.StartLinePosition);
    }
}
