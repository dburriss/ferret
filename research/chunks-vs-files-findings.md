# Chunks vs Files: Semantic Rollup Findings

Status: Findings from the `ffind` PoC. Informs section 8 of [bm25-ranked-search.md](bm25-ranked-search.md).

## Question

The semantic index holds chunks, but consumers (Eru, the CLI, the MCP) return ranked files with matching lines. To fuse a semantic ranking with a file-level BM25 ranking via RRF, the semantic side must produce a ranked list of files. How should chunk hits roll up to files, and what is lost?

## Experiment

PoC change in `ffind`: a pure `Rollup` function over chunk hits, plus `--by-file` search and an `eval-files` command.

Strategies:

| Strategy | File score |
|----------|-----------|
| `max` | best chunk score |
| `top-n-mean` | mean of the file's top N chunk scores (N = 3) |
| `sum-top-n` | sum of the file's top N chunk scores (N = 3) |

The excerpt is always the file's best chunk. Ties break by ordinal path. The candidate depth (top-K chunks before rollup) was a parameter, tested at 10, 25, 50 and 100.

Corpora:

1. **Synthetic:** 24 files, 143 chunks (about 6 per file), 6 queries labelled before running. Written by the same person as the queries, and easy: every strategy reaches R@10 = 1.00 at depth 100.
2. **Real:** an internal repo of .NET package docs, 137 indexed markdown files (140 tracked), 1,142 chunks (8.3 per file, median 5, max 103). 3 queries, with relevance labels written from grepping the content, not from search results.

Real-corpus queries and labels:

| Query | Labelled relevant |
|-------|-------------------|
| mock time unit tests | `Stopwatch/README.md` |
| retries for AWS calls | `docs/DEPRECATED_PACKAGES.md`, `Resilience/README.md` |
| health check redis | the Redis health test README, its MonitoringEvents add-on README, `ApplicationHealth/README.md` |

Label caveats: no live AWS-retry package exists in the repo, so the labels for query 2 are a judgement. The root `README.md` contains the query text verbatim as an example prompt and is labelled not relevant.

## Findings

### 1. `max` is the right rollup

On the real corpus (3 queries, N = 3):

| Strategy | R@3 | MRR@10 |
|----------|-----|--------|
| `max` | 0.72 at every depth | 1.00 at every depth |
| `top-n-mean` | 0.72-0.83 | 0.83-1.00, varies with depth |
| `sum-top-n` | 0.50-0.83 | 0.67-0.78, worst at depths 25-100 |

The synthetic corpus agrees: `max` is best or tied-best, and `sum-top-n` drops MRR@10 to 0.75-0.85 at depths 25 and 50. One relevant file is missed by every strategy at every depth on the real corpus (the weakly related `Resilience/README.md` on the AWS query).

`max` also has the useful property that the excerpt is the matched chunk by construction, and its ranking does not depend on how many chunks the candidate set contains.

### 2. Sum-based strategies favour long files

Real corpus, moving a file from `max` to `sum-top-n`:

| File size | Mean rank change |
|-----------|------------------|
| 1-2 chunks | -12.5 positions |
| 3-5 chunks | +1.7 |
| 6+ chunks | +5.6 |

The top-1 file changed on 1 of 3 queries. `ApplicationHealth/README.md` (37 chunks) ranks 3rd for "mock time unit tests" under `max`: a large generic file that collects weak hits. `top-n-mean` is less biased but penalises a file with one excellent chunk and weaker others, which is the usual shape of a multi-section document.

On the synthetic corpus the same pattern held (1-2 chunk files lost 6.6 positions on average).

### 3. Chunk depth is an artefact of top-K selection, not a design parameter

The PoC index is brute-force: every chunk is already scored against the query. With `max`, keeping the best chunk per file over all chunks is exact, and no relevant file can be dropped. The measured "minimum depth" (5 on synthetic, 20 on the first real corpus, 184 on the real corpus) only describes how deep a top-K cut has to go to keep every labelled file. The 184 came from one weakly related labelled file.

Depth only becomes a real parameter if an approximate nearest-neighbour index (HNSW, IVF) is added, because ANN returns only the top-K chunks. At this scale it isn't needed:

| Measurement (real corpus) | Result |
|---------------------------|--------|
| Files / chunks | 137 / 1,142 |
| Cold index | 6.0 s (parallel, about 6.6 cores) |
| Re-run, nothing changed | 13 ms in the indexer, 0.14 s process |
| Index file | 1.9 MB |

