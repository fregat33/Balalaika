# Index storage and partial updates

The two questions raised before any code was written were "how do we store the index" and "how do we update it
partially when switching git branches". This is the answer that was chosen, with the alternatives that were rejected.

## Storage choice

Options considered: SQLite, LiteDB, custom binary files per project, Lucene.NET. SQLite won because it is queryable
(joins across references and the working tree are the core operation), supports FTS5 for name search, needs no
server, and is trivially inspectable with any SQLite browser. Lucene was overkill; binary files cannot be queried
without loading everything; LiteDB adds a dependency without a clear benefit.

Location: `%LOCALAPPDATA%\Balalaika\index.db`, WAL mode, `synchronous=NORMAL`, foreign keys on. One connection
guarded by a lock. Throughput is not the constraint at seventy projects; correctness is.

## Content-addressed schema

The unit of indexing is a **document content**, not a path and not a project.

| Table | Key | Holds |
|---|---|---|
| `project` | `path` (unique, case-insensitive) | name, assembly name, owning solution |
| `blob` | `(project_id, content_hash)` | `created_at`, `last_used_at` |
| `symbol` | `blob_id` | doc id, kind, name, fully qualified name, signature, accessibility, containing doc id, line, column, end line of the declaration |
| `reference` | `blob_id` | target doc id, target assembly, kind (usage / invocation / creation / base / attribute), line, column, doc id of the enclosing declaration (`from_doc_id`; `<top-level>` for top-level statements) |
| `relation` | `blob_id` | from doc id, to doc id, to assembly, kind (inherits / implements / overrides) |
| `tree` | `(project_id, path)` | `content_hash`, `mtime`, `size`, `seen_at` |
| `symbol_fts` | external-content FTS5 over `symbol(name, fq_name)` | maintained by insert/delete triggers |

**Every query joins `blob` to `tree` on `(project_id, content_hash)`.** A blob that no tree row points at is invisible.
That single join is what makes branch switching cheap: switching branches only rewrites `tree`.

Why the blob is keyed per project and not globally: the same file content compiled in two projects can bind
differently (different references, different `#if` symbols). Multi-targeted projects share one csproj path and
therefore one project row; `#if` differences between target frameworks are not distinguished.

## What a branch switch costs

Developers bounce between the same few branches. When `dev` is checked out again after a feature branch, every file
whose content was seen before already has a blob. The work is:

1. The watcher sees `.git/HEAD` change (and a burst of file events). A batch that contains a `HEAD` change triggers one
   **full reconcile** of each loaded solution and ignores the individual file events in the same batch.
2. Reconcile stats every document in the solution (mtime and size) against `tree`. Only files that differ are read,
   pushed into the in-memory `Solution` with `WithDocumentText`, and hashed. New `.cs` files under a project directory
   are added; missing ones are removed.
3. For each changed document: if a blob with that hash exists, re-point `tree` and stop. Otherwise extract once and
   insert the blob.
4. `IndexSolutionAsync` runs afterwards as a safety net and reports extracted / reused / removed counts.
5. If any changed document's declared surface differs from what the path declared before, the owning projects and
   their dependents are re-extracted (see "Dependent re-extraction" below).

Roslyn is incremental on its side: `WithDocumentText` reuses every unchanged syntax tree and declaration table, so
re-binding changed files costs seconds per project rather than a reload.

### Files under a project directory that are not part of the project

Two kinds of `.cs` files sit under a project directory without being documents, and a reconcile must not add them.

