module Ferret.Domain

/// How a plain-search pattern is interpreted.
type MatchOptions =
    { IgnoreCase: bool
      FixedStrings: bool
      WordRegexp: bool }

module MatchOptions =
    let defaults =
        { IgnoreCase = false
          FixedStrings = false
          WordRegexp = false }

/// A matching line. `LineNumber` is 1-based.
type Hit =
    { Path: string
      LineNumber: int
      Line: string }

/// A ranked-search query: the raw text and its distinct normalised terms.
type Query =
    { Text: string
      Terms: string list }

/// Where a boostable query-term match was found, beyond the body text.
[<RequireQualifiedAccess>]
type Field =
    | Path
    | Heading
    | Title
    | Description
    | TagList

/// A file offered to a ranker: its token count, the frequency of each query term in it and, per
/// field, which query terms appear there.
type Candidate =
    { Path: string
      Length: int
      TermFreq: Map<string, int>
      FieldTerms: Map<Field, Set<string>> }

/// A file ranked by relevance, with the lines that matched the query terms.
type ScoredDoc =
    { File: string
      Score: float
      Excerpts: Hit list }

/// The ranked files for a query, best first, and whether more files ranked than the limit allowed.
type SearchResult = { Docs: ScoredDoc[]; Truncated: bool }
