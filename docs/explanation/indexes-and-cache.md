# How indexes and the cache work

## Indexes are disposable

Ferret keeps indexes in a cache directory, not next to your files. Nothing in them is authoritative: delete them and they rebuild on the next search. That is why an unreadable, corrupt or out-of-date index is rebuilt silently and an unwritable cache directory is ignored.

## Lexical index

One file, `lexical.idx`, holds for each file its token count, term frequencies and the terms in boostable fields. Each entry is keyed by full path and stamped with size and modified time.

On each search Ferret walks the files, reuses entries whose stamp is unchanged, re-reads new or changed files, drops entries for files that are gone, and saves only if something changed. Results are identical to `--no-index`; the index saves tokenizing, not correctness.

Two consequences:

- An edit that keeps both size and modified time is not noticed.
- A warm query still loads the whole index, so on very large trees it may not beat `--no-index`.

## Semantic index

One index per search root (`semantic/<hash-of-root>.idx`), so projects never overwrite each other and load cost is bounded by the tree searched. Only new or changed files are embedded; embedding is the slow part, which is why `--index --sem` exists.

## Why `--index` is not faster

Pre-building moves the cost, it does not remove it. The first search without it does the same work. Use it when you want that cost at a time you choose, such as CI or editor start-up.

## The model

The semantic model (`bge-small-en-v1.5`, int8, about 32 MB) is fixed for now. It is downloaded on first use rather than shipped in the package, so lexical-only users never fetch it. The `Ferret` package still depends on ONNX Runtime; lexical search does not load it.
