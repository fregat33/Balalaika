# AGENTS.md

Guidance for AI coding agents working in this repository (Balalaika). Human-facing material is in `README.md`;
design rationale is in `docs/`. Read `docs/indexing-and-invalidation.md` before touching anything
under `Balalaika.Index` or the job consumer.

## Commands

```powershell
dotnet build Balalaika.slnx
dotnet test tests/Balalaika.Tests/Balalaika.Tests.csproj
dotnet test tests/Balalaika.Tests/Balalaika.Tests.csproj --filter "FullyQualifiedName~SolutionIndexerTests"

# run the server from the build output (reads src/Balalaika.Server/balalaika.json, then balalaika.local.json)
dotnet run --project src/Balalaika.Server --no-build

# call a tool against a running server
scripts\mcp-call.ps1 -Tool status
scripts\mcp-call.ps1 -Tool find_references -Arguments @{ symbol = "IUnitOfWork"; limit = 10 }
```

The solution file is `Balalaika.slnx` (not `.sln`). The server locks its own build output while running: stop it
before `dotnet build` of the Server project, or the copy step fails.

## Architecture in one minute

Server -> Workspace -> Index, and Server -> Watcher -> Index. Workspace never references Watcher.

| Where | What lives there |
|---|---|
| `src/Balalaika.Index/IndexStore.cs` | the whole SQLite schema and every query; content-addressed `blob` + `tree` |
| `src/Balalaika.Index/DeclarationSurface.cs` | hash of a blob's non-private declarations and relations; equal hashes mean other files bind the same |
| `src/Balalaika.Index/EnclosingDisplay.cs` | `Type.Member` display of the declaration a reference sits in, shared by the live and the index path; overload suffix |
| `src/Balalaika.Workspace/LoadedSolution.cs` | in-memory `Solution` forked on file changes; never writes to disk |
| `src/Balalaika.Workspace/WorkspaceManager.cs` | lazy, deduplicated loads; restore-and-reload when a project has no references; eviction |
| `src/Balalaika.Workspace/DocumentExtractor.cs` | syntax walk producing symbols, references, relations for one document; `UsageKindOf` is also used by live hits |
| `src/Balalaika.Workspace/EnclosingMember.cs` | the declaration a syntax node sits in (member, else type, `<top-level>`); used by the extractor and by live `find_references` |
| `src/Balalaika.Workspace/FileOutline.cs` | `get_outline` entries from syntax (kind, compact signature, every attribute, line span) or from stored symbol rows |
| `src/Balalaika.Workspace/SolutionIndexer.cs` | stat fast path, hash, blob reuse or extraction, tree update, surface diff per changed document, forced project re-extraction |
| `src/Balalaika.Workspace/DependentProjects.cs` | transitive dependents in a solution; consumers in other loaded solutions by DLL name or csproj path |
| `src/Balalaika.Workspace/IndexFreshness.cs` | stat-only check whether a solution's index matches the disk; source-file enumeration shared with reconcile |
| `src/Balalaika.Workspace/SourceFilter.cs` | `IgnoredFolders` option: the path check and source enumeration shared by indexer, reconcile, freshness, watcher and live lists |
| `src/Balalaika.Workspace/IndexJobQueue.cs` | job records and the single-consumer channel; `Enqueue` returns a task that completes when the job ran |
| `src/Balalaika.Workspace/WarmUpTracker.cs` | progress of the startup warm-up for `status` |
| `src/Balalaika.Workspace/PendingRefreshes.cs` | changes seen under unloaded solutions, released for refresh after a quiet period or a max wait |
| `src/Balalaika.Workspace/LiveTextSync.cs` | on-demand apply of one file's disk text to the live solution ahead of the watcher (`get_diagnostics` on a file); the shared retrying file read |
| `src/Balalaika.Workspace/PackageReferences.cs` | package id and version from NuGet packages-folder paths; a project's declared version from its compilation |
| `src/Balalaika.Workspace/SymbolResolver.cs` | spec string -> `ISymbol`, including `path:line:col`; see `docs/tools.md` for accepted forms |
| `src/Balalaika.Workspace/QueryService.cs` | every tool's logic and text formatting |
| `src/Balalaika.Workspace/ResultPager.cs` | in-memory overflow lines behind `next_page` cursors; every list goes through `AppendPage` |
| `src/Balalaika.Workspace/CallGraph.cs` | callers via `SymbolFinder`, callees via a syntax walk of the declaring body |
| `src/Balalaika.Workspace/GitRepository.cs` | `IBaseContentProvider` over the git command line: probe, repository root, changed files since the merge base, base text; tests fake it |
| `src/Balalaika.Workspace/ChangeReview.cs` | `review_changes`: in-memory fork of the live `Solution` with the base text, declaration comparison by doc id, formatting; impact counts come from `QueryService` |
| `src/Balalaika.Watcher/` | `ChangeClassifier` (pure, tested) and `RepoWatcher` (debounce, overflow) |
| `src/Balalaika.Server/IndexingHostedService.cs` | startup, preload, warm-up and refresh of unloaded solutions, the single consumer of `IndexJobQueue`, reconcile |
| `src/Balalaika.Server/BalalaikaTools.cs` | MCP tool declarations only; delegate to `QueryService` |

