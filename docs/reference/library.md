# Library API reference

Package `Ferret` (namespace `Ferret`) is the only library package. It bundles `Ferret.Core` and `Ferret.Semantic` as internal projects and depends on ONNX Runtime, so lexical-only use still carries that dependency. The model is not shipped: it downloads on first semantic use.

## FerretClient

Implements `IDisposable`. Reuse one instance across searches: it holds the loaded model.

| Member | Description |
|---|---|
| `new FerretClient()` / `new FerretClient(FerretOptions)` | Create a client. |
| `Search(query, limit, mode) : ScoredDoc list` | Rank files, best first, at most `limit` (≥ 1, else `ArgumentException`). |
| `Search(query, limit, mode, ct)` / `Search(query, limit, ct)` | Same, cancellable. |
| `Search(query, limit)` | Uses `DefaultMode`. |
| `Search(query)` | `DefaultMode`, limit 10. |
| `Grep(pattern, MatchOptions) : Hit seq` | Regex search like `rg`. Lazy. A bad pattern throws `ArgumentException`. |
| `Grep(pattern)` | Case-sensitive regex. |
| `SearchAsync(query, limit, mode, ct) : Task<ScoredDoc list>` / `SearchAsync(query, limit, ct)` | `Search` on a thread-pool thread. |
| `BuildIndex(mode) : int` | Build or update the index; returns files indexed (for `Hybrid`, in the lexical index). |
| `BuildIndex(mode, ct)` | Same, cancellable. |
| `BuildIndexAsync(mode, ct) : Task<int>` / `BuildIndexAsync(ct)` | `BuildIndex` on a thread-pool thread. |
| `EnsureModel() : string` | Make the model available; returns its directory, or `"bundled"`. |
| `EnsureModelAsync() : Task<string>` / `EnsureModelAsync(ct)` | Same, as asynchronous I/O. |

A missing root throws `DirectoryNotFoundException`. A missing model throws `ModelUnavailable`.

### Cancellation

Cancelling the token stops the call with `OperationCanceledException` (a cancelled `Task` for the async members). A cancelled search or build writes nothing, so the saved indexes stay as they were; a cancelled model download leaves no partial file and throws `OperationCanceledException`, not `ModelUnavailable`.

`SearchAsync` and `BuildIndexAsync` run the sync code on a thread-pool thread: they keep the caller free, but the work is CPU-bound and holds a pool thread until it ends or is cancelled. Only `EnsureModelAsync` is asynchronous I/O.

## FerretOptions

| Property | Type | Default |
|---|---|---|
| `Roots` | `string[]` | Current directory (`[| "" |]`) |
| `CacheDir` | `string` | Ferret's cache directory; `FERRET_CACHE_DIR` overrides |
| `ModelDir` | `string` | `FERRET_MODEL_DIR`, else cache, else download |
| `DefaultMode` | `SearchMode` | `Lexical` |
| `UseIndex` | `bool` | `true` (lexical only) |
| `Offline` | `bool` | `false`; `FERRET_OFFLINE=1` also sets it |
| `IgnorePatterns` | `string[]` | none |
| `Progress` | `Action<string>` | none |

`ModelDir` must contain `model_quantized.onnx` and `vocab.txt`.

`Offline` stops the client downloading the model. A missing model throws `ModelUnavailable` naming the directory it looked in.

`IgnorePatterns` are gitignore-style lines, applied to every root on top of the ignore files found in the tree and with the same last-match-wins rule, so `!` re-includes work. The caller passes patterns in; the library reads no extra files for them.

`IgnorePatterns` is ignored when you pass your own `IFileSource` to the `FerretClient` constructor.

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
