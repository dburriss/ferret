module Ferret.Grep

open System.Text.RegularExpressions
open Ferret.Domain

/// Compile a pattern to a regex. Raises `ArgumentException` for an invalid regex.
let compile (options: MatchOptions) (pattern: string) : Regex =
    let body = if options.FixedStrings then Regex.Escape pattern else pattern
    let body = if options.WordRegexp then $@"(?<!\w)(?:{body})(?!\w)" else body
    let flags =
        RegexOptions.CultureInvariant
        ||| (if options.IgnoreCase then RegexOptions.IgnoreCase else RegexOptions.None)
    Regex(body, flags)

/// Matching lines as (1-based line number, line), in line order.
let matchLines (regex: Regex) (lines: seq<string>) : seq<int * string> =
    lines
    |> Seq.indexed
    |> Seq.filter (fun (_, line) -> regex.IsMatch line)
    |> Seq.map (fun (i, line) -> i + 1, line)
