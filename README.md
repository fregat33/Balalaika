# Balalaika

Persistent, cross-solution C# code intelligence for coding agents over MCP.

```
                             SQLite index
                            /|
                          /  |
                        /    |
  Agent ≡≡≡ HTTP ≡≡≡≡ < MCP  ^ Roslyn (live)
                        \    |
                          \  |
                            \|
                             C# repositories
```

One long-running HTTP server, shared by every agent session on the machine. The agent connects through the neck.
The body holds three things together: the live Roslyn workspaces as the source of truth, a content-addressed
SQLite index as a derived cache, and the C# repositories on disk, watched for changes. The three strings are the
three places an answer can come from, and every answer says which: `Live` from Roslyn, `Index` from SQLite, or
decompiled from the NuGet cache.

## What it does

- **Find references and implementations across repositories.** A contract type published by one solution is found
  in every other solution that consumes it, loaded or not, through the package that carries it. The result says
  which consumer is on an older package version than the source.
- **Go to definition with decompilation.** NuGet and framework types come back as decompiled C# with XML docs.
- **Symbol info, members, derived types, call graph, camel-hump symbol search.**
- **Compiler diagnostics without a build**, per file or per project, reflecting a saved edit within seconds.
- **Stays fresh on its own.** A file watcher applies edits to the in-memory workspace, a branch switch only rewrites
  path-to-blob mappings, a change to a public declaration re-extracts the dependents, and solutions nobody has
  opened are indexed in the background and refreshed when they change.

Output is compact text built for an agent's context window: `path:line:col  project` per hit, no snippets, every
list paged with a cursor, every printed position usable as the next query's input.

## Why not one of the other Roslyn MCP servers

Several exist, and they share a shape: a stdio process per agent session, one workspace, no state between runs.
Balalaika is the opposite shape, and that is the whole point:

| | Per-session stdio servers | Balalaika |
|---|---|---|
| Lifetime | starts with the session, loads the solution every time | one daemon per machine, solutions stay loaded |
| Scope | one solution | every solution under the configured roots, queried together |
| Memory between runs | none | content-addressed index; a restart reuses every unchanged file in milliseconds |
| Unloaded code | invisible | answered from the index, marked `Index`, refreshed on change |
| Writes to disk | some edit the solution | never; the live workspace is forked in memory |

If you work in one solution at a time and want edit tools, one of the others may fit better. If you work across a
family of repositories and want answers that stay correct while you edit, this is for that.

## Requirements

- .NET 10 SDK (tested with 10.0.401)
- `git` on the PATH of the server process for `review_changes` (or `GitPath` in `balalaika.json`); every other tool works without it.
- PowerShell 7 (`pwsh`) for `scripts\mcp-call.ps1` and the post-edit diagnostics hook; the server itself does not need it.
- The target solutions must restore successfully with `dotnet restore`; the server runs a restore itself when a
  loaded project turns out to have no references.
- Windows paths are assumed in a few places (change classification, `%LOCALAPPDATA%` default).

## Build, test, run

```powershell
dotnet build Balalaika.slnx
dotnet test tests/Balalaika.Tests/Balalaika.Tests.csproj

dotnet run --project src/Balalaika.Server --no-build
# or publish once and run the exe from anywhere
dotnet publish src/Balalaika.Server -c Release -o C:\tools\balalaika
C:\tools\balalaika\Balalaika.Server.exe
```

Health and status text: `http://localhost:5301/health`. MCP endpoint: `http://localhost:5301/mcp` (streamable HTTP,
stateless). The server is meant to run once per machine and be shared by every agent session.

To have it up before any session starts, register the published exe as a scheduled task that runs at logon (or as
a Windows service through a wrapper); the server has no installer and does not register itself. A `status` call
is the check that it is running; the target repositories' agent instructions can tell the agent to make it first.

Logs go to the console and to a daily rolling file, `%LOCALAPPDATA%\Balalaika\logs\balalaika-yyyyMMdd.clef`, in
Serilog's compact JSON format with one event per line. Seven days are kept by default; `LogDirectory` and
`LogRetainedDays` in `balalaika.json` change the folder and retention. The server has no prompts or resources; a
client's `prompts/list`, `resources/list` and `resources/templates/list` probes get an empty list and leave no warning.

