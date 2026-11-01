# Examples

Runnable `.fsx` scripts for the Ferret library. Each takes an optional query and folder, and defaults to searching this repo's `docs/`.

Build once, then run any of them with `dotnet fsi`:

```sh
dotnet build -c Release src/Ferret
dotnet fsi examples/01-lexical-search.fsx "install the tool"
```

| Script | Shows |
|---|---|
| [01-lexical-search.fsx](01-lexical-search.fsx) | BM25 ranking with excerpt lines |
| [02-semantic-search.fsx](02-semantic-search.fsx) | Semantic search and the progress callback |
| [03-compare-modes.fsx](03-compare-modes.fsx) | One query in lexical, semantic and hybrid mode, side by side |
| [04-grep.fsx](04-grep.fsx) | Regex search with `MatchOptions`, lazily enumerated |
| [05-prebuild-and-offline.fsx](05-prebuild-and-offline.fsx) | `EnsureModel`, `BuildIndex` and the cold versus warm cost |
| [06-timings-listener.fsx](06-timings-listener.fsx) | Phase timings through an `ActivityListener` |
| [07-jsonl-for-agents.fsx](07-jsonl-for-agents.fsx) | Bounded JSON lines for an agent or script |

`02`, `03` and `05` need the semantic model and download it on first run (about 32 MB). `_ferret.fsx` holds the assembly references; outside this repo, replace it with `#r "nuget: Ferret, *-*"`.
