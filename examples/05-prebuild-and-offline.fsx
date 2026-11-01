// Fetches the model and builds the hybrid index ahead of time, then times a search to show the warm cost.
// After this, searches work offline (set FERRET_OFFLINE=1 to forbid downloads).
// Usage: dotnet fsi examples/05-prebuild-and-offline.fsx [folder]
#load "_ferret.fsx"
open System
open System.Diagnostics
open System.IO
open Ferret

let args = fsi.CommandLineArgs |> Array.skip 1
let folder = if args.Length > 0 then args[0] else Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "docs"))

let options = FerretOptions(Roots = [| folder |], Progress = Action<string>(eprintfn "%s"))
let ferret = new FerretClient(options)

let timed label f =
    let clock = Stopwatch.StartNew()
    let result = f ()
    printfn "%-14s %5d ms" label clock.ElapsedMilliseconds
    result

printfn "model: %s" (timed "model" ferret.EnsureModel)
printfn "indexed %d files" (timed "index" (fun () -> ferret.BuildIndex Hybrid))
timed "first search" (fun () -> ferret.Search("install", 5, Hybrid)) |> ignore
timed "second search" (fun () -> ferret.Search("configure the cache", 5, Hybrid)) |> ignore