Every tool call logs one line (`Balalaika.Server.ToolCalls`): tool, arguments, found count, client, `caller` (an optional
query parameter), `part` and elapsed time; the MCP SDK's own per-request lines are off. See [docs/tools.md](docs/tools.md).

Quick manual call:

```powershell
scripts\mcp-call.ps1 -Tool status
scripts\mcp-call.ps1 -Tool find_references -Arguments @{ symbol = "Acme.Contracts.Events.OrderPlaced"; limit = 20 }
```

## Configuration

`balalaika.json` next to the executable (or in the working directory) holds the defaults and is committed with empty
`Roots` and `Preload`. Put your machine's values in `balalaika.local.json` beside it: it is read after the main file,
copied to the build output, and ignored by git. Any key can also be overridden with an environment variable of the
form `BALALAIKA_Balalaika__Key`. Arrays merge by index across the files, which is why the committed file keeps them
empty.

```json
{
  "Balalaika": {
    "Roots": [ "C:\\src\\acme" ],
    "IgnoredFolders": [ "Migrations" ],
    "Port": 5301,
    "Preload": [ "Acme.Orders" ],
    "DefaultSolution": "Acme.Orders",
    "IdleEvictionMinutes": 0,
    "LoadWaitSeconds": 120,
    "RestoreOnProjectChange": true,
    "ReextractDependents": true,
    "WarmUp": true,
    "UnloadedRefreshSeconds": 60,
    "UnloadedRefreshMaxWaitSeconds": 600,
    "DiscoverySweepSeconds": 30,
    "BlobRetentionDays": 30,
    "DebounceMilliseconds": 500
  }
}
```

| Key | Meaning |
|---|---|
| `Roots` | folders scanned recursively for `*.sln` / `*.slnx` (build output and `.git` folders skipped) |
| `Preload` | solutions loaded at startup and never evicted |
| `IgnoredFolders` | folder names, matched anywhere on a file's path, whose files get no index rows and no watcher jobs and are left out of result lists; `Migrations` drops EF migrations. The files stay in the compilation so diagnostics match a real build. Empty by default |
| `WarmUp` | after the preloads, load every other discovered solution once, index it and evict it, so index results exist for solutions nobody opened; solutions whose index already matches the disk are skipped |
| `ReextractDependents` | when a change alters a project's public surface, re-extract that project and its dependents so stored references stay correct |
| `UnloadedRefreshSeconds` | when files change under a solution that is not loaded, refresh its index (load, index, evict) after this many quiet seconds; `0` leaves only the `[stale]` marker |
| `UnloadedRefreshMaxWaitSeconds` | refresh at the latest this long after the first change even if changes keep arriving |
| `DiscoverySweepSeconds` | every this many seconds, forget known solutions whose `.sln` / `.slnx` file no longer exists (new ones are found from watcher events); `0` turns the sweep off, the check still runs after every change batch |
| `DefaultSolution` | used when a query names none |
| `IdleEvictionMinutes` | unload a non-preloaded solution after this idle time; `0` keeps everything |
| `LoadWaitSeconds` | how long a query waits for a load before answering from the index only |
| `RestoreOnProjectChange` | run `dotnet restore` on a changed csproj before reloading its solution |
| `BlobRetentionDays` | unreferenced index blobs older than this are deleted at startup |
| `IndexPath` | optional; defaults to `%LOCALAPPDATA%\Balalaika\index.db` |

## Register in Claude Code

User-scoped, no repository change:

```powershell
claude mcp add --transport http balalaika http://localhost:5301/mcp
```

Or project-scoped in a `.mcp.json`. Claude Code also reads this file from parent folders, so one copy in the root
folder of the target repositories (for example `C:\src\acme\.mcp.json`) covers every repository beneath it. The
first session in each repository asks once to approve the server:

```json
{
  "mcpServers": {
    "balalaika": { "type": "http", "url": "http://localhost:5301/mcp" }
  }
}
```

Any MCP client with streamable HTTP transport works the same way.

### Diagnostics after every edit

A `PostToolUse` hook makes the compiler errors of a file the agent just edited appear in its context without a tool
call, the way a language-server plugin pushes them. `scripts/post-edit-diagnostics.ps1` reads the hook event, calls
`get_diagnostics` on the edited `.cs` file with `minSeverity=error`, and returns the errors as additional context. It
prints nothing when the file is clean, not C#, unknown to the server, or the server is not running, so an edit is never
blocked. `get_diagnostics` applies the saved text to the live workspace before answering, so the hook does not race
the watcher.

