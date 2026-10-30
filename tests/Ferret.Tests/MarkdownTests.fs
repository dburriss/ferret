module Ferret.Tests.MarkdownTests

open Xunit
open Ferret
open Ferret.Domain
open Ferret.FileSource

let private fields (lines: string list) = Markdown.fieldText lines

let private texts field lines =
    fields lines |> List.filter (fst >> (=) field) |> List.map snd

[<Fact>]
let ``markdown extensions are recognised case-insensitively`` () =
    Assert.True(Markdown.isMarkdown "a/b.md")
    Assert.True(Markdown.isMarkdown "B.MDX")
    Assert.False(Markdown.isMarkdown "a.txt")

[<Fact>]
let ``headings are extracted, other lines and hashes without a space are not`` () =
    let lines = [ "# Mocking"; "body"; "#nospace"; "  ## Sub"; "    ### code block" ]
    Assert.Equal<string list>([ "# Mocking"; "  ## Sub" ], texts Field.Heading lines)

[<Fact>]
let ``headings inside fenced code are skipped`` () =
    let lines = [ "```"; "# comment"; "```"; "~~~"; "# other"; "~~~"; "# Real" ]
    Assert.Equal<string list>([ "# Real" ], texts Field.Heading lines)

[<Fact>]
let ``frontmatter title, description and tags are extracted`` () =
    let lines =
        [ "---"
          "title: \"Unit testing\""
          "description: How to mock"
          "ms.date: 2024-01-01"
          "tags: [alpha, beta]"
          "---"
          "# Body" ]
    Assert.Equal<string list>([ "Unit testing" ], texts Field.Title lines)
    Assert.Equal<string list>([ "How to mock" ], texts Field.Description lines)
    Assert.Equal<string list>([ "alpha"; "beta" ], texts Field.TagList lines)
    Assert.Equal<string list>([ "# Body" ], texts Field.Heading lines)

[<Fact>]
let ``block lists and folded scalars are read as yaml`` () =
    let lines =
        [ "---"
          "tags:"
          "  - alpha"
          "  - beta"
          "description: >"
          "  folded"
          "  text"
          "other: x"
          "---" ]
    Assert.Equal<string list>([ "alpha"; "beta" ], texts Field.TagList lines)
    Assert.Equal<string list>([ "folded text\n" ], texts Field.Description lines)

[<Fact>]
let ``a scalar tags value and numbers are read as text`` () =
    let lines = [ "---"; "tags: mocking"; "title: 2024"; "---" ]
    Assert.Equal<string list>([ "mocking" ], texts Field.TagList lines)
    Assert.Equal<string list>([ "2024" ], texts Field.Title lines)

[<Fact>]
let ``invalid frontmatter yields no frontmatter fields but keeps headings`` () =
    let lines = [ "---"; "title: [unclosed"; "tags: {a: b"; "---"; "# Heading" ]
    Assert.Empty(texts Field.Title lines)
    Assert.Empty(texts Field.TagList lines)
    Assert.Equal<string list>([ "# Heading" ], texts Field.Heading lines)

[<Fact>]
let ``frontmatter that is not a mapping yields no fields`` () =
    Assert.Empty(fields [ "---"; "- a"; "- b"; "---" ] |> List.filter (fst >> (<>) Field.Heading))

[<Fact>]
let ``an unterminated frontmatter block is not frontmatter`` () =
    Assert.Empty(texts Field.Title [ "---"; "title: Never closed"; "text" ])

[<Fact>]
let ``yaml comments in frontmatter are not headings`` () =
    Assert.Empty(texts Field.Heading [ "---"; "# a comment"; "title: x"; "---" ])

[<Fact>]
let ``frontmatter must start on the first line`` () =
    let lines = [ "intro"; "---"; "title: Late"; "---" ]
    Assert.Empty(texts Field.Title lines)

[<Fact>]
let ``search boosts a heading match above a body match`` () =
    let files =
        let docs =
            Map.ofList
                [ "a.md", [ "# Other"; "mock mock" ]
                  "b.md", [ "# Mock"; "other text" ]
                  "c.txt", [ "# mock"; "other text" ] ]
        { new IFileSource with
            member _.Kind _ = IsDirectory
            member _.Enumerate _ = docs |> Map.toSeq |> Seq.map fst
            member _.ReadLines p = docs |> Map.tryFind p |> Option.map Seq.ofList
            member _.ReadStdin() = Seq.empty }
    let result = Search.search Ranking.bm25 files [ "" ] "mock" 10
    Assert.Equal<string list>([ "b.md"; "a.md"; "c.txt" ], result |> List.map _.File)
