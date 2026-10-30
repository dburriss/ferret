module Ferret.Tests.TextTests

open Xunit
open Ferret

[<Fact>]
let ``lowercases and splits on non-alphanumerics`` () =
    Assert.Equal<string list>([ "hello"; "world"; "42"; "foo"; "bar" ], Text.tokenize "Hello, WORLD! 42 foo_bar")

[<Fact>]
let ``unicode letters stay in the token`` () =
    Assert.Equal<string list>([ "café"; "naïve" ], Text.tokenize "Café, naïve")

[<Fact>]
let ``empty and punctuation-only input has no tokens`` () =
    Assert.Empty(Text.tokenize "")
    Assert.Empty(Text.tokenize " .,;-- ")
