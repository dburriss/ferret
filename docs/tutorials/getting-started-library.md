# Get started with the Ferret library

In this tutorial you will build a small F# console program that ranks the files in a folder against a question. You need the .NET SDK and a folder of markdown files.

## 1. Create the project

```sh
dotnet new console -lang F# -o FerretDemo
cd FerretDemo
dotnet add package Ferret
```

## 2. Search with BM25

Replace `Program.fs` with:

```fsharp
open Ferret

[<EntryPoint>]
let main _ =
    use ferret = new FerretClient(FerretOptions(Roots = [| "../docs" |]))

    for hit in ferret.Search("install the tool", 5, Lexical) do
        printfn "%.3f %s" hit.Score hit.File

    0
```

Change `../docs` to a folder of your own files, then run:

```sh
dotnet run
```

You should see up to five files, best first, each with a score, for example `0.926 ../docs/PROJECT.md`. Only files that match a search term appear in a lexical search.

## 3. Show the matching lines

Each result carries excerpts. Replace the loop:

```fsharp
    for hit in ferret.Search("install the tool", 5, Lexical) do
        printfn "%.3f %s" hit.Score hit.File
        for line in hit.Excerpts do
            printfn "  %d: %s" line.LineNumber line.Line
```

## 4. Search by meaning

Change `Lexical` to `Semantic` and run again. The first run downloads the model and builds the index, so it takes longer. Later runs are fast.

To see progress while it works, add a callback:

```fsharp
    let options = FerretOptions(Roots = [| "../docs" |], Progress = System.Action<string>(eprintfn "%s"))
    use ferret = new FerretClient(options)
```

## What you learned

You created a `FerretClient`, ran a ranked search and read its results.

Next:

- Full API: [Library reference](../reference/library.md)
- Ship without a download: [Search offline](../how-to/search-offline.md)
