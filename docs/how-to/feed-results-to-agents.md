# Feed ranked results to an agent or script

Use `--jsonl` for one JSON object per result line, and `--limit` to bound the output:

```sh
ferret --hybrid "retries for aws calls" docs --limit 5 --jsonl
```

Each line looks like:

```json
{"file":"docs/retries.md","line":12,"score":0.0328,"text":"Retry failed AWS calls..."}
```

Notes:

- Each file shows up to 3 lines, so output is bounded by `--limit` × 3 lines.
- Scores compare within one run only. Do not set a threshold on them.
- In a .NET program, call the library instead: `FerretClient.Search` returns `ScoredDoc` values directly.

See [Command line reference](../reference/cli.md) for the exit codes.
