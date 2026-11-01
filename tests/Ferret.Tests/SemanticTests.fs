module Ferret.Tests.SemanticTests

open System
open System.Diagnostics
open System.IO
open System.Threading
open Xunit
open Ferret
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
    let mutable embedded = 0
    member _.Embedded = Volatile.Read(&embedded)

    interface IEmbedder with
        member _.Dimension = 384
        member _.Timings = { TokenizeTicks = 10L; InferenceTicks = 20L; Tokens = 5 }
        member _.CountTokens t = (words t).Length
        member _.EmbedBatch texts =
            texts
            |> Array.map (fun t ->
                Interlocked.Increment(&embedded) |> ignore
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
        // The listener is process-wide, so parallel lexical tests also emit "ferret.index.build" (untagged).
        let phase name =
            lock finished (fun () ->
                finished |> Seq.find (fun a -> a.OperationName = name && not (isNull (a.GetTagItem "tokenize.ms"))))
        for name in [ "ferret.index.build"; "ferret.embed" ] do
            let a = phase name
            for key in [ "tokenize.ms"; "inference.ms"; "tokens" ] do
                Assert.NotNull(a.GetTagItem key)
        Assert.Equal("query", string ((phase "ferret.embed").GetTagItem "kind")))

[<Fact>]
let ``a warm build never loads the embedder`` () =
    withCorpus corpus (fun root cache ->
        let fake = new FakeEmbedder()
        SemanticSearch.build cache (lazy (fake :> IEmbedder)) (fun _ _ -> ()) disk root |> ignore
        let count = SemanticSearch.build cache (lazy (failwith "no embedder")) (fun _ _ -> ()) disk root
        Assert.Equal(3, count))

[<Fact>]
let ``parallel embedding matches sequential single embedding in order, with serialised progress`` () =
    let fake = new FakeEmbedder()
    let texts = Array.init 200 (fun i -> String.replicate (1 + (i * 7) % 13) (if i % 2 = 0 then "cats purr " else "dogs bark ") + string i)
    let sequential = texts |> Array.map (fun t -> (fake :> IEmbedder).EmbedBatch [| t |] |> Array.head)
    let seen = ResizeArray<int>()
    let parallel' = SemanticSearch.embedAll (fake :> IEmbedder) (fun n _ -> seen.Add n) texts
    Assert.Equal<float32[][]>(sequential, parallel')
    Assert.Equal<int>(List.init 201 id, List.ofSeq seen)

/// The real int8 model, when one is configured or cached: parallel single-chunk embedding must be
/// bit-identical to sequential, and tokenizing from many threads must agree with one thread.
[<Fact>]
let ``parallel onnx embedding is bit-identical to sequential`` () =
    let dir =
        match Environment.GetEnvironmentVariable "FERRET_MODEL_DIR" with
        | null | "" -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".cache/ferret/models/bge-small-en-v1.5-int8")
        | d -> d
    let onnx = Path.Combine(dir, "model_quantized.onnx")
    let vocab = Path.Combine(dir, "vocab.txt")
    if File.Exists onnx && File.Exists vocab then
        use embedder = OnnxEmbedder.FromFiles(onnx, vocab)
        let e = embedder :> IEmbedder
        let texts = Array.init 64 (fun i -> String.replicate (1 + (i * 5) % 17) "Tokenize the chunks, then embed them. " + string i)
        let sequential = texts |> Array.map (fun t -> (e.EmbedBatch [| t |]).[0])
        let par = SemanticSearch.embedAll e (fun _ _ -> ()) texts
        Assert.Equal<float32[][]>(sequential, par)
        let counts = texts |> Array.map e.CountTokens
        let parCounts = texts |> Array.Parallel.map e.CountTokens
        Assert.Equal<int[]>(counts, parCounts)

// HybridSearch: BM25 and the fake semantic ranker, fused.

let private hybrid cache (fake: FakeEmbedder) root query limit =
    let lexical = Search.search Ranking.bm25 disk [ root ] query HybridSearch.ArmDepth
    let semantic = search cache fake root query HybridSearch.ArmDepth
    HybridSearch.fuse HybridSearch.DefaultK 1.0 1.0 limit lexical semantic

[<Fact>]
let ``hybrid ranks a file both rankers like first`` () =
    withCorpus corpus (fun root cache ->
        let result = hybrid cache (new FakeEmbedder()) root "cats purr" 10
        Assert.Equal(Path.Combine(root, "cats.md"), result.Head.File))

[<Fact>]
let ``hybrid includes files only the semantic ranker finds`` () =
    withCorpus corpus (fun root cache ->
        // "felines" matches no file lexically; the fake embedder still ranks all three files.
        let result = hybrid cache (new FakeEmbedder()) root "felines" 10
        Assert.Equal(3, result.Length))

[<Fact>]
let ``hybrid prefers lexical excerpts and falls back to the semantic chunk`` () =
    withCorpus corpus (fun root cache ->
        let result = hybrid cache (new FakeEmbedder()) root "cats purr" 10
        let cats = result |> List.find (fun d -> d.File.EndsWith "cats.md")
        Assert.All(cats.Excerpts, fun h -> Assert.Contains("cats", h.Line.ToLowerInvariant()))
        let dogs = result |> List.find (fun d -> d.File.EndsWith "dogs.md")
        Assert.NotEmpty dogs.Excerpts)

[<Fact>]
let ``hybrid limit caps the number of files`` () =
    withCorpus corpus (fun root cache ->
        Assert.Single(hybrid cache (new FakeEmbedder()) root "cats purr" 1) |> ignore)

[<Fact>]
let ``mergeByScore sorts by score then path and truncates`` () =
    let d file score : Ferret.Domain.ScoredDoc = { File = file; Score = score; Excerpts = [] }
    let result = HybridSearch.mergeByScore 2 [ d "b" 1.0; d "a" 1.0; d "c" 2.0 ]
    Assert.Equal<string list>([ "c"; "a" ], result |> List.map _.File)

[<Fact>]
let ``a cancelled build writes no index`` () =
    withCorpus corpus (fun root cache ->
        let fake = new FakeEmbedder()
        use source = new System.Threading.CancellationTokenSource()
        // Cancel once the first chunk is embedded, part way through the build.
        let progress done' _ = if done' = 1 then source.Cancel()
        Assert.ThrowsAny<OperationCanceledException>(fun () ->
            SemanticSearch.buildWith source.Token cache (lazy (fake :> IEmbedder)) progress disk root |> ignore)
        |> ignore
        Assert.False(File.Exists(VectorStore.pathFor cache root))
        Assert.Equal(3, SemanticSearch.build cache (lazy (fake :> IEmbedder)) (fun _ _ -> ()) disk root))

[<Fact>]
let ``a cancelled search stops before embedding the query`` () =
    withCorpus corpus (fun root cache ->
        let fake = new FakeEmbedder()
        use source = new System.Threading.CancellationTokenSource()
        source.Cancel()
        Assert.ThrowsAny<OperationCanceledException>(fun () ->
            SemanticSearch.searchWith source.Token cache (lazy (fake :> IEmbedder)) (fun _ _ -> ()) disk root "cats" 10 |> ignore)
        |> ignore
        Assert.Equal(0, fake.Embedded))