This repository registers it in `.claude/settings.json`; for every repository on the machine put the same block in
`~/.claude/settings.json` with the published script's absolute path:

```json
{
  "hooks": {
    "PostToolUse": [
      {
        "matcher": "Edit|Write|MultiEdit",
        "hooks": [
          {
            "type": "command",
            "command": "pwsh",
            "args": ["-NoProfile", "-NonInteractive", "-File", "C:/tools/balalaika/scripts/post-edit-diagnostics.ps1"],
            "timeout": 45
          }
        ]
      }
    ]
  }
}
```

The hook reports the edited file only. An edit that breaks callers in other files is not shown; `find_references`
before a signature change, or `get_diagnostics` on the project afterwards, covers that. `BALALAIKA_URL` overrides the
server address.

## Tools

| Tool | Answers |
|---|---|
| `find_references` | every reference to a symbol with its usage kind and enclosing member, grouped by solution, live and index, with package drift noted; `kind` and `scope` filters keep only creations, base lists, attributes or invocations, in production or test projects |
| `find_implementations` | implementations of an interface or abstract member |
| `find_derived_types` | types deriving from a class or implementing an interface |
| `go_to_definition` | source, or decompiled source for NuGet and framework types |
| `get_symbol_info` | kind, signature, containing type, XML docs |
| `list_members` | members of a type, optionally inherited |
| `get_outline` | a file or type as one line per declaration with attributes and line spans, to read only the range needed |
| `get_call_graph` | one level of callers and callees of a member, optionally in production or test projects only |
| `get_diagnostics` | compiler diagnostics for a file or project |
| `review_changes` | a branch against its merge base: changed declarations as added, removed, signature or body, with reference and caller counts per solution and diagnostics on the changed files |
| `search_symbols` | symbols by prefix or camel humps |
| `next_page` | the rest of any truncated list |
| `package_drift` | consumers on a package version other than the source |
| `reindex`, `load_solution`, `forget_solution`, `status` | maintenance |

Every tool accepts a symbol as a doc-comment id, a fully qualified name, a bare name, or a `path:line:col` copied
from any earlier result. The full reference, with output formats, is in [`docs/tools.md`](docs/tools.md).

### Token cost in practice

Measured on 2026-10-13 against a loaded 31-project solution (1052 documents): each scenario was run once through the
tools and once the way an agent works without them (`git grep`, then reading the files the hits are in). Tokens are
approximated as characters / 4; the real count depends on the tokenizer and is within about 20 percent of these.
"Round trips" counts tool or shell calls, each of which also costs a few hundred tokens of request and response
framing that is not included below.

| Scenario | With the tools | Plain grep and read | Saving |
|---|---|---|---|
| Who uses `UpdateOrderCommand`, and in which role (sender, handler, validator, test) | `find_references`: ~660 tokens, 1 round trip; every hit says `creation  in OrdersController.UpdateOrder` | grep ~750 tokens, then the 3 files with hits: ~7,000 tokens read whole, ~4,000 with a 60-line window per hit; 4 round trips | 6x to 10x |
| Read the handler's `Handle` method in a 148-line command file | `get_outline` + ranged read: ~1,700 tokens, 2 round trips | whole file: ~1,900 tokens, 1 round trip | about even: the method is most of the file |
| Read one method in a 510-line service class | `get_outline` + ranged read: ~1,300 tokens | whole file: ~6,100 tokens | 4.6x |
| Implementations of `IEmailService` | `find_implementations`: ~140 tokens | grep: ~630 tokens (every injection site is a hit too) | 4.5x |
| Callers of `IEmailService.SendOrderShipped` | `get_call_graph`: ~120 tokens, 1 round trip | grep ~140 tokens, then the 3 files: ~7,600 tokens whole, ~1,600 windowed; 4 round trips | 13x to 60x |
| Compiler errors and warnings of one project | `get_diagnostics`: ~6,200 tokens (77 compiler warnings), no build | `dotnet build --no-incremental`: ~156,000 tokens (1,552 lines, mostly analyzer warnings), ~15,600 after filtering to `warning CS|error`; 4 s of build time | 2.5x to 25x |
| Find symbols whose name contains `Upgrade` | `search_symbols`: ~455 tokens for 9 hits (types and methods, each with its position; ~660 before the doc id was dropped from the line) | `git grep "(class|record|interface) \w*Upgrade"`: ~150 tokens for 4 type declarations | grep wins by 3x when the exact declaration keyword is known |
| Orient in an 18-file branch (62 changed declarations, 44 of them public surface) before reading it | `review_changes`: ~2,100 tokens, 1 round trip, 6 s; every changed declaration with its kind, span and spec, `+ implements` on the six commands that gained a marker interface, implementer counts for the new interface and its member, caller counts for body edits, 1 compiler warning | `git diff --name-status` ~430 tokens, the full diff ~9,600 tokens dumped once, a grep for the implementers of the new interface, a build for compile status; 4 round trips | 4.5x on the orientation step; the logic is still read afterwards, by printed line range instead of from the diff |