## Invariants to preserve

- **Do not call `TryApplyChanges` on an `MSBuildWorkspace`.** It writes source files back to disk. Fork the
  `Solution` in `LoadedSolution.ApplyFileChange` instead.
- **All index mutations go through `IndexJobQueue`** and are executed by `IndexingHostedService`. Tools only enqueue.
- **Hash decoded text** with `ContentHasher.HashText(sourceText.ToString())`, never raw file bytes, so disk reads and
  in-memory documents agree.
- **Every index query joins `blob` to `tree`.** A new query that bypasses that join will return results from other
  branches.
- **Symbol identity is doc-comment id + assembly name.** Do not introduce `SymbolKey` strings into the database.
  Derive the assembly half with `SymbolIdentity.AssemblyOf`, never `ContainingAssembly.Name` directly: framework
  assemblies (`netstandard`, `mscorlib`, `System.*`, ...) collapse to one token so references agree across target
  frameworks. Display paths may still print the real name.
- **Tool output stays compact text**: `path:line:col  project` per hit, no snippets, `Live` vs `Index` labelled.
- **Lists are cut by `ResultPager.AppendPage`, never by `Take(limit)`**, so every truncated list carries a `next_page` cursor.
- **Delete child rows explicitly before deleting a blob** so the FTS delete trigger fires (see `DeleteBlobRows`).
- **Schema changes go through `EnsureColumn` and bump `IndexStore.SchemaVersion`** when an index pass starts writing
  new per-project or per-blob data. The freshness check reindexes projects stamped with an older version, and
  `SolutionIndexer.IndexProjectAsync` re-extracts every document of such a project (the content hash alone would reuse
  the old blobs forever), so existing databases fill the new columns on the next load instead of staying empty.
- **Surface changes propagate.** `SolutionIndexer.IndexDocumentsAsync` reports the projects whose non-private
  declarations changed and `IndexingHostedService.PropagateSurfaceChangesAsync` re-extracts them with their dependents.
  Route file deletions through `SolutionIndexer.RemoveDocument`, not `RemoveTreeEntriesByPath`, so they are counted.
- **A reconcile never adds a file the compilation excluded.** `LoadedSolution` records at load which `.cs` files under
  each project directory are not documents (`Compile Remove` globs, typically old EF migrations); `ReconcileAsync`,
  `ApplyFileChange` and `IndexFreshness` skip them. Folders in `IgnoredFolders` (`SourceFilter`) are skipped in the
  same places, in the watcher and in live list output. Any new code that walks a project directory for `.cs` files must
  go through `SourceFilter.EnumerateSourceFiles` and honour `LoadedSolution.IsExcludedSourceFile`.

## Code conventions in this repository

- File-scoped namespaces, explicit types rather than `var`, `this.` for instance members, primary constructors for DI.
- Records for data (`Model.cs`, jobs, results), sealed classes for services.
- Tests: xUnit 2.9 + FluentAssertions 7. `// Arrange`, `// Act`, `// Assert` comments; names
  `Method_WhenCondition_Expected`. Fixtures come from `TestWorkspace.CreateProject` (AdhocWorkspace with a real file
  path) and temp SQLite files under `%TEMP%\balalaika-tests`.
- No MSBuild in tests. Anything needing `MSBuildWorkspace` is a smoke test against a running server (a trait-gated
  fixture test is planned for the next Roslyn or SDK upgrade; it must stay out of the default run).

## Pitfalls already hit

- A tool method parameter named `query` shadows the injected `QueryService query`; the MCP-visible name changed to
  `pattern`. Keep parameter names distinct from the primary-constructor parameter.
- `ISymbol` is ambiguous between `Microsoft.CodeAnalysis` and `ICSharpCode.Decompiler.TypeSystem`; alias it.
- NuGet audit warnings, source-mapping warnings and "will not be pruned" arrive as `WorkspaceDiagnosticKind.Failure`
  from the design-time build. They are filtered from the log in `LoadedSolution.IsNuGetAuditNoise` but kept in
  `LoadDiagnostics`.
