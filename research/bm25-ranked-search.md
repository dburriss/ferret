# BM25 Ranked Content Search

Status: Proposed

## Summary

Replace unranked OR-matching content search with BM25 ranking over the existing token index. The CLI and the MCP server share one ranked search engine. MCP output becomes bounded (limit, excerpt caps, truncation flag). A small scoring interface leaves room for a future semantic ranker to be fused with BM25.

## Goals

- Relevant results first, for both CLI and MCP.
- Bounded, predictable MCP output size.
- One search engine behind both front ends.
- Keep the basic CLI a drop-in ripgrep replacement; ranking is opt-in.
- A seam for a second scorer, without building it now.

## Non-goals

- Building the semantic ("Ferret" embedded-ONNX) ranker or reciprocal rank fusion.
- Re-ranking results from the `ck` backend.
- Changing tag filtering semantics.

## Design

### 1. BM25 scorer

A new pure module with no I/O, unit-testable in isolation.

Constants (named, not inlined):

| Name | Value |
|------|-------|
| `K1` | 1.2 |
| `B`  | 0.75 |

**Corpus stats**

- `N`: document count
- `df(t)`: number of documents containing term `t`
- `avgdl`: average document length (in tokens)

Stats are computed from the existing token index, cached, and reused across queries. They are rebuilt whenever the index changes (cache keyed on an index version or timestamp, so invalidation is automatic and never manual).

**IDF** uses the non-negative variant:

```
idf(t) = ln(1 + (N - df + 0.5) / (df + 0.5))
```

**Term score** for document `d`:

```
score(d, q) = Σ_t idf(t) * ( tf(t,d) * (K1 + 1) ) / ( tf(t,d) + K1 * (1 - B + B * |d| / avgdl) )
```

**Ordering:** score descending, with a stable tie-break on path (ascending, ordinal). Output is deterministic for a given index and query.

### 2. Boosts

Applied after the BM25 base score. Multiplicative or additive; pick one and keep the factors as named constants so they are tunable and testable.

| Signal | Effect |
|--------|--------|
| Query term appears in path or title | boost |
| File is a README | boost |
| File is a concept file | boost |

Boosts are part of scoring, so they live in the pure scorer module, not in the callers.

### 3. Ignore globs

Nothing is excluded by default. In particular, `index.md` is no longer special-cased. Exclusion is driven by ignore globs instead.

**Library:** the search API accepts an optional list of ignore glob patterns. Candidates whose path matches any pattern are dropped in `CandidateBuilder`, before corpus stats are computed and before scoring. Doing it there, rather than in the scorer, means ignored files never inflate `df` or skew `avgdl`. Patterns are gitignore-style, matched against the path relative to the knowledge root (e.g. `**/index.md`, `drafts/**`), and `!pattern` re-includes a path. The library does not read any file itself; callers pass patterns in, which keeps it free of I/O.

**CLI:** reads a `.ferretignore` file from the knowledge root, one pattern per line, with blank lines and `#` comments ignored, and passes the patterns to the library. A generated-index user would add `**/index.md` there.

Open points:
- The index cache key must include the ignore patterns (or their hash), so changing `.ferretignore` rebuilds the stats.
- How the MCP server receives patterns (it could read the same `.ferretignore`, or take them from its configuration). Recommendation: read the same file, so CLI and MCP agree. The loading code lives in the host, not the library.
- Glob library choice: use an existing gitignore-compatible matcher rather than writing one.

### 4. Bounded MCP output

`search_knowledge` changes:

| Parameter / field | Behaviour |
|-------------------|-----------|
| `limit` (optional input) | Max files returned. Default **15**. |
| Excerpts per file | Capped at **3**. |
| Excerpt line length | Truncated, with an ellipsis marker. |
| `totalMatches` (output) | Total matching files before `limit`. |
| `truncated` (output) | `true` if results were cut by `limit`. |

