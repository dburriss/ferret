module Ferret.Cli.MaintenanceCli

open Ferret
open Ferret.FileSource
open Ferret.Cli.Deps

type MaintenanceArgs =
    { Index: bool
      Clean: bool
      Paths: string list }

/// `--index` and `--clean` for one `mode` of index. Returns the exit code: 0 on success, 2 on a missing path.
/// `--clean` removes the index; with `--index` it is then rebuilt from scratch.
let private runMode (mode: SearchMode) (deps: AdapterDeps) (args: MaintenanceArgs) : int =
    let roots = if args.Paths.IsEmpty then [ "" ] else args.Paths
    let missing = roots |> List.filter (fun r -> deps.Files.Kind r = Missing)
    use ferret = AdapterDeps.client deps (roots |> List.except missing) true
    if args.Clean then ferret.Clean mode
    if not args.Index then
        0
    else
        for root in missing do
            deps.Err.WriteLine $"ferret: {root}: No such file or directory (os error 2)"
        let count = ferret.BuildIndex mode
        deps.Out.WriteLine $"indexed {count} files"
        deps.Out.Flush()
        if missing.IsEmpty then 0 else 2

/// `--index` and `--clean` for the lexical index.
let run (deps: AdapterDeps) (args: MaintenanceArgs) : int = runMode Lexical deps args

/// `--index --sem` and `--clean --sem`: the same, for the semantic indexes.
let runSemantic (deps: AdapterDeps) (args: MaintenanceArgs) : int = runMode Semantic deps args

/// Removes the semantic indexes (the model stays).
let clearSemantic (deps: AdapterDeps) =
    use ferret = AdapterDeps.client deps [] true
    ferret.Clean Semantic
