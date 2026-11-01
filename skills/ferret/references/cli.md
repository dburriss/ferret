# Ferret command-line reference

Repo docs: `docs/reference/cli.md`, `docs/reference/configuration.md`, and the how-to guides in `docs/how-to/`.

Contents: [Synopsis and flags](#synopsis-and-flags) · [Feeding an agent or script](#feeding-an-agent-or-script) · [Indexes, model, offline](#indexes-model-offline) · [Excluding files](#excluding-files)

## Synopsis and flags

```
ferret [flags] <pattern-or-query> [path...]     # no path = current directory
```

- Plain flags: `-i`, `-F`, `-w`, `-n`, `-l`, `-c`, `-V`, `--`; short flags cluster (`-in`).
- Not supported yet: `--json`, `-e`, `-v`, `-q`, `-A/-B/-C`, `-g`, `--hidden`, `-S`. Fall back to `rg` for those.
- Ranked flags: `--limit N` (max files, default 100), `--scores` (prefix `[score]`), `--jsonl`, `--no-index` (`--lex` only), `--timings` (phase table on stderr).
- With a ranked mode the first positional is query terms, not a regex. Terms are OR'd; ranking makes that safe. Each file shows up to 3 matching lines.
- Usage errors (exit 2): `-i -F -w -l -c` with a ranked mode; ranked-only flags without a ranked mode; `--no-index` with `--sem`/`--hybrid`; combining `--lex/--sem/--hybrid`.
- Exit codes: 0 match, 1 no match, 2 error (missing path, bad regex).
- Stdin: piped input is searched when no path is given; `-` names stdin.
- Skipped: hidden files, symlinks, binary files, anything in `.gitignore` (inside a git repo), `.ignore`, `.ferretignore`.

Recipes:

```sh
ferret -n -i retry docs                          # rg-style
ferret --lex "mock time unit tests" docs --limit 5 --scores
ferret --hybrid "what happens when a request fails" docs --limit 5 --jsonl
```

## Feeding an agent or script

Use `--jsonl` with `--limit`; each line is `{"file","line","score","text"}`. Output is at most limit x 3 lines. Scores only compare within one run and differ by scale (BM25 unbounded, hybrid ~0.03 max), so rank by order and never threshold on a score. Semantic search returns every file, so lower results are weak matches: apply `--limit`.

## Indexes, model, offline

These apply to the library too (same cache and model).

- First ranked search builds an index (cached outside your tree); later runs reuse it. Pre-build at a time you choose: `ferret --index .`, `--index --sem .`, `--index --hybrid .`. Rebuild: `ferret --index --clean .`. Delete: `ferret --clean`. These take paths only, never a pattern.
- `--index` builds only what the mode needs: plain/`--lex` the lexical index, `--sem` the semantic one, `--hybrid` **both**. One `ferret --index --hybrid .` is enough; no separate lexical and semantic runs.
- Slow? Add `--timings` (stderr). Slow first run = `ferret.index.build`; slow semantic = `ferret.embed`.
- Offline: while online run `ferret --model download`. It lands in `models/<model-id>/` inside the cache dir (`$FERRET_CACHE_DIR`, else `$XDG_CACHE_HOME/ferret`, else `~/.cache/ferret`; `%LOCALAPPDATA%\ferret` on Windows). Copy that directory to the offline machine, either into its cache dir or anywhere with `FERRET_MODEL_DIR` pointing at it (must hold `model_quantized.onnx` and `vocab.txt`), then `export FERRET_OFFLINE=1`. A missing model then errors with the directory it looked in. Lookup order: `FERRET_MODEL_DIR`, cache `models/<model-id>/`, bundled build, download.
- Env vars: `FERRET_CACHE_DIR`, `FERRET_MODEL_DIR`, `FERRET_MODEL_URL` (download mirror), `FERRET_OFFLINE`, `FERRET_THREADS`.
- Indexes are disposable. A same-size, same-mtime edit is not noticed by the lexical index; clean if results look stale.

## Excluding files

Put gitignore-syntax patterns in `.ferretignore` (Ferret-only; overrides `.ignore`, which overrides `.gitignore`; deeper dirs win). Ignore files above the search root are not applied, so search from that directory. Not available: `--exclude`, `--no-ignore`, global gitignore, `.git/info/exclude`.
