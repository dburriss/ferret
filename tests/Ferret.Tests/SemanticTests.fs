module Ferret.Tests.SemanticTests

open System
open System.Diagnostics
open System.IO
open Xunit
open Ferret.FileSource
open Ferret.Semantic

let private words (s: string) = s.Split([| ' '; '\n' |], StringSplitOptions.RemoveEmptyEntries)

let private chunks (content: string) =
    Chunker.chunk "doc" (fun s -> (words s).Length) 40 content

// Chunker

[<Fact>]
let ``a heading-only section is merged into the section after it`` () =
    let result = chunks "# Title\n\n## Part\n\nBody text here.\n"
    let only = Assert.Single result
    Assert.Equal(1, only.StartLine)
    Assert.Contains("Body text here.", only.Text)
    Assert.Contains("# Title", only.Text)

[<Fact>]
let ``front matter is merged into the first section`` () =
    let result = chunks "---\ntitle: X\n---\n\n# Heading\n\nBody.\n"
    let only = Assert.Single result
    Assert.Equal(1, only.StartLine)
    Assert.Contains("title: X", only.Text)

[<Fact>]
let ``the embed text carries the file title and the heading path`` () =
    let result = chunks "# Guide\n\nIntro.\n\n## Install\n\nRun it.\n\n### Linux\n\nUse apt.\n"
    let last = List.last result
    Assert.StartsWith("doc > Guide > Install > Linux\n", last.EmbedText)
    Assert.DoesNotContain(">", last.Text)

[<Fact>]
let ``a sibling heading replaces the previous one in the path`` () =
    let result = chunks "# Guide\n\nIntro.\n\n## One\n\nA.\n\n## Two\n\nB.\n"
    Assert.StartsWith("doc > Guide > Two\n", (List.last result).EmbedText)

[<Fact>]
let ``a heading inside a code fence does not start a section`` () =
    let result = chunks "# Guide\n\nText.\n\n```\n# not a heading\n```\n"
    Assert.Single result |> ignore

[<Fact>]
let ``a section over the token limit is split into line ranges that cover it`` () =
    let long = String.Join("\n\n", [ for i in 1..8 -> String.Join(" ", [ for j in 1..15 -> $"w{i}x{j}" ]) ])
    let result = chunks ("# Big\n\n" + long)
    Assert.True(result.Length > 1)
    for c in result do
        Assert.True((words c.Text).Length <= 40)
        Assert.True(c.StartLine <= c.EndLine)

[<Fact>]
let ``empty content gives no chunks`` () = Assert.Empty(chunks "\n\n")

// VectorStore

let private withDir (body: string -> unit) =
    let dir = Directory.CreateTempSubdirectory "ferret-semantic-tests-"
    try
        body dir.FullName
    finally
        dir.Delete true

[<Fact>]
let ``a saved snapshot loads back identical`` () =
    withDir (fun d ->
        let path = VectorStore.pathFor d "some/root"
        let snapshot =
            { ModelId = "m"
              Dimension = 3
              Files =
                Map.ofList
                    [ "/a.md", { Stamp = "s1"; Chunks = [| { StartLine = 1; EndLine = 4 }; { StartLine = 5; EndLine = 9 } |]; Vectors = [| 1f; 0f; 0f; 0f; 1f; 0f |] }
                      "/b.md", { Stamp = "s2"; Chunks = [||]; Vectors = [||] } ] }
        VectorStore.save path snapshot
        Assert.Equal(Some snapshot, VectorStore.load path))

[<Fact>]
let ``a missing, corrupt or truncated file loads as none`` () =
    withDir (fun d ->
        let path = VectorStore.pathFor d "r"
        Assert.Equal(None, VectorStore.load path)
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, "not an index")
        Assert.Equal(None, VectorStore.load path)
        let snapshot = { ModelId = "m"; Dimension = 2; Files = Map.ofList [ "/a", { Stamp = "s"; Chunks = [| { StartLine = 1; EndLine = 1 } |]; Vectors = [| 1f; 0f |] } ] }
        VectorStore.save path snapshot
        let bytes = File.ReadAllBytes path
        File.WriteAllBytes(path, bytes[.. bytes.Length - 3])
        Assert.Equal(None, VectorStore.load path))

[<Fact>]
let ``each root gets its own index file`` () =
    withDir (fun d -> Assert.NotEqual<string>(VectorStore.pathFor d "a", VectorStore.pathFor d "b"))

[<Fact>]
let ``clear removes every index`` () =
    withDir (fun d ->
        let snapshot = { ModelId = "m"; Dimension = 1; Files = Map.empty }
        VectorStore.save (VectorStore.pathFor d "a") snapshot
        VectorStore.clear d
        Assert.Equal(None, VectorStore.load (VectorStore.pathFor d "a")))

// SemanticSearch, with a bag-of-words embedder: texts sharing words score high.

let private vocabulary = [| "cats"; "dogs"; "birds"; "fish"; "purr"; "bark"; "sing"; "swim" |]

