using System.ComponentModel;
using ModelContextProtocol.Server;
using Balalaika.Workspace;

namespace Balalaika.Server;

[McpServerToolType]
public sealed class BalalaikaTools(QueryService query)
{
    private const string SymbolHelp =
        "Symbol to look up. Accepts a documentation-comment id (T:Ns.Type, M:Ns.Type.Method), a fully qualified name " +
        "(Ns.Type, Ns.Type.Member, Ns.Type<T>, Ns.Type.Method(int)), a bare type/member name (source first, then referenced assemblies; " +
        "an attribute may be named as applied: Authorize, HttpGet), or a source position " +
        "'C:\\path\\File.cs:line:col' (1-based; omit :col to take the first declaration on the line). Positions are what every hit line prints.";

    private const string SolutionHelp =
        "Solution name (file name without extension) to resolve the symbol in. Defaults to loaded solutions, then the configured default.";

    [McpServerTool(Name = "find_references", ReadOnly = true), Description(
        "Find every usage of a symbol. Live Roslyn results for loaded solutions, index results for the rest. " +
        "Returns one 'path:line:col  project  kind  in Type.Member' line per hit: kind is usage, invocation, creation, base or attribute, " +
        "and 'in' names the enclosing member or type, so a handler, a sender and a test are told apart without opening the file. " +
        "Filter with kind (who instantiates, derives, applies, calls) and scope (production or tests); group lines then read 'N of M references'.")]
    public Task<string> FindReferences(
        [Description(SymbolHelp)] string symbol,
        [Description(SolutionHelp)] string? solution = null,
        [Description("Search every known solution (default true). False limits to the defining solution.")] bool allSolutions = true,
        [Description("Keep only these usage kinds: usage, invocation, creation, base, attribute; several comma-separated (e.g. 'creation,base'). Default: every kind.")] string? kind = null,
        [Description("all (default), production (no test projects) or tests (test projects only). A test project has a Test/Tests hump in its name or a tests folder in its path.")] string scope = "all",
        [Description("Maximum hits listed per solution.")] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        return query.FindReferencesAsync(symbol, solution, allSolutions, kind, scope, limit, cancellationToken);
    }

    [McpServerTool(Name = "find_implementations", ReadOnly = true), Description(
        "For an interface: implementing types and derived interfaces. For a class: derived classes. " +
        "For a member: implementations and overrides. Across all known solutions.")]
    public Task<string> FindImplementations(
        [Description(SymbolHelp)] string symbol,
        [Description(SolutionHelp)] string? solution = null,
        [Description("Maximum hits listed per solution.")] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        return query.FindImplementationsAsync(symbol, solution, typesOnly: false, limit, cancellationToken);
    }

    [McpServerTool(Name = "find_derived_types", ReadOnly = true), Description(
        "Types deriving from a class or implementing / extending an interface, transitively, across all known solutions.")]
    public Task<string> FindDerivedTypes(
        [Description(SymbolHelp)] string type,
        [Description(SolutionHelp)] string? solution = null,
        [Description("Maximum hits listed per solution.")] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        return query.FindImplementationsAsync(type, solution, typesOnly: true, limit, cancellationToken);
    }

    [McpServerTool(Name = "go_to_definition", ReadOnly = true), Description(
        "Where a symbol is declared. Source symbols return path:line:col. Metadata symbols (NuGet, framework) return " +
        "decompiled C# of the declaring type.")]
    public Task<string> GoToDefinition(
        [Description(SymbolHelp)] string symbol,
        [Description(SolutionHelp)] string? solution = null,
        [Description("Maximum decompiled lines returned for metadata symbols.")] int maxLines = 300,
        CancellationToken cancellationToken = default)
    {
        return query.GoToDefinitionAsync(symbol, solution, maxLines, cancellationToken);
    }

