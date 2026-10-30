module Ferret.Tests.IgnoreTests

open Xunit
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open Ferret

type Segment = Segment of string

type Gens =
    static member Segment() : Arbitrary<Segment> =
        Gen.elements [ 'a' .. 'e' ]
        |> Gen.nonEmptyListOf
        |> Gen.map (fun cs -> Segment(System.String(Array.ofList cs)))
        |> Arb.fromGen

let private rules lines = [ Ignore.compile "" lines ]
let private verdict lines path isDir = Ignore.check (rules lines) path isDir

[<Fact>]
let ``star matches at any depth`` () =
    Assert.Equal(Ignore.Ignored, verdict [ "*.log" ] "a.log" false)
    Assert.Equal(Ignore.Ignored, verdict [ "*.log" ] "x/y/a.log" false)
    Assert.Equal(Ignore.NoMatch, verdict [ "*.log" ] "a.txt" false)

[<Fact>]
let ``star does not cross directories`` () =
    Assert.Equal(Ignore.Ignored, verdict [ "docs/*.md" ] "docs/a.md" false)
    Assert.Equal(Ignore.NoMatch, verdict [ "docs/*.md" ] "docs/x/a.md" false)

[<Fact>]
let ``leading slash anchors to the rules directory`` () =
    Assert.Equal(Ignore.Ignored, verdict [ "/build" ] "build" true)
    Assert.Equal(Ignore.NoMatch, verdict [ "/build" ] "src/build" true)

[<Fact>]
let ``a slash in the middle anchors`` () =
    Assert.Equal(Ignore.Ignored, verdict [ "a/b" ] "a/b" false)
    Assert.Equal(Ignore.NoMatch, verdict [ "a/b" ] "x/a/b" false)

[<Fact>]
let ``trailing slash matches directories only`` () =
    Assert.Equal(Ignore.Ignored, verdict [ "build/" ] "build" true)
    Assert.Equal(Ignore.Ignored, verdict [ "build/" ] "src/build" true)
    Assert.Equal(Ignore.NoMatch, verdict [ "build/" ] "build" false)

[<Fact>]
let ``last matching rule wins`` () =
    Assert.Equal(Ignore.Whitelisted, verdict [ "*.log"; "!keep.log" ] "keep.log" false)
    Assert.Equal(Ignore.Ignored, verdict [ "!keep.log"; "*.log" ] "keep.log" false)
    Assert.Equal(Ignore.Ignored, verdict [ "*.log"; "!keep.log" ] "other.log" false)

[<Fact>]
let ``double star forms`` () =
    Assert.Equal(Ignore.Ignored, verdict [ "**/foo" ] "foo" false)
    Assert.Equal(Ignore.Ignored, verdict [ "**/foo" ] "a/b/foo" false)
    Assert.Equal(Ignore.Ignored, verdict [ "a/**/b" ] "a/b" false)
    Assert.Equal(Ignore.Ignored, verdict [ "a/**/b" ] "a/x/y/b" false)
    Assert.Equal(Ignore.Ignored, verdict [ "abc/**" ] "abc/x/y" false)
    Assert.Equal(Ignore.NoMatch, verdict [ "abc/**" ] "abc" true)

[<Fact>]
let ``question mark and character classes`` () =
    Assert.Equal(Ignore.Ignored, verdict [ "a?c" ] "abc" false)
    Assert.Equal(Ignore.NoMatch, verdict [ "a?c" ] "a/c" false)
    Assert.Equal(Ignore.Ignored, verdict [ "[ab].txt" ] "b.txt" false)
    Assert.Equal(Ignore.NoMatch, verdict [ "[ab].txt" ] "c.txt" false)
    Assert.Equal(Ignore.Ignored, verdict [ "[!ab].txt" ] "c.txt" false)
    Assert.Equal(Ignore.NoMatch, verdict [ "[!ab].txt" ] "a.txt" false)

