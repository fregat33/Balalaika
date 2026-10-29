# Tool reference

All tools are exposed over MCP at `http://localhost:5301/mcp`. Every result is plain text designed to be read by an
agent: one hit per line, `path:line:col` first so it is clickable, no code snippets.

## Parts: a per-request view over worktrees

Solutions inside **linked git worktrees** (a directory whose `.git` is a file pointing into the `worktrees` folder of
another repository's git directory, as `git worktree add` makes) are private. A request sees:

| Request | Visible solutions |
|---|---|
| `/mcp` (no `part`) | base checkouts and every solution that is not in a linked worktree; no worktree |
| `/mcp?part=<worktree dir>` | that worktree's solutions **instead of** the base checkout of the same repository; other repositories' base checkouts; no other worktree |
| `/mcp?part=<a>&part=<b>` | the same for each part (one part per repository) |

`part` is the worktree's root directory (the one holding `.git`), repeatable. Solutions of a part are listed and searched
first. The view is built per request from the query string and travels with the call only (an `AsyncLocal` set by the
MCP call filter); concurrent requests with different parts never share it. `GET /health?part=...` takes the same
parameter. Privacy is read from the `.git` entry of the solution's directory tree (the git directory differs from the
common git directory) and cached per solution.

Errors (a tool error result, `/health` answers 400; nothing else runs):

- `Unknown part: <path>`: not the root of a linked worktree that holds a known solution under the roots (a main checkout is not one).
- `Two parts of one repository: <a>, <b>`: both worktrees belong to the same repository.

A `solution` hint that is a full path is compared as a full path, so `/` versus `\` and a trailing separator do not
matter; the `Known:` list in the `Unknown solution` error still prints names.

What the view covers: the known-solution list, loaded-solution iteration, name/path resolution of `solution` hints, the
owner lookup for a file path, and every index query (`find_references`, `find_implementations`, `get_outline`
fallback, `search_symbols`, symbol lookup by name or doc id, package drift and consumers, tree lookups) through a
`solution_path NOT IN (...)` exclusion. A solution outside the view is *unknown* for that request: a path inside it
reports `not under any known solution` (`go_to_definition`) or `is not part of <solution>` (`get_outline`, which falls
back to the first visible solution), a hint naming it gives `Unknown solution: ...` where the tool checks the hint
(`review_changes`, `reindex`, `load_solution`, `forget_solution`), and `load_solution` with its path neither adds it to the
view nor registers anything. `find_references` and the other symbol tools ignore a hint that matches nothing visible, as
they always did, and resolve among the visible solutions.

| Tool | View applies |
|---|---|
| `find_references`, `find_implementations`, `find_derived_types`, `get_call_graph` | yes: live solutions, twins in other solutions and the index groups |
| `go_to_definition`, `get_symbol_info`, `list_members`, `get_outline`, `get_diagnostics` | yes: symbol/hint/path resolution |
| `review_changes` | yes: the `solution` hint resolves among visible solutions; the review runs on the part's worktree (the git root is the worktree's `.git` file, the merge base is resolved there) |
| `search_symbols` | yes |
| `package_drift` | yes: consumers and sources in hidden solutions are left out |
| `reindex` | yes: `all` means the visible loaded solutions; a hint among the visible ones |
| `load_solution` | yes: never registers or loads a hidden solution |
| `forget_solution` | yes: the hint resolves among visible solutions only |
| `status`, `/health` | yes: lists only visible solutions (index statistics and job counts stay global) |
| `next_page` | no: a cursor replays what the original call produced |

Cost note: background work (`PropagateSurfaceChangesAsync`, watchers, warm-up) is not part of any request and covers
every known solution, so each loaded worktree adds to the cost of a surface change; `IdleEvictionMinutes` is 0 by default.

## Caller and the per-call log line

`caller=<text>` is an optional query parameter on `/mcp`, for example `/mcp?caller=worker/sonnet/t1`. It
is free text, written into the per-call log line and nowhere else: it changes no result, and an absent or blank value
is never an error.

Every tool call writes one line to the `Balalaika.Server.ToolCalls` logger (Warning with `error=` appended when the
call returned an error):

```
search_symbols pattern="UpgradeCase" limit=20 found=18 client="claude-code 2.1.311" caller="worker/sonnet/t1" part=- 3ms
```

- tool name and its arguments as `name=value` in the order received (strings quoted and cut to 120 characters, as run after an accepted argument rename);
- `found`: the number of results the tool found (`0` for an empty result), `review_changes` logs the number of changed declarations; `-` for tools without a list (`go_to_definition`, `get_symbol_info`, `next_page`, `status`, `reindex`, `load_solution`, `forget_solution`);
- `client`: name and version from the MCP `clientInfo`, or `-` when the client sent none;
- `caller`: the `caller` parameter (trimmed, at most 100 characters), or `-`;
- `part`: the `part` values joined by `;`, or `-`;
- elapsed milliseconds.

Argument keys that are not plain identifiers (`[A-Za-z0-9_]+`) are quoted and escaped like values (cut to 120
characters first), also in the "Rejected ... call with arguments" warning. Quoted values (string arguments, `client`, `caller`, `part`) escape `"` as `\"`, `\` as `\\`, and CR and LF as `\r`
and `\n`, so the line stays one line and can be split back into fields. Values are cut first and escaped after, so the
first characters stay recognisable and the visible length is the limit plus the escapes.