What the numbers say:

- The large savings come from not opening files. A bare grep hit forces a read to learn what the line is part of;
  `find_references` and `get_call_graph` print the enclosing member and the usage kind, so the read is skipped.
  The more files a symbol is used in, the bigger the gap.
- `get_outline` pays on files over roughly 200 lines, or whenever the method wanted is a small part of the file.
  On a file that is one handler, read the file.
- `get_diagnostics` is cheaper than a build and faster, but 77 warnings are still ~6,000 tokens. Pass `minSeverity`
  `error` or a single file when checking an edit.
- `search_symbols` is a discovery tool, not a cheap lookup: each hit is a long line (full name and path, about 50
  tokens). Use it when the name is approximate or when the hit is going to be passed to another tool as a position; a
  targeted grep is cheaper when the exact declaration is known.
- `review_changes` replaces the diff dump as the first step of a review, not the reading. Its output is a fifth of
  the diff and says which files changed their surface, which only their bodies, and what binds to each, so bodies are
  then read by line range and the diff is never pasted whole. An agent that reviewed that branch both ways put the
  saving at 15 to 25 percent of the whole review on an additive branch, and expected 40 percent or more on a
  refactoring branch, where the reference counts for removed and signature-changed members replace one
  `find_references` call per member (they did not fire on the branch measured, which breaks nothing).
- Index results for unloaded solutions cost the same as live results, so cross-repository questions do not need the
  other repositories in memory.

## Repository layout

| Path | Purpose |
|---|---|
| `src/Balalaika.Index` | SQLite store (schema, content-addressed blobs, tree, FTS). No Roslyn dependency. |
| `src/Balalaika.Workspace` | Solution loading, in-memory updates, extraction, symbol resolution, decompilation, query logic. |
| `src/Balalaika.Watcher` | File system watcher, change classification, debouncing. |
| `src/Balalaika.Server` | ASP.NET host, MCP tools, job consumer, `balalaika.json`. |
| `tests/Balalaika.Tests` | xUnit tests over AdhocWorkspace fixtures and temp SQLite files. |
| `scripts/mcp-call.ps1` | Call one tool from PowerShell. |
| `docs/` | Design, indexing and invalidation, tool reference. |

Guidance for AI agents working on this code is in [`AGENTS.md`](AGENTS.md).

## Known limits

- Diagnostics are compiler-only; analyzers (Meziantou, StyleCop, EF) are not executed.
- Projects that build their own analyzer or source generator (`ProjectReference` with `OutputItemType="Analyzer"`)
  are untested: the server loads the generator DLL from that project's output and holds it open, so `dotnet build`
  of the generator project can fail with a file-in-use error while the solution is loaded. Analyzers and generators
  from NuGet packages are unaffected.
- Generated code is not indexed. Generators run in the compilation, so their symbols resolve and diagnostics are
  correct, but declarations in generated documents and references inside them do not appear in `Index` results.
- Solutions contribute to the cross-repository index only after they have been loaded at least once; the warm-up
  does this in the background after startup.
- Multi-targeted projects are indexed once per csproj; `#if` differences between targets are not distinguished.
- Memory grows with every loaded solution (about 700 MB for a 31-project solution). Enable `IdleEvictionMinutes`
  if several large solutions are loaded on demand.
- Docker is deliberately unsupported: file change events do not cross Docker Desktop bind mounts on Windows.
- Windows only for now. Nothing in the design requires it, but it has only been run there.

## License

MIT, see [`LICENSE`](LICENSE).

## Name

A balalaika has a triangular body and three strings. So does this server: three sources held together by one neck
toward the agent, and three kinds of answer. It is spelled b-a-l-a-l-a-i-k-a.
