// Runs Ferret's BM25 over fetched BEIR datasets and the F# docs set and prints nDCG@10, recall@100 and MRR@10.
// Compares against benchmarks/relevance-baseline.json and exits 1 if nDCG@10 drops by more than the tolerance.
// Build first: dotnet build -c Release src/Ferret
// Usage: dotnet fsi scripts/eval-relevance.fsx [scifact|nfcorpus|fsharp-docs ...] [--update]
//   no dataset: run all. --update: write the measured scores as the new baseline.
#r "../src/Ferret/bin/Release/net10.0/Ferret.dll"

open System
open System.IO
open System.Text.Json
open Ferret
open Ferret.FileSource

let tolerance = 0.01
let root = Path.Combine(__SOURCE_DIRECTORY__, "..") |> Path.GetFullPath
let baselinePath = Path.Combine(root, "benchmarks", "relevance-baseline.json")

let args = fsi.CommandLineArgs |> Array.skip 1 |> List.ofArray
let update = List.contains "--update" args

let names =
    match args |> List.filter (fun a -> a <> "--update") with
    | [] -> [ "scifact"; "nfcorpus"; "fsharp-docs" ]
    | ns -> ns

type Scores = { ndcg: float; recall: float; mrr: float }

let readBaseline () =
    if File.Exists baselinePath then
        use doc = JsonDocument.Parse(File.ReadAllText baselinePath)
        [ for p in doc.RootElement.EnumerateObject() ->
            p.Name,
            { ndcg = p.Value.GetProperty("ndcg10").GetDouble()
              recall = p.Value.GetProperty("recall100").GetDouble()
              mrr = p.Value.GetProperty("mrr10").GetDouble() } ]
        |> Map.ofList
    else
        Map.empty

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

type Dataset =
    { docs: string
      queries: (string * string * string)[] // id, text, kind
      qrels: Map<string, Map<string, int>>
      docId: string -> string }

let loadBeir name =
    let dir = Path.Combine(root, "corpus", name)
    let docs = Path.Combine(dir, "docs")
    if not (Directory.Exists docs) then failwith $"{docs} not found, run scripts/fetch-corpus.fsx {name}"

    { docs = docs
      queries =
        File.ReadAllLines(Path.Combine(dir, "queries.tsv"))
        |> Array.map (fun l -> let p = l.Split('\t', 2) in p[0], p[1], "")
      qrels =
        File.ReadAllLines(Path.Combine(dir, "qrels.tsv"))
        |> Array.map (fun l -> l.Split '\t')
        |> Array.groupBy (fun p -> p[0])
        |> Map.ofArray
        |> Map.map (fun _ rows -> rows |> Array.map (fun p -> p[1], int p[2]) |> Map.ofArray)
      docId = Path.GetFileNameWithoutExtension }

// Hand-written queries over the F# docs; labels are paths relative to docs/fsharp, graded 1-2.
let loadFsharpDocs () =
    let docs = Path.Combine(root, "corpus", "fsharp-docs", "repo", "docs", "fsharp")
    if not (Directory.Exists docs) then failwith $"{docs} not found, run scripts/fetch-fsharp-docs.fsx"
    let qs = Path.Combine(root, "tests", "corpus", "relevance", "fsharp-docs", "queries.json")
    use doc = JsonDocument.Parse(File.ReadAllText qs)
    let items = [| for q in doc.RootElement.EnumerateArray() -> q |]
    let rel (q: JsonElement) =
        [ for p in q.GetProperty("relevant").EnumerateObject() -> p.Name, p.Value.GetInt32() ] |> Map.ofList
    for q in items do
        for path in (rel q).Keys do
            if not (File.Exists(Path.Combine(docs, path))) then failwith $"label {path} not found in corpus"
    { docs = docs
      queries = items |> Array.map (fun q -> q.GetProperty("id").GetString(), q.GetProperty("query").GetString(), q.GetProperty("type").GetString())
      qrels = items |> Array.map (fun q -> q.GetProperty("id").GetString(), rel q) |> Map.ofArray
      docId = fun f -> Path.GetRelativePath(docs, f).Replace('\\', '/') }

