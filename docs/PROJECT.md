# Ferret

Ferret is a .NET library and CLI for searching text and markdown knowledge bases. It is a drop-in replacement for ripgrep that adds ranked lexical (BM25), semantic and hybrid search.

Ferret is primarily a library for .NET developers, with the CLI as a thin front end over it. It is not trying to replace [ck](https://github.com/BeaconBay/ck), which is more full featured. It is a slight alternative with different search details (BM25 lexical arm, file-level rollup, bounded output) and a first-class .NET API.

Status of features is marked **Built**, **Planned** (designed, not built) or **Future** (direction only). Detail lives in `research/`.

## Problem space

### Grep finds matches, not answers

ripgrep is fast and precise, but it returns every match in file order. On a documentation or knowledge corpus, a query like `retries for AWS calls` returns a flat, unranked list. The relevant file may be buried below a README that quotes the query verbatim, or a generated `index.md`.

### Agents need bounded, ranked output

Tools such as Eru expose search to LLM agents through an MCP server. Agents have limited context, so they need:

- the most relevant files first,
- a bounded result size (limit, capped excerpts, a truncation flag),
- file paths plus matching lines, so they can read on demand.

Unranked OR-matching with unbounded output wastes context and buries the answer.

### Lexical and semantic search fail differently

- **Lexical (BM25)** is exact and predictable, but misses paraphrases and needs term overlap.
- **Semantic (embeddings)** handles meaning, but blurs exact terms. In the PoC, "mock time unit tests" pulled in "timeouts" and "usage" chunks, and "retries for AWS calls" returned unrelated rows.
- **Existing tools** (ck) cover parts of this, but not all. Its hybrid mode uses a regex keyword arm instead of BM25, it returns very few results under its default threshold, and it took 4.5 minutes to index a repo that a markdown-only index handled in seconds.

### Constraints

- Must not break muscle memory: plain `ferret <pattern>` behaves like `rg`.
- Must work offline, with no service to run.
- Library users who only want lexical search must not pay for the semantic ranker at run time: the 32 MB model downloads on first semantic use and lexical search never loads ONNX Runtime. The `Ferret` package still carries ONNX Runtime (about 17–43 MB per platform), because `Ferret.Core` and `Ferret.Semantic` are bundled into it. A separate lexical-only package is a possible later split. The CLI includes the semantic ranker, in two distributions: one downloads the model on first use, one bundles it.
- Indexes are disposable caches that rebuild lazily.

## Target users

- .NET developers and applications that want to embed ranked search through the library. This is the primary audience.
- LLM agents and tools (Eru, MCP clients) that need ranked, bounded knowledge retrieval.
- Developers searching docs and notes from the terminal, especially in the .NET ecosystem. `ck` is still the more feature full option and is amazing software.

## Features

### Plain search (ripgrep-compatible) — Built

- `ferret <pattern> [path...]` matches regex or literal text, unranked, in file and line order.
- Flags: `-i/--ignore-case`, `-F/--fixed-strings`, `-w/--word-regexp`, `-n/--line-number`, `-l/--files-with-matches`, `-c/--count`, `-V/--version`. Clustered short flags (`-in`) and `--` work.
- Output follows `rg` when not on a terminal: `path:line`, or `path:line:text` with `-n`. A single file argument has no path prefix.
- Exit codes as `rg`: 0 on match, 1 on none, 2 on error (including a missing path or a bad regex).
- Skips hidden files, symlinks and binary files, and follows ignore files (see below).
- No score, no limit, no ranking in this mode.
- Stdin: with no path and piped input, Ferret searches stdin. `-` means stdin explicitly and can be mixed with paths. Stdin hits have no path prefix unless several inputs are searched, then stdin is named `<stdin>`. With a terminal or `/dev/null` on stdin it searches the current directory.
- Not yet: `--json`, `-e`, `-v`, `-q`, `-A/-B/-C`, `-g`, `--hidden`, `-S/--smart-case`.

### Ranked lexical search (BM25) — Partly built

Opt-in, via a mode flag (`--lex`, `--sem` and `--hybrid`) on the default command. There are no subcommands, so plain `ferret <pattern>` is never shadowed. In a ranked mode the argument is terms, not a regex.

- **Built:** `ferret --lex <terms> [path...] [--limit N] [--scores] [--jsonl]`. Reads files through a persisted index (see below; `--no-index` reads every file instead), over the same file walk and ignore rules as plain search. The tokenizer lowercases, splits on non-alphanumerics, drops stop words and applies light suffix stemming (`mock` matches `mocked`). Because stop words are dropped, a query that is only a stop word (`in`, `is`, `to`) returns nothing. Files are ranked with BM25 (OR semantics), ties break by path, `--limit` caps files (default 100), and each file shows up to 3 `path:line:text` lines containing a query term. `--scores` prefixes each line with `[score]` (4 decimals, e.g. `[2.4131] path:line:text`). `--jsonl` prints one JSON object per excerpt line (`file`, `line`, `score`, `text`) for agents. Regex-only flags (`-i`, `-F`, `-w`, `-l`, `-c`) are a usage error with `--lex`, and `--limit`, `--scores`, `--jsonl`, `--no-index` or `--timings` without `--lex` is a usage error.
- **Built (timings):** `--timings` prints a table to stderr after the results (phase, ms, tags); stdout is unchanged. The library emits one `System.Diagnostics.Activity` per phase on the `Ferret` `ActivitySource` (no per-file spans, no OpenTelemetry packages; with no listener it costs nothing). Indexed phases: `ferret.index.load`, `ferret.enumerate` (list the files, applying ignore rules), `ferret.stamp` (one stat per file), `ferret.index.build` (read + tokenize new or changed files; tags `files.total`, `files.reused`, `files.built`), `ferret.rank`, `ferret.index.save` (`saved`, `entries`, `files.removed`), `ferret.excerpts`. With `--no-index`: `ferret.enumerate`, `ferret.read`, `ferret.rank`, `ferret.excerpts`. Semantic (`--sem`) runs emit `ferret.index.load`, `ferret.stamp`, `ferret.index.build`, `ferret.index.save`, `ferret.embed` (the query; tag `kind=query`), `ferret.rank` and `ferret.excerpts`. Tokenizing and inference interleave per chunk, so they are not activities of their own: `ferret.index.build` and `ferret.embed` carry the totals as tags `tokenize.ms`, `inference.ms` and `tokens`. The two do not sum to the phase time (file reads, chunking and pooling make up the rest).
- **Built (index commands):** `ferret --index [path...]` builds or updates the persisted index without searching, so the first query does not pay for the build (it is no faster, just moved up front). `--index --clean` deletes the index and rebuilds it from scratch; `--clean` alone deletes it. A query with no index still builds it, as before; `--no-index` is the opt-out. They take paths only, so combining them with a pattern or any search flag is a usage error.
- **Planned:** everything below that is not in the Built line: README and concept-file boosts, tag filtering, the `--status` command, an inverted index for large corpora, the table and json output.

- BM25 over a token index (`K1 = 1.2`, `B = 0.75`, non-negative IDF), deterministic ordering with path tie-break.
- OR semantics, made safe by ranking: more and rarer matched terms rise to the top.
- Boosts for query terms in path, Markdown headings and frontmatter `title`/`description`/`tags` (**Built**, named constants), plus README files and concept files (planned).
- Stop-word removal and light suffix stemming (`mock` matches `mocked`), applied identically at index and query time.
- Corpus stats come from the whole indexed corpus, so scores are comparable across filtered queries.
- Tag filtering (AND) narrows candidates first, then BM25 ranks them.
- Ferret-only flags: `--limit` (max files), `--scores` (prefix each ranked line with `[score]`, 4 decimals); json and jsonl will carry a score field. These use names ripgrep does not, and `--limit` never reuses `-m/--max-count`.
- Output formats: text, table, json, and `--jsonl` for agents.

### Ignore globs — Built, flags Planned

- Built: `.gitignore` (inside a git repository), `.ignore` and `.ferretignore`, per directory, deeper directories win, and within a directory `.ferretignore` overrides `.ignore` overrides `.gitignore`. Gitignore syntax including `!` re-includes. Ignored directories are not entered.
- Ferret adds no excludes of its own beyond `rg`'s defaults (hidden, binary, symlinks).
- The library matcher (`Ferret.Ignore`) accepts patterns and does no file I/O. For ranked search, ignored files are dropped before corpus stats are computed, so they never skew `df` or `avgdl`.
- Planned: `--exclude`, `--no-ignore` and `--no-ferretignore` for per-run control, ignore files in parent directories, global gitignore and `.git/info/exclude`.

### Library

- One ranked search engine behind both front ends.
- Pure BM25 scorer module, unit-testable without I/O.
- Ranking sits behind a seam, `Ranker = Query -> Candidate list -> ScoredDoc list`, so other rankers can be added.

### Index and cache

- **Built (lexical):** one binary file, `lexical.idx`, under `$FERRET_CACHE_DIR`, else `$XDG_CACHE_HOME/ferret`, else `~/.cache/ferret` (`%LOCALAPPDATA%\ferret` on Windows). It holds, per file, the token count, every term frequency and the terms in each boostable field, keyed by full path and stamped with size and modified time. A `--lex` run walks the files as before, reuses entries whose stamp is unchanged, re-reads new or changed files, drops entries for files gone from the searched roots, and saves only when something changed. Excerpts still read the top files. Results are identical to `--no-index` (tested exactly, including scores).
- Stats and index are a disposable cache, built on first use and rebuilt when the index version changes. A missing, corrupt or other-version file is rebuilt silently; an unwritable cache dir is ignored.
- Known limits: an edit that keeps both size and modified time is not noticed; a warm query still loads the whole index, so on very large corpora it may not beat `--no-index` (see `benchmarks/README.md`).
- Cache key includes ignore patterns and the normaliser version.
- Incremental by file hash where feasible.

### Semantic search — Built (`--sem`)

- Embedded ONNX model (`bge-small-en-v1.5`, int8), chunked by heading, then paragraph, then line or word.
- Chunk hits roll up to files with `max` (best chunk per file) over a full scan. That chunk is the excerpt. Sum-based rollups are rejected because they favour long files.
- Chunker fixes needed first: merge heading-only and front-matter chunks, or prefix chunks with a title and section breadcrumb.
- Behaves like lexical: `--sem` syncs the index on query, embedding only new or changed files, with progress on stderr. `--index --sem` pre-builds it.
- One index file per search root, `semantic/<hash-of-root>.idx` under the cache dir, with vectors in a contiguous block. Load and rewrite cost is bounded by the searched tree, and projects never overwrite each other.
- `Ferret.Semantic` is an internal project bundled into the `Ferret` package, and the model is not part of it. The CLI always includes it and ships two ways: the default downloads the int8 model and vocab on first `--sem` use into `models/<model-id>/` under the cache dir (`FERRET_MODEL_DIR` overrides, `FERRET_OFFLINE=1` disables the download, `ferret --model download` pre-fetches); a bundled build (`-p:BundleModel=true`) embeds them and works offline. Both ship as GitHub release archives per platform (`ferret-<rid>` and `ferret-bundled-<rid>`), each holding the exe and the ONNX Runtime library it loads from beside itself.
- One fixed model for now. Choosing or switching models (for example a larger or code-specialised one, with an index rebuild) might come later.

### Hybrid search — Built (opt-in)

- **Built:** `ferret --hybrid <terms> [path...] [--limit N] [--scores] [--jsonl] [--timings]`. BM25 and semantic file rankings are fused with reciprocal rank fusion (`rrf(d) = Σ weight / (k + rank)`, k = 60, weights 1:1 in the CLI; `Ranking.fuse` takes any weights). No score normalisation. Each ranker contributes its top 100 files before fusion (fixed, independent of `--limit`); the semantic arm is already rolled up to files with `max`. Ties break by ordinal path. Scores shown are RRF scores (small, about 0.03 at most).
- Mutually exclusive with `--lex` and `--sem`. `--no-index` is a usage error, as for `--sem`. `--index --hybrid` builds both indexes and `--clean --hybrid` removes both. `--timings` shows both arms' phases plus `ferret.fuse` (tags `lexical`, `semantic`, `fused`).
- Excerpts: the lexical matching lines when the file has them, otherwise the best semantic chunk's first lines.
- Opt-in. See Open questions for the eval; `--lex` and `--sem` are unchanged.

## Non-goals

- Re-ranking ck results.
- Query syntax (phrases, field filters, negation).
- Score thresholds (BM25 scores are unbounded and corpus-dependent).
- Whole-function retrieval via tree-sitter.
- ANN indexes, unless a corpus passes roughly 100k chunks.
- Pagination cursors, for now.

## Open questions

- Boost magnitudes need tuning on a real corpus.
- Stemmer conservatism versus false matches.
- Whether hybrid beats BM25 on real content. First semantic numbers on the F# docs (nDCG@10, BM25 vs `--sem`): overall 0.863 vs 0.884, keyword 0.963 vs 0.961, question 0.721 vs 0.744, exact 0.905 vs 1.000. RRF (k = 60, 1:1) on the same queries and labels: overall 0.911, keyword 0.981, question 0.791, exact 1.000, so on this set hybrid beats both single rankers. Weights 2:1 and 1:2 and k = 20 scored 0.885, 0.896 and 0.913. It is 42 queries on one corpus, so it is evidence, not proof; see `benchmarks/README.md` for how to re-run.

## Related docs

- [BM25 ranked search design](../research/bm25-ranked-search.md)
- [Chunks vs files findings](../research/chunks-vs-files-findings.md)