[<Fact>]
let ``comments, blanks and escapes`` () =
    Assert.Equal(Ignore.NoMatch, verdict [ "# *.log"; ""; "   " ] "a.log" false)
    Assert.Equal(Ignore.Ignored, verdict [ @"\#file" ] "#file" false)
    Assert.Equal(Ignore.Ignored, verdict [ @"\!file" ] "!file" false)
    Assert.Equal(Ignore.Ignored, verdict [ "*.log   " ] "a.log" false)
    Assert.Equal(Ignore.Ignored, verdict [ "*.log\r" ] "a.log" false)

[<Fact>]
let ``a layer applies below its base only`` () =
    let layers = [ Ignore.compile "sub" [ "*.tmp" ] ]
    Assert.Equal(Ignore.Ignored, Ignore.check layers "sub/a.tmp" false)
    Assert.Equal(Ignore.Ignored, Ignore.check layers "sub/deep/a.tmp" false)
    Assert.Equal(Ignore.NoMatch, Ignore.check layers "a.tmp" false)
    Assert.Equal(Ignore.NoMatch, Ignore.check layers "subx/a.tmp" false)

[<Fact>]
let ``a deeper layer overrides a shallower one`` () =
    let layers = [ Ignore.compile "" [ "*.log" ]; Ignore.compile "sub" [ "!*.log" ] ]
    Assert.Equal(Ignore.Ignored, Ignore.check layers "a.log" false)
    Assert.Equal(Ignore.Whitelisted, Ignore.check layers "sub/a.log" false)

[<Fact>]
let ``a file in an ignored directory stays ignored`` () =
    let layers = rules [ "build/"; "!x.txt" ]
    Assert.True(Ignore.isIgnored layers "build/x.txt" false)
    Assert.False(Ignore.isIgnored layers "src/x.txt" false)

[<Property(Arbitrary = [| typeof<Gens> |])>]
let ``no rules never match`` (dirs: Segment list) (Segment name) =
    let path = String.concat "/" ([ for Segment d in dirs -> d ] @ [ name ])
    Ignore.check [] path false = Ignore.NoMatch

[<Property(Arbitrary = [| typeof<Gens> |])>]
let ``an extension rule ignores the extension at any depth`` (dirs: Segment list) (Segment name) =
    let path = String.concat "/" ([ for Segment d in dirs -> d ] @ [ name + ".log" ])
    verdict [ "*.log" ] path false = Ignore.Ignored

[<Property(Arbitrary = [| typeof<Gens> |])>]
let ``a negation after a match re-includes`` (dirs: Segment list) (Segment name) =
    let path = String.concat "/" ([ for Segment d in dirs -> d ] @ [ name ])
    verdict [ name; "!" + name ] path false = Ignore.Whitelisted

[<Property(Arbitrary = [| typeof<Gens> |])>]
let ``an anchored rule matches only at the root`` (Segment dir) (Segment name) =
    verdict [ "/" + name ] name false = Ignore.Ignored
    && verdict [ "/" + name ] (dir + "/" + name) false = Ignore.NoMatch

[<Fact>]
let ``a plain name matches the last segment only`` () =
    Assert.Equal(Ignore.Ignored, verdict [ "bin" ] "bin" true)
    Assert.Equal(Ignore.Ignored, verdict [ "bin" ] "a/b/bin" false)
    Assert.Equal(Ignore.NoMatch, verdict [ "bin" ] "binx" false)
    Assert.Equal(Ignore.NoMatch, verdict [ "bin" ] "xbin" false)
    Assert.Equal(Ignore.NoMatch, verdict [ "bin" ] "bin/x" false)

[<Fact>]
let ``an anchored literal matches the whole path`` () =
    Assert.Equal(Ignore.Ignored, verdict [ "/a/b" ] "a/b" false)
    Assert.Equal(Ignore.Ignored, verdict [ "a/b/c" ] "a/b/c" false)
    Assert.Equal(Ignore.NoMatch, verdict [ "a/b/c" ] "a/b" false)
    Assert.Equal(Ignore.NoMatch, verdict [ "a/b/c" ] "a/b/cd" false)