let evaluate name =
    let ds = if name = "fsharp-docs" then loadFsharpDocs () else loadBeir name
    let sw = Diagnostics.Stopwatch.StartNew()

    let scored =
        ds.queries
        |> Array.choose (fun (id, text, kind) ->
            ds.qrels.TryFind id
            |> Option.map (fun rel ->
                let ranked =
                    Search.search Ranking.bm25 files [ ds.docs ] text 100 |> List.map (fun d -> ds.docId d.File)
                kind, (ndcgAt 10 ranked rel, recallAt 100 ranked rel, mrrAt 10 ranked rel)))

    let scores = scored |> Array.map snd
    let mean f = scores |> Array.averageBy f
    printfn "%s: %d queries, %d docs, %.0fs" name scores.Length (Directory.GetFiles(ds.docs, "*", SearchOption.AllDirectories).Length) sw.Elapsed.TotalSeconds
    let kinds =
        [ for kind, rows in scored |> Array.groupBy fst |> Array.filter (fst >> (<>) "") ->
            kind, rows.Length, rows |> Array.averageBy (fun (_, (a, _, _)) -> a), rows |> Array.averageBy (fun (_, (_, _, c)) -> c) ]
    for kind, n, ndcg, mrr in kinds do
        printfn "  %-8s n=%-3d nDCG@10 %.3f  MRR@10 %.3f" kind n ndcg mrr
    { ndcg = mean (fun (a, _, _) -> a); recall = mean (fun (_, b, _) -> b); mrr = mean (fun (_, _, c) -> c) }, scores.Length, kinds

let baseline = readBaseline ()

let results =
    names
    |> List.map (fun n ->
        let s, count, kinds = evaluate n
        let delta (cur: float) (prev: float option) =
            match prev with
            | Some p -> sprintf " (%+.3f)" (cur - p)
            | None -> ""
        let b = baseline.TryFind n
        printfn "nDCG@10    %.3f%s" s.ndcg (delta s.ndcg (b |> Option.map _.ndcg))
        printfn "recall@100 %.3f%s" s.recall (delta s.recall (b |> Option.map _.recall))
        printfn "MRR@10     %.3f%s" s.mrr (delta s.mrr (b |> Option.map _.mrr))
        n, s, count, kinds)

// Every run leaves a timestamped record, so scores can be compared over time.
let git (args: string) =
    let psi = Diagnostics.ProcessStartInfo("git", args, WorkingDirectory = root, RedirectStandardOutput = true)
    use p = Diagnostics.Process.Start psi
    let out = p.StandardOutput.ReadToEnd().Trim()
    p.WaitForExit()
    out

let logRun () =
    let sha = git "rev-parse --short HEAD"
    let dirty = git "status --porcelain -- src scripts" <> ""
    let now = DateTimeOffset.UtcNow
    let dir = Path.Combine(root, "benchmarks", "results")
    Directory.CreateDirectory dir |> ignore
    let stamp = now.ToString "yyyy-MM-dd-HHmmss"
    let path = Path.Combine(dir, $"relevance-{stamp}-{sha}.json")
    use stream = File.Create path
    use w = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
    w.WriteStartObject()
    w.WriteString("timestamp", now.ToString("o"))
    w.WriteString("commit", sha)
    w.WriteBoolean("dirty", dirty)
    w.WriteStartObject "datasets"
    for n, s, count, kinds in results do
        w.WriteStartObject n
        w.WriteNumber("queries", count)
        w.WriteNumber("ndcg10", Math.Round(s.ndcg, 3))
        w.WriteNumber("recall100", Math.Round(s.recall, 3))
        w.WriteNumber("mrr10", Math.Round(s.mrr, 3))
        if not kinds.IsEmpty then
            w.WriteStartObject "byType"
            for kind, kn, ndcg, mrr in kinds do
                w.WriteStartObject kind
                w.WriteNumber("queries", kn)
                w.WriteNumber("ndcg10", Math.Round(ndcg, 3))
                w.WriteNumber("mrr10", Math.Round(mrr, 3))
                w.WriteEndObject()
            w.WriteEndObject()
        w.WriteEndObject()
    w.WriteEndObject()
    w.WriteEndObject()
    w.Flush()
    printfn "run logged to %s" (Path.GetRelativePath(root, path))

logRun ()

if update then
    let merged = results |> List.fold (fun m (n, s, _, _) -> Map.add n s m) baseline
    use stream = File.Create baselinePath
    use w = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
    w.WriteStartObject()
    for KeyValue(n, s) in merged do
        w.WriteStartObject n
        w.WriteNumber("ndcg10", Math.Round(s.ndcg, 3))
        w.WriteNumber("recall100", Math.Round(s.recall, 3))
        w.WriteNumber("mrr10", Math.Round(s.mrr, 3))
        w.WriteEndObject()
    w.WriteEndObject()
    w.Flush()
    printfn "baseline written to %s" baselinePath
else
    let regressions =
        results
        |> List.choose (fun (n, s, _, _) ->
            match baseline.TryFind n with
            | Some b when s.ndcg < b.ndcg - tolerance -> Some $"{n}: nDCG@10 {s.ndcg:F3} < baseline {b.ndcg:F3} - {tolerance}"
            | Some _ -> None
            | None ->
                printfn "%s: no baseline, run with --update to record one" n
                None)
    if not regressions.IsEmpty then
        regressions |> List.iter (eprintfn "REGRESSION %s")
        exit 1
