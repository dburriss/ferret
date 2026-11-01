// Times `rg` against `ferret` with hyperfine and writes benchmarks/results/<date>-<machine>.json.
// Needs hyperfine on PATH (mise.toml) and a Release build: dotnet build -c Release src/Ferret.Cli
// Usage: dotnet fsi benchmarks/run.fsx [runs]   (default 10, after one warmup run; warm cache only)
// FERRET_BENCH_CORPORA=scifact,fsharp-docs limits both sections to the named corpora (default: all present).
// Also times ranked search (--lex) three ways per query: per-run (--no-index), index build (cache deleted before
// each run) and warm index. It uses the perf corpora plus any fetched BEIR / F# docs sets, and records the index size.
// Then times --sem and --hybrid, ferret next to `ck`, two ways per query: index build (embedding included) and warm
// index (model load and query embedding included). Default corpus is fsharp-docs; FERRET_BENCH_SEM_CORPORA=names opts
// in to others (embedding the multi-GB ones takes hours). FERRET_BENCH_BUILD_RUNS sets the build runs (default 3).
// Needs the ferret model (`ferret --model download`) and ck's own model cached (run ck --sem once); never downloads.
open System
open System.Diagnostics
open System.IO
open System.Text.Json

let here = __SOURCE_DIRECTORY__
let data = Path.Combine(here, "data")
let runs = match fsi.CommandLineArgs |> Array.skip 1 with [| n |] -> int n | _ -> 10
let ferretDll = Path.Combine(here, "..", "src", "Ferret.Cli", "bin", "Release", "net10.0", "ferret.dll") |> Path.GetFullPath

let wanted =
    match Environment.GetEnvironmentVariable "FERRET_BENCH_CORPORA" with
    | null | "" -> fun (_: string) -> true
    | names -> let set = names.Split ',' |> Set.ofArray in set.Contains

let corpora = [ "linux"; "dotnet-runtime" ] |> List.filter (fun c -> wanted c && Directory.Exists(Path.Combine(data, c)))
let patterns = [ "mutex_lock", []; "TODO", []; "struct file_operations", [ "-F" ]; "[A-Z]{5}_[A-Z]{5}", []; "hello", [ "-i" ]; "zzzznomatch", [] ]

if not (File.Exists ferretDll) then failwith $"{ferretDll} not found, build src/Ferret.Cli in Release"

/// Single-quotes an argument for hyperfine's command parser.
let quote (s: string) = "'" + s.Replace("'", "'\\''") + "'"

/// hyperfine runs each command with stdin on /dev/null, so neither tool searches stdin.
/// `label` (corpus and pattern) prefixes every command name so hyperfine's output says what it ran; the
/// results keep the short name.
let hyperfineWith (warmup: int) (runs: int) (label: string) (cwd: string) (env: (string * string) list) (commands: (string * string * string option) list) =
    printfn "\n=== %s ===" label
    let json = Path.GetTempFileName()
    let psi = ProcessStartInfo("hyperfine", WorkingDirectory = cwd)
    for k, v in env do
        psi.Environment[k] <- v
    for a in [ "-N"; "--warmup"; string warmup; "--runs"; string runs; "--ignore-failure"; "--export-json"; json ] do
        psi.ArgumentList.Add a
    for name, command, prepare in commands do
        // hyperfine pairs --prepare with commands by position: give every command one or none.
        psi.ArgumentList.Add "--prepare"
        psi.ArgumentList.Add(defaultArg prepare "true")
        psi.ArgumentList.Add "--command-name"
        psi.ArgumentList.Add $"{label} | {name}"
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
        {| tool = r.GetProperty("name").GetString().Substring(label.Length + 3)
           mean_ms = ms "mean"
           median_ms = ms "median"
           min_ms = ms "min"
           stddev_ms = ms "stddev"
           peak_rss_mib = memory / 1048576.0 |} ]

let hyperfine = hyperfineWith 1 runs

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
                    hyperfine
                        $"{corpus} | {pattern} {String.Join(' ', flags)}"
                        cwd
                        []
                        [ "rg", "rg --no-config " + args, None
                          "ferret", $"dotnet {quote ferretDll} {args}", None ] |} ]

