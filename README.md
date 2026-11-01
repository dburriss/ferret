# Ferret

Search files by keyword, meaning or both. Ferret is a drop-in `rg` for plain searches, and adds ranked lexical (BM25), semantic and hybrid search with a persisted index.

## What is Ferret?

`grep` and `rg` return every match in file order. On a folder of docs or notes, the file you want can sit below a README that merely quotes your query. Ferret ranks whole files instead, so the best one comes first, and it caps the output so a person or an LLM agent can take the top few results.

- **Lexical (BM25)**: exact and predictable. Files with more and rarer query terms rank higher.
- **Semantic**: finds files by meaning, so "what happens when a request fails" matches a page about retries. Uses a small local model, with no service to run.
- **Hybrid**: fuses both rankings. It beat either alone on the F# docs benchmark.

Plain `ferret pattern` behaves like `rg`, so it will not surprise anyone used to it. Ranked modes are opt-in flags. Indexes are a disposable cache that rebuilds on demand, and everything works offline once the model is downloaded.

Two packages:

- **Ferret.Tool**: the `ferret` command.
- **Ferret**: a library with the same search, for use from .NET code.

## Command

```sh
dotnet tool install --global Ferret.Tool
```

```sh
ferret "pattern" src            # like rg
ferret --lex "query terms"      # ranked, BM25
ferret --sem "a question"       # ranked, by meaning (downloads a small model on first use)
ferret --hybrid "query"         # both, fused
ferret --index --hybrid .       # build the indexes ahead of searching
```

Flags that rank also take `--limit`, `--scores`, `--jsonl` and `--timings`. `ferret --model download` fetches the semantic model so `--sem` can run offline; `FERRET_MODEL_DIR` points at an existing copy.

## Library

```sh
dotnet add package Ferret
```

```fsharp
open Ferret

use ferret = new FerretClient(FerretOptions(Roots = [| "docs" |]))
ferret.EnsureModel() |> ignore      // optional: download the semantic model now
ferret.BuildIndex Hybrid |> ignore  // optional: index ahead of the first search
for hit in ferret.Search("how do I install it", 10, Hybrid) do
    printfn "%.3f %s" hit.Score hit.File
```

`FerretOptions` sets the roots, the cache directory, an existing model directory, the default mode (`Lexical`, `Semantic` or `Hybrid`) and a progress callback. The cache is shared with the `ferret` command.

## Documentation

See [docs/index.md](docs/index.md): tutorials, how-to guides, reference and explanation.

## License

MIT
