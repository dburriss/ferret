# Smoke corpus

Small corpus for fast ranking checks. Runs in CI.

- `ddd/`: 10 short DDD notes.
- `noise/`: 20 extracts of *Moby Dick* (public domain, Project Gutenberg), used as distractors.
- `queries.json`: labelled queries. `kind` is `semantic` (not asserted for BM25), `exact` (all `relevant` files must be in the top 10) or `weighted` (`top1` must rank first).

Labels were written from reading the notes, before running Ferret.
