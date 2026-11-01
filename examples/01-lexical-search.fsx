// Ranks the files in a folder against a query with BM25 and prints the matching lines.
// Usage: dotnet fsi examples/01-lexical-search.fsx ["query" [folder]]
#load "_ferret.fsx"
open System.IO
open Ferret

let args = fsi.CommandLineArgs |> Array.skip 1
let query = if args.Length > 0 then args[0] else "install the tool"
let folder = if args.Length > 1 then args[1] else Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "docs"))

let ferret = new FerretClient(FerretOptions(Roots = [| folder |]))
// In a program, write `use ferret = ...` so the client is disposed.

for doc in ferret.Search(query, 5, Lexical).Docs do
    printfn "%.3f %s" doc.Score doc.File
    for line in doc.Excerpts do
        printfn "    %d: %s" line.LineNumber (line.Line.Trim())
