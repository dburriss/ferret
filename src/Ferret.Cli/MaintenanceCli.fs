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

let private cacheDir () = System.IO.Path.GetDirectoryName(DiskIndexStore.defaultPath ())

/// Removes the semantic indexes (the model stays).
let clearSemantic () = Semantic.VectorStore.clear (cacheDir ())

/// `--index --sem` and `--clean --sem`: the same as `run`, for the semantic indexes. Returns the exit
/// code: 0 on success, 2 on a missing path.
let runSemantic (deps: AdapterDeps) (args: MaintenanceArgs) : int =
    if args.Clean then clearSemantic ()
    if not args.Index then
        0
    else
        let roots = if args.Paths.IsEmpty then [ "" ] else args.Paths
        let missing = roots |> List.filter (fun r -> deps.Files.Kind r = Missing)
        for root in missing do
            deps.Err.WriteLine $"ferret: {root}: No such file or directory (os error 2)"
        let embedder =
            lazy
                (let source = Semantic.ModelLocator.resolve (fun m -> deps.Err.WriteLine $"ferret: {m}") (cacheDir ())
                 Semantic.ModelLocator.load source)
        let progress done' total =
            if done' = 0 then deps.Err.WriteLine $"ferret: embedding {total} chunks"
            elif total >= 200 && done' % (total / 10) = 0 then deps.Err.WriteLine $"ferret: embedded {done'}/{total} chunks"
        try
            let count =
                roots
                |> List.except missing
                |> List.sumBy (Semantic.SemanticSearch.build (cacheDir ()) embedder progress deps.Files)
            deps.Out.WriteLine $"indexed {count} files"
            deps.Out.Flush()
        finally
            if embedder.IsValueCreated then embedder.Value.Dispose()
        if missing.IsEmpty then 0 else 2
