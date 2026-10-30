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

[<Fact>]
let ``stop words are dropped`` () =
    Assert.Equal<string list>([ "quick"; "fox" ], Text.tokenize "The quick fox of the")
    Assert.Empty(Text.tokenize "to in on")

[<Fact>]
let ``suffix variants stem to the same token`` () =
    for word in [ "mock"; "mocked"; "mocking"; "mocks" ] do
        Assert.Equal<string list>([ "mock" ], Text.tokenize word)
    Assert.Equal<string list>([ "run" ], Text.tokenize "running")
    Assert.Equal<string list>([ "match" ], Text.tokenize "matches")
    Assert.Equal<string list>([ "file" ], Text.tokenize "files")

[<Fact>]
let ``stemmer leaves short, digit and -ss -us -is tokens alone`` () =
    for word in [ "class"; "status"; "analysis"; "string"; "add"; "v2s"; "bus"; "ing" ] do
        Assert.Equal<string list>([ word ], Text.tokenize word)