The structured fields (`Tool`, `Arguments`, `Found`, `Client`, `Caller`, `Part`, `ElapsedMs`, `Error`) are separate
properties in the CLEF file.

## Symbol specs

Every tool that takes a symbol accepts these forms, tried in this order inside each compilation:

| Form | Example |
|---|---|
| Documentation-comment id | `T:Acme.Orders.Domain.Order`, `M:Acme.Orders.Domain.Order.SetStatus(Acme.Orders.Domain.Status,System.Guid,System.DateTime)` |
| Fully qualified type | `Acme.Orders.Domain.Order`, nested `Ns.Outer.Inner`, generic `MediatR.IRequestHandler<TRequest,TResponse>` |
| Fully qualified member | `Acme.Orders.Domain.Order.SetStatus`, with parameter types to pick an overload `Demo.Foo.Run(int)`, constructor `Demo.Foo.Foo(int)` |
| Bare name | `IUnitOfWork`, `PlaceOrderCommand` (types preferred over members, shortest display string wins). Source first, then the referenced assemblies, so `AuthorizeAttribute` resolves; an attribute may be named as applied, `Authorize` or `HttpGet` |
| Source position | `C:\src\acme\...\Order.cs:42:17` or `...\Order.cs:42` (1-based line and column, the form every hit line prints) |

Resolution searches loaded solutions first, preferring symbols declared in source over metadata. If nothing is found,
the index is asked which solution declares the symbol and that solution is loaded (waiting up to `LoadWaitSeconds`).
When a `solution` argument is given, only that solution is searched.

A source position resolves the identifier under the cursor in the solution that owns the file, so any line from a
previous result can be passed straight back. A column one past an identifier (on the `(` of a call) still picks that
identifier. Without a column the first declaration on the line wins (type, then member, then field), falling back to
the first referenced symbol; locals and parameters are chosen only when nothing else is there. Constructed generics
and reduced extension methods are normalised to their original definition so the result has a doc id.

## Paging

Every list is cut at the tool's `limit`. When more lines exist the result ends with
`... N more (next_page cursor=xxxxxxxxxx)`. Cursors are held in server memory for 30 minutes (at most 128 at a time)
and the query is not re-run.

### `next_page(cursor, limit?)`

Returns the next page of the same list under a `Continued: <label>` header, with a fresh cursor if more remain. The
page is as long as the original call's `limit` unless `limit` is given, which then also sizes the pages after it.

## What a count means

References count usages that bind to the declaration itself: a call through `IShape.Area()` is a reference of
`IShape.Area()`, not of the `Square.Area()` that implements it (the family of an interface or base member is
`find_implementations`). `find_references`, its index groups and the impact lines of `review_changes` follow this one
rule. Callers keep dispatch through base and interface members, because that call does run the member: they are
counted apart from direct callers and tagged `via <member>`, and `direct = true` gives the symbol's own callers only.

