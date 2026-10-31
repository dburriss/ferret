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

// Reciprocal rank fusion.

let private doc file excerpts : ScoredDoc =
    { File = file
      Score = 0.0
      Excerpts = excerpts }

let private hit path line text : Hit =
    { Path = path
      LineNumber = line
      Line = text }

let private ranked files = files |> List.map (fun f -> doc f [])

[<Fact>]
let ``fuse sums weight over k plus rank`` () =
    let result = Ranking.fuse 60 [ 1.0, ranked [ "a"; "b" ]; 1.0, ranked [ "b"; "a" ] ]
    Assert.Equal<string list>([ "a"; "b" ], result |> List.map _.File)
    Assert.Equal(1.0 / 61.0 + 1.0 / 62.0, result.Head.Score, 12)

[<Fact>]
let ``fuse applies weights`` () =
    let result = Ranking.fuse 60 [ 2.0, ranked [ "a" ]; 1.0, ranked [ "b" ] ]
    Assert.Equal<string list>([ "a"; "b" ], result |> List.map _.File)
    Assert.Equal(2.0 / 61.0, result.Head.Score, 12)

[<Fact>]
let ``fuse breaks ties by ordinal path`` () =
    let result = Ranking.fuse 60 [ 1.0, ranked [ "b" ]; 1.0, ranked [ "a" ] ]
    Assert.Equal<string list>([ "a"; "b" ], result |> List.map _.File)

[<Fact>]
let ``fuse takes excerpts from the first ranking that has any`` () =
    let lexical = [ doc "a" [ hit "a" 1 "lex" ]; doc "b" [] ]
    let semantic = [ doc "b" [ hit "b" 2 "sem" ]; doc "a" [ hit "a" 9 "sem" ]; doc "c" [ hit "c" 3 "sem" ] ]
    let result = Ranking.fuse 60 [ 1.0, lexical; 1.0, semantic ] |> List.map (fun d -> d.File, d.Excerpts |> List.map _.Line) |> Map.ofList
    Assert.Equal<string list>([ "lex" ], result["a"])
    Assert.Equal<string list>([ "sem" ], result["b"])
    Assert.Equal<string list>([ "sem" ], result["c"])

[<Fact>]
let ``fuse of nothing is empty`` () =
    Assert.Empty(Ranking.fuse 60 [])
    Assert.Empty(Ranking.fuse 60 [ 1.0, [] ])

let private names (n: int) (seed: int) =
    let rng = System.Random seed
    [ 0..25 ] |> List.sortBy (fun _ -> rng.Next()) |> List.truncate (abs n % 12) |> List.map (fun i -> $"f{i:D2}")

open FsCheck.Xunit

[<Property>]
let ``fuse is deterministic`` (n1: int) (s1: int) (n2: int) (s2: int) =
    let rankings = [ 1.0, ranked (names n1 s1); 1.0, ranked (names n2 s2) ]
    Ranking.fuse 60 rankings = Ranking.fuse 60 rankings
    && Ranking.fuse 60 rankings = Ranking.fuse 60 (List.rev rankings)

[<Property>]
let ``a document in both rankings outranks the same document in one`` (n: int) (s: int) =
    // Same position in both lists vs the same position in one list: the former always scores higher.
    let both = names n s
    both.IsEmpty
    || (let target = both.Head
        let others = [ for i in 0..30 -> $"z{i:D2}" ]
        let one = Ranking.fuse 60 [ 1.0, ranked (target :: others); 1.0, ranked others ]
        let two = Ranking.fuse 60 [ 1.0, ranked (target :: others); 1.0, ranked (target :: others) ]
        let score docs = docs |> List.find (fun d -> d.File = target) |> _.Score
        score two > score one)

[<Property>]
let ``a zero weight reduces fusion to the other ranker`` (n1: int) (s1: int) (n2: int) (s2: int) =
    let a = names n1 s1
    let b = names n2 s2
    let fused = Ranking.fuse 60 [ 1.0, ranked a; 0.0, ranked b ] |> List.map _.File
    fused = (Ranking.fuse 60 [ 1.0, ranked a ] |> List.map _.File)
