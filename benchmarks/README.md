# Benchmarks

Two kinds of measurement live here: speed (against `rg`, and for the persisted index), and relevance (ranking quality). Neither runs in CI. The index's correctness checks (exact parity, invalidation, corrupt cache) are unit tests and do run in CI. The plan and rationale are in `docs/TEST-CORPUS.md`.

```
benchmarks/
  fetch.fsx                  clones the perf corpora into data/
  run.fsx                    times rg vs ferret with hyperfine
  relevance-baseline.json    accepted relevance scores (committed)
  data/                      perf corpora (gitignored, several GB)
  results/                   perf results and relevance run logs
```

## Performance

Times `rg` and `ferret` on the same patterns over large trees.

```sh
mise run fetch:perf        # shallow clones of Linux v6.12 and dotnet/runtime v9.0.0 (several GB)
mise run bench [-- runs]   # builds Release, then runs hyperfine (default 10 runs after 1 warmup)
```

- Warm cache only. Cold-cache runs need a page-cache purge (`sudo purge` on macOS), so they are manual.
- `hyperfine` comes from `mise.toml` (`mise install`).
- The patterns cover a literal, a rare literal, a fixed string (`-F`), a regex, a case-insensitive match and a pattern with no match. They are listed at the top of `run.fsx`.
- Output is `results/<date>-<machine>.json`: per tool and pattern, mean, median, min, stddev and peak RSS.
- This is a search-speed check, so it uses code corpora. It says nothing about ranking quality.
- It also times `--lex` per query on the perf corpora and any fetched BEIR / F# docs sets, in three modes: `per-run` (`--no-index`), `index-build` (cache deleted before every run) and `index-warm`. The cache lives in a throwaway dir (`FERRET_CACHE_DIR`). Results go under `lex` in the same file, with `indexBytes`.
- `FERRET_BENCH_CORPORA=scifact,fsharp-docs` limits the run to named corpora, which skips the multi-GB ones.

