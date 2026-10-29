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

## Commits

- Use conventional commits (`feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`).
- Do not add co-author attribution or any "generated with" lines.

## Communication

- Be succinct.
- Disagree with me if something I say or ask for does not make sense. Say why, and propose an alternative.