- **Files the compilation excludes.** A `<Compile Remove="Migrations\2024*" />` glob leaves files on disk that MSBuild
  never compiles. `LoadedSolution` measures them once at load (everything `EnumerateSourceFiles` finds minus the
  solution's documents), and the index pass persists the set per project in `excluded_file`. `ReconcileAsync` and
  `IndexFreshness.IsFresh` skip them, and `ApplyFileChange` refuses to add one when a watcher event touches it, so the
  live compilation and the index keep matching a real build. Before this existed, a `HEAD` change pulled 172 excluded
  migrations into Acme.Orders, reported a false surface change that re-extracted four dependent projects for
  twenty seconds, and the next startup load removed the rows again, on every restart. The globs themselves are not
  evaluated: a file created after the load that would match one is still added until the next reload.
- **Ignored folders.** `IgnoredFolders` in `balalaika.json` names folders, such as `Migrations`, whose files are kept
  out of the index entirely: no rows, no reconcile, no freshness failure, no watcher jobs, and live list results leave
  them out so the `Live` and `Index` halves of a tool's output agree (`SourceFilter`). The files stay in the compilation,
  so diagnostics still match a real build. A folder added to the list later makes the solution stale once, so the next
  index pass drops the existing rows.

Blobs no tree row references are deleted at startup once `last_used_at` is older than `BlobRetentionDays` (30).

## Change classification

| Path | Kind | Action |
|---|---|---|
| `*.cs` outside `bin/`, `obj/` | Source | apply text to in-memory solution, re-extract if hash is new |
| `*.csproj`, `*.props`, `*.targets`, `Directory.Packages.props`, `packages.lock.json`, `global.json`, `nuget.config` | ProjectFile | `dotnet restore` the csproj (configurable) and reload the solution |
| `*.sln`, `*.slnx` | SolutionFile | reload the solution |
| `.git/HEAD` | Git | full reconcile of loaded solutions |
| anything else under `.git/`, `bin/`, `obj/`, `node_modules/`, `.vs/`, `TestResults/` | Ignored | dropped before debouncing |

Debounce: 500 ms quiet period, forced flush after about 3 s of continuous activity. A `FileSystemWatcher` buffer
overflow (64 KB buffer) is reported as a batch flagged `Overflowed`, which the consumer treats like a `HEAD` change.
Overflow notifications go through the same quiet period as ordinary events, so the burst a large checkout raises (one
per filled buffer) becomes one batch. As a second guard, `IndexJobQueue` coalesces a `FullReconcileJob` into one that
is already queued and not yet started; a reconcile that is already running does not absorb new requests, because it may
have passed the files that changed after it began.

One query bypasses the debounce on purpose. `get_diagnostics` with a file target reads the file from disk, compares it
with the live document and applies it to the in-memory solution when they differ (`LiveTextSync`), so a call made in
the same second as a save, as a post-edit hook makes, sees the saved text. This touches no index row: the watcher's
batch for the same save arrives after the debounce, reads the same text, and does the extraction, surface diff and
dependent propagation as for any change. A removed file is dropped from the live solution the same way;
`RemoveDocument` later counts it from the stored tree rows, not from the live document, so nothing is lost.

## Dependent re-extraction on surface change ("step 4")

A pure content-addressed cache is only exactly right if a file's extracted data depends on its own text alone.
References do not: they depend on what the file binds against. If a public method is removed in `A.cs`, the reference
rows stored for `B.cs` still point at the old doc id until `B.cs` itself changes; if an overload is added, an existing
call in `B.cs` may now bind to it.

The design discussion weighed two options:

- **Re-extract dependents on surface change.** After a batch, diff the declaration set of the changed documents. If
  the public surface changed, re-extract every document in that project and in projects depending on it transitively
  (project references inside a solution, package references across solutions matched by assembly name). Correct, costs
  a few seconds per affected project.
- **Changed documents only, with a manual escape hatch.** Faster, simpler, and the staleness is bounded: it only
  affects reference *rows in the index*, never live `SymbolFinder` results, which are always exact.

The second option shipped first, to learn how often the first actually matters. The first was promoted on 2026-10-11
and is the default; `ReextractDependents: false` in `balalaika.json` restores the old behaviour, and `reindex` with
`force=true` remains the manual escape hatch.

Why a whole project and not a per-symbol dependency graph: a *new* overload can capture existing call sites that never
referenced the changed symbol, so re-extracting only the files that referenced a removed symbol would miss them.

### What counts as a surface change

`DeclarationSurface.Hash` reduces a blob to what other documents can bind against: every `symbol` row whose
accessibility is not `private` (doc id, signature, accessibility) plus every `relation` row, sorted and hashed. After
each change batch, `SolutionIndexer.IndexDocumentsAsync` compares the surface hash of the blob a path pointed at before
with the surface hash of the blob it points at now. Both are read from stored rows, so a reverted file that reuses an
old blob is compared too. A new file with a non-private declaration, and a deleted file that had one
(`SolutionIndexer.RemoveDocument`), count as changes. Body edits, comments, usings, attributes and private members do
not. Internal members do: test projects usually see them through `InternalsVisibleTo`.

### What gets re-extracted

`IndexingHostedService.PropagateSurfaceChangesAsync` runs inside the same job, after the changed documents were
indexed and after the reconcile safety net:

1. In the solution that changed: the projects with a surface change plus every project that transitively references
   them (`Solution.GetProjectDependencyGraph`).
2. In every other loaded solution: projects whose metadata references include a DLL named like a changed project's
   assembly (how a NuGet reference to a sibling repository appears), or that are the same csproj, plus their transitive
   dependents (`DependentProjects`).
3. Each target project is walked with `force`: every document is extracted again against the current compilation and
   `WriteBlob(replace: true)` overwrites the blob for its content hash. Tree rows do not change.

Unloaded solutions are not touched. There is no compilation to extract with, and their hits already carry the `[stale]`
marker when the file changed since indexing.

### Cost and remaining gaps

A re-extraction costs the per-project share of a full index, about a second per average project in the large test
solution. A branch switch that changes declarations in a central project therefore re-extracts most of the solution
once; body-only and private edits stay free. Still not covered: a changed csproj (new package version) that alters
binding without any source change, and `#if` differences between target frameworks. Both still need
`reindex` with `force=true`.

## Unloaded solutions

Changes under a solution that is not in memory are not applied; there is no compilation to extract with. Their index
rows describe the state at last load. At query time each index hit's file is hashed and compared with the tree hash;
mismatches are marked `[stale: file changed since indexed]`. Loading the solution (explicitly via `load_solution`, or
implicitly by resolving a symbol the index says it declares) reconciles it.

### Warm-up at startup

A solution contributes nothing to the index until it has been loaded once. So that cross-repository results exist for
solutions nobody opens, the server warms them up after the preloaded solutions are loaded: one at a time, it loads the
solution, enqueues an `IndexSolutionJob` and awaits it, then evicts the solution again. Memory peaks at the preloads
plus one extra solution. Before loading, `IndexFreshness.IsFresh` checks whether the index already matches the disk
(every tracked file exists with the recorded mtime and size, and no untracked `.cs` file sits under a project
directory); a fresh solution is skipped, so a restart on an unchanged machine loads nothing extra. The check stats
files and walks project directories but does not read or hash content, and it cannot see a changed csproj; that case
still needs a load. `status` reports progress as `Warm-up: 2 of 3 done, loading X` and then a summary.
`WarmUp: false` turns it off. The warm-up uses the same job queue as everything else, so file-change jobs for the
preloaded solutions interleave with it rather than waiting for it.

### Watcher-driven refresh

The watcher covers the whole root, so changes under unloaded solutions are seen but cannot be applied. Instead of
dropping them, the consumer records the changed paths per unloaded solution in `PendingRefreshes`. A loop checks every
few seconds and refreshes a solution once it has been quiet for `UnloadedRefreshSeconds` (default 60) or has kept
changing for `UnloadedRefreshMaxWaitSeconds` (default 600). The refresh is the warm-up step for one solution: skip if
somebody loaded it meanwhile (its own load indexed it and the normal file-change path now applies), skip if the index
already matches the disk, otherwise load, index, evict. A `.git/HEAD` change marks every known solution under that
repository root, since a checkout rewrites files without the solution directory necessarily containing `.git`; a
watcher overflow marks every unloaded solution, which costs only a stat pass per solution when nothing changed. Between
the change and the refresh, hits keep the `[stale]` marker. `UnloadedRefreshSeconds: 0` disables the refresh and
restores the marker-only behaviour.

## Package version drift

A consumer repository may pin a shared `Acme.Contracts` package at 3.1 while its source repository is at 3.4.
Cross-repository results then merge symbols from different versions under one identity. The index records both sides
so the mismatch can be shown: each project row carries the version the project declares
(`PackageReferences.ReadVersion`, from the generated `AssemblyInformationalVersion`, null for the SDK default), and the
`package_ref` table holds, per project, the packages it compiles against with the version parsed from the NuGet
packages-folder path of each metadata reference (`PackageReferences.Collect`). Both are rewritten on every index pass
of the project. Consumers and sources are joined by assembly name, the same key symbol identity uses. `package_drift`
lists all mismatches; `find_references` and `find_implementations` tag the affected solution groups (see tools.md).

Each project row also records the schema version it was last indexed under (`IndexStore.SchemaVersion`). When a new
server version starts recording more per-project data, `IndexFreshness.IsFresh` reports such projects as stale, so the
startup warm-up reindexes them once instead of leaving the new columns empty for solutions nobody loads. When the new
data is per blob (schema 4 added the enclosing declaration per reference and the end line per symbol), reusing blobs
by content hash would keep the old rows forever, so `SolutionIndexer.IndexProjectAsync` treats a project stamped with
an older version as `force`: every document is re-extracted once, and the stamp is written after the walk so a pass
cancelled halfway is repeated. The upgrade from schema 3 to 4 re-extracted 1681 documents across five solutions in
about 40 seconds of indexing time at startup; the next start reused everything.
