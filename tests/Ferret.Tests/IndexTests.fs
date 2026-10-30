module Ferret.Tests.IndexTests

open System
open System.IO
open System.Text.Json
open Xunit
open Ferret
open Ferret.Domain
open Ferret.FileSource
open Ferret.Index

let private disk = DiskFileSource() :> IFileSource

/// Counts `ReadLines` calls on the disk source.
let private counting () =
    let reads = ref 0
    let source =
        { new IFileSource with
            member _.Kind p = disk.Kind p
            member _.Enumerate r = disk.Enumerate r
            member _.ReadLines p =
                reads.Value <- reads.Value + 1
                disk.ReadLines p
            member _.Stamp p = disk.Stamp p
            member _.ReadStdin() = Seq.empty }
    source, reads

/// Creates the files, runs `body` with the root, then cleans up.
let private withTree (files: (string * string) list) (body: string -> unit) =
    let dir = Directory.CreateTempSubdirectory "ferret-index-tests-"
    try
        for rel, content in files do
            let path = Path.Combine(dir.FullName, rel)
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, content)
        body dir.FullName
    finally
        dir.Delete true

let private perRun root query = Search.search Ranking.bm25 disk [ root ] query 100

let private indexed (store: IIndexStore) root query =
    Search.searchIndexed store Ranking.bm25 disk [ root ] query 100

/// Same files, order, scores and excerpts, compared exactly.
let private assertSame (expected: ScoredDoc list) (actual: ScoredDoc list) = Assert.Equal<ScoredDoc list>(expected, actual)

let private queries () =
    use doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(SmokeTests.smokeDir (), "queries.json")))
    [ for q in doc.RootElement.EnumerateArray() -> q.GetProperty("query").GetString() ]

[<Fact>]
let ``indexed search equals per-run search on the smoke corpus, cold and warm`` () =
    let root = SmokeTests.smokeDir ()
    let store = MemoryIndexStore()
    let qs = queries ()
    Assert.NotEmpty qs
    for _ in 1..2 do // cold, then warm
        for q in qs do
            assertSame (perRun root q) (indexed store root q)
    Assert.Equal(Seq.length (disk.Enumerate root), store.Current.Value.Entries.Count)

[<Fact>]
let ``a warm query reads only the files it shows excerpts for`` () =
    let root = SmokeTests.smokeDir ()
    let source, reads = counting ()
    let store = MemoryIndexStore()
    let run () = Search.searchIndexed store Ranking.bm25 source [ root ] "domain aggregate" 3
    run () |> ignore
    Assert.True(reads.Value >= 30, $"cold run read {reads.Value} files")
    reads.Value <- 0
    let results = run ()
    Assert.NotEmpty results
    Assert.True(reads.Value <= results.Length, $"warm run read {reads.Value} files for {results.Length} results")

[<Fact>]
let ``new, edited, deleted and newly ignored files are picked up`` () =
    withTree [ "a.txt", "apple\n"; "b.txt", "apple banana\n"; "c.txt", "cherry apple apple\n" ] (fun d ->
        let store = MemoryIndexStore()
        let check () =
            for q in [ "apple"; "banana"; "cherry"; "date"; "apple date" ] do
                assertSame (perRun d q) (indexed store d q)
        check ()
        File.WriteAllText(Path.Combine(d, "new.txt"), "date apple\n")
        check ()
        let b = Path.Combine(d, "b.txt")
        File.WriteAllText(b, "banana banana date\n")
        File.SetLastWriteTimeUtc(b, File.GetLastWriteTimeUtc b + TimeSpan.FromSeconds 5.0)
        check ()
        File.Delete(Path.Combine(d, "a.txt"))
        check ()
        Assert.False(store.Current.Value.Entries.ContainsKey(Path.Combine(d, "a.txt")))
        File.WriteAllText(Path.Combine(d, ".ignore"), "c.txt\n")
        check ()
        Assert.False(store.Current.Value.Entries.ContainsKey(Path.Combine(d, "c.txt"))))

