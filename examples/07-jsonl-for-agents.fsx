// Prints ranked results as JSON lines, the same shape as `ferret --jsonl`: one object per excerpt with
// file, line, score and text. A template for a retrieval step in an agent or MCP tool.
// Usage: dotnet fsi examples/07-jsonl-for-agents.fsx ["query" [folder]]
#load "_ferret.fsx"
open System.IO
open System.Text.Json
open Ferret

let args = fsi.CommandLineArgs |> Array.skip 1
let query = if args.Length > 0 then args[0] else "install the tool"
let folder = if args.Length > 1 then args[1] else Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "docs"))

let ferret = new FerretClient(FerretOptions(Roots = [| folder |]))

// A bounded result: at most 5 files, and each file carries a few excerpt lines.
for doc in ferret.Search(query, 5, Lexical).Docs do
    for line in doc.Excerpts do
        JsonSerializer.Serialize(
            {| file = doc.File
               line = line.LineNumber
               score = doc.Score
               text = line.Line |}
        )
        |> printfn "%s"
