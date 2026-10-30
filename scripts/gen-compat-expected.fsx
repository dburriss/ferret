// Regenerates tests/corpus/compat/*/expected.txt and exit from ripgrep. `--check` compares instead of writing.
// Usage: dotnet fsi scripts/gen-compat-expected.fsx [--check]
open System
open System.Diagnostics
open System.IO

let root = Path.Combine(__SOURCE_DIRECTORY__, "..", "tests", "corpus", "compat") |> Path.GetFullPath
let check = fsi.CommandLineArgs |> Array.contains "--check"

/// `dot-name` is stored as `.name` so the fixtures' own ignore files don't affect this repo.
let materialise (src: string) (dst: string) (git: bool) =
    for file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories) do
        let rel = Path.GetRelativePath(src, file)
        let name = Path.GetFileName rel
        let rel = if name.StartsWith "dot-" then Path.Combine(Path.GetDirectoryName rel, "." + name.Substring 4) else rel
        let target = Path.Combine(dst, rel)
        Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
        File.Copy(file, target)
    if git then Directory.CreateDirectory(Path.Combine(dst, ".git")) |> ignore

let rg (cwd: string) (args: string list) =
    let psi = ProcessStartInfo("rg", RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd)
    for a in [ "--no-config"; "--sort"; "path" ] @ args do
        psi.ArgumentList.Add a
    use p = Process.Start psi
    let out = p.StandardOutput.ReadToEnd()
    p.WaitForExit()
    out, p.ExitCode

let mutable differences = 0
for dir in Directory.GetDirectories root |> Array.sort do
    let name = Path.GetFileName dir
    let args = File.ReadAllLines(Path.Combine(dir, "args")) |> List.ofArray
    let tmp = Directory.CreateTempSubdirectory "ferret-compat-"
    try
        materialise (Path.Combine(dir, "tree")) tmp.FullName (File.Exists(Path.Combine(dir, "git")))
        let out, code = rg tmp.FullName args
        let expectedPath = Path.Combine(dir, "expected.txt")
        let exitPath = Path.Combine(dir, "exit")
        if check then
            let same = File.Exists expectedPath && File.ReadAllText expectedPath = out && File.ReadAllText(exitPath).Trim() = string code
            if not same then
                differences <- differences + 1
                printfn "DIFF %s" name
        else
            File.WriteAllText(expectedPath, out)
            File.WriteAllText(exitPath, string code + "\n")
            printfn "wrote %s (exit %d)" name code
    finally
        tmp.Delete true
if check && differences > 0 then exit 1
