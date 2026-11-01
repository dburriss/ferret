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
- **Lexical stays small.** Semantic search is a separate package so library users who only want lexical search avoid the 32 MB model and ONNX Runtime (17–43 MB per platform). The CLI includes it, as a downloading build and a bundled build, both published as release archives.
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
| `Ferret.Markdown` | Heading text and frontmatter (`title`, `description`, `tags`, parsed with Flexi.Yaml; invalid YAML yields no fields) extraction | yes |
| `Ferret.Bm25` | Corpus stats, IDF, term score, boosts, deterministic ordering | yes |
| `Ferret.Ranking` | `Ranker` seam and fusion (RRF) | yes |
| `Ferret.Index` | Index entries, the `IIndexStore` port, version check (built per file; searched via `Search.searchIndexed`) | no (ports) |
| `Ferret.Search` | Orchestration: candidates, filter, rank, limit, excerpts | no (ports) |

**Built:** `Domain`, `Ignore`, `Grep`, `FileSource`, `Text` (tokenizer, stop words, light suffix stemming; `NormaliserVersion`), `Bm25`, `Ranking` (the `Ranker` seam and `Ranking.bm25`), `Search` (per-run `search`, and `searchIndexed` over a persisted index), `Index` (`Entry`, `Snapshot`, `IIndexStore`, `MemoryIndexStore`, `Index.version`), `DiskIndexStore` (binary file, atomic save). `Search.buildIndex` (build without ranking; `IIndexStore.Clear`). **Planned:** the `--status` command and an inverted index. `Ranking.fuse` (RRF). `Ferret.Semantic` (bundled into the `Ferret` package, below) is built.

### Ports

The library defines interfaces. Callers provide adapters.

| Port | Purpose |
|------|---------|
| `IFileSource` | `Kind` (missing, file, directory), `Enumerate` a root, `ReadLines` of a file, `ReadStdin` (**Built**) |
| `IIndexStore` | Persist and load the cache (index, stats, version) |
| `IEmbedder` | Embed text to vectors (**Built**, semantic package only: `OnnxEmbedder`) |

The default adapters (disk file source, on-disk index store) ship in the library so embedding is a one-liner, but nothing in the core depends on them. `DiskFileSource` and `DiskIndexStore` exist. `IFileSource.Stamp` gives the change fingerprint index entries are checked against.

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

- `Ranking.bm25`: **Built.** `K1 = 1.2`, `B = 0.75`, non-negative IDF, score multiplied by `Bm25.boost` (named per-field constants: path 0.3, heading 0.5, frontmatter title 0.5, description 0.3, tags 0.5, each scaled by the share of query terms in that field), ties broken by ordinal path. Headings and frontmatter are read only from `.md`/`.markdown`/`.mdx` files (`Ferret.Markdown`); README and concept-file boosts are not built.
- `SemanticSearch` (in `Ferret.Semantic`): **Built.** Chunk by heading, then paragraph, then line or word, merging heading-only and front-matter sections and prefixing each chunk with `title > heading path`. Score every chunk with a dot product, roll chunk hits up to files with `max`, and use the best chunk's first lines as the excerpt. It has its own entry points (`search`, `build`) rather than the `Ranker` seam, because it ranks chunk vectors, not `Candidate`s.
- `Ranking.fuse`: **Built.** Reciprocal rank fusion, `k = 60`, optional per-ranker weights, over file-level rankings. Pure, so it lives in `Ferret` with no knowledge of semantic search. Ties break by ordinal path, documents scoring 0 are dropped, and excerpts come from the first ranking that has any.
- `HybridSearch` (in `Ferret.Semantic`): **Built.** Owns the arm depth (top 100 each) and runs `Ranking.fuse` in a `ferret.fuse` activity. The CLI and the eval script compute the BM25 and semantic arms and pass them in, so `Ferret.Core` never depends on `Ferret.Semantic`.

### Index and cache

