module Ferret.Tests.Bm25Tests

open Xunit
open FsCheck.Xunit
open Ferret

let private stats docCount avg df : Bm25.CorpusStats =
    { DocCount = docCount
      AvgLength = avg
      DocFreq = Map.ofList [ "x", df ] }

[<Property>]
let ``idf is never negative`` (n: int) (df: int) =
    let n = abs n % 1000 + 1
    let df = abs df % (n + 1)
    Bm25.idf n df >= 0.0

[<Fact>]
let ``rarer terms have a higher idf`` () =
    Assert.True(Bm25.idf 100 1 > Bm25.idf 100 50)

[<Fact>]
let ``a shorter document outranks a longer one at equal term frequency`` () =
    let s = stats 2 100.0 2
    let tf = Map.ofList [ "x", 3 ]
    Assert.True(Bm25.score s [ "x" ] tf 50 > Bm25.score s [ "x" ] tf 150)

[<Fact>]
let ``term frequency saturates`` () =
    let s = stats 10 100.0 3
    let at tf = Bm25.score s [ "x" ] (Map.ofList [ "x", tf ]) 100
    Assert.True(at 2 > at 1)
    Assert.True(at 2 - at 1 > at 20 - at 19)

[<Fact>]
let ``a document without the term scores zero`` () =
    Assert.Equal(0.0, Bm25.score (stats 10 100.0 3) [ "x" ] Map.empty 100)
