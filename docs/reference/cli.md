# Command line reference

```
ferret [flags] <pattern-or-query> [path...]
```

Plain `ferret <pattern>` matches `rg`. Ranked modes are opt-in flags; there are no subcommands. With no path, the current directory is searched.

## Search modes

| Flag | Meaning |
|---|---|
| (none) | Regex search, unranked, in file and line order. |
| `--lex` | Ranked lexical (BM25). The first positional is terms, not a regex. |
| `--sem` | Ranked semantic. Downloads the model on first use. |
| `--hybrid` | BM25 and semantic fused with reciprocal rank fusion. |

`--lex`, `--sem` and `--hybrid` are mutually exclusive.

## Plain search flags

| Flag | Meaning |
|---|---|
| `-i`, `--ignore-case` | Case-insensitive. |
| `-F`, `--fixed-strings` | Literal pattern. |
| `-w`, `--word-regexp` | Whole words only. |
| `-n`, `--line-number` | Show line numbers. |
| `-l`, `--files-with-matches` | Print paths only. |
| `-c`, `--count` | Print match count per file. |
| `-V`, `--version` | Print the version. |
| `--` | End of flags. |

Short flags cluster (`-in`). Using `-i`, `-F`, `-w`, `-l` or `-c` with a ranked mode is a usage error.

Not supported yet: `--json`, `-e`, `-v`, `-q`, `-A/-B/-C`, `-g`, `--hidden`, `-S`.

## Ranked search flags

| Flag | Meaning | Default |
|---|---|---|
| `--limit N` | Maximum files returned. | 100 |
| `--scores` | Prefix each line with `[score]` (4 decimals). | off |
| `--jsonl` | One JSON object per line: `file`, `line`, `score`, `text`. | off |
| `--no-index` | `--lex` only: ignore the persisted index and read every file. | off |
| `--timings` | Print phase timings to stderr. | off |

Using these without a ranked mode is a usage error, as is `--no-index` with `--sem` or `--hybrid`.

Each ranked file shows up to 3 lines containing a query term.

## Index and model commands

| Command | Meaning |
|---|---|
| `--index [path...]` | Build or update the index without searching. With `--sem` the semantic index; with `--hybrid` both. |
| `--clean` | Delete the persisted indexes. |
| `--index --clean [path...]` | Delete and rebuild. |
| `--model download` | Fetch the semantic model. |

These take paths only; combining them with a pattern or search flag is a usage error.

## Other flags

| Flag | Meaning |
|---|---|
| `--debug` | Print stack traces for unexpected errors. |

## Output

Not on a terminal, output follows `rg`: `path:line`, or `path:line:text` with `-n`. A single file argument has no path prefix.

## Stdin

With no path and piped input, Ferret searches stdin. `-` names stdin explicitly and can be mixed with paths. With several inputs stdin is named `<stdin>`. A terminal or `/dev/null` on stdin searches the current directory.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | At least one match. |
| 1 | No match. |
| 2 | Error, including a missing path or a bad regex. |

## Files skipped

Hidden files, symlinks, binary files, and anything matched by `.gitignore` (in a git repository), `.ignore` or `.ferretignore`.