type private FakeEmbedder() =
    member val Embedded = 0 with get, set

    interface IEmbedder with
        member _.Dimension = 384
        member _.Timings = { TokenizeTicks = 10L; InferenceTicks = 20L; Tokens = 5 }
        member _.CountTokens t = (words t).Length
        member this.EmbedBatch texts =
            texts
            |> Array.map (fun t ->
                this.Embedded <- this.Embedded + 1
                let v = Array.zeroCreate<float32> 384
                for w in words (t.ToLowerInvariant()) do
                    match Array.tryFindIndex ((=) (w.Trim('.', ','))) vocabulary with
                    | Some i -> v[i] <- v[i] + 1f
                    | None -> v[100 + abs (w.GetHashCode() % 200)] <- v[100 + abs (w.GetHashCode() % 200)] + 0.01f
                let norm = v |> Array.sumBy (fun x -> x * x) |> sqrt |> float32
                v |> Array.map (fun x -> x / norm))
        member _.Dispose() = ()

let private disk = DiskFileSource() :> IFileSource

let private withCorpus (files: (string * string) list) (body: string -> string -> unit) =
    let dir = Directory.CreateTempSubdirectory "ferret-semantic-corpus-"
    let cache = Directory.CreateTempSubdirectory "ferret-semantic-cache-"
    try
        for name, content in files do
            File.WriteAllText(Path.Combine(dir.FullName, name), content)
        body dir.FullName cache.FullName
    finally
        dir.Delete true
        cache.Delete true

let private corpus =
    [ "cats.md", "# Cats\n\nCats purr. Cats purr loudly.\n"
      "dogs.md", "# Dogs\n\nDogs bark. Dogs bark often.\n"
      "birds.md", "# Birds\n\nBirds sing in the morning.\n" ]

let private search cache (fake: FakeEmbedder) root query limit =
    SemanticSearch.search cache (lazy (fake :> IEmbedder)) (fun _ _ -> ()) disk root query limit

[<Fact>]
let ``files rank by similarity to the query`` () =
    withCorpus corpus (fun root cache ->
        let fake = new FakeEmbedder()
        let result = search cache fake root "cats purr" 10
        Assert.Equal(Path.Combine(root, "cats.md"), result.Head.File)
        Assert.True(result.Head.Score > result[1].Score))

[<Fact>]
let ``the excerpt is the lines of the best chunk`` () =
    withCorpus corpus (fun root cache ->
        let result = search cache (new FakeEmbedder()) root "dogs bark" 1
        let doc = Assert.Single result
        Assert.Equal(Path.Combine(root, "dogs.md"), doc.File)
        Assert.NotEmpty doc.Excerpts
        Assert.All(doc.Excerpts, fun h -> Assert.Equal(doc.File, h.Path)))

[<Fact>]
let ``limit caps the number of files`` () =
    withCorpus corpus (fun root cache -> Assert.Equal(2, (search cache (new FakeEmbedder()) root "cats" 2).Length))

[<Fact>]
let ``a second query embeds only the query`` () =
    withCorpus corpus (fun root cache ->
        let fake = new FakeEmbedder()
        search cache fake root "cats" 10 |> ignore
        let afterFirst = fake.Embedded
        search cache fake root "dogs" 10 |> ignore
        Assert.Equal(afterFirst + 1, fake.Embedded))

[<Fact>]
let ``a changed file is re-embedded and a removed file is dropped`` () =
    withCorpus corpus (fun root cache ->
        let fake = new FakeEmbedder()
        search cache fake root "cats" 10 |> ignore
        let afterFirst = fake.Embedded
        File.WriteAllText(Path.Combine(root, "dogs.md"), "# Dogs\n\nDogs bark. Dogs swim. Fish swim.\n\nMore text for a new size.\n")
        File.Delete(Path.Combine(root, "birds.md"))
        let result = search cache fake root "cats" 10
        Assert.Equal(2, result.Length)
        Assert.True(fake.Embedded > afterFirst + 1)
        Assert.DoesNotContain(result, fun d -> d.File.EndsWith "birds.md"))

[<Fact>]
let ``build indexes without a query and a later search reuses it`` () =
    withCorpus corpus (fun root cache ->
        let fake = new FakeEmbedder()
        let count = SemanticSearch.build cache (lazy (fake :> IEmbedder)) (fun _ _ -> ()) disk root
        Assert.Equal(3, count)
        let afterBuild = fake.Embedded
        search cache fake root "cats" 10 |> ignore
        Assert.Equal(afterBuild + 1, fake.Embedded))

[<Fact>]
let ``a blank query finds nothing and never loads the embedder`` () =
    withCorpus corpus (fun root cache ->
        let result = SemanticSearch.search cache (lazy (failwith "no embedder")) (fun _ _ -> ()) disk root "  " 10
        Assert.Empty result)

[<Fact>]
let ``embedding phases carry the tokenize and inference breakdown`` () =
    withCorpus corpus (fun root cache ->
        let finished = ResizeArray<Activity>()
        use listener = new ActivityListener()
        listener.ShouldListenTo <- (fun source -> source.Name = "Ferret")
        listener.Sample <- SampleActivity<ActivityContext>(fun _ -> ActivitySamplingResult.AllData)
        listener.ActivityStopped <- (fun a -> lock finished (fun () -> finished.Add a))
        ActivitySource.AddActivityListener listener
        search cache (new FakeEmbedder()) root "cats" 10 |> ignore
        let phase name = finished |> Seq.find (fun a -> a.OperationName = name)
        for name in [ "ferret.index.build"; "ferret.embed" ] do
            let a = phase name
            for key in [ "tokenize.ms"; "inference.ms"; "tokens" ] do
                Assert.NotNull(a.GetTagItem key)
        Assert.Equal("query", string ((phase "ferret.embed").GetTagItem "kind")))