[<Fact>]
let ``an edit that keeps the size is picked up when the modified time moves`` () =
    withTree [ "a.txt", "apple\n"; "b.txt", "apple\n" ] (fun d ->
        let store = MemoryIndexStore()
        indexed store d "apple" |> ignore
        let a = Path.Combine(d, "a.txt")
        File.WriteAllText(a, "grape\n")
        File.SetLastWriteTimeUtc(a, File.GetLastWriteTimeUtc a + TimeSpan.FromSeconds 5.0)
        assertSame (perRun d "apple") (indexed store d "apple")
        assertSame (perRun d "grape") (indexed store d "grape"))

[<Fact>]
let ``markdown headings and frontmatter still boost through the index`` () =
    withTree
        [ "a.md", "# Other\nmock mock\n"
          "b.md", "---\ntitle: Mock\ntags: [mock]\n---\nother text\n"
          "c.md", "# mock\nother text\n" ]
        (fun d ->
            let store = MemoryIndexStore()
            let expected = perRun d "mock"
            assertSame expected (indexed store d "mock")
            assertSame expected (indexed store d "mock"))

[<Fact>]
let ``a snapshot from other code is ignored and replaced`` () =
    withTree [ "a.txt", "apple\n"; "b.txt", "banana\n" ] (fun d ->
        let bogus =
            Collections.Generic.Dictionary<string, Entry>(
                dict
                    [ Path.GetFullPath(Path.Combine(d, "b.txt")),
                      { Stamp = disk.Stamp(Path.Combine(d, "b.txt")).Value
                        Length = 1
                        TermFreq = Map.ofList [ "apple", 99 ]
                        FieldTerms = Map.empty } ]
            )
        let store = MemoryIndexStore()
        (store :> IIndexStore).Save { Version = version + 1; Entries = bogus }
        assertSame (perRun d "apple") (indexed store d "apple")
        Assert.Equal(version, store.Current.Value.Version))

[<Fact>]
let ``entries outside the searched roots are kept`` () =
    withTree [ "x/a.txt", "apple\n"; "y/b.txt", "apple\n" ] (fun d ->
        let store = MemoryIndexStore()
        indexed store (Path.Combine(d, "x")) "apple" |> ignore
        indexed store (Path.Combine(d, "y")) "apple" |> ignore
        Assert.Equal(2, store.Current.Value.Entries.Count))

let private sameEntries (a: Snapshot) (b: Snapshot) =
    a.Version = b.Version
    && a.Entries.Count = b.Entries.Count
    && a.Entries |> Seq.forall (fun kv -> match b.Entries.TryGetValue kv.Key with | true, e -> e = kv.Value | _ -> false)

[<Fact>]
let ``the disk store round-trips a snapshot`` () =
    withTree [ "a.md", "---\ntitle: Mock\n---\n# Heading\nmock text\n"; "b.txt", "other\n" ] (fun d ->
        let memory = MemoryIndexStore()
        indexed memory d "mock" |> ignore
        let disk = DiskIndexStore.DiskIndexStore(Path.Combine(d, "cache", "lexical.idx")) :> IIndexStore
        disk.Save memory.Current.Value
        let loaded = disk.Load()
        Assert.True(loaded.IsSome)
        Assert.True(sameEntries memory.Current.Value loaded.Value))

