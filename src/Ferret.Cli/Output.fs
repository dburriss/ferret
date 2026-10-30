module Ferret.Cli.Output

open System.Globalization
open System.IO
open Ferret.Domain

let private writePrefix (w: TextWriter) (withFilename: bool) (path: string) =
    if withFilename then
        w.Write path
        w.Write ':'

/// rg's non-tty text format: `path:line` or `path:line:text` with `-n`; no path for a single file.
let writeHit (w: TextWriter) (withFilename: bool) (lineNumbers: bool) (hit: Hit) =
    writePrefix w withFilename hit.Path
    if lineNumbers then
        w.Write hit.LineNumber
        w.Write ':'
    w.WriteLine hit.Line

let writePath (w: TextWriter) (path: string) = w.WriteLine path

let writeCount (w: TextWriter) (withFilename: bool) (path: string) (count: int) =
    writePrefix w withFilename path
    w.WriteLine count

/// One `path:line:text` line per excerpt, files in rank order. With `scores`, each line is
/// prefixed by `[score]` (4 decimals, invariant culture).
let writeRanked (w: TextWriter) (scores: bool) (docs: ScoredDoc list) =
    for doc in docs do
        for hit in doc.Excerpts do
            if scores then
                w.Write '['
                w.Write(doc.Score.ToString("F4", CultureInfo.InvariantCulture))
                w.Write "] "
            writeHit w true true hit
