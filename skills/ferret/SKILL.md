---
name: ferret
description: How to use Ferret, the ripgrep-compatible search tool with ranked lexical (BM25), semantic and hybrid search, both the `ferret` CLI and the `Ferret` .NET/F# library (FerretClient). Use this whenever the user runs or mentions `ferret`, `ferret --lex/--sem/--hybrid`, `--jsonl`, `.ferretignore`, FerretClient, Ferret.Core or Ferret.Semantic, or wants to search docs/notes/code by keyword relevance or meaning, rank files for an agent or script, pre-build search indexes, run semantic search offline, or embed ranked search in an F#/C# program, even if they don't name Ferret explicitly but the project has it installed.
---

# Ferret

Ferret searches files by regex, ranked keywords (BM25), meaning (semantic), or both (hybrid). `ferret <pattern>` is a drop-in for `rg`; ranked modes are opt-in flags. There are no subcommands.

If the repo has Ferret's docs (`docs/index.md`, with `reference/cli.md`, `reference/library.md`, `reference/configuration.md`), prefer them over this skill when they disagree: they are the source of truth and this skill may lag.

## Where to read next

Read the reference that matches the task before answering; each holds details this page leaves out.

- Running the `ferret` command (flags, JSONL for agents, indexes, offline and model setup, `.ferretignore`, timings): `references/cli.md`.
- Writing .NET/F# code with `FerretClient` (API, options, types, telemetry): `references/library.md`.
- Offline, model, cache and index questions apply to both, and live in `references/cli.md`.

## Install

Two packages, so pick by what the user is doing:

- **Ferret.Tool**, the `ferret` command: `dotnet tool install --global Ferret.Tool` (check with `ferret --version`). Needs the .NET SDK.
- **Ferret**, the library for .NET code: `dotnet add package Ferret`. Lexical-only consumers can reference `Ferret.Core` instead to skip ONNX Runtime and the model.

A project that calls `FerretClient` does not need the tool, and the tool does not need the library. They share the same cache directory.

## Pick a mode

| Need | Use |
|---|---|
| Exact string, identifier, regex, every match | `ferret PATTERN` (plain, like rg) |
| Keywords, best files first, no model | `--lex` |
| A question or vague recall, paraphrases | `--sem` (downloads ~32 MB model on first use) |
| Unsure, or natural-language questions | `--hybrid` (best measured quality; needs model + semantic index) |

Why it matters: lexical only matches words that are present; semantic blurs exact terms (a "mock time" query can pull in "timeouts"); hybrid fuses both rankings with RRF. Plain mode never ranks, so for a research question over many docs prefer a ranked mode, which returns the best file first and bounds output.

Library equivalents are `Lexical`, `Semantic` and `Hybrid` (`SearchMode`).

## The semantic model

Only `--sem` and `--hybrid` (library: `Semantic`, `Hybrid`) need it; lexical never does. It is `bge-small-en-v1.5`, int8, about 32 MB, and is not shipped in the package.

- **When it downloads:** on first semantic or hybrid use, or ahead of time with `ferret --model download` (library: `EnsureModel()`).
- **Where it lands:** `models/<model-id>/` inside the cache dir: `$FERRET_CACHE_DIR`, else `$XDG_CACHE_HOME/ferret`, else `~/.cache/ferret` (`%LOCALAPPDATA%\ferret` on Windows). The CLI and library share it, so one download serves both.
- **Use your own copy:** set `FERRET_MODEL_DIR` (library: `FerretOptions.ModelDir`) to a directory holding `model_quantized.onnx` and `vocab.txt`.
- **No network:** set `FERRET_OFFLINE=1`. A missing model is then an error naming the directory it looked in (library: `ModelUnavailable`). Full offline steps are in `references/cli.md`.

## Mistakes to avoid

These cause the most wrong answers, so they are repeated here. Details are in `references/cli.md`.

- `-i -F -w -l -c` with `--lex/--sem/--hybrid` is a usage error (exit 2). A request for "ranked, case-insensitive, whole-word" has no single command: say so and offer options, such as dropping the flags or filtering the ranked files with a plain `ferret -i -w` afterwards.
- Never threshold on a score. Scores compare only within one run, and scales differ by mode (BM25 unbounded, hybrid about 0.03 at most). Use rank order and `--limit`.
- `.ferretignore` must be inside the search root. Ignore files above it are not applied, and there is no `--exclude` flag.
- `--index --hybrid` builds both indexes; one command is enough.
- Plain search is case-sensitive like rg; add `-i`.
- Hybrid and semantic cost a model download plus a semantic index per search root; pre-build in CI or at editor start.
