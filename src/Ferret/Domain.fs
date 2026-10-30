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

/// A file ranked by relevance, with the lines that matched the query terms.
type ScoredDoc =
    { File: string
      Score: float
      Excerpts: Hit list }