## Query tools (read-only)

### `find_references(symbol, solution?, allSolutions = true, kind?, scope = "all", limit = 200)`

Header with signature, kind, doc id, assembly, and declaration location. Then one block per solution:
`Live <name>: N references` for loaded solutions (`SymbolFinder`, keeping the usages that bind to the declaration
itself), `Index <name>: N references (solution
not loaded; from index)` for the rest, with `[stale]` on hits whose file changed since indexing. Footer `Total: N`.

`kind` keeps only the listed usage kinds (`creation`, or several as `creation,base`); `scope` keeps `production`
(no test projects) or `tests` (test projects only). A test project has a `Test` or `Tests` hump in its name
(`Acme.UnitTests`, `TestUtilities`, not `Latest`) or a `test`/`tests` folder in the file's path, the rule
`search_symbols` ranks by (`ProjectKinds.IsTestProject`). With a filter the group lines and the footer read
`6 of 41 references (kind=creation; scope=production)`, so the cut is visible, and an index solution whose hits were
all cut still prints its `0 of M` line. Live hits are filtered after classification, index hits in the query. A kind
or scope outside the accepted list is answered with that list.

Each hit is `path:line:col  project  kind  in Type.Member`:

```
Live Acme.Orders: 4 references
  ...\UpdateOrderCommand.cs:27:75  Acme.Orders.Application  usage  in UpdateOrderCommandValidator
  ...\UpdateOrderCommand.cs:43:34  Acme.Orders.Application  usage  in UpdateOrderCommandHandler.Handle
  ...\OrderController.cs:58:31  Acme.Orders.Presentation.Api  creation  in OrderController.UpdateOrder
  ...\UpdateOrderCommandHandlerTests.cs:57:44  Acme.Orders.UnitTests  creation  in UpdateOrderCommandHandlerTests.Handle_WhenPaid_Upgrades
```

- `kind` is how the symbol is used at that position: `invocation` (called), `creation` (`new`), `base` (in a base
  list), `attribute`, or plain `usage` (type of a parameter, field, variable, generic argument, member access).
- `in` names the innermost declaration the hit sits in: a method, constructor, property (also from inside an accessor),
  event, field (from its initializer) or enum member, else the type itself (base lists, attributes, positional record
  parameters). Lambdas and local functions report their containing member. Top-level statements print `in <top-level>`.
  The name is `Type.Member` with no namespace; constructors print as `Type.ctor`. When two overloads of one method both
  appear in a result they get `(n params)` appended; when two types of one short name in different namespaces both
  appear, their hits print the namespace-qualified name instead.
- Live and index hits print the same words for the same source (`EnclosingMember`, `DocumentExtractor.UsageKindOf`,
  `EnclosingDisplay`). Index rows extracted before the column existed print no `in` part.

### `find_implementations(symbol, solution?, limit = 200)`

Interface: implementing types (transitive) and derived interfaces. Class: derived classes (transitive). Member:
implementations and overrides. Lines are `kind  typekind FullyQualifiedName  path:line:col` where kind is `implements`,
`inherits` or `overrides`. Index relations are appended for unloaded solutions.

### `find_derived_types(type, solution?, limit = 200)`

Same as `find_implementations` but rejects non-type symbols with a hint.

### `go_to_definition(symbol, solution?, maxLines = 300)`

Source symbols: every declaration location (partial classes list several). Metadata symbols: the declaring type
decompiled with ICSharpCode.Decompiler, prefixed with the assembly path. When the compile-time reference is a `ref/`
assembly, the sibling `lib/` assembly is decompiled instead so bodies are present. Output is cut at `maxLines`.

### `get_symbol_info(symbol, solution?)`

Signature, kind, doc id, accessibility and modifiers, namespace, containing type, assembly with source/metadata
marker, where it was resolved, declaration locations, base type, interfaces, type parameters, return type and
parameters (methods), property type and accessors, field type and constant, attribute names, XML docs rendered as
`summary:`, `param x:`, `returns:` lines, and the first 60 members of a type.

