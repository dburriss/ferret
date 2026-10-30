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
      TermFreq = Map.ofList tf
      FieldTerms = Map.empty }

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

let private withFields (c: Candidate) fields : Candidate =
    { c with FieldTerms = fields |> List.map (fun (f, ts) -> f, Set.ofList ts) |> Map.ofList }

[<Fact>]
let ``a path match outranks an equal body match`` () =
    let docs =
        [ candidate "a" 10 [ "x", 1 ]
          withFields (candidate "b" 10 [ "x", 1 ]) [ Field.Path, [ "x" ] ] ]
    let result = Ranking.bm25 (query [ "x" ]) docs
    Assert.Equal<string list>([ "b"; "a" ], result |> List.map _.File)

[<Fact>]
let ``heading, title, description and tags matches each outrank an equal body match`` () =
    for field in [ Field.Heading; Field.Title; Field.Description; Field.TagList ] do
        let docs =
            [ candidate "a" 10 [ "x", 1 ]
              withFields (candidate "b" 10 [ "x", 1 ]) [ field, [ "x" ] ] ]
        let result = Ranking.bm25 (query [ "x" ]) docs
        Assert.Equal<string list>([ "b"; "a" ], result |> List.map _.File)

[<Fact>]
let ``the boost scales with the share of query terms in the field`` () =
    let tf = [ "x", 1; "y", 1 ]
    let docs =
        [ withFields (candidate "a" 10 tf) [ Field.Heading, [ "x" ] ]
          withFields (candidate "b" 10 tf) [ Field.Heading, [ "x"; "y" ] ] ]
    let result = Ranking.bm25 (query [ "x"; "y" ]) docs
    Assert.Equal<string list>([ "b"; "a" ], result |> List.map _.File)

[<Fact>]
let ``a boost does not revive a candidate without a body match`` () =
    let docs = [ withFields (candidate "a" 10 []) [ Field.Path, [ "x" ] ] ]
    Assert.Empty(Ranking.bm25 (query [ "x" ]) docs)

[<Fact>]
let ``no field matches leaves the plain bm25 score`` () =
    let plain = Ranking.bm25 (query [ "x" ]) [ candidate "a" 10 [ "x", 2 ] ]
    let stats: Bm25.CorpusStats = { DocCount = 1; AvgLength = 10.0; DocFreq = Map.ofList [ "x", 1 ] }
    Assert.Equal(Bm25.score stats [ "x" ] (Map.ofList [ "x", 2 ]) 10, plain.Head.Score)
