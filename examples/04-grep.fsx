// Regex search like rg. Grep is lazy: files are read as the result is enumerated, so Seq.truncate stops early.
// Usage: dotnet fsi examples/04-grep.fsx ["pattern" [folder]]
#load "_ferret.fsx"
open System.IO
open Ferret
open Ferret.Domain

let args = fsi.CommandLineArgs |> Array.skip 1
let pattern = if args.Length > 0 then args[0] else "ferret --"
let folder = if args.Length > 1 then args[1] else Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "docs"))

let ferret = new FerretClient(FerretOptions(Roots = [| folder |]))

let options = { MatchOptions.defaults with IgnoreCase = true; FixedStrings = true }

for hit in ferret.Grep(pattern, options) |> Seq.truncate 10 do
    printfn "%s:%d:%s" hit.Path hit.LineNumber hit.Line
