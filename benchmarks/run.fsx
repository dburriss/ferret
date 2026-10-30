// Times `rg` against `ferret` with hyperfine and writes benchmarks/results/<date>-<machine>.json.
// Needs hyperfine on PATH (mise.toml) and a Release build: dotnet build -c Release src/Ferret.Cli
// Usage: dotnet fsi benchmarks/run.fsx [runs]   (default 10, after one warmup run; warm cache only)
open System
open System.Diagnostics
open System.IO
open System.Text.Json

let here = __SOURCE_DIRECTORY__
let data = Path.Combine(here, "data")
let runs = match fsi.CommandLineArgs |> Array.skip 1 with [| n |] -> int n | _ -> 10
let ferretDll = Path.Combine(here, "..", "src", "Ferret.Cli", "bin", "Release", "net10.0", "Ferret.Cli.dll") |> Path.GetFullPath

let corpora = [ "linux"; "dotnet-runtime" ] |> List.filter (fun c -> Directory.Exists(Path.Combine(data, c)))
let patterns = [ "mutex_lock", []; "TODO", []; "struct file_operations", [ "-F" ]; "[A-Z]{5}_[A-Z]{5}", []; "hello", [ "-i" ]; "zzzznomatch", [] ]

if corpora.IsEmpty then failwith "no corpora, run benchmarks/fetch.fsx"
if not (File.Exists ferretDll) then failwith $"{ferretDll} not found, build src/Ferret.Cli in Release"

/// Single-quotes an argument for hyperfine's command parser.
let quote (s: string) = "'" + s.Replace("'", "'\\''") + "'"

/// hyperfine runs each command with stdin on /dev/null, so neither tool searches stdin.
let hyperfine (cwd: string) (commands: (string * string) list) =
    let json = Path.GetTempFileName()
    let psi = ProcessStartInfo("hyperfine", WorkingDirectory = cwd)
    for a in [ "-N"; "--warmup"; "1"; "--runs"; string runs; "--ignore-failure"; "--export-json"; json ] do
        psi.ArgumentList.Add a
    for name, command in commands do
        psi.ArgumentList.Add "--command-name"
        psi.ArgumentList.Add name
        psi.ArgumentList.Add command
    use p = Process.Start psi
    p.WaitForExit()
    if p.ExitCode <> 0 then failwith "hyperfine failed"
    use doc = JsonDocument.Parse(File.ReadAllText json)
    File.Delete json
    [ for r in doc.RootElement.GetProperty("results").EnumerateArray() ->
        let wall = r.GetProperty("summary").GetProperty("time_wall_clock")
        let ms (name: string) = wall.GetProperty(name).GetDouble() * 1000.0
        let memory = r.GetProperty("summary").GetProperty("memory_peak_resident").GetProperty("median").GetDouble()
        {| tool = r.GetProperty("name").GetString()
           mean_ms = ms "mean"
           median_ms = ms "median"
           min_ms = ms "min"
           stddev_ms = ms "stddev"
           peak_rss_mib = memory / 1048576.0 |} ]

let hyperfineVersion =
    let psi = ProcessStartInfo("hyperfine", "--version", RedirectStandardOutput = true)
    use p = Process.Start psi
    p.StandardOutput.ReadToEnd().Trim()

let results =
    [ for corpus in corpora do
        for pattern, flags in patterns do
            let args = String.Join(" ", [ for f in flags -> quote f ] @ [ quote pattern ])
            let cwd = Path.Combine(data, corpus)
            yield
                {| corpus = corpus
                   pattern = pattern
                   flags = flags
                   timings =
                    hyperfine cwd [ "rg", "rg --no-config " + args
                                    "ferret", $"dotnet {quote ferretDll} {args}" ] |} ]

let out = Path.Combine(here, "results")
Directory.CreateDirectory out |> ignore
let date = DateTime.UtcNow.ToString "yyyy-MM-dd"
let file = Path.Combine(out, date + "-" + Environment.MachineName + ".json")
let summary =
    {| runs = runs
       os = Runtime.InteropServices.RuntimeInformation.OSDescription
       hyperfine = hyperfineVersion
       results = results |}
File.WriteAllText(file, JsonSerializer.Serialize(summary, JsonSerializerOptions(WriteIndented = true)))
printfn "wrote %s" file
