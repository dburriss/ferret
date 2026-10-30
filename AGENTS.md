# AGENTS.md

## Project

- Ferret is a library plus a .NET tool/binary that supports lexical (BM25) and semantic search.
- Design docs are in `docs/` 
- Research findings live in `research/`.

## CLI

- Basic CLI usage must be a drop-in replacement for ripgrep: same core flags, same output conventions.
- Ferret-specific features (ranking, scores, `--limit`, semantic search) are additions and must not break ripgrep-compatible behaviour.

## Workflow

- Do not make any code changes unless I say "implement".
- Docs and discussion are fine without "implement". Do not touch code.
- Do not commit unless I say so.

## Testing

- `dotnet test` runs everything in CI: unit tests, smoke (`tests/corpus/smoke`) and compat (`tests/corpus/compat`).
- Compat cases are `args` + `tree/` + `expected.txt` + `exit`, with expected output from `rg`. `dot-name` files are stored as `.name`; a `git` file adds a `.git` dir. A `gap` file records a known difference from rg: the case must still differ, so delete `gap` when fixed.
- Regenerate or verify compat expectations (needs `rg`): `dotnet fsi scripts/gen-compat-expected.fsx [--check]`.
- Relevance (not in CI): `mise run fetch:corpus -- scifact`, then `mise run eval:relevance -- scifact` (builds Release first). Data goes in `corpus/` (gitignored). SciFact takes about 80s.
- Perf (manual): `mise run fetch:perf` (multi-GB clones), then `mise run bench [-- runs]` (builds Release first; uses `hyperfine` from `mise.toml`, warm cache only). Results go in `benchmarks/results/`.
- Plan and rationale: `docs/TEST-CORPUS.md`.

### Fetching corpora

Nothing is downloaded by `dotnet test`. On a fresh checkout run `mise run bootstrap`: it restores packages and fetches SciFact and NFCorpus. `mise run bootstrap:all` also clones the multi-GB perf corpora, so only use it when asked. Fetch only what you need otherwise.

- Tasks live in `mise.toml`: `mise tasks` lists them. They wrap the `.fsx` scripts, which can still be run directly with `dotnet fsi`.
- Tools: `mise install` (gives `hyperfine`), `rg` on PATH for compat regeneration, and the .NET SDK from `global.json`.
- BEIR relevance sets: `mise run fetch:corpus [-- scifact|nfcorpus]` (no argument fetches both). Downloads a zip from the BEIR host and writes `corpus/<name>/docs/<id>.txt`, `queries.tsv` and `qrels.tsv`. SciFact is 20 MB on disk (5,183 docs, 300 queries); NFCorpus is 15 MB (3,633 docs, 323 queries). Re-running skips a dataset that is already present; delete `corpus/<name>` to refetch.
- Perf corpora: `mise run fetch:perf`. Shallow clones of the Linux kernel (`v6.12`) and `dotnet/runtime` (`v9.0.0`) into `benchmarks/data/`, several GB in total. Re-running skips existing clones. Do not run it without the user's say-so.
- Both `corpus/` and `benchmarks/data/` are gitignored. Never commit dataset files; the BEIR sets are for research use.
- Both fetch scripts need network access. The smoke and compat corpora are checked in and need none.

## Commits

- Use conventional commits (`feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`).
- Do not add co-author attribution or any "generated with" lines.

## Communication

- Be succinct.
- Disagree with me if something I say or ask for does not make sense. Say why, and propose an alternative.
