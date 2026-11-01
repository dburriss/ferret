module Ferret.Cli.Output

open System.Globalization
open System.IO
open System.Text.Encodings.Web
open System.Text.Json
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
let writeRanked (w: TextWriter) (scores: bool) (docs: ScoredDoc seq) =
    for doc in docs do
        for hit in doc.Excerpts do
            if scores then
                w.Write '['
                w.Write(doc.Score.ToString("F4", CultureInfo.InvariantCulture))
                w.Write "] "
            writeHit w true true hit

let private jsonOptions = JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

let private jsonString (s: string) = JsonSerializer.Serialize(s, jsonOptions)

/// One JSON object per excerpt, files in rank order: `{"file","line","score","text"}`. The score is
/// always present (4 decimals, invariant culture).
let writeRankedJsonl (w: TextWriter) (docs: ScoredDoc seq) =
    for doc in docs do
        let score = doc.Score.ToString("F4", CultureInfo.InvariantCulture)
        for hit in doc.Excerpts do
            w.Write $"{{\"file\":{jsonString doc.File},\"line\":{hit.LineNumber},\"score\":{score},\"text\":{jsonString hit.Line}}}"
            w.Write '\n'
