# Search modes: lexical, semantic and hybrid

## Why ranking at all

ripgrep returns every match in file order. On a documentation corpus, a query such as "retries for AWS calls" yields a flat list in which the relevant file may sit below a README that merely quotes the query. People can scroll; agents with limited context cannot. Ferret ranks files and bounds the output so the best file comes first.

## Lexical (BM25)

BM25 scores a file by how many query terms it contains, weighted by how rare each term is in the corpus and by file length. It is exact and predictable: if a term is not in the file, it does not help. It cannot match paraphrases.

Ferret lowercases, splits on non-alphanumerics, drops stop words and applies light suffix stemming, so `mock` matches `mocked`. Terms are combined with OR; ranking makes that safe because files matching more and rarer terms rise. Terms in the path, headings or front matter (`title`, `description`, `tags`) count extra.

## Semantic

Semantic search embeds chunks of each file (split by heading, then paragraph) with a small model and ranks by similarity to the embedded query. It finds "what do I do when a request fails" for a file about retries. It also blurs exact terms: in testing, "mock time unit tests" pulled in files about timeouts.

A file's score is its best chunk (`max`). Summing chunk scores would favour long files.

## Hybrid

Hybrid runs both and merges the two file rankings with reciprocal rank fusion: each ranker contributes `1 / (60 + rank)` per file. RRF uses ranks, not scores, because BM25 and similarity scores are on unrelated scales.

On a 42-query set over the F# docs, nDCG@10 was 0.863 (BM25), 0.884 (semantic) and 0.911 (hybrid). Hybrid helped most on natural-language questions. That is one corpus, so treat it as evidence, not proof; see `benchmarks/README.md`.

## Choosing

- Exact identifiers and error messages: `--lex`, or plain `ferret`.
- Questions and vague recall: `--sem` or `--hybrid`.
- Not sure: `--hybrid`, at the cost of the model and a semantic index.

Plain regex search stays the default so that `ferret pattern` never surprises someone used to `rg`.
