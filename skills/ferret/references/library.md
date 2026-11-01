# Ferret library reference

Package `Ferret` (namespace `Ferret`) is the only library package. It bundles `Ferret.Core` and `Ferret.Semantic` as internal projects and depends on ONNX Runtime, so lexical-only use still carries that dependency. The model is not shipped: it downloads on first semantic use. Repo docs: `docs/reference/library.md`, runnable scripts in `examples/` (`01-lexical-search.fsx` to `07-jsonl-for-agents.fsx`; outside the repo replace `_ferret.fsx` with `#r "nuget: Ferret, *-*"`).

Setup: `dotnet add package Ferret`.

## FerretClient (IDisposable; reuse one instance)

| Member | Description |
|---|---|
| `new FerretClient()` / `new FerretClient(FerretOptions)` | Create. |
| `Search(query, limit, mode) : ScoredDoc list` | Best first, at most `limit` (>= 1 else `ArgumentException`). |
| `Search(query, limit)` | Uses `DefaultMode`. |
| `Search(query)` | `DefaultMode`, limit 10. |
| `Grep(pattern, MatchOptions) : Hit seq` | rg-like regex, lazy. Bad pattern throws `ArgumentException`. |
| `Grep(pattern)` | Case-sensitive regex. |
| `BuildIndex(mode) : int` | Build/update index; returns files indexed (Hybrid builds both indexes; the count is the lexical one). Calling it for Lexical, Semantic and Hybrid is redundant: `Hybrid` alone covers both. |
| `EnsureModel() : string` / `EnsureModelAsync()` | Make the model available; returns its dir or `"bundled"`. |

Errors: missing root throws `DirectoryNotFoundException`; missing model throws `ModelUnavailable`.

## FerretOptions

| Property | Type | Default |
|---|---|---|
| `Roots` | `string[]` | current directory |
| `CacheDir` | `string` | Ferret cache dir; `FERRET_CACHE_DIR` overrides |
| `ModelDir` | `string` | `FERRET_MODEL_DIR`, else cache, else download |
| `DefaultMode` | `SearchMode` | `Lexical` |
| `UseIndex` | `bool` | `true` (lexical only) |
| `Offline` | `bool` | `false`; never download the model (`FERRET_OFFLINE=1` also sets it) |
| `IgnorePatterns` | `string[]` | none; gitignore-style, applied after each root's ignore files |
| `Progress` | `Action<string>` | none |

`ModelDir` must contain `model_quantized.onnx` and `vocab.txt`.

## Types

```fsharp
type SearchMode = Lexical | Semantic | Hybrid
type Hit = { Path: string; LineNumber: int; Line: string }   // 1-based
type ScoredDoc = { File: string; Score: float; Excerpts: Hit list }
type MatchOptions = { IgnoreCase: bool; FixedStrings: bool; WordRegexp: bool }  // MatchOptions.defaults: all false
```

Score scales differ by mode (BM25 unbounded and corpus-dependent; semantic = embedding similarity; hybrid = RRF, ~0.03 max). Compare within one result list only.

## Examples

Excerpts and progress:

```fsharp
open Ferret

let options = FerretOptions(Roots = [| "docs" |], Progress = System.Action<string>(eprintfn "%s"))
use ferret = new FerretClient(options)

for doc in ferret.Search("what do I do when a request fails", 5, Semantic) do
    printfn "%.3f %s" doc.Score doc.File
    for h in doc.Excerpts do
        printfn "  %d: %s" h.LineNumber h.Line
```

Regex search:

```fsharp
ferret.Grep("todo", { MatchOptions.defaults with IgnoreCase = true }) |> Seq.truncate 20 |> Seq.iter (fun h -> printfn "%s:%d:%s" h.Path h.LineNumber h.Line)
```

Warm up in CI or at start-up: `ferret.EnsureModel() |> ignore; ferret.BuildIndex Hybrid |> ignore`.

## Telemetry

One `System.Diagnostics.Activity` per phase on the `Ferret` `ActivitySource`; no per-file spans, no OpenTelemetry dependency, free with no listener. Phases: `ferret.enumerate`, `ferret.stamp`, `ferret.index.load|build|save`, `ferret.read` (`--no-index`), `ferret.embed`, `ferret.rank`, `ferret.excerpts`, `ferret.fuse` (hybrid). `ferret.index.build` tags: `files.total`, `files.reused`, `files.built`, `tokenize.ms`, `inference.ms`, `tokens`. See `examples/06-timings-listener.fsx`.