A brute-force scan over 1,142 chunks of 384 dimensions is well under a millisecond. Query time is dominated by model load and query embedding. The real cost is the cold index, which means the design question is when the first index is built, not how search is accelerated.

### 4. Excerpts are weak where chunks are structural

Many top hits point at lines 1-6, which is the YAML front matter and description, or at a heading-only chunk (`# Logging`, lines 1-1). These chunks embed as "what this file is about", so they win topic queries, but they are a poor pointer to the matching lines. On the synthetic corpus, 8 of 9 top-3 excerpts for the three requested queries pointed at the right section, and the misses were title chunks. This is a chunker problem, not a rollup problem.

### 5. Semantic ranking is blurry on exact terms

On the real corpus:

- "health check redis": best result. The Redis health test and its add-on rank 1 and 2. Near-misses (S3, SQS, SNS, Kafka health tests) follow.
- "mock time unit tests": `Stopwatch/README.md` is rank 1, but ranks 2 and 3 are "timeouts" and "usage" chunks from the health test docs: semantic neighbours on "time" and "test".
- "retries for AWS calls": weak. Rank 1 is a deprecated-packages table row, rank 2 an unrelated CDK changelog entry. This is where BM25 on "retries" and "AWS" should do better. Not verified, because BM25 does not exist yet.

### 6. Generated index files and verbatim query text hurt

`ApplicationHealth/index.md` ranks 3rd for "health check redis", and the generated `index.md` is first in ck's lexical results for two queries. The root `README.md`, which contains "retries for AWS calls" verbatim, is ranked first by ck's hybrid mode on that query. These support the ignore-glob design (section 3 of the design doc) and a ranker that does not over-reward literal repeats.

## Comparison with ck (0.7.11), same queries

ck ran over all 1,395 files in the repo, not only markdown. Results are filtered to `.md`, deduplicated from chunk or line results to files.

| Query | `--lex` | `--sem` | `--hybrid` |
|-------|---------|---------|------------|
| mock time | `index.md` first, Stopwatch README at 2 | Stopwatch README only | Stopwatch README only |
| retries for AWS | HttpClient resilience, MySql, BigQuery, root README; no labelled file | `MonitoringEvents/README.md` only; no labelled file | root README first, then `MonitoringEvents/README.md`; no labelled file |
| health check redis | `index.md` first, then both Redis READMEs | both Redis READMEs, then `ApplicationHealth/README.md` | `ApplicationHealth/README.md` first, then both Redis READMEs |

- ck's semantic and hybrid modes return only 1-4 files (a default score threshold). That is precise but misses results.
- ck missed both labelled files for the AWS query in every mode.
- ck ranks generated `index.md` highly in lexical mode.
- ck's `--lex` is BM25 (Tantivy), and its `--hybrid` keyword arm is regex-based. See the prior-art section of the design doc.

Operational notes on ck: indexing the repo took 4.5 minutes (1,395 files, all types) versus 6 seconds for 137 markdown files in ffind. `CK_INDEX_DIR` did not redirect the index, so ck wrote `.ck/` and `.ckignore` into the searched repo. Both were removed.

## Decisions for the design

1. **Semantic ranker returns files by `max` over a full scan.** Best chunk per file, ranked by that chunk's score, with the chunk as the excerpt. No chunk-depth parameter. The ranker's contract stays file-level, as in section 8.
2. **Drop `sum-top-n`.** Keep it as a documented negative result. Do not expose it from the library.
3. **Fix the chunker before judging semantic quality:** merge heading-only and front-matter chunks into the following chunk, or embed each chunk with a breadcrumb prefix (file title and section) while storing the unprefixed text as the preview.
4. **Choose excerpt lines separately.** Take the best chunk's range, then highlight lines inside it using BM25 term matches, falling back to the chunk's first lines.
5. **Revisit ANN only if** a corpus exceeds roughly 50-100x the real corpus (about 100k chunks, untested). Then depth returns as a tuning parameter, in files, not chunks.
6. **Build the first index off the query path** (on refresh, in the background, or lazily with a clear message). 6 s cold is acceptable once, not per query.

## Limitations

- 3 real queries and 6 synthetic. Differences of one file moving one place are noise.
- Labels were written by one person. The AWS-retry labels are a judgement.
- BM25 and fusion were not run, so this does not say whether hybrid beats either alone. The next experiment is BM25, semantic and RRF on these queries and labels.
- Excerpt quality was judged by reading, not scored.
- ck was compared over a different file set (all types) and with a different dedupe path, so its numbers are indicative only.
- Not tested: corpora of hundreds of files or more, queries with a single relevant file beyond query 1, `top-n-mean` with N other than 3, non-English content.