- Disposable. Built on first use, rebuilt when the index version changes.
- Built for lexical: entries are per file and keyed by full path, so ignore rules are applied by the walk on every run rather than baked into the cache key. The version is `FormatVersion * 1000 + NormaliserVersion`.
- Incremental by file hash where feasible.
- The semantic index is synced on query like the lexical one (only new or changed files are embedded), with one file per search root. `--index --sem` pre-builds it.

### Packages

| Package | Contents |
|---------|----------|
| `Ferret` | `FerretClient`, bundling `Ferret.Core` and `Ferret.Semantic` (below). Depends on ONNX Runtime. |
| `Ferret.Tool` | The `ferret` tool, built from `Ferret.Cli` |

Internal projects, not published on their own:

| Project | Contents |
|---------|----------|
| `Ferret.Core` | Core, BM25, default adapters |
| `Ferret.Semantic` | ONNX embedder (`bge-small-en-v1.5`, int8), chunker, model locator and downloader, per-root vector store, semantic search (**Built**) |

## CLI: `Ferret.Cli`

Modelled on [Eru.Cli](https://github.com/dburriss/eru/blob/main/src/Eru.Cli/Program.fs): Argu for parsing, one module per command, and a `Program.fs` that only parses and dispatches. Unlike Eru, Ferret has **no subcommands**. Like `rg`, everything is a flag, so the first positional is always the pattern or query.

### Usage

```
ferret <pattern> [path...]            # plain grep, rg-compatible
ferret --lex "retries aws" docs/      # BM25 ranked
ferret --sem "retries aws" docs/      # semantic
ferret --hybrid "retries aws" docs/   # BM25 + semantic via RRF
ferret --index [--clean] [path...]    # build or fully rebuild the index (optional, queries still build lazily)
ferret --clean                        # delete the index
ferret --status | --inspect <file>    # index state, chunk/token inspection (Planned)
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

**Built:** `Args`, `Deps`, `GrepCli`, `RankedCli` (`--lex`, `--limit` default 100, and `--scores`), `Output` (text only, with an optional `[score]` prefix) and `Program`, with `--version` handled inline in `Program.fs`. `--sem` (with `--index --sem`, `--clean --sem` and `--model download`). `--hybrid` (`RankedCli.runHybrid`; `--index`/`--clean --hybrid` run both maintenance paths). **Planned:** `VersionCli` and the other renderers. Plain mode supports `-i`, `-F`, `-n`, `-l`, `-c`, `-w`, `-V` and `--debug`.

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

`Output.fs` renders library results to text (default), table, `--json` or `--jsonl`. `--json` in plain mode is `rg`'s JSON Lines format. `--jsonl` is the ranked-mode equivalent for agents, with one object per excerpt line: `{"file","line","score","text"}`, score always present (4 decimals). `--scores` is ignored with it. `--no-snippet` returns metadata only and `--snippet-length <n>` caps excerpt size. Renderers take `SearchResult` and write to a `TextWriter`. The library never prints.

## Tests

`tests/Ferret.Tests`: xunit v3 on Microsoft Testing Platform (`global.json` selects the runner for `dotnet test`), with FsCheck properties (`FsCheck.Xunit.v3`) for the pure modules. `Ignore` and `Grep` have example tests plus properties. CLI tests run `GrepCli.run` against temp directory trees and compare fixed expected output and exit codes. They do not need `rg` installed.

## Consumers

- **CLI:** `ferret` binary.
- **Eru MCP server:** calls the library directly for ranked, bounded results (limit, capped excerpts, truncation flag).
- **.NET apps:** reference `Ferret` and supply their own ports if needed.

## Open questions

- **CI: stdin detection is only verified on macOS (arm64).** Add a CI matrix (Linux x64, Linux arm64 if available, Windows, macOS) that runs the real binary for each case: pipe, empty pipe, `< nonempty file`, `< emptyfile`, `< /dev/null` (`< NUL` on Windows), and a terminal if one can be allocated. Expected: pipe and non-empty file search stdin, empty pipe and empty file exit 1, `/dev/null` searches the current directory. The Linux offsets, the `__fxstat` fallback (needs an image with glibc before 2.33, or musl) and the Windows path have never been run. The unit tests only cover the mode decision, not the syscall.
