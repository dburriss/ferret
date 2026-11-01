// Prints where a search spends its time, like `ferret --timings`. Ferret emits one Activity per phase on the
// "Ferret" ActivitySource; with no listener that costs nothing.
// Usage: dotnet fsi examples/06-timings-listener.fsx ["query" [folder]]
#load "_ferret.fsx"
open System.Diagnostics
open System.IO
open Ferret

let args = fsi.CommandLineArgs |> Array.skip 1
let query = if args.Length > 0 then args[0] else "install the tool"
let folder = if args.Length > 1 then args[1] else Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "docs"))

let phases = ResizeArray<string * float * string>()

let listener = new ActivityListener()
listener.ShouldListenTo <- fun source -> source.Name = "Ferret"
listener.Sample <- fun _ -> ActivitySamplingResult.AllData
listener.ActivityStopped <-
    fun activity ->
        let tags = activity.Tags |> Seq.map (fun tag -> $"{tag.Key}={tag.Value}") |> String.concat " "
        phases.Add(activity.OperationName, activity.Duration.TotalMilliseconds, tags)
ActivitySource.AddActivityListener listener

let ferret = new FerretClient(FerretOptions(Roots = [| folder |]))
ferret.Search(query, 5, Lexical) |> ignore

for name, ms, tags in phases do
    printfn "%-20s %8.1f ms  %s" name ms tags