### `list_members(type, solution?, filter?, includeInherited = false, limit = 200)`

`accessibility [static] kind CompactSignature`, public first, then fields, properties, events, constructors,
methods. Inherited members carry `[from Base]`. Accessors and compiler-generated members are hidden.

### `get_outline(target, solution?, depth = 2, includePrivate = true, limit = 300)`

A map of a file or a type, so the caller reads only the lines it needs instead of the whole file. `target` is a `.cs`
path (absolute, or relative to a solution directory or a configured root) or any symbol spec that resolves to a type;
a member spec outlines its containing type, and a type lists every partial part under its own file line. One line per
declaration, in source order, indented by nesting, with the line span (excluding leading trivia, so doc comments are
not counted):

```
Outline: src\Application\...\UpdateOrderCommand.cs  (Live Acme.Orders, Acme.Orders.Application)
  record UpdateOrderCommand : IRequest, IAllowedRolesCommand  21-25
    property Guid OrderId { get; init; }  21
    private static field ImmutableArray<Role> _allowedRoles  23
  internal class UpdateOrderCommandValidator : AbstractValidator<UpdateOrderCommand>  27-34
    constructor UpdateOrderCommandValidator(IUserContext userContext)  29-33
  internal class UpdateOrderCommandHandler : IRequestHandler<UpdateOrderCommand>  36-156
    method Task Handle(UpdateOrderCommand command, CancellationToken cancellationToken)  43-155
Total: 7 declarations
```

- Each line is `[accessibility] [static] kind CompactSignature [: base list]  [attributes]  span`. Accessibility is
  printed only when it is not public; the signature is the compact form `list_members` uses; types show their base
  list as written.
- Every attribute is shown in brackets with no allow-list: the short name without the `Attribute` suffix, plus its first
  positional argument when that is a string literal or a constant that resolves to one (`[HttpGet("{id}")]`,
  `[Route(Routes.Orders)]` prints the constant's value, `[Fact]`, `[Authorize]`). Other arguments are dropped. Routes,
  test markers and job triggers therefore show the same way without the server knowing any framework.
- `depth`: 1 lists types only, 2 adds their members and nested types, 3 adds the nested types' members. An entry at
  nesting `n` appears when `depth >= n + 1`. `includePrivate=false` hides private members and private nested types.
  Accessors and compiler-generated members are never listed; positional record parameters are listed as properties.
- A loaded solution is outlined from live syntax (`Live`). A file in an unloaded solution is outlined from the index
  (`Index ...; solution not loaded, attributes not shown`): the stored signature per symbol, its start and end line,
  nesting from the containing doc id, `[stale]` when the file changed since indexing. A file nobody indexed yet loads
  its solution first. The list goes through the pager, so a long file ends with a `next_page` cursor.

### `get_call_graph(symbol, solution?, direction = "both", includeExternal = false, scope = "all", direct = false, limit = 100)`

One level in each direction for a method, constructor, property or event (types are rejected with a hint).
`Callers` come from `SymbolFinder.FindCallersAsync` in the defining solution and in every other loaded solution
where the symbol's twin resolves; one line per call site, `Ns.Type.Member(params)  path:line:col  solution`.
`Callees` are the invocations and object creations inside the symbol's own declaring syntax, listed once each at their
first call site as `Ns.Type.Member(params)  declaration  called at path:line:col`. Metadata callees (framework,
NuGet) appear only with `includeExternal=true`; a metadata symbol has no body and therefore no callees. Pass any
printed `path:line:col` back as `symbol` to walk a further level. `direction` is `both`, `callers` or `callees`.

`scope` is `all`, `production` or `tests` and is judged on the declaration at the other end of each edge: the calling
member's project for callers, the callee's declaring project for callees (a metadata callee has no project and counts
as production). The same test-project rule as `find_references` applies, and the list headers read
`Callers (3 of 12, scope=tests, live, loaded solutions only):`.

Callers include calls dispatched through a base or interface member of the symbol. Such a row ends in
`  via Ns.IShape.Area()`, and the header counts them apart: `Callers (7: 3 direct, 4 via base or interface members,
live, loaded solutions only):` (with a filter, `Callers (3 of 12: 1 direct, 2 via base or interface members,
scope=tests, live, loaded solutions only):`). A header without via rows is unchanged. `direct = true` drops the via
rows, leaving the symbol's own callers.

### `get_diagnostics(target, solution?, minSeverity = "warning", limit = 200)`

`target` is an absolute `.cs` path (solution inferred from the path; semantic-model diagnostics for that file) or a
project name (`compilation.GetDiagnostics()` for the project). Only compiler diagnostics; analyzers do not run.

A file target reflects the file as saved on disk at the moment of the call: before answering, the tool compares the
disk text with the live document and, when they differ, applies the saved text to the in-memory solution
(`LiveTextSync`). The watcher's own job, which arrives after the debounce, finds the same text and brings the index
up to date. So a call made immediately after a save, as from a post-edit hook, already sees the edit. A project
target does not do this and reflects the watcher's state, normally within a second of a save.

`scripts/post-edit-diagnostics.ps1` is a Claude Code `PostToolUse` hook built on this: after every `Edit` or
`Write` of a `.cs` file it calls `get_diagnostics` with `minSeverity=error` and returns the errors as additional
context, nothing when the file is clean, not C#, unknown to the server, or the server is down. The registration
snippet is in the README.

### `review_changes(solution?, base?, includeTests = true, limit = 200)`

The first call of a branch review; the diff itself is read afterwards. Compares the working tree of a loaded
solution with `merge-base(base, HEAD)`, so uncommitted edits count and a clean checkout reviews exactly the branch.
Without `base` the repository's default branch is used: the branch `origin/HEAD` points at (its local branch when one
exists), else the first of `main`, `master`, `develop`, `dev` that exists; the first line names the ref chosen.
The base side is a fork of the live Roslyn `Solution` in memory with the base text of each changed document (from
`git show`); unchanged trees are shared, the fork is dropped with the result, and nothing is written to disk or to the
index. A solution inside a linked worktree is reviewed through `?part=<worktree dir>` with the worktree's solution
path; the default base is then resolved in that worktree. Needs `git` on the PATH of the server process, or `GitPath` in `balalaika.json`; `status` prints the version
found. Without it, or with an unknown `base`, the first line says so and nothing else runs.

