// Ranks files by meaning. The first run downloads the model (about 32 MB) and builds the index, and reports
// progress through the Progress callback; later runs are fast.
// Usage: dotnet fsi examples/02-semantic-search.fsx ["question" [folder]]
#load "_ferret.fsx"
open System
open System.IO
open Ferret

let args = fsi.CommandLineArgs |> Array.skip 1
let question = if args.Length > 0 then args[0] else "how do I run it without internet"
let folder = if args.Length > 1 then args[1] else Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "docs"))

let options = FerretOptions(Roots = [| folder |], Progress = Action<string>(eprintfn "%s"))
let ferret = new FerretClient(options)

for doc in ferret.Search(question, 5, Semantic) do
    printfn "%.3f %s" doc.Score doc.File
    // For semantic search the excerpt is the best-matching chunk.
    doc.Excerpts |> List.tryHead |> Option.iter (fun line -> printfn "    %d: %s" line.LineNumber (line.Line.Trim()))
