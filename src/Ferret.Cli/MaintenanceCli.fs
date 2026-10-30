module Ferret.Cli.MaintenanceCli

open Ferret
open Ferret.FileSource
open Ferret.Cli.Deps

type MaintenanceArgs =
    { Index: bool
      Clean: bool
      Paths: string list }

/// `--index` and `--clean`. Returns the exit code: 0 on success, 2 on a missing path.
/// `--clean` removes the cache; with `--index` it is then rebuilt from scratch.
let run (deps: AdapterDeps) (args: MaintenanceArgs) : int =
    if args.Clean then deps.Index.Clear()
    if not args.Index then
        0
    else
        let roots = if args.Paths.IsEmpty then [ "" ] else args.Paths
        let missing = roots |> List.filter (fun r -> deps.Files.Kind r = Missing)
        for root in missing do
            deps.Err.WriteLine $"ferret: {root}: No such file or directory (os error 2)"
        let count = Search.buildIndex deps.Index deps.Files (roots |> List.except missing)
        deps.Out.WriteLine $"indexed {count} files"
        deps.Out.Flush()
        if missing.IsEmpty then 0 else 2
