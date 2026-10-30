# Architecture

Two parts: the `Ferret` library and the `Ferret.Cli` tool. The CLI is a thin front end. All search behaviour lives in the library. See [PROJECT.md](PROJECT.md) for scope and [research/](../research/) for design detail.

```
Ferret.Cli  ──►  Ferret (library)
 (Argu, I/O)      (pure core + ports)
```

Status markers match PROJECT.md: **Built**, **Planned** (designed, not built), **Future** (direction only).

## Principles

- **Pure core, effects at the edge.** Scoring, tokenising, ranking and rollup are pure functions. File system, cache and model I/O sit behind interfaces supplied by the caller.
- **One engine, many front ends.** The CLI, Eru's MCP server and embedding apps all call the same search API.
- **Lexical stays small.** Semantic search is a separate package so lexical-only installs avoid the ~80 MB model and ONNX Runtime.
- **Indexes are disposable.** Everything under the cache can be deleted and rebuilt lazily.

## Library: `Ferret`

### Layout

| Module | Responsibility | Pure |
|--------|----------------|------|
| `Ferret.Domain` | Types: `Query`, `Candidate`, `ScoredDoc`, `Hit`, `SearchResult` | yes |
| `Ferret.Text` | Normaliser: tokenise, stop words, light suffix stemming. Versioned, shared by index and query | yes |
| `Ferret.Ignore` | Gitignore-style pattern matching. Takes patterns, does no file I/O | yes |
| `Ferret.Grep` | Regex/literal line matching for plain (ripgrep-compatible) search | yes |
| `Ferret.FileSource` | `IFileSource` port and the `DiskFileSource` adapter (ignore-aware walk) | no (disk) |
| `Ferret.Bm25` | Corpus stats, IDF, term score, boosts, deterministic ordering | yes |
| `Ferret.Ranking` | `Ranker` seam and fusion (RRF) | yes |
| `Ferret.Index` | Build, load and invalidate the token index and corpus stats | no (ports) |
| `Ferret.Search` | Orchestration: candidates, filter, rank, limit, excerpts | no (ports) |

**Built:** `Domain`, `Ignore`, `Grep`, `FileSource`, `Text` (minimal tokenizer: no stop words or stemming), `Bm25`, `Ranking` (the `Ranker` seam and `Ranking.bm25`), `Search` (per-run, no index). **Planned:** `Index`. **Future:** `Ferret.Semantic` (separate package), hybrid fusion, RRF.

### Ports

The library defines interfaces. Callers provide adapters.

| Port | Purpose |
|------|---------|
| `IFileSource` | `Kind` (missing, file, directory), `Enumerate` a root, `ReadLines` of a file, `ReadStdin` (**Built**) |
| `IIndexStore` | Persist and load the cache (index, stats, version) |
| `IEmbedder` | Embed text to vectors (**Future**, semantic package only) |

The default adapters (disk file source, on-disk index store) ship in the library so embedding is a one-liner, but nothing in the core depends on them. Only `DiskFileSource` exists so far.

### Plain search (**Built**)

`ferret <pattern> [path...]`: `Grep.compile` builds a `Regex` from the pattern and `MatchOptions` (`-F`, `-i`, `-w`), `Grep.matchLines` filters lines, and `GrepCli` writes them. No index, no ranking.

`DiskFileSource.Enumerate` walks like ripgrep:

- Skips hidden entries, symlinks and binary files (a NUL byte in the first 8000 bytes).
- Directory entries are visited in ordinal name order, so output is deterministic. Paths keep the root as given (nothing is prefixed when no path is passed).
- An explicit file argument is always searched, whatever the ignore rules say.
- Ignored directories are not entered.

Stdin: `GrepCli` searches stdin for a `-` path, or when no path is given and `AdapterDeps.StdinIsReadable` is true. Otherwise no path means the current directory. Stdin is read once and named `<stdin>` in output when several inputs are searched.

`StdinIsReadable` follows `rg`: stdin must be a regular file, pipe or socket, so a terminal or `/dev/null` falls back to the directory and an empty file or empty pipe searches nothing (exit 1). `Deps.fs` stats fd 0 with a direct `fstat` P/Invoke because .NET has no portable way to do it. `st_mode` offsets are listed per platform (macOS, Linux x64, Linux arm64), with `__fxstat` as the fallback for glibc before 2.33. On Windows, or any platform not listed, it uses `Console.IsInputRedirected`, then a seekable-and-empty heuristic that wrongly treats `< emptyfile` as no stdin. `Deps.stdinModeIsSearchable` is the pure decision and is unit tested.

Ignore rules (`Ferret.Ignore`, pure: it takes lines, never reads files):