Tool descriptions are updated: drop the "OR semantics" wording and describe results as ranked by relevance, best first, bounded by `limit`.

### 5. Matching

- **OR semantics are kept.** Ranking makes them safe: documents matching more or rarer terms rise to the top.
- **Stop words:** a small list (e.g. `a`, `an`, `the`, `of`, `and`, `or`, `to`, `in`, `is`, `for`, `on`). Removed at both index and query time.
- **Light stemming:** `mock` must match `mocked`. Two options:
  - Light suffix stemming (`-ed`, `-ing`, `-s`, `-es`), applied to tokens.
  - Prefix expansion of query terms against the term dictionary.

  Recommendation: suffix stemming. It keeps `df` and `tf` well-defined, whereas prefix expansion makes query terms fan out into several index terms with their own `df`. Either way, the **same normalisation function** is used at index and query time. Changing it requires an index rebuild (include the normaliser version in the index version).

### 6. Shared engine

CLI and MCP both call the same ranked content search function.

- Tag filtering is unchanged (AND). Tags filter the candidate set first; BM25 then ranks the survivors.
- Output formats (text, json, table) are preserved, with a **score** column (table) or field (json) added when ranked output is on.

**ripgrep compatibility.** The basic CLI is a drop-in replacement for ripgrep (see `AGENTS.md`), so ranking is opt-in:

- `ferret <pattern> [path...]` behaves like `rg`: regex or literal matching, unranked, file/line order, ripgrep's default output format and flags. No score, no limit, no ranking.
- Ranked search is explicitly requested, via a flag (e.g. `--rank`) or a subcommand (e.g. `ferret search`). Only then do BM25 ordering, the score column or field, and `--limit` apply. Recommendation: a subcommand, since ranked search takes terms rather than a regex, so it doesn't fit rg's contract.
- `--limit` means "max files returned" and is only valid in ranked mode. It must not reuse ripgrep's `-m/--max-count` (per-file match cap) or change its meaning.
- Flags that exist in ripgrep keep ripgrep's meaning. Ferret-only flags use names ripgrep doesn't.
- Ignore handling in plain mode follows ripgrep's conventions; `.ferretignore` is additive (see section 3). Open point: whether plain mode also honours `.gitignore` and `.ignore` as rg does. Recommendation: yes, for compatibility.
- Corpus stats come from the whole indexed corpus, not the tag-filtered subset, so scores are comparable across differently filtered queries.

### 7. Backends

- BM25 is the default ranking over the indexed backend.
- `ck` stays opt-in via `ERU_SEARCH_BACKEND=ck` or `PATH` detection. This path must not regress, but its results are not re-ranked in this change.
- `limit` and ignore globs should still apply to `ck` results where it is cheap to do so. Score is empty or absent for `ck` results.

### 8. Seam for a future semantic ranker

Ranking sits behind a small function type:

```
type Ranker = Query -> Candidate list -> ScoredDoc list
```

- `Bm25.rank` is the first implementation.
- A later semantic ranker implements the same type.
- A future fusion step takes the ranked lists from several `Ranker`s and combines them with reciprocal rank fusion: `rrf(d) = Σ_r 1 / (k + rank_r(d))`.

Only the type and the single BM25 implementation are built now. No fusion code, no ONNX dependency.

### Hybrid mode (future)

