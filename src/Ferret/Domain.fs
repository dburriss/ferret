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

/// A file offered to a ranker: its token count and the frequency of each query term in it.
type Candidate =
    { Path: string
      Length: int
      TermFreq: Map<string, int> }

/// A file ranked by relevance, with the lines that matched the query terms.
type ScoredDoc =
    { File: string
      Score: float
      Excerpts: Hit list }
