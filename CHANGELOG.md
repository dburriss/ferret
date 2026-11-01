# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `FerretOptions.ModelUrl` and `FERRET_MODEL_URL` to download the model from a mirror (checksums still verified).
- `FerretClient.IsModelAvailable`, a non-throwing check so a host can decide when to download the model.
- `CancellationToken` overloads of `FerretClient.Search` and `BuildIndex`, `SearchAsync` and `BuildIndexAsync`, and `EnsureModelAsync(ct)`. A cancelled run throws `OperationCanceledException` and leaves the saved indexes untouched; the model download is now asynchronous I/O and cancellable.
- Ctrl+C during `--index`, `--lex`, `--sem`, `--hybrid` or `--model download` now stops cleanly (exit 130).

## [0.2.0] - 2026-11-01

### Added

- `FerretOptions.Offline` and `FerretOptions.IgnorePatterns`, library equivalents of `FERRET_OFFLINE` and the ignore rules, so a host process need not set environment variables or write ignore files.

### Changed

- GitHub release assets are now archives (`ferret-<rid>.tar.gz`, `.zip` on Windows) holding the exe and the ONNX Runtime library it loads. A `ferret-bundled-<rid>` archive per platform embeds the semantic model and works offline.

### Fixed

- `--sem` and `--hybrid` crashed with `DllNotFoundException: onnxruntime` on the release binaries, because the library was left out of the upload.

## [0.1.0] - 2026-11-01

### Added

- `ferret` command: ripgrep-compatible plain search with `.gitignore` support, line numbers, counts, fixed strings, word matching and stdin.
- Ranked search with `--lex` (BM25, with boosts for path, heading and front-matter matches), plus `--limit`, `--scores`, `--jsonl` and `--timings`.
- Semantic search with `--sem` and hybrid search with `--hybrid` (BM25 and semantic rankings fused). The model downloads on first use; `--model download` fetches it ahead of time.
- A persisted index so repeated ranked searches only read new or changed files, with `--index` and `--clean` to build or remove it.
- `Ferret` library: a `FerretClient` for setting up, indexing and searching in lexical, semantic or hybrid mode from .NET code.