// Ranked search. The index lives in a throwaway cache dir, so the user's own cache is untouched.
let repo = Path.Combine(here, "..") |> Path.GetFullPath
let lexCorpora =
    [ for c, queries in [ "linux", [ "mutex lock"; "memory allocation failure" ]; "dotnet-runtime", [ "thread pool"; "garbage collection" ] ] do
        let dir = Path.Combine(data, c)
        if wanted c && Directory.Exists dir then yield c, dir, queries
      for c, rel, queries in
        [ "scifact", "corpus/scifact/docs", [ "gene expression cancer"; "vitamin deficiency" ]
          "nfcorpus", "corpus/nfcorpus/docs", [ "breast cancer diet"; "cholesterol" ]
          "fsharp-docs", "corpus/fsharp-docs/repo/docs/fsharp", [ "pattern matching"; "async workflow" ] ] do
        let dir = Path.Combine(repo, rel)
        if wanted c && Directory.Exists dir then yield c, dir, queries ]

let lexResults =
    [ for corpus, dir, queries in lexCorpora do
        let cache = Path.Combine(Path.GetTempPath(), $"ferret-bench-{Guid.NewGuid():N}")
        let indexFile = Path.Combine(cache, "lexical.idx")
        try
            for query in queries do
                let ferret extra = $"dotnet {quote ferretDll} --lex {extra}{quote query}"
                let timings =
                    hyperfine $"{corpus} | --lex {query}" dir [ "FERRET_CACHE_DIR", cache ]
                        [ "per-run", ferret "--no-index ", None
                          "index-build", ferret "", Some("rm -f " + quote indexFile)
                          "index-warm", ferret "", None ]
                yield
                    {| corpus = corpus
                       query = query
                       indexBytes = FileInfo(indexFile).Length
                       timings = timings |}
        finally
            if Directory.Exists cache then Directory.Delete(cache, true) ]

// Semantic and hybrid. ferret keeps one cache dir per corpus and mode; ck keeps its index in `.ck` under the searched dir.
let semWanted =
    match Environment.GetEnvironmentVariable "FERRET_BENCH_SEM_CORPORA" with
    | null | "" -> fun c -> c = "fsharp-docs"
    | names -> let set = names.Split ',' |> Set.ofArray in set.Contains

let buildRuns =
    match Environment.GetEnvironmentVariable "FERRET_BENCH_BUILD_RUNS" with
    | null | "" -> 3
    | n -> int n

let semQueries =
    dict [ "fsharp-docs", [ "pattern matching"; "how do I handle errors in async code" ]
           "scifact", [ "gene expression cancer"; "vitamin deficiency" ]
           "nfcorpus", [ "breast cancer diet"; "cholesterol" ]
           "linux", [ "mutex lock"; "memory allocation failure" ]
           "dotnet-runtime", [ "thread pool"; "garbage collection" ] ]

let semCorpora = [ for c, dir, _ in lexCorpora do if semWanted c && semQueries.ContainsKey c then yield c, dir, semQueries[c] ]

let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
let ferretModelDir =
    match Environment.GetEnvironmentVariable "FERRET_MODEL_DIR" with
    | null | "" ->
        let cache =
            match Environment.GetEnvironmentVariable "FERRET_CACHE_DIR", Environment.GetEnvironmentVariable "XDG_CACHE_HOME" with
            | c, _ when not (String.IsNullOrEmpty c) -> c
            | _, x when not (String.IsNullOrEmpty x) -> Path.Combine(x, "ferret")
            | _ -> Path.Combine(home, ".cache", "ferret")
        Path.Combine(cache, "models", "bge-small-en-v1.5-int8")
    | dir -> dir

if not semCorpora.IsEmpty then
    if not (File.Exists(Path.Combine(ferretModelDir, "model_quantized.onnx")) && File.Exists(Path.Combine(ferretModelDir, "vocab.txt"))) then
        failwith $"ferret model not found in {ferretModelDir}: run `ferret --model download` first (the benchmark never downloads)"
    let ckModels = Path.Combine(home, ".cache", "ck", "models")
    if not (Directory.Exists ckModels && Directory.EnumerateFileSystemEntries ckModels |> Seq.isEmpty |> not) then
        failwith $"ck model not found in {ckModels}: run `ck --sem x <dir>` once to cache it (the benchmark never downloads)"

