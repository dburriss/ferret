// Runs one query in all three modes and prints the top files side by side, to show where they disagree.
// Usage: dotnet fsi examples/03-compare-modes.fsx ["query" [folder]]
#load "_ferret.fsx"
open System
open System.IO
open Ferret

let args = fsi.CommandLineArgs |> Array.skip 1
let query = if args.Length > 0 then args[0] else "retries when a request fails"
let folder = if args.Length > 1 then args[1] else Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "docs"))

let options = FerretOptions(Roots = [| folder |], Progress = Action<string>(eprintfn "%s"))
let ferret = new FerretClient(options)

let top mode =
    ferret.Search(query, 5, mode) |> List.map (fun doc -> Path.GetRelativePath(folder, doc.File))

let columns = [ "lexical", top Lexical; "semantic", top Semantic; "hybrid", top Hybrid ]

for name, _ in columns do
    printf "%-44s" name
printfn ""

for rank in 0..4 do
    for _, files in columns do
        printf "%-44s" (files |> List.tryItem rank |> Option.defaultValue "")
    printfn ""
