module Ferret.Tests.RankingTests

open Xunit
open Ferret
open Ferret.Domain

let private query terms : Query =
    { Text = String.concat " " terms
      Terms = terms }

let private candidate path length tf : Candidate =
    { Path = path
      Length = length
      TermFreq = Map.ofList tf }

[<Fact>]
let ``bm25 ranks higher term frequency first`` () =
    let docs = [ candidate "a" 10 [ "x", 1 ]; candidate "b" 10 [ "x", 5 ] ]
    let result = Ranking.bm25 (query [ "x" ]) docs
    Assert.Equal<string list>([ "b"; "a" ], result |> List.map _.File)

[<Fact>]
let ``bm25 drops candidates without a query term`` () =
    let docs = [ candidate "a" 10 [ "x", 1 ]; candidate "b" 10 [] ]
    let result = Ranking.bm25 (query [ "x" ]) docs
    Assert.Equal<string list>([ "a" ], result |> List.map _.File)

[<Fact>]
let ``bm25 breaks ties by ordinal path`` () =
    let docs = [ candidate "b" 10 [ "x", 1 ]; candidate "a" 10 [ "x", 1 ] ]
    let result = Ranking.bm25 (query [ "x" ]) docs
    Assert.Equal<string list>([ "a"; "b" ], result |> List.map _.File)

[<Fact>]
let ``bm25 returns scores best-first and no excerpts`` () =
    let docs = [ candidate "a" 10 [ "x", 1 ]; candidate "b" 10 [ "x", 5 ]; candidate "c" 10 [ "x", 3 ] ]
    let result = Ranking.bm25 (query [ "x" ]) docs
    let scores = result |> List.map _.Score
    Assert.Equal<float list>(List.sortDescending scores, scores)
    Assert.All(result, fun d -> Assert.Empty d.Excerpts)

[<Fact>]
let ``bm25 with no terms or no candidates is empty`` () =
    Assert.Empty(Ranking.bm25 (query []) [ candidate "a" 10 [] ])
    Assert.Empty(Ranking.bm25 (query [ "x" ]) [])