- Per directory, `.gitignore`, `.ignore` and `.ferretignore` are concatenated in that order, and the last matching rule wins, so `.ferretignore` can re-include with `!`. Deeper directories override shallower ones.
- `.gitignore` is honoured only inside a git repository (a `.git` in the root or an ancestor), as in `rg`.
- Syntax: `#` comments, `!`, leading `/` and inner `/` anchor, trailing `/` is directory-only, `*`, `?`, `[...]`, `**` (leading, middle, trailing), `\` escapes, trailing spaces trimmed.
- `check` matches one path; `isIgnored` also checks its ancestors.
- Hand-written (patterns compile to `Regex`) rather than `Microsoft.Extensions.FileSystemGlobbing`, which has only include/exclude (no `!` with last-match-wins), only `*` and `**`, no gitignore anchoring or directory-only rules, and walks the tree itself so it cannot prune.
- Not yet: ignore files in parent directories of the root, global gitignore, `.git/info/exclude`, `--no-ignore`, `--hidden`, `--exclude`.

### Search flow

```
paths + ignore patterns
   │  IFileSource
   ▼
CandidateBuilder ── drops ignored files ──► corpus stats (N, df, avgdl)
   │                                              │
   ▼                                              ▼
tag filter (AND) ──► Ranker: Query -> Candidate list -> ScoredDoc list
                                  │
                                  ▼
                        limit, excerpt cap, truncation flag
                                  │
                                  ▼
                            SearchResult
```

Ignored files are dropped before stats are computed, so they never skew `df` or `avgdl`. Stats cover the whole indexed corpus, so scores stay comparable across filtered queries.

### Ranker seam

```fsharp
type Ranker = Query -> Candidate list -> ScoredDoc list
```

- `Ranking.bm25`: **Built.** `K1 = 1.2`, `B = 0.75`, non-negative IDF, boosts as named constants, ties broken by ordinal path.
- `Semantic.ranker`: **Future.** Chunk by heading, paragraph, then line or word. Roll chunk hits up to files with `max`. The best chunk is the excerpt.
- `Ranking.fuse`: **Future.** Reciprocal rank fusion, `k = 60`, optional weights, top 50-100 per ranker.

### Index and cache

- Disposable. Built on first use, rebuilt when the index version changes.
- Cache key includes ignore patterns and the normaliser version.
- Incremental by file hash where feasible.
- First semantic index is built off the query path.

### Packages

| Package | Contents |
|---------|----------|
| `Ferret` | Core, BM25, default adapters |
| `Ferret.Semantic` | ONNX embedder (`bge-small-en-v1.5`, int8), chunker, semantic ranker (**Future**) |
| `Ferret.Cli` | The `ferret` tool |

## CLI: `Ferret.Cli`

Modelled on [Eru.Cli](https://github.com/dburriss/eru/blob/main/src/Eru.Cli/Program.fs): Argu for parsing, one module per command, and a `Program.fs` that only parses and dispatches. Unlike Eru, Ferret has **no subcommands**. Like `rg`, everything is a flag, so the first positional is always the pattern or query.

### Usage

```
ferret <pattern> [path...]            # plain grep, rg-compatible
ferret --lex "retries aws" docs/      # BM25 ranked
ferret --sem "retries aws" docs/      # semantic (Future)
ferret --hybrid "retries aws" docs/   # BM25 + semantic via RRF (Future)
ferret --index [--clean]              # build or fully rebuild the index (optional, builds lazily)
ferret --status | --inspect <file>    # index state, chunk/token inspection
ferret --version
```

### Layout

```
src/Ferret.Cli/
  Args.fs            Argu DU: FerretArgs (flat, no subcommands), argv helpers
  Deps.fs            AdapterDeps: file source, stdout, stderr, stdin check, debug flag
  GrepCli.fs         plain mode: regex/literal, file and line order
  RankedCli.fs       --lex | --sem | --hybrid
  MaintenanceCli.fs  --index, --clean, --status, --inspect
  VersionCli.fs
  Output.fs          text, table, json (rg JSON Lines), jsonl renderers
  Program.fs         entry point, mode selection, dispatch
```

**Built:** `Args`, `Deps`, `GrepCli`, `RankedCli` (`--lex`, `--limit` default 100, and `--scores`), `Output` (text only, with an optional `[score]` prefix) and `Program`, with `--version` handled inline in `Program.fs`. **Planned:** `--sem`/`--hybrid` in `RankedCli`, `MaintenanceCli`, `VersionCli` and the other renderers. Plain mode supports `-i`, `-F`, `-n`, `-l`, `-c`, `-w`, `-V` and `--debug`.

The assembly is `Ferret.Cli`, not `ferret`: `ferret.dll` would collide with the library's `Ferret.dll` on case-insensitive file systems. The installed command is still `ferret` (`ToolCommandName`).

### Conventions

- **Dependencies:** `AdapterDeps.create isDebug` builds the adapters (file source, index store, cwd, console) once in `main` and passes them to every command as `deps`.
- **Commands:** each `XCli` module exposes `run : AdapterDeps -> XArgs -> int`, returning the exit code. Commands do no parsing and no process exit.
- **Dispatch:** `Program.fs` uses active patterns over `ParseResults<FerretArgs>` to pick a command: maintenance flags and `--version` first, then a ranked mode if one is set, otherwise plain grep.
- **Errors:** `ArguParseException` prints the message to stderr. Any other exception prints `ferret: unexpected error: <msg>`, plus the stack trace only with `--debug`.
- **Argu gaps, handled in `Args.fs`:** Argu has no `--` end-of-options marker (`splitSeparator` cuts argv there and appends the rest as positionals), does not expand clustered short flags (`expandShortFlags`: `-in` becomes `-i -n`), and passes unknown flags to the `MainCommand` as positionals (`tryFindUnrecognizedFlag` turns them into a usage error).

### Mode rules

- `--lex`, `--sem` and `--hybrid` are mutually exclusive. Setting more than one is a usage error.
- Ferret-only flags (`--limit`, `--scores`, `--jsonl`, `--no-snippet`, `--snippet-length`) require a ranked mode. Without one they are a usage error, so plain mode stays rg-pure.
- In a ranked mode the argument is terms, not a regex. `--help` says so.
- No short flags for the mode flags. `-l` is `rg`'s `--files-with-matches`.

### Program.fs shape

```fsharp
module Ferret.Cli.Program