[<Fact>]
let ``suffix and prefix globs`` () =
    Assert.Equal(Ignore.Ignored, verdict [ "*.o" ] "x/a.o" false)
    Assert.Equal(Ignore.NoMatch, verdict [ "*.o" ] "x/a.o/b" false)
    Assert.Equal(Ignore.NoMatch, verdict [ "*.o" ] "x.o/a" false)
    Assert.Equal(Ignore.Ignored, verdict [ "/*.o" ] "a.o" false)
    Assert.Equal(Ignore.NoMatch, verdict [ "/*.o" ] "x/a.o" false)
    Assert.Equal(Ignore.Ignored, verdict [ "tmp*" ] "x/tmpfile" false)
    Assert.Equal(Ignore.NoMatch, verdict [ "tmp*" ] "x/atmp" false)
    Assert.Equal(Ignore.Ignored, verdict [ "/tmp*" ] "tmpfile" false)
    Assert.Equal(Ignore.NoMatch, verdict [ "/tmp*" ] "x/tmpfile" false)

[<Fact>]
let ``dir-only and negation apply to the fast matchers`` () =
    Assert.Equal(Ignore.Ignored, verdict [ "obj/" ] "a/obj" true)
    Assert.Equal(Ignore.NoMatch, verdict [ "obj/" ] "a/obj" false)
    Assert.Equal(Ignore.Ignored, verdict [ "*.o/" ] "a.o" true)
    Assert.Equal(Ignore.NoMatch, verdict [ "*.o/" ] "a.o" false)
    Assert.Equal(Ignore.Whitelisted, verdict [ "*.o"; "!keep.o" ] "keep.o" false)
    Assert.Equal(Ignore.Ignored, verdict [ "*.o"; "!keep.o"; "keep*" ] "keep.o" false)
    Assert.Equal(Ignore.Ignored, verdict [ "*.o"; "!keep.o" ] "x.o" false)

[<Fact>]
let ``a match in a deep layer stops the search but a deep miss falls back`` () =
    let layers =
        [ Ignore.compile "" [ "*.log"; "!a.log" ]
          Ignore.compile "sub" [ "unrelated" ]
          Ignore.compile "sub/deep" [ "a.log" ] ]
    Assert.Equal(Ignore.Ignored, Ignore.check layers "sub/deep/a.log" false)
    Assert.Equal(Ignore.Whitelisted, Ignore.check layers "sub/a.log" false)
    Assert.Equal(Ignore.Ignored, Ignore.check layers "sub/b.log" false)
    Assert.Equal(Ignore.NoMatch, Ignore.check layers "sub/b.txt" false)

/// The same rule written so that it is matched by the regex rather than a string comparison.
let private viaRegex (line: string) =
    let negate = line.StartsWith "!"
    let body = if negate then line.Substring 1 else line
    let slash = body.StartsWith "/"
    let body = if slash then body.Substring 1 else body
    let first = body |> Seq.findIndex (fun c -> c <> '*')
    let body = body.Substring(0, first) + "[" + string body[first] + "]" + body.Substring(first + 1)
    (if negate then "!" else "") + (if slash then "/" else "") + body

[<Property(Arbitrary = [| typeof<Gens> |])>]
let ``string matchers agree with the regex form`` (Segment a) (Segment b) (dirs: Segment list) (isDir: bool) =
    let path = String.concat "/" ([ for Segment d in dirs -> d ] @ [ a + b ])
    let forms = [ a; "/" + a; "*" + b; "/*" + b; a + "*"; "/" + a + "*"; a + "/" + b; "!" + a; "*" + a + b ]
    let lines = forms @ [ for f in forms -> f + "/" ]
    lines
    |> List.forall (fun line ->
        verdict [ "*"; line ] path isDir = verdict [ "*"; viaRegex line ] path isDir
        && verdict [ line ] path isDir = verdict [ viaRegex line ] path isDir
        && verdict [ line ] a isDir = verdict [ viaRegex line ] a isDir)
