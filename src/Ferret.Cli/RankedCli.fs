module Ferret.Cli.RankedCli

open System.Diagnostics
open System.IO
open Ferret
open Ferret.Domain
open Ferret.FileSource
open Ferret.Cli.Deps

type RankedArgs =
    { Query: string
      Paths: string list
      Limit: int
      Scores: bool
      Jsonl: bool
      NoIndex: bool
      Timings: bool }

/// Collects every finished "Ferret" activity until disposed.
let private listen (finished: ResizeArray<Activity>) =
    let listener = new ActivityListener()
    listener.ShouldListenTo <- (fun source -> source.Name = Search.ActivitySourceName)
    listener.Sample <- SampleActivity<ActivityContext>(fun _ -> ActivitySamplingResult.AllData)
    listener.ActivityStopped <- (fun activity -> lock finished (fun () -> finished.Add activity))
    ActivitySource.AddActivityListener listener
    listener

/// Phase, milliseconds and tags, one row per phase in the order they ran.
let private writeTimings (w: TextWriter) (phases: Activity seq) =
    let rows =
        phases
        |> Seq.sortBy (fun a -> a.StartTimeUtc)
        |> Seq.map (fun a ->
            let tags = a.TagObjects |> Seq.map (fun kv -> $"{kv.Key}={kv.Value}") |> String.concat " "
            a.OperationName, $"{a.Duration.TotalMilliseconds:F1}", tags)
        |> List.ofSeq
    let width = rows |> List.fold (fun m (n: string, _, _) -> max m n.Length) 5
    w.WriteLine("phase".PadRight width + "  " + "ms".PadLeft 8 + "  tags")
    for (name: string), ms, tags in rows do
        w.WriteLine $"{name.PadRight width}  {ms,8}  {tags}"

/// Ferret's cache directory: where the lexical index, the semantic indexes and the model live.
let private cacheDir () = Path.GetDirectoryName(DiskIndexStore.defaultPath ())

/// Progress lines on stderr while embedding: the total up front, then every tenth for large jobs.
let private embedProgress (err: TextWriter) : int -> int -> unit =
    fun done' total ->
        if done' = 0 then err.WriteLine $"ferret: embedding {total} chunks"
        elif total >= 200 && done' % (total / 10) = 0 then err.WriteLine $"ferret: embedded {done'}/{total} chunks"

let private semanticEmbedder (deps: AdapterDeps) =
    lazy
        (let source = Semantic.ModelLocator.resolve (fun m -> deps.Err.WriteLine $"ferret: {m}") (cacheDir ())
         Semantic.ModelLocator.load source)

/// Reports missing roots, runs `search` over the rest, prints the results and, with `--timings`, the
/// phases. Returns the exit code: 0 on results, 1 on none, 2 on error.
let private runWith (deps: AdapterDeps) (args: RankedArgs) (search: string list -> ScoredDoc list) : int =
    let roots = if args.Paths.IsEmpty then [ "" ] else args.Paths
    let missing = roots |> List.filter (fun r -> deps.Files.Kind r = Missing)
    for root in missing do
        deps.Err.WriteLine $"ferret: {root}: No such file or directory (os error 2)"
    let searchRoots = roots |> List.except missing
    let finished = ResizeArray<Activity>()
    use listener = if args.Timings then listen finished else null
    let results = search searchRoots
    if args.Jsonl then
        Output.writeRankedJsonl deps.Out results
    else
        Output.writeRanked deps.Out args.Scores results
    deps.Out.Flush()
    if args.Timings then writeTimings deps.Err finished

    if not missing.IsEmpty then 2
    elif results.IsEmpty then 1
    else 0

/// Ranked lexical search (`--lex`).
let run (deps: AdapterDeps) (args: RankedArgs) : int =
    runWith deps args (fun searchRoots ->
        if args.NoIndex then
            Search.search Ranking.bm25 deps.Files searchRoots args.Query args.Limit
        else
            Search.searchIndexed deps.Index Ranking.bm25 deps.Files searchRoots args.Query args.Limit)

/// Ranked semantic search (`--sem`): each root has its own index, and results merge by score.
let runSemantic (deps: AdapterDeps) (args: RankedArgs) : int =
    let embedder = semanticEmbedder deps
    try
        runWith deps args (fun searchRoots ->
            searchRoots
            |> List.collect (fun root ->
                Semantic.SemanticSearch.search (cacheDir ()) embedder (embedProgress deps.Err) deps.Files root args.Query args.Limit)
            |> List.sortWith (fun a b ->
                match compare b.Score a.Score with
                | 0 -> System.String.CompareOrdinal(a.File, b.File)
                | c -> c)
            |> List.truncate args.Limit)
    finally
        if embedder.IsValueCreated then embedder.Value.Dispose()

/// Downloads the semantic model into the cache so `--sem` can later run offline.
let downloadModel (deps: AdapterDeps) : int =
    let dir = Semantic.ModelLocator.modelDir (cacheDir ())
    Semantic.ModelLocator.download (fun m -> deps.Err.WriteLine $"ferret: {m}") dir |> ignore
    deps.Out.WriteLine $"model ready in {dir}"
    deps.Out.Flush()
    0