- The web template's `Properties/launchSettings.json` overrides the Kestrel port; it was removed. Do not add it back.
- A folder with both `Foo.sln` and `Foo.slnx` is one solution; `SolutionDiscovery` keeps the `.sln`.
- `dotnet package search --format json` output did not parse reliably here; `dotnet add package` without a version
  resolves the latest and is simpler.

## Verifying a change

1. `dotnet build Balalaika.slnx` and `dotnet test ...` must be green (277 tests at the time of writing).
2. Start the server, wait for `Loaded <solution>` and `Indexed <solution>` for the preloaded solution in the console, then run
   at least: `status`, `find_references` on a known symbol, `get_diagnostics` on a project, `search_symbols` with a
   camel-hump pattern.
3. For watcher or reconcile changes: create a throwaway `.cs` file with an obvious error under a loaded project
   directory, confirm `get_diagnostics` reports it within a few seconds, delete the file, confirm it is gone from
   `search_symbols`. Leave the target repository's `git status` clean.
4. For index changes: restart the server and confirm the `Indexed` line reports everything reused and nothing
   extracted.
5. For surface-change changes: add a public method to a class in a project with dependents, confirm the console logs
   `declared surface of <Project> changed; re-extracted N dependent projects`, then revert and confirm a body-only
   edit logs no such line.

## Using the server from an agent session

When the `balalaika` MCP server is registered (`claude mcp add --transport http balalaika http://localhost:5301/mcp`):

- Start a branch review with `review_changes` (solution and base ref), then read the diff. It lists every changed
  declaration with a spec to pass on, the reference counts of removed and signature-changed members per solution,
  caller counts of body edits, and the compiler diagnostics of the changed files, in one call. Counts only: follow up
  with `find_references` or `get_call_graph` on the printed spec where a count needs names.
- Prefer `find_references` and `find_implementations` over grep for anything semantic; grep is still right for
  strings, comments and configuration.
- Call `search_symbols` first when unsure of the exact name; pass the printed `path:line:col` or the fully qualified
  name to the other tools (hits no longer print the doc id; it repeated the name and cost a third of each line). Its search
  argument is `pattern`. A call that sends one unknown name in place of the one missing required argument (`query` for
  `pattern`, `file` for `get_outline`'s `target`) is renamed and runs, logged at INFO. Any other call that omits a
  required argument comes back with the missing name and the accepted names (`ToolArgumentCheck`), not with the
  SDK's bare "An error occurred invoking" text.
- Any `path:line:col` printed by a tool is itself a valid symbol spec; pass it back instead of guessing a name.
- Call `get_outline` before reading a file longer than about 100 lines, then read only the printed line span. A type
  spec outlines every partial part. `find_references` hits already say which member they sit in (`in Type.Member`)
  and how (`base`, `creation`, `invocation`, `attribute`, `usage`), so a handler, a sender and a test are told apart
  without opening the file.
- Ask `find_references` one question at a time with `kind` and `scope` instead of reading the whole list: `kind=creation`
  with `scope=production` is "who sends this command", `kind=base` is "who derives from this", `kind=attribute` on
  `HttpGet` lists every action with its method, `kind=invocation` on `AddScoped` lists registrations, `scope=tests`
  is "is this covered". Several kinds are comma-separated. `get_call_graph` takes the same `scope`. The group line
  then reads `N of M references`, so a filtered call still tells how much it hid.
- A result ending in `... N more (next_page cursor=...)` is continued with `next_page`; do not re-run with a bigger limit.
- `get_call_graph` gives one level of callers and callees; walk further by passing a printed position back.
- Use `get_diagnostics` on the file you just edited instead of `dotnet build` for compiler errors. It does not run
  analyzers, so a final build is still needed for Meziantou and StyleCop rules. A file target is read from disk
  first, so the call is correct immediately after a save. With the `PostToolUse` hook in `.claude/settings.json`
  (`scripts/post-edit-diagnostics.ps1`) the errors of an edited `.cs` file arrive on their own; a clean file adds
  nothing.
- `go_to_definition` on a NuGet or framework type returns decompiled source; raise `maxLines` if it is truncated.
- If results look stale after a csproj or package version change, run `reindex` with `force=true` for that solution.
  Source-level surface changes re-extract the affected projects on their own.
- Check `status` when a solution seems missing; it may be `unloaded` and need `load_solution`.
