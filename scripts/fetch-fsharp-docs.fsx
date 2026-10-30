// Sparse-checks-out the F# docs (docs/fsharp from dotnet/docs, pinned commit) into corpus/fsharp-docs/repo (gitignored).
// The hand-written queries and labels live in tests/corpus/relevance/fsharp-docs/queries.json.
// Usage: dotnet fsi scripts/fetch-fsharp-docs.fsx
open System.Diagnostics
open System.IO

let sha = "faf8e5d14b5ff22f4be9e6a4d182b8bd18103a80"
let url = "https://github.com/dotnet/docs"
let target = Path.Combine(__SOURCE_DIRECTORY__, "..", "corpus", "fsharp-docs", "repo") |> Path.GetFullPath

let git (dir: string) (args: string) =
    let psi = ProcessStartInfo("git", args, WorkingDirectory = dir)
    use p = Process.Start psi
    p.WaitForExit()
    if p.ExitCode <> 0 then failwith $"git {args} failed"

if Directory.Exists(Path.Combine(target, "docs", "fsharp")) then
    printfn "fsharp-docs: already fetched"
else
    printfn "fsharp-docs: fetching %s" (sha.Substring(0, 8))
    Directory.CreateDirectory target |> ignore
    git target "init -q"
    git target $"remote add origin {url}"
    git target "sparse-checkout set docs/fsharp"
    git target $"fetch -q --depth 1 --filter=blob:none origin {sha}"
    git target "checkout -q FETCH_HEAD"
