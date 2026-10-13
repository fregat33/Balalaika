# Design

This document captures the reasoning behind Balalaika as it was discussed and decided on 2026-10-11. The
companion documents are [indexing-and-invalidation.md](indexing-and-invalidation.md) for the storage and update
strategy and [tools.md](tools.md) for the tool surface.

## Problem

An AI coding agent working inside one repository of a family of sibling repositories (several .NET solutions under one
root folder, dozens of projects in total) has only text search available. The gaps that hurt most:

1. **Cross-repository navigation.** Message contracts published by one service are consumed by workers and functions
   in other repositories. A rename or signature change needs every consumer found, and grep misses renamed or
   generic usages and cannot tell comments from code.
2. **Precise find-references and find-implementations.** A method name matches overloads, unrelated types and
   comments. Roslyn's `SymbolFinder` is exact.
3. **NuGet and framework types.** Signatures and defaults of third-party packages and of the organisation's own
   shared packages were otherwise guessed from memory or dug out of the NuGet cache by hand.
4. **Compile feedback without a full build.** `dotnet build` of a large solution takes long enough that iterating on a
   single file is slow. Diagnostics for one file from a live compilation are near-instant.
5. **Type hierarchies.** Members of a type and derived types of an entity or handler base class, which grep cannot
   compute.

What was explicitly *not* wanted: raw syntax tree dumps (larger than the source and harder to read), decompiling the
solutions' own projects (source is right there), and static pre-generated snapshots that drift from uncommitted edits.

## Two layers, one identity

**Live Roslyn workspace is the source of truth.** Diagnostics, go-to-definition, decompilation and precise references
all need a real `Compilation`. One `MSBuildWorkspace` per solution, loaded lazily on first use or eagerly when listed
in `Preload`. Preloaded solutions are pinned; others may be evicted after an idle period (disabled by default).

**SQLite is a derived cache.** It serves cross-solution queries against solutions that are not in memory, name
search, and cold start. It is always rebuildable and never authoritative. Results from it are labelled `Index` in tool
output; results from Roslyn are labelled `Live`.

**Symbol identity** across both layers is the Roslyn documentation-comment id plus the containing assembly name, for
example `M:Acme.Contracts.Events.OrderPlaced.#ctor` in `Acme.Contracts`. This pair is identical for a source symbol in
the repository that owns the contracts and its metadata twin in a consumer repository, which references the same
assembly as a NuGet package. Roslyn's `SymbolKey` is more precise but opaque and version-bound, so it is not stored.

Framework types are the exception to "containing assembly name": `System.String` is in `netstandard` for
netstandard2.0, `System.Runtime` for net8.0 and later, `mscorlib` for net48 and `System.Private.CoreLib` at run time,
and `List<T>` or `Console` move between facades the same way. `SymbolIdentity.AssemblyOf` therefore maps every
framework assembly (the facades and everything under `System.*`) to the single token `<framework>` before it is stored
or compared, so references from solutions on different target frameworks merge. Display output keeps the real name.
Changing this mapping requires `reindex` with `force=true`, because content-addressed blobs are otherwise reused.

The twin lookup (`SymbolResolver.FindTwinAsync`) is what makes `find_references` return live hits from several loaded
solutions for one symbol: the doc id is resolved again in each other solution's compilations, source or metadata, and
`SymbolFinder` runs there too.

## Component map

| Project | Responsibility |
|---|---|
| `Balalaika.Index` | SQLite schema, content-addressed blob store, tree reconciliation, FTS search. No Roslyn dependency. |
| `Balalaika.Workspace` | Solution discovery and loading, in-memory text updates, extraction, resolution, decompilation, query formatting, job queue contract. |
| `Balalaika.Watcher` | FileSystemWatcher per root, change classification, debounce, overflow signalling. No Roslyn dependency. |
| `Balalaika.Server` | ASP.NET host, MCP tool definitions, the single job consumer (`IndexingHostedService`). |
| `Balalaika.Tests` | xUnit tests over AdhocWorkspace fixtures and temp SQLite files. |

Dependencies flow Server -> Workspace -> Index and Server -> Watcher -> Index. Workspace never references Watcher.

## Key invariants

- **Never apply changes through `MSBuildWorkspace`.** `TryApplyChanges` on that workspace writes files to disk. The
  live `Solution` snapshot is forked with `WithDocumentText` / `AddDocument` / `RemoveDocument` and held in
  `LoadedSolution`; the workspace object is kept only for disposal.
- **All index writes go through `IndexJobQueue`.** One consumer, so the tree table never has racing writers.
- **Hash decoded text, not bytes.** `ContentHasher.HashText(SourceText.ToString())` is used for files read from disk
  and for documents already in memory so that BOM and encoding differences never cause a spurious miss.
- **Extraction is a pure function of (document, compilation at that moment)** and is stored keyed by text hash alone.
  When a change batch alters a document's declared surface, the owning project and its dependents are re-extracted so
  the stored rows catch up with the new binding; see "Dependent re-extraction" in the invalidation document.
- **Tool output is compact text**: one hit per line as `path:line:col  project`, no snippets. The agent reads the
  surrounding code itself.

## Hosting

One long-lived HTTP process on `localhost:5301` rather than a stdio process per client session. A stdio server would
pay the cold load for every new session; the whole point of keeping a workspace in memory is to share it. The MCP
transport is streamable HTTP in stateless mode, so clients need no session handshake beyond the protocol's own.

**Docker Desktop was considered and rejected.** The server must receive file change events from `C:\src`, and
Docker Desktop on Windows does not forward those across bind mounts. Reads of thousands of files over a bind mount are
slow, returned paths would be Linux paths the agent cannot open, and the container would need the full .NET SDK plus
the NuGet cache. Docker stays in use for SQL Server and Azurite in the application repositories only.

## Measured on 2026-10-11

| Operation | Result |
|---|---|
| Load a 31-project solution | 7 to 9 s |
| First index of that solution | 1168 documents in 26 s |
| Re-index after restart (stat fast path) | 1168 reused, 0 extracted, 0.0 s |
| Load a 12-project solution | 3.6 s |
| Index that solution | 135 documents in 1.3 s |
| Process memory with the 31-project solution loaded | about 690 MB working set |
| New file with an error visible in `get_diagnostics` | about 3 s after save (500 ms debounce plus extraction) |