```
Review: Acme.Orders  working tree vs main (merge-base 3f2a1c9)  12 files changed: 9 source, 1 project, 2 other
...\Application\Orders\UpdateOrderCommand.cs  modified  surface
  ~ signature  Acme.Orders.Application.IFileRepository.UploadFile(Stream, string, CancellationToken)  Task UploadFile(Stream s, string name) -> Task UploadFile(Stream s, string name, CancellationToken ct)  31
      references: Live Acme.Orders 7, Index Acme.Workers 2 [package 0.1.4, source is 0.1.6]; the old signature had 7 in Acme.Orders
  ~ body  Acme.Orders.Application.UpdateOrderCommandHandler.Handle(UpdateOrderCommand, CancellationToken)  public  99-230
      callers: 3
  - method  Acme.Orders.Application.OrderService.RecalculateAsync(Guid)  public  was 40-58
      references: Live Acme.Orders 0 before removal (usages left show as errors below), Index Acme.Support.Api 1 remaining
  + method  Acme.Orders.Application.UpdateOrderCommandHandler.SendOrderUpdateAsync(Order, CancellationToken)  private  240-258
...\Infrastructure\Infrastructure.csproj  modified  (project file, not analysed; run reindex force=true if a package version changed)
...\Presentation.Api\Controllers\NewController.cs  added  (14 declarations; get_outline lists them)
Diagnostics on changed files: 1 errors, 0 warnings (compiler only; analyzers are not run)
  ...\Application\Orders\UpdateOrderCommand.cs:105:17  error CS1503: ...
Total: 9 source files, 14 changed declarations (3 surface, 11 body-only or private), 1 errors, 0 warnings
```

