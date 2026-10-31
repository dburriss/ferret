// Runs Ferret's BM25 over fetched BEIR datasets and the F# docs set and prints nDCG@10, recall@100 and MRR@10.
// Compares against benchmarks/relevance-baseline.json and exits 1 if nDCG@10 drops by more than the tolerance.
// Build first: dotnet build -c Release src/Ferret.Semantic
// Usage: dotnet fsi scripts/eval-relevance.fsx [scifact|nfcorpus|fsharp-docs ...] [--update] [--no-parity] [--ranker lex|sem|hybrid] [--rrf-k N] [--rrf-w LEX:SEM]
//   no dataset: run all. --update: write the measured scores as the new baseline.
//   --ranker sem: semantic search instead of BM25 (downloads the model on first use and embeds every document, so
//   fsharp-docs takes about a minute and SciFact/NFCorpus much longer). It has its own baseline file
//   (benchmarks/relevance-baseline-sem.json), skips the parity check and is not logged.
//   --ranker hybrid: reciprocal rank fusion of both (HybridSearch, top 100 of each). Prints a table of BM25, semantic and
//   RRF variants (k=60 with weights 1:1, 2:1, 1:2, plus k=20 as a sensitivity point) per dataset and per type. The
//   scored variant (baseline, gate) is k=60 1:1 unless --rrf-k / --rrf-w pick another. Own baseline file
//   (benchmarks/relevance-baseline-hybrid.json); not logged. Never writes a baseline without --update.
// Every query also runs through the persisted-index path (Search.searchIndexed, in-memory store) and must give
// exactly the same files, order, scores and excerpts as the per-run path; any difference exits 1.
// --no-parity skips that and times only the per-run path. A saved and reloaded index must equal the in-memory one.
#r "../src/Ferret.Core/bin/Release/net10.0/Ferret.Core.dll"
#r "../src/Ferret.Semantic/bin/Release/net10.0/Microsoft.ML.OnnxRuntime.dll"
#r "../src/Ferret.Semantic/bin/Release/net10.0/Microsoft.ML.Tokenizers.dll"
#r "../src/Ferret.Semantic/bin/Release/net10.0/Ferret.Semantic.dll"

open System
open System.IO
open System.Text.Json
open Ferret
open Ferret.FileSource

let tolerance = 0.01
let root = Path.Combine(__SOURCE_DIRECTORY__, "..") |> Path.GetFullPath

let args = fsi.CommandLineArgs |> Array.skip 1 |> List.ofArray
let update = List.contains "--update" args

let option (name: string) =
    match args |> List.tryFindIndex ((=) name) with
    | Some i when i + 1 < args.Length -> Some args[i + 1]
    | Some _ -> failwith $"{name} takes a value"
    | None -> None

let ranker =
    match option "--ranker" with
    | Some "sem" -> "sem"
    | Some "hybrid" -> "hybrid"
    | Some "lex" | None -> "lex"
    | Some _ -> failwith "--ranker takes lex, sem or hybrid"
let semantic = ranker = "sem"
let hybrid = ranker = "hybrid"

// The variant a hybrid run scores and gates on.
let primaryVariant =
    let k = option "--rrf-k" |> Option.map int |> Option.defaultValue 60
    match option "--rrf-w" with
    | Some w ->
        let p = w.Split ':'
        k, float p[0], float p[1]
    | None -> k, 1.0, 1.0

let baselinePath =
    let file =
        match ranker with
        | "sem" -> "relevance-baseline-sem.json"
        | "hybrid" -> "relevance-baseline-hybrid.json"
        | _ -> "relevance-baseline.json"
    Path.Combine(root, "benchmarks", file)

// Semantic and hybrid results come from an index that is built once, so there is no per-run path to compare with.
let parity = ranker = "lex" && not (List.contains "--no-parity" args)

let flagValues = [ "--ranker"; "--rrf-k"; "--rrf-w" ] |> List.choose option
let requested =
    args
    |> List.filter (fun a -> a <> "--update" && a <> "--no-parity")
    |> List.filter (fun a -> not (List.contains a [ "--ranker"; "--rrf-k"; "--rrf-w" ]) && not (List.contains a flagValues))
let fullRun = requested.IsEmpty
let names = if fullRun then [ "scifact"; "nfcorpus"; "fsharp-docs" ] else requested

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

