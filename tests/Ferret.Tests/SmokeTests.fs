module Ferret.Tests.SmokeTests

open System
open System.IO
open System.Text.Json
open Xunit
open Ferret
open Ferret.FileSource

type private Labelled =
    { Query: string
      Kind: string
      Relevant: string list
      Top1: string option }

let repoRoot () =
    let rec up (d: DirectoryInfo) =
        if isNull d then failwith "Ferret.slnx not found above the test assembly"
        elif File.Exists(Path.Combine(d.FullName, "Ferret.slnx")) then d.FullName
        else up d.Parent
    up (DirectoryInfo AppContext.BaseDirectory)

let smokeDir () = Path.Combine(repoRoot (), "tests", "corpus", "smoke")

let private labels () : Labelled list =
    use doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(smokeDir (), "queries.json")))
    [ for q in doc.RootElement.EnumerateArray() ->
        { Query = q.GetProperty("query").GetString()
          Kind = q.GetProperty("kind").GetString()
          Relevant = [ for r in q.GetProperty("relevant").EnumerateArray() -> r.GetString() ]
          Top1 =
            match q.TryGetProperty "top1" with
            | true, v -> Some(v.GetString())
            | _ -> None } ]

/// Top 10 files for `query`, relative to the smoke directory with `/` separators.
let private top10 (query: string) : string list =
    let root = smokeDir ()
    Search.search Ranking.bm25 (DiskFileSource()) [ root ] query 10
    |> List.map (fun doc -> Path.GetRelativePath(root, doc.File).Replace('\\', '/'))

let private ofKind kind =
    labels () |> List.filter (fun l -> l.Kind = kind) |> List.map (fun l -> [| box l |])

let exact = ofKind "exact"
let weighted = ofKind "weighted"

[<Theory>]
[<MemberData(nameof exact)>]
let ``exact term queries return every relevant file in the top 10`` (label: obj) =
    let l = label :?> Labelled
    let found = top10 l.Query
    for r in l.Relevant do
        Assert.True(List.contains r found, $"'{l.Query}': {r} missing from {found}")

[<Theory>]
[<MemberData(nameof weighted)>]
let ``weighted queries rank the expected file first`` (label: obj) =
    let l = label :?> Labelled
    Assert.Equal(l.Top1, List.tryHead (top10 l.Query))
