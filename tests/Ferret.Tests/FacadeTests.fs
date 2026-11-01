module Ferret.Tests.FacadeTests

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open Xunit
open Ferret
open Ferret.Domain

/// Creates the files, runs `body` with the root and a cache directory, then cleans up.
let private withTree (files: (string * string) list) (body: string -> string -> unit) =
    let dir = Directory.CreateTempSubdirectory "ferret-facade-tests-"
    try
        let root = Path.Combine(dir.FullName, "tree")
        for rel, content in files do
            let path = Path.Combine(root, rel)
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, content)
        body root (Path.Combine(dir.FullName, "cache"))
    finally
        dir.Delete true

let private tree = [ "apple.txt", "apple pie recipe\n"; "grape.txt", "grape juice\n" ]

let private client root cache = new Ferret.FerretClient(FerretOptions(Roots = [| root |], CacheDir = cache))

[<Fact>]
let ``a lexical search finds the file with the term`` () =
    withTree tree (fun root cache ->
        use ferret = client root cache
        let hits = ferret.Search("apple", 10)
        Assert.Equal<string list>([ Path.Combine(root, "apple.txt") ], hits |> List.map _.File))

[<Fact>]
let ``the limit caps the results`` () =
    withTree [ "a.txt", "apple\n"; "b.txt", "apple apple\n" ] (fun root cache ->
        use ferret = client root cache
        Assert.Single(ferret.Search("apple", 1)) |> ignore)

[<Fact>]
let ``building the index creates the lexical index file and cleaning removes it`` () =
    withTree tree (fun root cache ->
        use ferret = client root cache
        Assert.Equal(2, ferret.BuildIndex Lexical)
        Assert.True(File.Exists(Path.Combine(cache, "lexical.idx")))
        ferret.Clean Lexical
        Assert.False(File.Exists(Path.Combine(cache, "lexical.idx"))))

[<Fact>]
let ``search without an index gives the same results as with one`` () =
    withTree tree (fun root cache ->
        use indexed = client root cache
        use direct = new Ferret.FerretClient(FerretOptions(Roots = [| root |], CacheDir = cache, UseIndex = false))
        Assert.Equal<ScoredDoc list>(indexed.Search("grape", 10), direct.Search("grape", 10)))

[<Fact>]
let ``the default mode is used when none is given`` () =
    withTree tree (fun root cache ->
        use ferret = new Ferret.FerretClient(FerretOptions(Roots = [| root |], CacheDir = cache, DefaultMode = Lexical))
        Assert.Equal<ScoredDoc list>(ferret.Search("apple", 10, Lexical), ferret.Search("apple", 10)))

[<Fact>]
let ``a missing root is an error`` () =
    withTree tree (fun root cache ->
        use ferret = client (Path.Combine(root, "nope")) cache
        Assert.Throws<DirectoryNotFoundException>(fun () -> ferret.Search("apple", 10) |> ignore) |> ignore)

[<Fact>]
let ``a limit below one is an error`` () =
    withTree tree (fun root cache ->
        use ferret = client root cache
        Assert.Throws<ArgumentException>(fun () -> ferret.Search("apple", 0) |> ignore) |> ignore)

[<Fact>]
let ``a model directory without the model files is reported`` () =
    withTree tree (fun root cache ->
        use ferret = new Ferret.FerretClient(FerretOptions(Roots = [| root |], CacheDir = cache, ModelDir = root))
        Assert.Throws<Semantic.ModelUnavailable>(fun () -> ferret.EnsureModel() |> ignore) |> ignore)

[<Fact>]
let ``grep returns the matching lines with their numbers`` () =
    withTree [ "a.txt", "one\ntwo apple\nthree\n"; "b.txt", "nothing\n" ] (fun root cache ->
        use ferret = client root cache
        let hits = ferret.Grep "a.ple" |> List.ofSeq
        Assert.Equal<Hit list>([ { Path = Path.Combine(root, "a.txt"); LineNumber = 2; Line = "two apple" } ], hits))

[<Fact>]
let ``grep honours the match options`` () =
    withTree [ "a.txt", "Apple\na.ple\n" ] (fun root cache ->
        use ferret = client root cache
        let options = { MatchOptions.defaults with IgnoreCase = true; FixedStrings = true }
        Assert.Equal<int list>([ 2 ], ferret.Grep("A.PLE", options) |> Seq.map _.LineNumber |> List.ofSeq))

[<Fact>]
let ``grep rejects a bad pattern and a missing root`` () =
    withTree tree (fun root cache ->
        use ferret = client root cache
        Assert.ThrowsAny<ArgumentException>(fun () -> ferret.Grep "(" |> ignore) |> ignore
        use missing = client (Path.Combine(root, "nope")) cache
        Assert.Throws<DirectoryNotFoundException>(fun () -> missing.Grep "a" |> ignore) |> ignore)

let private grepFiles (ferret: Ferret.FerretClient) =
    ferret.Grep "a" |> Seq.map (fun h -> Path.GetFileName h.Path) |> Seq.distinct |> List.ofSeq

[<Fact>]
let ``ignore patterns exclude files from grep and search`` () =
    withTree tree (fun root cache ->
        use ferret = new Ferret.FerretClient(FerretOptions(Roots = [| root |], CacheDir = cache, IgnorePatterns = [| "apple.txt" |]))
        Assert.Equal<string list>([ "grape.txt" ], grepFiles ferret)
        Assert.Empty(ferret.Search("apple", 10)))

