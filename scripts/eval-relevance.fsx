// Runs Ferret's BM25 over a fetched BEIR dataset and prints nDCG@10, recall@100 and MRR@10.
// Build first: dotnet build -c Release src/Ferret
// Usage: dotnet fsi scripts/eval-relevance.fsx <scifact|nfcorpus>
#r "../src/Ferret/bin/Release/net10.0/Ferret.dll"

open System
open System.IO
open Ferret
open Ferret.FileSource

let name =
    match fsi.CommandLineArgs |> Array.skip 1 |> List.ofArray with
    | [ n ] -> n
    | _ -> failwith "usage: eval-relevance.fsx <scifact|nfcorpus>"

let dir = Path.Combine(__SOURCE_DIRECTORY__, "..", "corpus", name) |> Path.GetFullPath
let docs = Path.Combine(dir, "docs")
if not (Directory.Exists docs) then failwith $"{docs} not found, run scripts/fetch-corpus.fsx {name}"

let queries =
    File.ReadAllLines(Path.Combine(dir, "queries.tsv"))
    |> Array.map (fun l -> let p = l.Split('\t', 2) in p[0], p[1])

let qrels =
    File.ReadAllLines(Path.Combine(dir, "qrels.tsv"))
    |> Array.map (fun l -> l.Split '\t')
    |> Array.groupBy (fun p -> p[0])
    |> Map.ofArray
    |> Map.map (fun _ rows -> rows |> Array.map (fun p -> p[1], int p[2]) |> Map.ofArray)

let ndcgAt k (ranked: string list) (rel: Map<string, int>) =
    let gain i id = (2.0 ** float (Map.tryFind id rel |> Option.defaultValue 0) - 1.0) / Math.Log2(float i + 2.0)
    let dcg = ranked |> List.truncate k |> List.mapi gain |> List.sum
    let ideal = rel |> Map.toList |> List.map snd |> List.sortDescending |> List.truncate k
    let idcg = ideal |> List.mapi (fun i r -> (2.0 ** float r - 1.0) / Math.Log2(float i + 2.0)) |> List.sum
    if idcg = 0.0 then 0.0 else dcg / idcg

let recallAt k (ranked: string list) (rel: Map<string, int>) =
    let hits = ranked |> List.truncate k |> List.filter rel.ContainsKey |> List.length
    float hits / float rel.Count

let mrrAt k (ranked: string list) (rel: Map<string, int>) =
    match ranked |> List.truncate k |> List.tryFindIndex rel.ContainsKey with
    | Some i -> 1.0 / float (i + 1)
    | None -> 0.0

let files = DiskFileSource() :> IFileSource
let sw = Diagnostics.Stopwatch.StartNew()

let scores =
    queries
    |> Array.choose (fun (id, text) ->
        qrels.TryFind id
        |> Option.map (fun rel ->
            let ranked =
                Search.search Ranking.bm25 files [ docs ] text 100
                |> List.map (fun d -> Path.GetFileNameWithoutExtension d.File)
            ndcgAt 10 ranked rel, recallAt 100 ranked rel, mrrAt 10 ranked rel))

let mean f = scores |> Array.averageBy f
printfn "%s: %d queries, %d docs, %.0fs" name scores.Length (Directory.GetFiles docs).Length sw.Elapsed.TotalSeconds
printfn "nDCG@10    %.3f" (mean (fun (a, _, _) -> a))
printfn "recall@100 %.3f" (mean (fun (_, b, _) -> b))
printfn "MRR@10     %.3f" (mean (fun (_, _, c) -> c))
