<p align="center">
  <img src="docs/assets/ferret-logo.svg" alt="Ferret" width="360">
</p>

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

With .NET:

```sh
dotnet tool install --global Ferret.Tool
```

With [mise](https://mise.jdx.dev), no .NET needed:

```sh
mise use -g github:dburriss/ferret
```

mise installs the release archive, which downloads the semantic model on first `--sem` use. For a build with the model embedded, so it works offline, ask for the `bundled` archive:

```sh
mise use -g 'github:dburriss/ferret[matching=bundled]'
```

```toml
# mise.toml
[tools]
"github:dburriss/ferret" = { version = "latest", matching = "bundled" }
```

The archives are also on the [releases page](https://github.com/dburriss/ferret/releases) for manual download. Keep the archive contents together, since the exe loads the ONNX Runtime library from beside itself. The default pick needs mise 2026.4.22 or later.

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

## Agent skill

A skill teaches coding agents (Claude Code and others) how to use the `ferret` command and the library: which mode to pick, the ripgrep-compatible flags, `--jsonl` for feeding results to an agent, offline setup and the `FerretClient` API.

```sh
npx skills add dburriss/ferret
```

The skill is in [skills/ferret](skills/ferret).

## Documentation

See [docs/index.md](docs/index.md): tutorials, how-to guides, reference and explanation.

## License

MIT