- It also times `--sem` and `--hybrid`, ferret next to [`ck`](https://github.com/BeaconBay/ck), in two modes per query: `index-build` (index deleted before every run, so it includes embedding) and `index-warm` (index built; includes loading the model and embedding the query). There is no per-run mode, since neither tool has a `--no-index` semantic path. Results go under `semantic` in the same file, with index sizes and a ferret `--timings` phase breakdown (embed, tokenize, inference, fuse) taken once per mode.
- Semantic rows default to the F# docs set only, because the build embeds the whole corpus: seconds there, hours on Linux or dotnet/runtime. `FERRET_BENCH_SEM_CORPORA=fsharp-docs,scifact,...` opts in to others (queries for each are in `run.fsx`). `FERRET_BENCH_BUILD_RUNS` sets the build runs (default 3, no warmup); warm rows use the normal `runs`.
- Each ferret mode gets its own `FERRET_CACHE_DIR`, so your cache is untouched. ck keeps its index in `.ck` under the searched dir, which the benchmark deletes afterwards. ferret gets `FERRET_OFFLINE=1`.
- Both models must already be cached, or the run fails before timing anything: `ferret --model download`, and `ck --sem x <dir>` once for ck (`~/.cache/ck/models`). The benchmark never downloads.
- Not like for like: ck uses its own chunker, model runtime and index, and `ck --hybrid` fuses regex and semantic results where ferret fuses BM25 and semantic (RRF). ck runs with `--topk 10 --threshold 0` so a score cutoff does not skip work. Compare them as end-user commands, not as identical algorithms.

### Profiling

Before optimising the index build or a warm query, run with `--timings` to see where the time goes: `ferret --lex --timings <terms> <path>`. It prints one row per phase to stderr (`ferret.index.load`, `ferret.enumerate`, `ferret.stamp`, `ferret.index.build`, `ferret.rank`, `ferret.index.save`, `ferret.excerpts`; `--no-index` gives `ferret.enumerate`, `ferret.read`, `ferret.rank`, `ferret.excerpts`), with file counts as tags. A cold run (no index) shows the build cost; a warm run should show `files.built=0`, so its time is load, walk, rank and excerpts.

## Relevance

Runs Ferret's BM25 over labelled datasets and reports nDCG@10, recall@100 and MRR@10. The runner is `scripts/eval-relevance.fsx`.

| Dataset | What it is | Queries | Source |
|---|---|---|---|
| `scifact` | BEIR SciFact, scientific abstracts | 300 | `mise run fetch:corpus` |
| `nfcorpus` | BEIR NFCorpus, nutrition and medical | 323 | `mise run fetch:corpus` |
| `fsharp-docs` | `docs/fsharp` from dotnet/docs, real markdown | 42 | `mise run fetch:fsharp-docs` |

The F# docs queries are hand-written with graded labels (2 = canonical page, 1 = also relevant). They are checked in at `tests/corpus/relevance/fsharp-docs/queries.json` and split into `keyword`, `question` and `exact` types. The eval prints a score per type, because the types behave very differently: BM25 is strong on keyword and exact-token queries and weak on natural-language questions.

```sh
mise run eval:relevance                    # all datasets, about 2.5 min (SciFact is most of it)
mise run eval:relevance -- fsharp-docs     # one dataset, about 1s
mise run eval:relevance -- --update        # record the measured scores as the new baseline
mise run eval:relevance -- fsharp-docs --ranker sem      # semantic (own baseline, not logged)
mise run eval:relevance -- fsharp-docs --ranker hybrid   # RRF of BM25 and semantic; prints a variant table
mise run eval:relevance -- --no-parity     # skip the index parity pass
```

### Index parity

Unless `--no-parity` is given, every query also runs through the persisted-index path (`Search.searchIndexed`, in-memory store, so the first query builds and the rest reuse). Its results must equal the per-run results exactly: files, order, scores and excerpts. Any difference prints `PARITY` and exits 1, before a log is written or a baseline updated. After each dataset the index is also saved to disk and reloaded, and must equal the in-memory one. The nDCG gate is far too loose for this (0.01), which is why parity is exact. The run prints per-run and indexed times, so the speed-up shows up too (SciFact: about 177s vs 28s for 300 queries). The parity pass adds to the full run time.

### Rankers

`--ranker lex|sem|hybrid` picks what is scored (default `lex`). `sem` and `hybrid` embed every document (fsharp-docs about a minute the first time; SciFact and NFCorpus much longer), skip the index parity pass and are not logged. Baselines: `relevance-baseline.json` (lex), `relevance-baseline-sem.json` and `relevance-baseline-hybrid.json`.

`hybrid` caches both rankings per query and prints a table of BM25, semantic and RRF variants (k = 60 with weights 1:1, 2:1 and 1:2, and k = 20), overall and per type. The row that is scored and gated is k = 60, 1:1, unless `--rrf-k N` and `--rrf-w LEX:SEM` choose another. Only `--update` writes a baseline.

### Baseline and regression gate

`relevance-baseline.json` holds the accepted nDCG@10, recall@100 and MRR@10 per dataset. Each run prints the delta from it and exits 1 with a `REGRESSION` line if any dataset's nDCG@10 is more than 0.01 below the baseline.

After an intentional change to ranking or tokenizing, run with `--update` and commit the file. Its git history is the record of accepted scores.

### Run logs

A full run (no dataset argument) also writes `results/relevance-<hash>.json`, with timestamp, git commit, dirty flag, and per-dataset and per-type scores.

`<hash>` is the first 8 hex characters of a SHA-256 over everything that can change a score:

- `src/Ferret.Core/**/*.fs` and `*.fsproj`
- `scripts/eval-relevance.fsx`, `fetch-corpus.fsx` and `fetch-fsharp-docs.fsx` (metrics, loaders and dataset pins)
- `tests/corpus/relevance/**` (queries and labels)

The hash is our own rather than the git sha because a git sha is the wrong key: docs-only commits would each get a log with identical scores, and a dirty tree shares its sha with the clean tree but can score differently. The hash changes only when a score could.

A log is written only if none exists for that hash, so docs-only commits and repeat runs add nothing, and each distinct code state is logged once, dirty or not. Partial runs (one dataset) are never logged, since they would block the full run for the same inputs. To force a new log, delete the file.

The logs are untracked by default. Commit the ones worth keeping.

### Caveats

- The F# docs set is small and its labels are one person's judgement, written before ferret was run on it. Treat its score as a regression reference, not an absolute measure.
- SciFact and NFCorpus have published BM25 figures (about 0.665 and 0.32 nDCG@10, quoted from memory and not re-verified). They are a sanity check that our BM25 behaves like a reference implementation.
- Datasets live in `corpus/` (gitignored). Never commit dataset files. The BEIR sets are for research use.
