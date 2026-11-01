# Ferret

Lexical (BM25), semantic and hybrid file search for .NET. Point it at some folders and search: it ranks whole files, best first, with excerpts, and keeps a persisted index so later searches are fast.

## Install

```sh
dotnet add package Ferret
```

## Use

```fsharp
open Ferret

use ferret = new FerretClient(FerretOptions(Roots = [| "docs" |]))
for doc in ferret.Search("how do I install it", 10, Hybrid) do
    printfn "%.3f %s" doc.Score doc.File
```

`Search` takes `Lexical`, `Semantic` or `Hybrid`. Semantic and hybrid use a small local model that downloads on first use; `EnsureModel()` fetches it ahead of time. `BuildIndex` indexes ahead of the first search, `Search` and `BuildIndex` take a `CancellationToken` and have `SearchAsync`/`BuildIndexAsync` variants, and `Grep` does regex search like `rg`. `FerretOptions` sets the roots, cache directory, model directory, default mode, offline mode, extra ignore patterns and a progress callback.

The cache is shared with the `ferret` command ([Ferret.Tool](https://www.nuget.org/packages/Ferret.Tool)).

Docs and source: https://github.com/dburriss/ferret
