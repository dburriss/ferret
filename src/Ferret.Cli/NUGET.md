# Ferret.Tool

`ferret`: a drop-in `rg` for plain searches that also ranks files by keyword (BM25), meaning (semantic) or both (hybrid). It gives the most relevant files first, with bounded output, which suits docs, notes and LLM agents.

## Install

```sh
dotnet tool install --global Ferret.Tool
```

## Use

```sh
ferret "pattern" src            # like rg
ferret --lex "query terms"      # ranked, BM25
ferret --sem "a question"       # ranked, by meaning (downloads a small model on first use)
ferret --hybrid "query"         # both, fused
ferret --index --hybrid .       # build the indexes ahead of searching
```

Ranked modes also take `--limit`, `--scores`, `--jsonl` and `--timings`. `ferret --model download` fetches the semantic model so `--sem` can run offline.

For .NET code, use the [Ferret](https://www.nuget.org/packages/Ferret) library.

Docs and source: https://github.com/dburriss/ferret
