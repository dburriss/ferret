# Library API reference

Package `Ferret` (namespace `Ferret`). `Ferret.Core` and `Ferret.Semantic` are its dependencies. Lexical-only consumers can use `Ferret.Core` and avoid the ONNX runtime and model.

## FerretClient

Implements `IDisposable`. Reuse one instance across searches: it holds the loaded model.

| Member | Description |
|---|---|
| `new FerretClient()` / `new FerretClient(FerretOptions)` | Create a client. |
| `Search(query, limit, mode) : ScoredDoc list` | Rank files, best first, at most `limit` (≥ 1, else `ArgumentException`). |
| `Search(query, limit)` | Uses `DefaultMode`. |
| `Search(query)` | `DefaultMode`, limit 10. |
| `Grep(pattern, MatchOptions) : Hit seq` | Regex search like `rg`. Lazy. A bad pattern throws `ArgumentException`. |
| `Grep(pattern)` | Case-sensitive regex. |
| `BuildIndex(mode) : int` | Build or update the index; returns files indexed (for `Hybrid`, in the lexical index). |
| `EnsureModel() : string` | Make the model available; returns its directory, or `"bundled"`. |
| `EnsureModelAsync() : Task<string>` | Same, without blocking. |

A missing root throws `DirectoryNotFoundException`. A missing model throws `ModelUnavailable`.

## FerretOptions

| Property | Type | Default |
|---|---|---|
| `Roots` | `string[]` | Current directory (`[| "" |]`) |
| `CacheDir` | `string` | Ferret's cache directory; `FERRET_CACHE_DIR` overrides |
| `ModelDir` | `string` | `FERRET_MODEL_DIR`, else cache, else download |
| `DefaultMode` | `SearchMode` | `Lexical` |
| `UseIndex` | `bool` | `true` (lexical only) |
| `Progress` | `Action<string>` | none |

`ModelDir` must contain `model_quantized.onnx` and `vocab.txt`.

## Types

```fsharp
type SearchMode = Lexical | Semantic | Hybrid

type Hit = { Path: string; LineNumber: int; Line: string }   // LineNumber is 1-based
type ScoredDoc = { File: string; Score: float; Excerpts: Hit list }
type MatchOptions = { IgnoreCase: bool; FixedStrings: bool; WordRegexp: bool }
// MatchOptions.defaults: all false
```

Score scales differ by mode: BM25 scores are unbounded and corpus-dependent; semantic scores come from the embedding similarity; hybrid scores are RRF scores (about 0.03 at most).

## Telemetry

The library emits one `System.Diagnostics.Activity` per phase on the `Ferret` `ActivitySource`. There are no per-file spans and no OpenTelemetry dependency. With no listener it costs nothing.

| Phase | Used by |
|---|---|
| `ferret.enumerate` | all |
| `ferret.stamp` | indexed |
| `ferret.index.load`, `ferret.index.build`, `ferret.index.save` | indexed |
| `ferret.read` | `--no-index` |
| `ferret.embed` | semantic |
| `ferret.rank`, `ferret.excerpts` | all ranked |
| `ferret.fuse` | hybrid |

`ferret.index.build` tags: `files.total`, `files.reused`, `files.built`, `tokenize.ms`, `inference.ms`, `tokens`.
