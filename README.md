# Ferret

Search files by keyword, meaning or both. Ferret is a drop-in `rg` for plain searches, and adds ranked lexical (BM25), semantic and hybrid search with a persisted index.

Two packages:

- **Ferret.Tool**: the `ferret` command.
- **Ferret**: a library with the same search, for use from .NET code.

## Command

```sh
dotnet tool install --global Ferret.Tool

ferret "pattern" src            # like rg
ferret --lex "query terms"      # ranked, BM25
ferret --sem "a question"       # ranked, by meaning (downloads a small model on first use)
ferret --hybrid "query"         # both, fused
ferret --index --hybrid .       # build the indexes ahead of searching
```

Flags that rank also take `--limit`, `--scores`, `--jsonl` and `--timings`. `ferret --model download` fetches the semantic model so `--sem` can run offline; `FERRET_MODEL_DIR` points at an existing copy.

## Library

```fsharp
open Ferret

use ferret = new FerretClient(FerretOptions(Roots = [| "docs" |]))
ferret.EnsureModel() |> ignore      // optional: download the semantic model now
ferret.BuildIndex Hybrid |> ignore  // optional: index ahead of the first search
for hit in ferret.Search("how do I install it", 10, Hybrid) do
    printfn "%.3f %s" hit.Score hit.File
```

`FerretOptions` sets the roots, the cache directory, an existing model directory, the default mode (`Lexical`, `Semantic` or `Hybrid`) and a progress callback. The cache is shared with the `ferret` command.

## License

MIT