/// Parity failures across all datasets, as readable lines.
let parityFailures = ResizeArray<string>()

let timed (watch: Diagnostics.Stopwatch) f =
    watch.Start()
    let result = f ()
    watch.Stop()
    result

let cacheDir = Path.Combine(Path.GetTempPath(), "ferret-eval-sem")

// Created once, and only for --ranker sem.
let embedder =
    lazy
        (Semantic.ModelLocator.resolve (eprintfn "%s") (Path.GetDirectoryName(DiskIndexStore.defaultPath ()))
         |> Semantic.ModelLocator.load)

let ignore2 (_: int) (_: int) = ()

// Hybrid arms per query text, so every RRF variant is computed without searching again.
let arms = Collections.Generic.Dictionary<string, Domain.ScoredDoc list * Domain.ScoredDoc list>()

let fused (k, wl, ws) (lex, sem) = Semantic.HybridSearch.fuse k wl ws 100 lex sem

let rank (store: Index.MemoryIndexStore) (text: string) docs =
    if hybrid then
        let lex = Search.search Ranking.bm25 files [ docs ] text 100
        let sem = Semantic.SemanticSearch.search cacheDir embedder ignore2 files docs text 100
        arms[text] <- (lex, sem)
        fused primaryVariant (lex, sem)
    elif semantic then
        Semantic.SemanticSearch.search cacheDir embedder ignore2 files docs text 100
    else
        Search.search Ranking.bm25 files [ docs ] text 100

let evaluate name =
    let ds = if name = "fsharp-docs" then loadFsharpDocs () else loadBeir name
    let sw = Diagnostics.Stopwatch.StartNew()
    let perRun = Diagnostics.Stopwatch()
    let viaIndex = Diagnostics.Stopwatch()
    let store = Index.MemoryIndexStore()
    let mismatches = ref 0

    let scored =
        ds.queries
        |> Array.choose (fun (id, text, kind) ->
            ds.qrels.TryFind id
            |> Option.map (fun rel ->
                let results = timed perRun (fun () -> rank store text ds.docs)
                if parity then
                    let fromIndex =
                        timed viaIndex (fun () -> Search.searchIndexed store Ranking.bm25 files [ ds.docs ] text 100)
                    if fromIndex <> results then
                        mismatches.Value <- mismatches.Value + 1
                        if mismatches.Value <= 5 then
                            parityFailures.Add $"{name} query {id}: indexed results differ from per-run results"
                let ranked = results |> List.map (fun d -> ds.docId d.File)
                kind, (ndcgAt 10 ranked rel, recallAt 100 ranked rel, mrrAt 10 ranked rel)))

    if parity then
        // The disk format must preserve every entry.
        let path = Path.Combine(Path.GetTempPath(), $"ferret-eval-{Guid.NewGuid():N}.idx")
        try
            let disk = DiskIndexStore.DiskIndexStore path :> Index.IIndexStore
            disk.Save store.Current.Value
            match disk.Load() with
            | Some loaded when
                loaded.Entries.Count = store.Current.Value.Entries.Count
                && loaded.Entries |> Seq.forall (fun kv -> store.Current.Value.Entries[kv.Key] = kv.Value)
                -> ()
            | _ -> parityFailures.Add $"{name}: index saved to disk and reloaded differs from the in-memory index"
        finally
            File.Delete path

    let scores = scored |> Array.map snd
    let mean f = scores |> Array.averageBy f
    printfn "%s: %d queries, %d docs, %.0fs" name scores.Length (Directory.GetFiles(ds.docs, "*", SearchOption.AllDirectories).Length) sw.Elapsed.TotalSeconds
    if parity then
        printfn "  per-run %.1fs, indexed %.1fs (includes the first build), %d of %d queries differ" perRun.Elapsed.TotalSeconds viaIndex.Elapsed.TotalSeconds mismatches.Value scores.Length
    let kinds =
        [ for kind, rows in scored |> Array.groupBy fst |> Array.filter (fst >> (<>) "") ->
            kind, rows.Length, rows |> Array.averageBy (fun (_, (a, _, _)) -> a), rows |> Array.averageBy (fun (_, (_, _, c)) -> c) ]
    for kind, n, ndcg, mrr in kinds do
        printfn "  %-8s n=%-3d nDCG@10 %.3f  MRR@10 %.3f" kind n ndcg mrr
    if hybrid then
        // One row per ranker or variant: overall and per type nDCG@10 from the cached arms.
        let variants =
            [ "bm25", fst
              "sem", snd
              "rrf k=60 1:1", fused (60, 1.0, 1.0)
              "rrf k=60 2:1", fused (60, 2.0, 1.0)
              "rrf k=60 1:2", fused (60, 1.0, 2.0)
              "rrf k=20 1:1", fused (20, 1.0, 1.0) ]
        let kindNames = kinds |> List.map (fun (k, _, _, _) -> k)
        printfn "  %-14s %8s %s" "variant" "nDCG@10" (kindNames |> List.map (sprintf "%9s") |> String.concat "")
        for label, pick in variants do
            let rows =
                ds.queries
                |> Array.choose (fun (id, text, kind) ->
                    ds.qrels.TryFind id
                    |> Option.map (fun rel ->
                        let ranked = pick arms[text] |> List.map (fun d -> ds.docId d.File)
                        kind, ndcgAt 10 ranked rel))
            let byKind = kindNames |> List.map (fun k -> rows |> Array.filter (fst >> (=) k) |> Array.averageBy snd)
            printfn "  %-14s %8.3f %s" label (rows |> Array.averageBy snd) (byKind |> List.map (sprintf "%9.3f") |> String.concat "")
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

