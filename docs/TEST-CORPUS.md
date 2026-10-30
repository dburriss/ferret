# Test corpus

Ferret needs test data for three different jobs. One corpus can't do all three, so there are three tiers.

| Tier | Purpose | Size | In git? |
|---|---|---|---|
| Compat fixtures | ripgrep-compatible behaviour | tiny | yes |
| Smoke | fast ranking sanity check, runs in CI | ~30 files | yes |
| Relevance | ranking quality vs published baselines | thousands of docs | no, fetched by script |
| Perf | speed and scale vs `rg` | GBs | no, fetched by script |

## The POC corpus

The POC lives outside this repo at `ws/temp/ffind/eval/`.

- `fixtures/ddd/`: 10 DDD notes (~6 KB).
- `fixtures/noise/`: 20 *Moby Dick* extracts (~28 KB), used as distractors.
- `queries.json`: 4 queries with `{query, relevant[]}` labels.
- `fixtures-knowledge-synthetic/` + `queries-knowledge.json`: 24 synthetic docs, 6 queries.
- `compare.sh`: R@10 and P@R against `ck`.

### Limits

- Ten queries in total. One file moving one rank changes the result.
- With 30 files, a lazy ranking still scores well on R@10.
- Moby Dick shares almost no vocabulary with DDD, so it tests topic separation, not BM25 term weighting.
- Labels were written for semantic retrieval. There are no exact-term, rare-token or identifier queries, which is where BM25 should beat embeddings.
- No ripgrep-compat cases.

### Reuse

- Port `ddd/`, `noise/` and `queries.json` as the smoke tier.
- Keep the label format. Add graded relevance later if nDCG is wanted.
- Keep the metrics (R@10, P@R) and the comparison-against-a-reference approach.
- The synthetic knowledge corpus belongs with the chunks-vs-files experiment (`research/chunks-vs-files-findings.md`), not here.

## Tier: compat fixtures

Hand-built, checked in, one directory per behaviour. Nothing public covers these.

- Binary files (NUL bytes), and files that turn binary after the first buffer.
- Very long lines, empty files.
- Non-UTF-8 encodings (UTF-16 with BOM, Latin-1).
- CRLF and mixed line endings.
- `.gitignore`, `.ignore`, nested ignore files, negation rules, hidden files.
- Symlinks (file, directory, loop).
- Stdin vs file-argument detection.

Tests assert Ferret's output against `rg`'s on the same inputs. Where `rg` isn't available, assert against checked-in expected output.

## Tier: smoke

The ported POC corpus, plus the following additions:

- About 20 exact-term and identifier queries (for example a rare word, a type name, a flag name) with labels. This lets BM25 and semantic search be compared per query type.
- More noise: more *Moby Dick* chapters, so the corpus has more than 30 files.
- Queries where the right answer needs term weighting, for example a common word plus a rare word.

Target is under a second to index and query. Runs in CI.

## Tier: relevance

Fetched by a script into a gitignored `corpus/` directory. Data is JSONL, so the script writes one file per doc.

| Dataset | Docs | Why |
|---|---|---|
| BEIR SciFact | ~5k | Small, with qrels. Published BM25 nDCG@10 ≈ 0.67. |
| BEIR NFCorpus | ~3.6k | Small, with qrels. Published BM25 nDCG@10 ≈ 0.32. |
| CodeSearchNet | large | Deferred past v1. Code plus natural-language queries, closer to Ferret's real use. Add once the eval harness works. |
| Cranfield, CACM | ~1.4k, ~3.2k | Tiny classics for fast iteration. |

Tooling: `.fsx` scripts. They download the BEIR zips, write one file per doc, and compute nDCG@10, recall and MRR from the qrels. The metrics are small enough to write ourselves.

The published BM25 numbers give a check that our BM25 matches a reference implementation. Large gaps point to tokenisation or parameter differences (`k1`, `b`). Treat the figures above as approximate and confirm them against Anserini or BEIR before relying on them.

## Tier: perf

Fetched by script, never checked in.

- Linux kernel source: realistic code, large.
- `dotnet/runtime`: realistic code, .NET-heavy.
- ripgrep's benchsuite corpora (Linux source, OpenSubtitles en): allows direct `rg` vs `ferret` timing.

Report warm-cache timings and peak memory via `hyperfine`. Cold-cache runs need a page-cache purge (`sudo purge` on macOS), so they are manual. Library-level micro-benchmarks, if added, would use BenchmarkDotNet.

Harness scripts and raw results live in `benchmarks/`. Write-ups that interpret them go in `research/`.

## Layout

```
tests/corpus/
  smoke/           ddd/, noise/, queries.json
  compat/<case>/   args, tree/, expected.txt, exit, [git], [gap]
corpus/            gitignored, fetched BEIR datasets
scripts/
  fetch-corpus.fsx
  eval-relevance.fsx
  gen-compat-expected.fsx
benchmarks/
  fetch.fsx, run.fsx
  results/         raw results, checked in
  data/            gitignored clones
```

## Status

- Smoke: built. 18 exact and 4 weighted queries asserted, 4 semantic recorded.
- Compat: built, 16 cases. Two known gaps vs rg: CRLF lines lose the `\r` (`crlf/gap`), and UTF-16 files with a BOM are skipped as binary (`utf16-bom/gap`).
- Relevance: built. SciFact nDCG@10 is 0.662 (published BM25 ≈ 0.665, from memory, not re-verified), recall@100 0.886, MRR@10 0.628, in about 80s. NFCorpus not yet run.
- Perf: scripts built and smoke-tested on a tiny directory. `run.fsx` drives `hyperfine` (pinned in `mise.toml`) and records mean, median, min, stddev and peak RSS. Warm cache only, since a cold run needs `sudo purge`. The Linux and dotnet/runtime clones have not been run yet.

## Decisions

- Scripts are `.fsx`.
- CodeSearchNet is deferred past v1. SciFact and NFCorpus first.
- Perf harness and results go in `benchmarks/`. Interpretation goes in `research/`.