    [McpServerTool(Name = "get_symbol_info", ReadOnly = true), Description(
        "Signature, kind, modifiers, containing type/namespace/assembly, base type, interfaces, attributes, XML docs, " +
        "parameters and (for types) the first members.")]
    public Task<string> GetSymbolInfo(
        [Description(SymbolHelp)] string symbol,
        [Description(SolutionHelp)] string? solution = null,
        CancellationToken cancellationToken = default)
    {
        return query.GetSymbolInfoAsync(symbol, solution, cancellationToken);
    }

    [McpServerTool(Name = "list_members", ReadOnly = true), Description(
        "Members of a type with accessibility, kind and compact signature. Optional name filter and inherited members.")]
    public Task<string> ListMembers(
        [Description(SymbolHelp)] string type,
        [Description(SolutionHelp)] string? solution = null,
        [Description("Case-insensitive substring filter on member name.")] string? filter = null,
        [Description("Include members declared on base types.")] bool includeInherited = false,
        [Description("Maximum members listed.")] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        return query.ListMembersAsync(type, solution, filter, includeInherited, limit, cancellationToken);
    }

    [McpServerTool(Name = "get_outline", ReadOnly = true), Description(
        "Map of a file or a type without reading it: one line per declaration in source order, indented by nesting, with kind, compact signature, " +
        "every attribute ([HttpGet(\"pages\")], [Fact], [Authorize]) and the line span. Call it before reading a file longer than about 100 lines, " +
        "then read only the span you need. Live syntax for loaded solutions; the index (no attributes) for the rest.")]
    public Task<string> GetOutline(
        [Description("A .cs file path (absolute, or relative to a solution directory or a configured root), or any symbol spec that resolves to a type " +
                     "(doc id, name, path:line:col). A type target lists all its partial parts.")] string target,
        [Description(SolutionHelp)] string? solution = null,
        [Description("1 = types only; 2 = types with their members and nested types (default); 3 = also the nested types' members.")] int depth = 2,
        [Description("Include private members and types (default true).")] bool includePrivate = true,
        [Description("Maximum lines; the rest is reachable with next_page.")] int limit = 300,
        CancellationToken cancellationToken = default)
    {
        return query.GetOutlineAsync(target, solution, depth, includePrivate, limit, cancellationToken);
    }

