# Pre-build indexes

The first ranked search builds its index. To pay that cost ahead of time:

```sh
ferret --index .             # lexical
ferret --index --sem .       # semantic
ferret --index --hybrid .    # both
```

Library: `ferret.BuildIndex Lexical` (or `Semantic`, `Hybrid`).

## Rebuild from scratch

```sh
ferret --index --clean .
```

## Delete the indexes

```sh
ferret --clean
```

Indexes are a disposable cache. Deleting them only costs a rebuild. To move them, set `FERRET_CACHE_DIR`.

`--index` and `--clean` take paths only; combining them with a pattern or a search flag is a usage error.