[<Fact>]
let ``ignore patterns win over ignore files and can re-include`` () =
    withTree [ ".ignore", "*.txt\n"; "apple.txt", "apple\n"; "grape.txt", "grape\n"; "plum.txt", "plum\n" ] (fun root cache ->
        use ferret =
            new Ferret.FerretClient(FerretOptions(Roots = [| root |], CacheDir = cache, IgnorePatterns = [| "!apple.txt"; "!plum.txt"; "plum.txt" |]))
        Assert.Equal<string list>([ "apple.txt" ], grepFiles ferret))

[<Fact>]
let ``offline mode reports a missing model without downloading`` () =
    withTree tree (fun root cache ->
        use ferret = new Ferret.FerretClient(FerretOptions(Roots = [| root |], CacheDir = cache, Offline = true))
        let ex = Assert.Throws<Semantic.ModelUnavailable>(fun () -> ferret.EnsureModel() |> ignore)
        Assert.Contains("offline", ex.Data0))

[<Fact>]
let ``IsModelAvailable is false when the model is missing`` () =
    withTree tree (fun root cache ->
        use ferret = new Ferret.FerretClient(FerretOptions(Roots = [| root |], CacheDir = cache, ModelDir = root))
        Assert.False ferret.IsModelAvailable)

[<Fact>]
let ``a failed download names the model url`` () =
    withTree tree (fun root cache ->
        use ferret = new Ferret.FerretClient(FerretOptions(Roots = [| root |], CacheDir = cache, ModelUrl = "http://127.0.0.1:1/m"))
        let ex = Assert.Throws<Semantic.ModelUnavailable>(fun () -> ferret.EnsureModel() |> ignore)
        Assert.Contains("http://127.0.0.1:1/m", ex.Data0))

let private cancelled =
    let source = new CancellationTokenSource()
    source.Cancel()
    source.Token

[<Fact>]
let ``a cancelled token stops a lexical search`` () =
    withTree tree (fun root cache ->
        use ferret = client root cache
        Assert.Throws<OperationCanceledException>(fun () -> ferret.Search("apple", 10, Lexical, cancelled) |> ignore) |> ignore
        use direct = new Ferret.FerretClient(FerretOptions(Roots = [| root |], CacheDir = cache, UseIndex = false))
        Assert.Throws<OperationCanceledException>(fun () -> direct.Search("apple", 10, Lexical, cancelled) |> ignore) |> ignore)

[<Fact>]
let ``a cancelled build leaves no index behind`` () =
    withTree tree (fun root cache ->
        use ferret = client root cache
        Assert.Throws<OperationCanceledException>(fun () -> ferret.BuildIndex(Lexical, cancelled) |> ignore) |> ignore
        Assert.False(File.Exists(Path.Combine(cache, "lexical.idx")))
        Assert.Equal(2, ferret.BuildIndex Lexical))

[<Fact>]
let ``an uncancelled token gives the same results as none`` () =
    withTree tree (fun root cache ->
        use ferret = client root cache
        Assert.Equal<ScoredDoc list>(ferret.Search("apple", 10), ferret.Search("apple", 10, CancellationToken.None)))

[<Fact>]
let ``the async search and build run and honour cancellation`` () =
    withTree tree (fun root cache ->
        use ferret = client root cache
        Assert.Equal(2, ferret.BuildIndexAsync(Lexical, CancellationToken.None).Result)
        Assert.Equal<string list>([ Path.Combine(root, "apple.txt") ], ferret.SearchAsync("apple", 10, CancellationToken.None).Result |> List.map _.File)
        let search = ferret.SearchAsync("apple", 10, cancelled)
        Assert.ThrowsAny<OperationCanceledException>(fun () -> search.GetAwaiter().GetResult() |> ignore) |> ignore
        let build = ferret.BuildIndexAsync(Lexical, cancelled)
        Assert.ThrowsAny<OperationCanceledException>(fun () -> build.GetAwaiter().GetResult() |> ignore) |> ignore)

let private noTempFiles (cache: string) =
    let models = Path.Combine(cache, "models")
    Assert.True(not (Directory.Exists models) || Directory.GetFiles(models, "*.tmp", SearchOption.AllDirectories).Length = 0)

[<Fact>]
let ``a cancelled model download is a cancellation, not a missing model`` () =
    withTree tree (fun root cache ->
        use ferret = new Ferret.FerretClient(FerretOptions(Roots = [| root |], CacheDir = cache, ModelUrl = "http://127.0.0.1:1/m"))
        let download = ferret.EnsureModelAsync cancelled
        Assert.ThrowsAny<OperationCanceledException>(fun () -> download.GetAwaiter().GetResult() |> ignore) |> ignore
        noTempFiles cache)

[<Fact>]
let ``cancelling a stalled model download stops it`` () =
    withTree tree (fun root cache ->
        // A mirror that accepts the connection and never answers.
        let listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        try
            let url = $"http://127.0.0.1:{(listener.LocalEndpoint :?> IPEndPoint).Port}/m"
            use ferret = new Ferret.FerretClient(FerretOptions(Roots = [| root |], CacheDir = cache, ModelUrl = url))
            use source = new CancellationTokenSource(TimeSpan.FromMilliseconds 300.0)
            let download = ferret.EnsureModelAsync source.Token
            Assert.ThrowsAny<OperationCanceledException>(fun () -> download.GetAwaiter().GetResult() |> ignore) |> ignore
            noTempFiles cache
        finally
            listener.Stop())