    [McpServerTool(Name = "get_call_graph", ReadOnly = true), Description(
        "One level of callers and callees of a method, constructor, property or event. Callers come from SymbolFinder across " +
        "loaded solutions; callees are the invocations and object creations in the symbol's own body. " +
        "Lines are 'Ns.Type.Member  path:line:col'; pass a line back as the symbol to walk further.")]
    public Task<string> GetCallGraph(
        [Description(SymbolHelp)] string symbol,
        [Description(SolutionHelp)] string? solution = null,
        [Description("both (default), callers or callees.")] string direction = "both",
        [Description("Include callees declared in framework or NuGet assemblies.")] bool includeExternal = false,
        [Description("all (default), production or tests: keep callers declared in, and callees declared in, production or test projects only. A metadata callee counts as production.")] string scope = "all",
        [Description("Maximum lines per list; the rest is reachable with next_page.")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        return query.GetCallGraphAsync(symbol, solution, direction, includeExternal, scope, limit, cancellationToken);
    }

    [McpServerTool(Name = "review_changes", ReadOnly = true), Description(
        "Start a branch review here, then read the diff. Compares the working tree of a loaded solution with the merge base of a git ref: " +
        "one line per changed file, then per changed declaration '+ added', '- removed', '~ signature' or '~ body' with a spec the other tools accept, " +
        "reference counts per solution (live and index, with package drift) for removed and signature-changed members, caller counts for body edits, " +
        "and compiler diagnostics on the changed files. Counts only; follow up with find_references or get_call_graph on a printed spec.")]
    public Task<string> ReviewChanges(
        [Description("Solution name (file name without extension) or path. Must be loaded; call load_solution first otherwise. Defaults to the configured default solution.")] string? solution = null,
        [Description("Git ref to compare with: branch, tag or commit. Default: the repository's default branch (origin/HEAD, else the first of main, master, develop, dev that exists). The comparison is merge-base(base, HEAD) against the working tree, so uncommitted edits count.")] string? @base = null,
        [Description("Include files in test projects (default true). False hides them and says how many were hidden.")] bool includeTests = true,
        [Description("Maximum file, declaration and diagnostic lines; the rest is reachable with next_page.")] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        return query.ReviewChangesAsync(solution, @base, includeTests, limit, cancellationToken);
    }

    [McpServerTool(Name = "next_page", ReadOnly = true), Description(
        "Continue a result that ended with '... N more (next_page cursor=...)'. Returns the next page of the same query without re-running it.")]
    public string NextPage(
        [Description("Cursor printed at the end of a truncated result.")] string cursor,
        [Description("Lines on this page and the pages after it (default: the limit of the original call).")] int? limit = null)
    {
        return query.NextPage(cursor, limit);
    }

    [McpServerTool(Name = "get_diagnostics", ReadOnly = true), Description(
        "Compiler diagnostics from the live compilation, without running a build. Target is a .cs file path or a project name. " +
        "Reflects unsaved-to-git but saved-to-disk edits immediately.")]
    public Task<string> GetDiagnostics(
        [Description("Absolute path to a .cs file, or a project name (e.g. Acme.Orders.Application).")] string target,
        [Description("Solution the project belongs to; inferred from the file path when a file is given.")] string? solution = null,
        [Description("Minimum severity: error, warning (default) or info.")] string minSeverity = "warning",
        [Description("Maximum diagnostics listed.")] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        return query.GetDiagnosticsAsync(target, solution, minSeverity, limit, cancellationToken);
    }

    [McpServerTool(Name = "search_symbols", ReadOnly = true), Description(
        "Find symbols by name pattern across all known solutions. Supports prefixes and camel-case humps (UOC -> UpdateOrderCommand). " +
        "Use this to discover the exact name before calling the other tools; each hit is 'kind Ns.Type.Member  path:line:col' and the position is a valid symbol argument.")]
    public Task<string> SearchSymbols(
        [Description("Required. The name or camel-hump pattern to search for (this is the only search argument; there is no 'query' or 'name').")] string pattern,
        [Description("Optional kind filter: class, interface, record, struct, enum, method, property, field, event, constructor.")] string? kind = null,
        [Description("Maximum hits per solution.")] int limit = 30,
        CancellationToken cancellationToken = default)
    {
        return query.SearchSymbolsAsync(pattern, kind, limit, cancellationToken);
    }

    [McpServerTool(Name = "reindex", Idempotent = true), Description(
        "Re-walk a loaded solution (or 'all') and refresh the index. Use force=true to re-extract every file even when its content hash is unchanged.")]
    public string Reindex(
        [Description("Solution name, or 'all'.")] string? solution = null,
        [Description("Re-extract unchanged files too.")] bool force = false)
    {
        return query.Reindex(solution, force);
    }

    [McpServerTool(Name = "package_drift", ReadOnly = true), Description(
        "List projects that consume a sibling repository's package at a version different from the one its source project currently declares. Covers every solution indexed at least once.")]
    public string PackageDrift(
        [Description("Maximum consumer lines per package.")] int limit = 50)
    {
        return query.PackageDrift(limit);
    }

    [McpServerTool(Name = "load_solution", Idempotent = true), Description(
        "Load a solution into memory so that live queries and diagnostics are available for it.")]
    public Task<string> LoadSolution(
        [Description("Solution name or path.")] string solution,
        CancellationToken cancellationToken = default)
    {
        return query.LoadSolutionAsync(solution, cancellationToken);
    }

    [McpServerTool(Name = "status", ReadOnly = true), Description(
        "Server status: memory, index statistics, pending jobs, and the load state of every known solution.")]
    public string Status()
    {
        return query.Status();
    }
}
