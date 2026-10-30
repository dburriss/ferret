module Ferret.Index

open System.Collections.Generic
open Ferret.Domain

/// Everything the lexical ranker needs to know about one file, independent of any query: the
/// token count, the frequency of every term and, per field, every term that appears there.
type Entry =
    { /// `IFileSource.Stamp` when the entry was built; a different stamp means the entry is stale.
      Stamp: string
      Length: int
      TermFreq: Map<string, int>
      FieldTerms: Map<Field, Set<string>> }

/// The whole cache: entries keyed by full file path, and the version they were built with.
type Snapshot =
    { Version: int
      Entries: Dictionary<string, Entry> }

/// Port: persist and load the cache. Implementations treat the cache as disposable: `Load`
/// returns `None` for anything missing or unreadable, and `Save` may silently do nothing.
type IIndexStore =
    abstract Load: unit -> Snapshot option
    abstract Save: Snapshot -> unit

/// Bump when the entry layout changes.
[<Literal>]
let FormatVersion = 1

/// A snapshot with another version was built by different code and is ignored. Includes the
/// normaliser version, so a change to tokenizing, stop words or stemming rebuilds the cache.
let version = FormatVersion * 1000 + Text.NormaliserVersion

/// Keeps the snapshot in memory. For tests, and for callers that search repeatedly in one process.
type MemoryIndexStore() =
    let mutable snapshot: Snapshot option = None
    member _.Current = snapshot
    interface IIndexStore with
        member _.Load() = snapshot
        member _.Save s = snapshot <- Some s