open Argu
open Ferret
open Ferret.Cli.GrepCli
open Ferret.Cli.RankedCli
open Ferret.Cli.MaintenanceCli
open Ferret.Cli.VersionCli

let private (|VersionCmd|_|) (r: ParseResults<FerretArgs>) =
    if r.Contains FerretArgs.Version then Some () else None

let private (|RankedCmd|_|) (r: ParseResults<FerretArgs>) =
    // validates mutual exclusion, returns the mode and its args
    RankedCli.tryParseMode r

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<FerretArgs>(programName = "ferret")
    try
        let parsed  = parser.ParseCommandLine argv
        let isDebug = parsed.Contains FerretArgs.Debug
        let deps    = AdapterDeps.create isDebug

        match parsed with
        | VersionCmd ()            -> VersionCli.run ()
        | MaintenanceCmd cmd       -> MaintenanceCli.run deps cmd   // --index, --clean, --status, --inspect
        | RankedCmd (mode, cmd)    -> RankedCli.run deps mode cmd   // --lex | --sem | --hybrid
        | GrepCmd cmd              -> GrepCli.run deps cmd          // default
        | _ ->
            printfn "%s" (parser.PrintUsage())
            0
    with
    | :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        1
    | ex ->
        let isDebug = argv |> Array.contains "--debug"
        eprintfn "ferret: unexpected error: %s" ex.Message
        if isDebug then eprintfn "%s" (ex.ToString())
        else eprintfn "run with --debug for the full stack trace"
        1
```

### ripgrep compatibility

- `ferret <pattern> [path...]` is plain grep. Core flags, output format and exit codes (0 on match, 1 on none) follow `rg`. Ignore handling follows `.gitignore` and `.ignore`, plus `.ferretignore`.
- Ferret-only features (`--lex`, `--sem`, `--hybrid`, `--limit`, `--scores`) use names `rg` does not. `--limit` never reuses `-m/--max-count`.
- With no subcommands there is nothing to shadow a pattern.

### Exit codes

| Code | Meaning |
|------|---------|
| 0 | Match found (or command succeeded) |
| 1 | No match |
| 2 | Usage or unexpected error (as `rg`) |

### Output

`Output.fs` renders library results to text (default), table, `--json` or `--jsonl`. `--json` in plain mode is `rg`'s JSON Lines format. `--jsonl` is the ranked-mode equivalent for agents, with one result per line. `--no-snippet` returns metadata only and `--snippet-length <n>` caps excerpt size. Renderers take `SearchResult` and write to a `TextWriter`. The library never prints.

## Tests

`tests/Ferret.Tests`: xunit v3 on Microsoft Testing Platform (`global.json` selects the runner for `dotnet test`), with FsCheck properties (`FsCheck.Xunit.v3`) for the pure modules. `Ignore` and `Grep` have example tests plus properties. CLI tests run `GrepCli.run` against temp directory trees and compare fixed expected output and exit codes. They do not need `rg` installed.

## Consumers

- **CLI:** `ferret` binary.
- **Eru MCP server:** calls the library directly for ranked, bounded results (limit, capped excerpts, truncation flag).
- **.NET apps:** reference `Ferret` and supply their own ports if needed.

## Open questions

- **CI: stdin detection is only verified on macOS (arm64).** Add a CI matrix (Linux x64, Linux arm64 if available, Windows, macOS) that runs the real binary for each case: pipe, empty pipe, `< nonempty file`, `< emptyfile`, `< /dev/null` (`< NUL` on Windows), and a terminal if one can be allocated. Expected: pipe and non-empty file search stdin, empty pipe and empty file exit 1, `/dev/null` searches the current directory. The Linux offsets, the `__fxstat` fallback (needs an image with glibc before 2.33, or musl) and the Windows path have never been run. The unit tests only cover the mode decision, not the syscall.
- `--clean` is only meaningful with `--index`. Decide whether it errors alone or deletes the cache.
