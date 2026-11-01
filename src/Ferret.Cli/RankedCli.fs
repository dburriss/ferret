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

/// Reports missing roots, runs `search` over the rest, prints the results and, with `--timings`, the
/// phases. Returns the exit code: 0 on results, 1 on none, 2 on error.
let private runWith (deps: AdapterDeps) (args: RankedArgs) (search: string list -> SearchResult) : int =
    let roots = if args.Paths.IsEmpty then [ "" ] else args.Paths
    let missing = roots |> List.filter (fun r -> deps.Files.Kind r = Missing)
    for root in missing do
        deps.Err.WriteLine $"ferret: {root}: No such file or directory (os error 2)"
    let searchRoots = roots |> List.except missing
    let finished = ResizeArray<Activity>()
    use listener = if args.Timings then listen finished else null
    let results = (search searchRoots).Docs
    if args.Jsonl then
        Output.writeRankedJsonl deps.Out results
    else
        Output.writeRanked deps.Out args.Scores results
    deps.Out.Flush()
    if args.Timings then writeTimings deps.Err finished

    if not missing.IsEmpty then 2
    elif results.Length = 0 then 1
    else 0

/// Runs a ranked search in `mode` through the facade.
let private runMode (deps: AdapterDeps) (args: RankedArgs) (mode: SearchMode) : int =
    runWith deps args (fun searchRoots ->
        use ferret = AdapterDeps.client deps searchRoots (not args.NoIndex)
        ferret.Search(args.Query, args.Limit, mode, AdapterDeps.cancelToken ()))

/// Ranked lexical search (`--lex`).
let run (deps: AdapterDeps) (args: RankedArgs) : int = runMode deps args Lexical

/// Ranked semantic search (`--sem`).
let runSemantic (deps: AdapterDeps) (args: RankedArgs) : int = runMode deps args Semantic

/// Ranked hybrid search (`--hybrid`): the BM25 and semantic top files fused with reciprocal rank fusion.
let runHybrid (deps: AdapterDeps) (args: RankedArgs) : int = runMode deps args Hybrid

/// Downloads the semantic model into the cache so `--sem` can later run offline.
let downloadModel (deps: AdapterDeps) : int =
    use ferret = AdapterDeps.client deps [] true
    let dir = ferret.EnsureModelAsync(AdapterDeps.cancelToken ()).GetAwaiter().GetResult()
    deps.Out.WriteLine $"model ready in {dir}"
    deps.Out.Flush()
    0
