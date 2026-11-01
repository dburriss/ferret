# Find out where search time goes

Add `--timings` to any ranked search:

```sh
ferret --lex "query" . --timings
```

A table of phases, milliseconds and tags is printed to stderr. Stdout is unchanged.

| Symptom | Look at |
|---|---|
| Slow first run only | `ferret.index.build`: files are being read and indexed. Pre-build with [`--index`](prebuild-indexes.md). |
| Slow every run on a large tree | `ferret.index.load`. The whole index loads on each query. Try `--no-index` with `--lex` to compare. |
| Slow semantic run | `ferret.embed` and the `inference.ms` tag. |

In .NET, subscribe to the `Ferret` `ActivitySource` to receive the same phases as `Activity` instances. Phase names are listed in the [library reference](../reference/library.md#telemetry).