// A full run is logged once per distinct set of score-affecting inputs, so scores can be compared over time.
let git (args: string) =
    let psi = Diagnostics.ProcessStartInfo("git", args, WorkingDirectory = root, RedirectStandardOutput = true)
    use p = Diagnostics.Process.Start psi
    let out = p.StandardOutput.ReadToEnd().Trim()
    p.WaitForExit()
    out

// Hash of everything that can change a score: the library source, this script, the labels and the dataset pins.
let inputHash () =
    let inBuildDir (f: string) =
        let rel = Path.GetRelativePath(root, f).Replace('\\', '/')
        rel.Contains "/bin/" || rel.Contains "/obj/"
    let under (dir: string) (pattern: string) =
        Directory.GetFiles(Path.Combine(root, dir), pattern, SearchOption.AllDirectories)
    let inputs =
        [ yield! under "src/Ferret.Core" "*.fs"
          yield! under "src/Ferret.Core" "*.fsproj"
          yield! under "tests/corpus/relevance" "*"
          for f in [ "eval-relevance.fsx"; "fetch-corpus.fsx"; "fetch-fsharp-docs.fsx" ] ->
              Path.Combine(root, "scripts", f) ]
        |> List.filter (inBuildDir >> not)
        |> List.map (fun f -> Path.GetRelativePath(root, f).Replace('\\', '/'), f)
        |> List.sortBy fst
    let content (rel: string, f: string) = rel + "\n" + File.ReadAllText(f).Replace("\r\n", "\n") + "\n"
    let bytes = inputs |> List.map content |> String.concat "" |> Text.Encoding.UTF8.GetBytes
    (Convert.ToHexString(Security.Cryptography.SHA256.HashData bytes)).Substring(0, 8).ToLowerInvariant()

let writeLog (path: string) (hash: string) =
    let sha = git "rev-parse --short HEAD"
    let dirty = git "status --porcelain -- src scripts tests" <> ""
    let now = DateTimeOffset.UtcNow
    use stream = File.Create path
    use w = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
    w.WriteStartObject()
    w.WriteString("timestamp", now.ToString("o"))
    w.WriteString("inputHash", hash)
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

let logRun () =
    let hash = inputHash ()
    let dir = Path.Combine(root, "benchmarks", "results")
    Directory.CreateDirectory dir |> ignore
    let path = Path.Combine(dir, $"relevance-{hash}.json")
    let rel = Path.GetRelativePath(root, path)
    if File.Exists path then
        printfn "run not logged: inputs %s already have %s" hash rel
    else
        writeLog path hash
        printfn "run logged to %s" rel

if parityFailures.Count > 0 then
    parityFailures |> Seq.iter (eprintfn "PARITY %s")
    exit 1

// Partial runs are not logged: they would block the full run for the same inputs. Neither are semantic runs.
if fullRun && ranker = "lex" then logRun ()

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
