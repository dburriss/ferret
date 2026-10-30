module Ferret.Cli.RankedCli

open System.Diagnostics
open System.IO
open Ferret
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

/// Ranked lexical search (`--lex`). Returns the exit code: 0 on results, 1 on none, 2 on error.
let run (deps: AdapterDeps) (args: RankedArgs) : int =
    let roots = if args.Paths.IsEmpty then [ "" ] else args.Paths
    let missing = roots |> List.filter (fun r -> deps.Files.Kind r = Missing)
    for root in missing do
        deps.Err.WriteLine $"ferret: {root}: No such file or directory (os error 2)"
    let searchRoots = roots |> List.except missing
    let finished = ResizeArray<Activity>()
    use listener = if args.Timings then listen finished else null
    let results =
        if args.NoIndex then
            Search.search Ranking.bm25 deps.Files searchRoots args.Query args.Limit
        else
            Search.searchIndexed deps.Index Ranking.bm25 deps.Files searchRoots args.Query args.Limit
    if args.Jsonl then
        Output.writeRankedJsonl deps.Out results
    else
        Output.writeRanked deps.Out args.Scores results
    deps.Out.Flush()
    if args.Timings then writeTimings deps.Err finished
    if not missing.IsEmpty then 2
    elif results.IsEmpty then 1
    else 0