/// Runs ferret once with --timings and returns its phase table (stderr), one entry per phase row.
let phases (cwd: string) (env: (string * string) list) (mode: string) (query: string) =
    let psi = ProcessStartInfo("dotnet", WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true)
    for a in [ ferretDll; mode; "--timings"; query ] do
        psi.ArgumentList.Add a
    for k, v in env do
        psi.Environment[k] <- v
    use p = Process.Start psi
    let err = p.StandardError.ReadToEndAsync()
    p.StandardOutput.ReadToEnd() |> ignore
    p.WaitForExit()
    [ for line in err.Result.Split '\n' do
        if line.StartsWith "ferret." then
            let parts = line.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
            yield {| phase = parts[0]; ms = float parts[1]; tags = String.Join(' ', parts |> Array.skip 2) |} ]

let semResults =
    [ for corpus, dir, queries in semCorpora do
        for mode in [ "--sem"; "--hybrid" ] do
            let cache = Path.Combine(Path.GetTempPath(), $"ferret-bench-{Guid.NewGuid():N}")
            let env = [ "FERRET_CACHE_DIR", cache; "FERRET_MODEL_DIR", ferretModelDir; "FERRET_OFFLINE", "1" ]
            let semDir, lexFile = Path.Combine(cache, "semantic"), Path.Combine(cache, "lexical.idx")
            let clearFerret = "rm -rf " + quote semDir + " " + quote lexFile
            let ck = Path.Combine(dir, ".ck")
            try
                let mutable first = true
                for query in queries do
                    let ferret = $"dotnet {quote ferretDll} {mode} --limit 10 {quote query}"
                    let ckCmd = $"ck {mode} --topk 10 --threshold 0 {quote query} ."
                    let label = $"{corpus} | {mode} {query}"
                    let build =
                        hyperfineWith 0 buildRuns label dir env
                            [ "ferret index-build", ferret, Some clearFerret
                              "ck index-build", ckCmd, Some("rm -rf .ck") ]
                    // The last build run of each tool left its index in place.
                    let warm = hyperfine label dir env [ "ferret index-warm", ferret, None; "ck index-warm", ckCmd, None ]
                    let breakdown =
                        if first then
                            first <- false
                            Directory.Delete(cache, true)
                            let b = phases dir env mode query
                            Some {| build = b; warm = phases dir env mode query |}
                        else None
                    yield
                        {| corpus = corpus
                           mode = mode
                           query = query
                           ferretIndexBytes = Directory.GetFiles(cache, "*.idx", SearchOption.AllDirectories) |> Array.sumBy (fun f -> FileInfo(f).Length)
                           ckIndexBytes = if Directory.Exists ck then DirectoryInfo(ck).EnumerateFiles("*", SearchOption.AllDirectories) |> Seq.sumBy (fun f -> f.Length) else 0L
                           buildRuns = buildRuns
                           timings = build @ warm
                           phases = breakdown |}
            finally
                if Directory.Exists cache then Directory.Delete(cache, true)
                if Directory.Exists ck then Directory.Delete(ck, true) ]

if corpora.IsEmpty && lexCorpora.IsEmpty then failwith "no corpora, run benchmarks/fetch.fsx or mise run bootstrap"

let out = Path.Combine(here, "results")
Directory.CreateDirectory out |> ignore
let date = DateTime.UtcNow.ToString "yyyy-MM-dd"
let file = Path.Combine(out, date + "-" + Environment.MachineName + ".json")
let summary =
    {| runs = runs
       os = Runtime.InteropServices.RuntimeInformation.OSDescription
       hyperfine = hyperfineVersion
       results = results
       lex = lexResults
       semantic = semResults |}
File.WriteAllText(file, JsonSerializer.Serialize(summary, JsonSerializerOptions(WriteIndented = true)))
printfn "wrote %s" file