- Files are listed in path order with `added`, `modified` or `deleted`. A modified source file is labelled
  `surface` when its non-private declarations or relations differ (`DeclarationSurface.Hash`, the test the indexer
  uses to re-extract dependents), else `body-only`. Project, props, targets and solution files are listed and not
  analysed; other files under the solution directory are listed by name; files outside it are counted in the header.
- Declarations are matched by doc id between the two sides: `+` added, `-` removed, `~ signature` for a changed
  signature, accessibility or parameter list (a removed and an added member of the same type, name and kind are one
  signature change; a rename stays `-` and `+`), `~ header` for a type whose attributes, base list, primary
  constructor or constraints changed, `~ body` for the same signature with different tokens. Whitespace, comments
  and doc comments do not count. Every line carries a spec the other tools accept (`Ns.Type.Member(paramTypes)`),
  the accessibility and the line span (`was` for removed). A `~ header` line ends with what the header gained or
  lost, from the relations and attributes of both sides: `+ implements Acme.IReadonlyRestrictedCommand`,
  `- [Obsolete]`; when neither records the change it says `(primary constructor or constraints)`.
- The indented line under a non-private declaration is its impact. Removed or signature-changed: direct references
  per solution, live for loaded solutions and from the index for the rest, with the package drift tag of
  `find_references`; counts bind to the declaration itself, not to the interface member or base member it satisfies.
  A removed member prints how many references it had on the base side (usages left in this solution are compile
  errors in the diagnostics section) and how many other solutions still hold. A parameter change also prints how many
  references the old overload had. Body-only changes of methods, constructors, properties and events print their
  one-level caller count, except in test projects, where nothing in production calls them and the runner's reflection
  is invisible. Every reference and caller count splits into its halves when a test project is involved,
  `callers: 25 (2 production, 23 tests)`, so the risk reads off the line. Callers dispatched through a base or
  interface member are split off too, parts joined by `; `: `callers: 3 (1 direct, 2 via base or interface members)`,
  `callers: 7 (3 direct, 4 via base or interface members; 2 production, 5 tests)`. An added interface or abstract class prints `implementations:` or `derived types:` per
  solution (the `find_implementations` lookups), and a member added to an interface or an abstract member prints
  `implementations:` or `overrides:` even when zero, since every implementer elsewhere has to add it. Other
  additions and private declarations print no impact.
- Added or deleted files list their declarations up to ten; larger files print the count.
- Diagnostics are the compiler errors and warnings of the changed files in the live solution, without analyzers.
- `includeTests=false` hides files in test projects (the `search_symbols` rule) and says how many were hidden.
- File, declaration, impact and diagnostic lines go through the pager; the header and the total do not.

### `search_symbols(pattern, kind?, limit = 30)`

Loaded solutions: `SymbolFinder.FindSourceDeclarationsWithPatternAsync`, which understands prefixes and camel-case
humps (`UOC` finds `UpdateOrderCommand`), plus a substring pass over declaration names so `Retry` also reaches
`CreateRetryOptions`. Unloaded solutions: the union of an FTS5 prefix match, a `LIKE` substring match and a `GLOB`
camel-hump match. Both paths rank the candidates with `SymbolRanker` (`Balalaika.Index`), so the same symbols print
in the same order either way:

1. production code before test projects (a `Test`/`Tests` hump in the project name, or a `tests` folder);
2. match quality: exact name, then the name's last hump (`Job` -> `CloseCompletedOrdersJob`), then prefix, then
   camel humps (`Retry` -> `CreateRetryOptions`, `UOC`), then plain substring;
3. kind: types and methods, then properties and events, then fields and enum members;
4. the shorter name, then the fully qualified name.

Each hit is `kind Ns.Type.Member  path:line:col`, grouped per solution. The position is the spec to pass to the other
tools; the doc id is not printed since 2026-10-13, because it repeated the name and made up a third of each line.

The last term of a dotted or spaced pattern names the symbol; earlier terms must occur in the fully qualified name
(`App.Orders` -> `App.OrdersController`). `kind` filters on `class`, `interface`, `record`, `struct`, `enum`, `method`,
`property`, `field`, `event`, `constructor`, `enum_member`, `delegate`.

