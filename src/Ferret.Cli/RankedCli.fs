module Ferret.Cli.RankedCli

open Ferret
open Ferret.FileSource
open Ferret.Cli.Deps

type RankedArgs =
    { Query: string
      Paths: string list
      Limit: int
      Scores: bool }

/// Ranked lexical search (`--lex`). Returns the exit code: 0 on results, 1 on none, 2 on error.
let run (deps: AdapterDeps) (args: RankedArgs) : int =
    let roots = if args.Paths.IsEmpty then [ "" ] else args.Paths
    let missing = roots |> List.filter (fun r -> deps.Files.Kind r = Missing)
    for root in missing do
        deps.Err.WriteLine $"ferret: {root}: No such file or directory (os error 2)"
    let results = Search.search Ranking.bm25 deps.Files (roots |> List.except missing) args.Query args.Limit
    Output.writeRanked deps.Out args.Scores results
    deps.Out.Flush()
    if not missing.IsEmpty then 2
    elif results.IsEmpty then 1
    else 0