BM25 fused with the semantic ranker via RRF is Ferret's equivalent of ck's `--hybrid`, with a real BM25 keyword arm (ck's hybrid keyword arm is a regex term-coverage heuristic).

**Unit of ranking.** The two rankers work on different units. BM25 scores files over the token index. The semantic PoC (`ffind`, `bge-small-en-v1.5` int8 via ONNX) indexes chunks (by heading, then paragraph, then line/word) and returns `path`, `line_start`, `line_end`, `score`, `preview`. Consumers such as Eru return files plus matching lines, so a mapping layer is needed:

- The semantic ranker rolls chunk hits up to files: **best chunk per file**, with that chunk's lines and preview used as the excerpt. The file's rank is the rank of its best chunk.
- Both rankers then produce a ranked list of files, which RRF can fuse.
- Excerpts for a fused result come from the semantic best chunk when present, otherwise from BM25 line matches. Excerpt caps and truncation (section 4) apply to both.
- This mapping belongs in the semantic ranker, so the `Ranker` type stays file-level and BM25 is unaffected.
- Use `max` over a full scan of all chunks. A brute-force index scores every chunk anyway, so no chunk-depth parameter is needed and no relevant file can be dropped. See [chunks-vs-files-findings.md](chunks-vs-files-findings.md) for the PoC results, including why sum-based rollups are rejected.

**Other fusion points:**

- Each ranker returns its top N (about 50-100) before fusion, so a document ranked well by only one ranker isn't dropped.
- RRF uses ranks only, so BM25 scores and cosine similarity need no normalisation. k = 60 by default, with an optional per-ranker weight.
- A fused score is on a different scale (about 0.01-0.05) from a BM25 score. Show the source of the score, and ideally each ranker's rank.

**Packaging.** The semantic ranker adds roughly 80 MB per platform (34 MB model plus native ONNX Runtime), which is too heavy for the core library or a single-file tool. It ships as a separate package or an on-demand model download, which is a second reason to keep it behind the `Ranker` seam. Lexical-only builds must work without it.

**Where lexical beats semantic.** Embeddings blur exact terms ("health check redis", "retries for AWS calls"), so BM25 stays the default and hybrid is opt-in until an eval on real content shows hybrid winning.

## Prior art: ck

Source: [ck advanced usage](https://beaconbay.github.io/ck/guide/advanced-usage.html). That page doesn't document ripgrep compatibility, reranking or RRF, so those points are unverified here.

What ck does, and what we take from it:

| ck | Ferret |
|----|--------|
| `--topk N` / `--limit N` caps results | Same flag name `--limit` in ranked mode, which keeps it familiar for ck users. ck's unit is results (not stated whether files or chunks); ours is files. Document this. |
| `--scores` shows relevance scores | We show the score only in ranked mode. Consider a `--scores` flag name for parity, instead of always adding a column. |
| `--threshold` filters scores in 0.0-1.0 | Not adopted. BM25 scores are unbounded and corpus-dependent, so a fixed threshold is meaningless. Revisit when fusion produces normalised scores. |
| `--json` (array) and `--jsonl` (one object per line, recommended for agents) | Add `--jsonl` alongside our json format. Fields: `file`, `line`, `score`. |
| Exit code 0 on match, 1 on none | Matches ripgrep. Adopt in all modes. |
| Ignores are additive: defaults, `.gitignore`, `.ckignore`, `--exclude` | Confirms the section 3 design. `.ferretignore` is additive to `.gitignore`. Add `--exclude` (library: extra patterns) and `--no-ignore` / `--no-ferretignore` opt-outs. |
| `.ckignore` is gitignore-style with `!` negation | Same syntax as `.ferretignore`. |
| `.ck/` cache (`tantivy_index/`, embeddings, ANN) is safely deletable; first search builds it, later searches re-index only changed files by hash | Same contract for our stats cache: disposable, rebuilt lazily, incremental by file hash where feasible. Requires the cache to be keyed on ignore patterns and normaliser version (sections 3 and 5). |
| `--page-size` and `--cursor` for MCP/agents | Alternative to our `limit` + `truncated`. Not adopted now. Pagination is a possible later addition if agents need more than one page. |
| `--full-section` returns whole functions via tree-sitter | Out of scope. |
| Hybrid mode (`--hybrid`) and `--sem` select the ranker | Matches the section 8 seam: a future mode flag picks the `Ranker`. |

What ck's search modes are (from the ck source, `ck-cli/src/main.rs` and `ck-engine/src/lib.rs`):

| Mode | Flag | Implementation |
|------|------|----------------|
| Regex | default (no flag) | grep-style regex scan, unranked. This is the default, not BM25. |
| Lexical | `--lex` | Tantivy full-text index, ranked by Tantivy's `QueryParser` and `TopDocs`, which score with BM25. Needs an index (auto-built). Content and path fields are indexed with Tantivy's stock `TEXT` options; no custom stemming or stop words were found. Top 100 by default. |
| Semantic | `--sem` | Embeddings (default fastembed, `bge-small`). |
| Hybrid | `--hybrid` | Semantic plus a keyword arm, merged with RRF (k = 60, with a keyword weight). The keyword arm is **regex-based, not BM25**: it retries with an alternation of query terms, drops a stop-word list, and ranks lines by how many distinct terms they cover. |

Consequences:

- `ck` plain mode is unranked, but `ck --lex` is BM25. The earlier assumption that ck results are never ranked only holds for the default mode.
- ck's hybrid mode doesn't fuse BM25. Ours would, which is the section 8 design, so we aren't duplicating it.
- ck's stop-word list and term-coverage ranking in hybrid mode confirm that a stop-word list and ranked OR matching are needed once OR semantics are used.
- Lexical scores shown by ck are described as 0.0-1.0; hybrid RRF scores are about 0.01-0.05.

Implications for the `ck` backend (section 7):

- When the ck backend is selected, ranked mode should call `ck --lex` (not plain `ck`) so the results are ranked, and the "not re-ranked" statement in section 7 becomes "uses ck's own BM25 ranking".

- ck's `.ckignore` is separate from ours. When delegating to ck, pass our ignore patterns via `--exclude` and don't rely on ck reading `.ferretignore`.
- ck results carry scores in 0.0-1.0 and ours don't; never mix or compare the two in one result list.
- ck uses exit code 1 for no matches. Preserve it when proxying.

## Testing

Scorer unit tests (pure, no I/O):

- IDF is non-negative, even when `df == N`.
- Rarer terms outweigh common terms.
- Length normalisation: with equal `tf`, a shorter document scores higher.
- Tie-break on path is stable.
- Boosts: path/title, README and concept each apply as specified.
- Stop words are dropped; `mock` matches `mocked` at both index and query time.
- Stats cache is reused across queries and rebuilt when the index changes.

Integration:

- Library: paths matching ignore globs are dropped, including `!` re-includes and nested `**` patterns. Ignored files do not affect `N`, `df` or `avgdl`.
- Library: with no patterns, nothing is excluded (`index.md` is returned like any other file).
- CLI: patterns in `.ferretignore` are applied, comments and blank lines are skipped, and a missing file means no ignores. Editing the file invalidates the cached stats.
- MCP: `limit` default and override, excerpt cap, line truncation, `totalMatches`, `truncated`.
- CLI plain mode: output and flags match `rg` for a set of golden cases; no score, ranking or limit appears.
- CLI ranked mode: `--limit`, score column and field in table and json, tag AND filtering unchanged.
- CLI: `--limit` is rejected outside ranked mode, and `-m/--max-count` keeps ripgrep semantics.
- `ck` backend output is unchanged.

## Risks and open questions

- **Boost magnitudes** need tuning on a real corpus. Keep them as named constants.
- **Stemming errors** (over-stemming) can create false matches. Keep the stemmer conservative.
- **Index format change** if stop words or stemming alter stored tokens: version the index so old indexes rebuild.
- **Corpus size:** the small-corpus case makes IDF less discriminative. The non-negative IDF variant avoids negative scores but doesn't fix that.
- **Naming:** the module and type names above are placeholders until this lands next to the existing code.

## Out of scope for this change

- Semantic ranker and RRF fusion.
- Re-ranking `ck` results.
- Query syntax (phrases, field filters, negation).
