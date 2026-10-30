// Shallow-clones the perf corpora into benchmarks/data/ (gitignored, several GB). Pinned tags.
// Usage: dotnet fsi benchmarks/fetch.fsx
open System.Diagnostics
open System.IO

let data = Path.Combine(__SOURCE_DIRECTORY__, "data") |> Path.GetFullPath

let repos =
    [ "linux", "https://github.com/torvalds/linux", "v6.12"
      "dotnet-runtime", "https://github.com/dotnet/runtime", "v9.0.0" ]

Directory.CreateDirectory data |> ignore
for name, url, tag in repos do
    let target = Path.Combine(data, name)
    if Directory.Exists target then
        printfn "%s: already cloned" name
    else
        printfn "%s: cloning %s" name tag
        let psi = ProcessStartInfo("git", $"clone --depth 1 --branch {tag} {url} {target}")
        use p = Process.Start psi
        p.WaitForExit()
        if p.ExitCode <> 0 then failwith $"git clone failed for {name}"