[<Fact>]
let ``a missing, garbage or truncated cache file is rebuilt, not fatal`` () =
    withTree [ "a.txt", "apple\n"; "b.txt", "apple apple\n" ] (fun d ->
        let cache = Path.Combine(d, "cache.idx")
        let store = DiskIndexStore.DiskIndexStore cache :> IIndexStore
        Assert.True(store.Load().IsNone)
        // A valid cache to truncate later. The cache file sits outside the searched root.
        let src = Path.Combine(d, "src")
        Directory.CreateDirectory src |> ignore
        File.Move(Path.Combine(d, "a.txt"), Path.Combine(src, "a.txt"))
        File.Move(Path.Combine(d, "b.txt"), Path.Combine(src, "b.txt"))
        assertSame (perRun src "apple") (indexed store src "apple")
        let valid = File.ReadAllBytes cache
        Assert.True(store.Load().IsSome)
        for bad in [ [| 1uy; 2uy; 3uy |]; Array.zeroCreate<byte> 64; valid[.. valid.Length / 2] ] do
            File.WriteAllBytes(cache, bad)
            Assert.True(store.Load().IsNone)
            assertSame (perRun src "apple") (indexed store src "apple")
            Assert.True(store.Load().IsSome))

[<Fact>]
let ``an unwritable cache location does not fail the search`` () =
    withTree [ "a.txt", "apple\n" ] (fun d ->
        // A file where the cache directory should be.
        let blocker = Path.Combine(d, "blocker")
        File.WriteAllText(blocker, "")
        let store = DiskIndexStore.DiskIndexStore(Path.Combine(blocker, "lexical.idx")) :> IIndexStore
        let src = Path.Combine(d, "src")
        Directory.CreateDirectory src |> ignore
        File.WriteAllText(Path.Combine(src, "a.txt"), "apple\n")
        assertSame (perRun src "apple") (indexed store src "apple"))

/// Runs `body` while a listener collects its finished "Ferret" activities, in completion order.
let private withPhases (body: unit -> unit) : Diagnostics.Activity list =
    let finished = ResizeArray<Diagnostics.Activity>()
    use listener = new Diagnostics.ActivityListener()
    listener.ShouldListenTo <- (fun source -> source.Name = Search.ActivitySourceName)
    listener.Sample <- Diagnostics.SampleActivity<Diagnostics.ActivityContext>(fun _ -> Diagnostics.ActivitySamplingResult.AllData)
    // Tests run in parallel and the source is process-wide; search is synchronous, so keep this thread's.
    let thread = Environment.CurrentManagedThreadId
    listener.ActivityStopped <- (fun a -> if Environment.CurrentManagedThreadId = thread then lock finished (fun () -> finished.Add a))
    Diagnostics.ActivitySource.AddActivityListener listener
    body ()
    List.ofSeq finished

let private tagOf (key: string) (activity: Diagnostics.Activity) = activity.GetTagItem key

[<Fact>]
let ``indexed search emits its phases, and a warm run builds no files`` () =
    withTree [ "a.txt", "apple pie\n"; "b.txt", "apple tart\n"; "c.txt", "cherry\n" ] (fun root ->
        let store = MemoryIndexStore()
        let expected =
            [ "ferret.index.load"; "ferret.walk"; "ferret.index.build"; "ferret.index.save"; "ferret.rank"; "ferret.excerpts" ]
        let cold = withPhases (fun () -> indexed store root "apple" |> ignore)
        let warm = withPhases (fun () -> indexed store root "apple" |> ignore)
        Assert.Equal<string list>(expected, cold |> List.map _.OperationName)
        Assert.Equal<string list>(expected, warm |> List.map _.OperationName)
        let build (phases: Diagnostics.Activity list) = phases |> List.find (fun a -> a.OperationName = "ferret.index.build")
        Assert.Equal(box 3, tagOf "files.built" (build cold))
        Assert.Equal(box 0, tagOf "files.reused" (build cold))
        Assert.Equal(box 0, tagOf "files.built" (build warm))
        Assert.Equal(box 3, tagOf "files.reused" (build warm)))

[<Fact>]
let ``per-run search emits walk, read, rank and excerpts`` () =
    withTree [ "a.txt", "apple\n" ] (fun root ->
        let phases = withPhases (fun () -> perRun root "apple" |> ignore)
        Assert.Equal<string list>([ "ferret.walk"; "ferret.read"; "ferret.rank"; "ferret.excerpts" ], phases |> List.map _.OperationName))
