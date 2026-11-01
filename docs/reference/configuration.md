# Environment variables and files

## Environment variables

| Variable | Effect |
|---|---|
| `FERRET_CACHE_DIR` | Directory for indexes and the downloaded model. |
| `FERRET_MODEL_DIR` | Directory holding `model_quantized.onnx` and `vocab.txt`. |
| `FERRET_OFFLINE` | `1` disables the model download. Library equivalent: `FerretOptions.Offline`. |
| `FERRET_THREADS` | Worker count for semantic work. A positive integer; default is the core count. |

## Cache directory

First match wins:

1. `$FERRET_CACHE_DIR`
2. `$XDG_CACHE_HOME/ferret`
3. `~/.cache/ferret` (`%LOCALAPPDATA%\ferret` on Windows)

| Path | Contents |
|---|---|
| `lexical.idx` | Lexical index for all searched files. |
| `semantic/<hash-of-root>.idx` | Semantic index for one search root. |
| `models/<model-id>/` | Downloaded model and vocabulary. |

The library and the `ferret` tool share this directory.

## Model lookup order

1. `FERRET_MODEL_DIR`
2. `models/<model-id>/` in the cache directory
3. Resources bundled in the build
4. Download into the cache (unless `FERRET_OFFLINE=1`)

## Ignore files

Read per directory: `.gitignore` (inside a git repository), `.ignore`, `.ferretignore`. Gitignore syntax, including `!`. Precedence within a directory: `.ferretignore` > `.ignore` > `.gitignore`. Deeper directories win. Library equivalent for extra patterns: `FerretOptions.IgnorePatterns`.
