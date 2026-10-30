module Ferret.Tests.CompatTests

open System
open System.IO
open Xunit
open Ferret.Domain
open Ferret.FileSource
open Ferret.Cli.Deps
open Ferret.Cli.GrepCli

let private repoRoot () =
    let rec up (d: DirectoryInfo) =
        if isNull d then failwith "Ferret.slnx not found above the test assembly"
        elif File.Exists(Path.Combine(d.FullName, "Ferret.slnx")) then d.FullName
        else up d.Parent
    up (DirectoryInfo AppContext.BaseDirectory)

let private compatDir () = Path.Combine(repoRoot (), "tests", "corpus", "compat")

/// Copies a case's tree. `dot-name` becomes `.name`, and a `git` marker adds an empty `.git`
/// directory, as scripts/gen-compat-expected.fsx does.
let private materialise (case: string) (dst: string) =
    let src = Path.Combine(case, "tree")
    for file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories) do
        let rel = Path.GetRelativePath(src, file)
        let name = Path.GetFileName rel
        let rel = if name.StartsWith "dot-" then Path.Combine(Path.GetDirectoryName rel, "." + name.Substring 4) else rel
        let target = Path.Combine(dst, rel)
        Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
        File.Copy(file, target)
    if File.Exists(Path.Combine(case, "git")) then
        Directory.CreateDirectory(Path.Combine(dst, ".git")) |> ignore

let private toGrepArgs (args: string list) (root: string) =
    let flags, rest = args |> List.partition (fun a -> a.StartsWith "-")
    { Pattern = List.exactlyOne rest
      Paths = [ root ]
      Options =
        { IgnoreCase = List.contains "-i" flags
          FixedStrings = List.contains "-F" flags
          WordRegexp = List.contains "-w" flags }
      LineNumbers = List.contains "-n" flags
      Mode =
        if List.contains "-l" flags then ShowPaths
        elif List.contains "-c" flags then ShowCounts
        else ShowLines }

/// Output and exit code of Ferret on a case, with the temp root stripped so paths read as rg's do.
let private runCase (case: string) =
    let tmp = Directory.CreateTempSubdirectory "ferret-compat-"
    try
        materialise case tmp.FullName
        let args = File.ReadAllLines(Path.Combine(case, "args")) |> List.ofArray
        let out = new StringWriter()
        let deps =
            { Files = DiskFileSource()
              Out = out
              Err = new StringWriter()
              StdinIsReadable = false
              IsDebug = false }
        let code = run deps (toGrepArgs args tmp.FullName)
        let text = out.ToString().Replace(Environment.NewLine, "\n")
        text.Replace(tmp.FullName.Replace('\\', '/') + "/", "").Replace(tmp.FullName + string Path.DirectorySeparatorChar, ""), code
    finally
        tmp.Delete true

let cases () =
    Directory.GetDirectories(compatDir ())
    |> Array.sort
    |> Seq.map (fun d -> [| box (Path.GetFileName d) |])

/// A `gap` file records a known difference from rg. The case must still differ, so the gap
/// file is removed when the difference is fixed.
[<Theory>]
[<MemberData(nameof cases)>]
let ``matches ripgrep`` (name: obj) =
    let case = Path.Combine(compatDir (), string name)
    let out, code = runCase case
    let expectedOut = File.ReadAllText(Path.Combine(case, "expected.txt"))
    let expectedCode = int (File.ReadAllText(Path.Combine(case, "exit")).Trim())
    let same = out = expectedOut && code = expectedCode
    let gap = Path.Combine(case, "gap")
    if File.Exists gap then
        Assert.False(same, $"{name}: now matches rg, delete tests/corpus/compat/{name}/gap")
    else
        Assert.Equal(expectedOut, out)
        Assert.Equal(expectedCode, code)