## Maintenance tools

### `reindex(solution?, force = false)`

Queues a re-walk of one loaded solution or `all`. `force=true` re-extracts every document regardless of hash. Since
public-surface changes re-extract the affected projects automatically (see the invalidation document), this is only
needed after a csproj or package version change, or when `ReextractDependents` is turned off.
A solution that is not loaded is loaded instead; indexing follows automatically.

### `package_drift(limit = 50)`

Lists every indexed project that consumes a sibling repository's assembly as a NuGet package at a version other than
the one the source project currently declares, grouped by source assembly:

```
Acme.Contracts  source 0.1.6 (Acme.Orders)
  Acme.Workers  Acme.Workers.Application.Contracts  0.1.4
Total: 1 consumer projects on a version other than the source. Only solutions indexed at least once are compared.
```

The consumer version comes from the resolved package path of the project's metadata references
(`...\packages\<id>\<version>\lib\...`); the source version is the `AssemblyInformationalVersion` the design-time
build generates from the `Version` property. A source project without a declared version (SDK default `1.0.0`) is not
compared, because its real version is set at pack time. Any difference is listed, consumer behind or ahead.

The same comparison annotates `find_references` and `find_implementations`: the `Assembly:` header shows the source
version, and a per-solution group line gets `[package 0.1.4, source is 0.1.6]` when that solution consumes the symbol's
assembly at another version. A group with no hits adds `the symbol may not exist in that version`, which is the usual
reason a cross-repository lookup comes back empty.

### `load_solution(solution)`

Starts or joins a load and waits up to `LoadWaitSeconds`. Reports project count, load time and the number of workspace
diagnostics. `solution` may be a known name or path, or the full path of an existing `.sln` / `.slnx` that is not known
yet (inside the roots or not); that file is registered first. A missing path or another extension answers
`Unknown solution: ...`.

### `forget_solution(solution)`

Removes a solution from the known list: unloads it (even if pinned), drops its pending refresh and deletes its project
rows (and with them blobs, symbols, references, relations) from the index. Answers
`Forgot Acme.Orders (C:\src\acme\Acme.Orders\Acme.Orders.sln): 4 projects removed from the index.`; an unknown name
answers `Unknown solution: ...`. Annotated destructive. If the solution file still exists it stays forgotten until
`load_solution` is called with its full path, a create or rename event for the file arrives, or the server restarts.
Solutions whose file disappears are forgotten automatically, and `*.sln` / `*.slnx` files that appear under a root are
registered automatically (see "Runtime discovery" in `indexing-and-invalidation.md`).

### `status()`

Working set and GC heap, index path and row counts, pending jobs and the last completed job, and every known
solution with its state (`unloaded`, `loading`, `loaded`, `failed`), pin flag, project and document counts, load time
and last-used time. The line after `Jobs:` is the readiness signal: `Ready: yes`, or `Ready: no (<n> jobs, <m> buffered)`
where `n` counts queued and running index jobs (plus unloaded refreshes started by a git change) and `m` the file events
the watcher still holds back in its debounce window. After a branch switch, wait for `Ready: yes` before querying. Each
solution line ends with `head <7 chars>` (the git commit its index was last brought in line with) and then the path;
`head -` means unknown (not yet checked since startup, or no repository). A `Warm-up:` line shows the startup warm-up progress (`2 of 3 done, loading X`, then
`done, N warmed, M already fresh`) when it is enabled and there are solutions beyond the preloads. An
`Unloaded refresh:` line lists solutions with changes waiting for a refresh (`X (3 files, last change 42s ago)`) and
how many refreshes ran. The same text is served at `GET /health`.

## Calling from PowerShell

```powershell
scripts\mcp-call.ps1 -Tool find_references -Arguments @{ symbol = "IUnitOfWork"; limit = 20 }
scripts\mcp-call.ps1 -Tool get_diagnostics -Arguments @{ target = "C:\src\acme\Acme.Orders\src\...\Foo.cs" }
```

The script posts a JSON-RPC `tools/call`, accepts both JSON and SSE responses, and prints the text content.
