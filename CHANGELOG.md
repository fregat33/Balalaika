# Changelog

## Unreleased

### Fixed
- `find_references` on a type lists the `new T(...)` expressions that create it, and on a constructor lists target-typed `new(...)` calls; `review_changes` impact counts follow the same rule.

## 0.2.1 - 2026-11-15

### Added
- Option `RootsOnly` (default `false`): when `true`, `load_solution` with the full path of a solution file outside every root answers `Outside the roots: ...` and registers nothing.

### Fixed
- A solution whose project files were written while MSBuild was loading it is reloaded once more, so a half-written file no longer leaves a broken compilation.
- A loaded solution whose solution file vanished (a removed worktree) is forgotten instead of reloaded, with no `Failed to load` or `Index job failed` error and the rest of the file-change batch still processed.
- At startup, index rows of a solution whose file is gone (a worktree removed while the daemon was down) are deleted and logged; solutions under a root that is missing (an unmounted drive) are kept.

## 0.2.0 - 2026-11-01

### Added
- Solutions are discovered and forgotten while the server runs, and a new `forget_solution` tool removes one by hand.
- Linked git worktrees are served as part views, so a request sees the worktree's solutions over the base checkouts.
- The index follows the branch behind a symbolic HEAD, because the loose ref and `packed-refs` are watched.
- Each tool call writes one log line with its arguments, found count and caller.
- `review_changes` works in a linked worktree through the part view.

### Changed
- `forget_solution` refuses an exact name shared by several solutions, and asks for the full path.
- The version is declared once, in `Directory.Build.props`, and the server reports it.
- Reference and caller counts follow one rule: references bind to the declaration itself, and callers through base or interface members are counted apart.
- Solution hints resolve the same way whether the path uses `/` or `\`.
- A watcher overflow reconciles only the configured root that overflowed.
- `forget_solution` refuses a hint that matches several solutions.

### Fixed
- A late event of a superseded HEAD watcher cannot release the newer registration.
- A removed linked worktree releases its HEAD watcher, and a worktree re-created at the same path is watched again.
- A worktree HEAD stays watched after a same-batch recreate, and a racing unwatch cannot orphan a watcher.
- The per-call log line escapes quotes, backslashes and line breaks inside quoted values.
- Argument keys in the per-call log line cannot forge fields or break the line.
- Client capability probes (`prompts/list`, `resources/list`, `resources/templates/list`) get an empty answer without a warning.

## 0.1.0 - 2026-10-14

### Added
- A Roslyn-backed MCP server for C# solutions, with live and indexed symbol queries.
- Find references and implementations across repositories, through the package that carries a contract type.
- Go to definition, with decompilation of NuGet and framework types.
- Symbol info, members, derived types, call graph and camel-hump symbol search.
- Compiler diagnostics without a build, per file or per project.
- A content-addressed SQLite index, invalidated by a file watcher, with branch review.
