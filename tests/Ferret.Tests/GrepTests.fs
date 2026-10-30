module Ferret.Tests.GrepTests

open System
open Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open Ferret
open Ferret.Domain

/// Text over an alphabet that includes regex metacharacters, to exercise escaping.
type Text = Text of string

type Letters = Letters of string

type Gens =
    static member Text() : Arbitrary<Text> =
        Gen.elements [ 'a'; 'B'; ' '; '.'; '*'; '('; '['; '\\'; '+'; '?' ]
        |> Gen.nonEmptyListOf
        |> Gen.map (fun cs -> Text(String(Array.ofList cs)))
        |> Arb.fromGen

    static member Letters() : Arbitrary<Letters> =
        Gen.elements ([ 'a' .. 'z' ] @ [ 'A' .. 'Z' ])
        |> Gen.nonEmptyListOf
        |> Gen.map (fun cs -> Letters(String(Array.ofList cs)))
        |> Arb.fromGen

let private matches options pattern (line: string) =
    Grep.matchLines (Grep.compile options pattern) [ line ] |> Seq.isEmpty |> not

[<Fact>]
let ``line numbers are 1-based and in order`` () =
    let rx = Grep.compile MatchOptions.defaults "b"
    let hits = Grep.matchLines rx [ "a"; "b"; "c"; "bb" ] |> List.ofSeq
    Assert.Equal<(int * string) list>([ 2, "b"; 4, "bb" ], hits)

[<Fact>]
let ``regex by default`` () =
    Assert.True(matches MatchOptions.defaults "a.c" "xabcx")
    Assert.False(matches MatchOptions.defaults "a.c" "ac")

[<Fact>]
let ``search is case sensitive by default`` () =
    Assert.False(matches MatchOptions.defaults "abc" "ABC")
    Assert.True(matches { MatchOptions.defaults with IgnoreCase = true } "abc" "ABC")

[<Fact>]
let ``invalid regex raises ArgumentException`` () =
    Assert.ThrowsAny<ArgumentException>(fun () -> Grep.compile MatchOptions.defaults "(" |> ignore) |> ignore

[<Fact>]
let ``invalid regex is fine as a fixed string`` () =
    Assert.True(matches { MatchOptions.defaults with FixedStrings = true } "(" "f(x)")

[<Property(Arbitrary = [| typeof<Gens> |])>]
let ``fixed strings agree with ordinal Contains`` (Text pattern) (Text line) =
    let found = matches { MatchOptions.defaults with FixedStrings = true } pattern line
    found = line.Contains(pattern, StringComparison.Ordinal)

[<Property(Arbitrary = [| typeof<Gens> |])>]
let ``ignore case ignores case`` (Letters word) =
    let options = { MatchOptions.defaults with IgnoreCase = true; FixedStrings = true }
    matches options (word.ToUpperInvariant()) ("x " + word.ToLowerInvariant() + " y")

[<Property(Arbitrary = [| typeof<Gens> |])>]
let ``word regexp matches whole words only`` (Letters word) =
    let options = { MatchOptions.defaults with WordRegexp = true; FixedStrings = true }
    matches options word $"x {word} y"
    && matches options word word
    && not (matches options word $"x{word}")
    && not (matches options word $"{word}y")
    && not (matches options word $"x{word}y")
