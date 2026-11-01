namespace Ferret.Semantic

open System
open System.Diagnostics
open System.Numerics.Tensors
open System.Threading
open System.Threading.Tasks
open Ferret.Domain
open Ferret.FileSource

/// Ranked semantic search: files are chunked and embedded, the query is embedded, and every chunk is
/// scored by a dot product (brute force). A file ranks as its best chunk (`max`), and that chunk's lines
/// are its excerpt. The index is synced on query, embedding only new or changed files.
module SemanticSearch =

    // Same source name as `Ferret.Search`, so one listener (`--timings`) sees both.
    let private activitySource = new ActivitySource("Ferret")

    let private phase (name: string) (body: Activity -> 'a) : 'a =
        use activity = activitySource.StartActivity name
        body activity

    let private tag (activity: Activity) (key: string) (value: obj) =
        if not (isNull activity) then activity.SetTag(key, value) |> ignore

    /// The embedder's timings so far; zero while it has not been created (so reading it never loads the model).
    let private timingsOf (embedder: Lazy<IEmbedder>) =
        if embedder.IsValueCreated then embedder.Value.Timings else EmbedTimings.Zero

    /// Tags `activity` with the tokenize and inference time and the tokens encoded since `before`. These
    /// interleave per chunk, so they are totals on the phase rather than activities of their own. With
    /// parallel workers they are summed per-thread times, so they can exceed the phase's wall time.
    let private tagEmbedTimings (activity: Activity) (before: EmbedTimings) (embedder: Lazy<IEmbedder>) =
        let after = timingsOf embedder
        let ms (ticks: int64) = Math.Round(float ticks * 1000.0 / float Stopwatch.Frequency, 1)
        tag activity "tokenize.ms" (ms (after.TokenizeTicks - before.TokenizeTicks))
        tag activity "inference.ms" (ms (after.InferenceTicks - before.InferenceTicks))
        tag activity "tokens" (after.Tokens - before.Tokens)

    /// Token budget of one chunk; the model's limit is 512, and the breadcrumb is counted inside it.
    let private maxTokens = 480

    /// Excerpt lines shown per file, as for lexical search.
    let private maxExcerpts = 3

    let private chunkFile (countTokens: string -> int) (files: IFileSource) (path: string) : Chunk list option =
        files.ReadLines path
        |> Option.map (fun lines ->
            let title = IO.Path.GetFileNameWithoutExtension path
            Chunker.chunk title countTokens maxTokens (String.concat "\n" lines))

    /// Worker count for parallel work: `FERRET_THREADS` if it is a positive integer, else the core count.
    let private degreeOfParallelism () =
        match Int32.TryParse(Environment.GetEnvironmentVariable "FERRET_THREADS") with
        | true, n when n > 0 -> n
        | _ -> Environment.ProcessorCount

    /// Embeds `texts` one chunk per call: with the int8 model a chunk's vector depends on its batch
    /// companions (dynamic quantisation), so singles keep vectors deterministic and incremental indexing
    /// stable. Parallelism is across chunks, longest first so the stragglers are short; results are
    /// written by original index, so the order is unchanged. `progress` gets 0 up front, then each
    /// completed count, serialised and increasing. Cancelling `ct` stops it with `OperationCanceledException`.
    let embedAllWith (ct: CancellationToken) (embedder: IEmbedder) (progress: int -> int -> unit) (texts: string[]) : float32[][] =
        let total = texts.Length
        let results = Array.zeroCreate<float32[]> total
        let order = Array.init total id |> Array.sortBy (fun i -> -texts[i].Length)
        let completed = ref 0
        let gate = obj ()
        progress 0 total
        Parallel.ForEach(
            order,
            ParallelOptions(MaxDegreeOfParallelism = degreeOfParallelism (), CancellationToken = ct),
            fun (i: int) ->
                results[i] <- (embedder.EmbedBatch [| texts[i] |])[0]
                lock gate (fun () -> progress (Interlocked.Increment &completed.contents) total)
        )
        |> ignore
        results

    /// `embedAllWith` that cannot be cancelled.
    let embedAll (embedder: IEmbedder) (progress: int -> int -> unit) (texts: string[]) : float32[][] =
        embedAllWith CancellationToken.None embedder progress texts

    let private entryKey (path: string) = IO.Path.GetFullPath path

    /// Brings the index for `root` up to date and returns it with the walked paths: `(path, key)` for
    /// every indexed file. Files that are new or changed are chunked and embedded; entries for files
    /// that have gone are dropped. `embedder` is created only if there is something to embed or search.
    let private sync
        (ct: CancellationToken)
        (cacheDir: string)
        (embedder: Lazy<IEmbedder>)
        (progress: int -> int -> unit)
        (files: IFileSource)
        (root: string)
        : Snapshot * (string * string) list =
        let storePath = VectorStore.pathFor cacheDir root
        let dim = 384
        let prior =
            phase "ferret.index.load" (fun a ->
                let loaded =
                    VectorStore.load storePath
                    |> Option.filter (fun s -> s.ModelId = ModelLocator.ModelId && s.Dimension = dim)
                tag a "loaded" loaded.IsSome
                match loaded with
                | Some s -> s.Files
                | None -> Map.empty)
        let walked =
            phase "ferret.stamp" (fun a ->
                let walked =
                    files.Enumerate root
                    |> Seq.distinct
                    |> Seq.map (fun p -> p, entryKey p, files.Stamp p)
                    |> List.ofSeq
                tag a "files" walked.Length
                walked)
        // Stamp first, then read: a file changed while being read then looks stale on the next run.
        let stale =
            walked
            |> List.filter (fun (_, key, stamp) ->
                match Map.tryFind key prior, stamp with
                | Some entry, Some s when entry.Stamp = s -> false
                | _, Some _ -> true
                | _ -> false)
        let built =
            phase "ferret.index.build" (fun a ->
                let before = timingsOf embedder
                // The model loads on the first token count, so a warm index, or stale files that
                // turn out unreadable (binary), never load it. `Lazy` is thread-safe.
                let countTokens text = embedder.Value.CountTokens text
                let chunked =
                    try
                        stale
                        |> Array.ofList
                        |> Array.Parallel.map (fun (path, key, stamp) ->
                            ct.ThrowIfCancellationRequested()
                            chunkFile countTokens files path
                            |> Option.map (fun chunks -> key, stamp.Value, Array.ofList chunks))
                        |> Array.choose id
                        |> List.ofArray
                    with :? AggregateException as ae when ae.InnerExceptions |> Seq.forall (fun e -> e :? OperationCanceledException) ->
                        // The parallel map wraps a cancellation thrown inside its body.
                        raise (OperationCanceledException ct)
                let texts = chunked |> List.collect (fun (_, _, cs) -> cs |> Array.map (fun c -> c.EmbedText) |> List.ofArray) |> Array.ofList
                let embedStart = Stopwatch.GetTimestamp()
                let vectors = if texts.Length = 0 then [||] else embedAllWith ct embedder.Value progress texts
                let mutable offset = 0
                let built =
                    chunked
                    |> List.map (fun (key, stamp, cs) ->
                        let own = vectors[offset .. offset + cs.Length - 1]
                        offset <- offset + cs.Length
                        key,
                        { Stamp = stamp
                          Chunks = cs |> Array.map (fun c -> { StartLine = c.StartLine; EndLine = c.EndLine })
                          Vectors = Array.concat own })
                tag a "files.total" walked.Length
                tag a "files.built" built.Length
                tag a "chunks" texts.Length
                tagEmbedTimings a before embedder
                tag a "embed.wall.ms" (Math.Round(float (Stopwatch.GetTimestamp() - embedStart) * 1000.0 / float Stopwatch.Frequency, 1))
                built)
        let current = walked |> List.map (fun (_, key, _) -> key) |> Set.ofList
        let kept = prior |> Map.filter (fun key _ -> current.Contains key)
        let merged = built |> List.fold (fun m (key, entry) -> Map.add key entry m) kept
        // Entries for files that are gone, and for files that can no longer be read, are dropped.
        let gone = prior.Count - (prior |> Map.filter (fun k _ -> merged.ContainsKey k) |> Map.count)
        let snapshot = { ModelId = ModelLocator.ModelId; Dimension = dim; Files = merged }
        // Nothing is written for a cancelled run, so the saved index stays as it was.
        ct.ThrowIfCancellationRequested()
        phase "ferret.index.save" (fun a ->
            let save = not built.IsEmpty || gone > 0
            tag a "saved" save
            tag a "files.removed" gone
            if save then VectorStore.save storePath snapshot)
        snapshot, walked |> List.filter (fun (_, key, _) -> merged.ContainsKey key) |> List.map (fun (p, k, _) -> p, k)

    /// Builds or updates the index for `root` without searching. Returns the number of files indexed.
    /// Cancelling `ct` stops it with `OperationCanceledException` and leaves the saved index as it was.
    let buildWith
        (ct: CancellationToken)
        (cacheDir: string)
        (embedder: Lazy<IEmbedder>)
        (progress: int -> int -> unit)
        (files: IFileSource)
        (root: string)
        : int =
        let _, paths = sync ct cacheDir embedder progress files root
        paths.Length

    /// `buildWith` that cannot be cancelled.
    let build (cacheDir: string) (embedder: Lazy<IEmbedder>) (progress: int -> int -> unit) (files: IFileSource) (root: string) : int =
        buildWith CancellationToken.None cacheDir embedder progress files root

    /// The first lines of the chunk at `start..end`, up to `maxExcerpts`, skipping blank ones.
    let private excerpt (files: IFileSource) (path: string) (startLine: int) (endLine: int) : Hit list =
        match files.ReadLines path with
        | None -> []
        | Some lines ->
            lines
            |> Seq.indexed
            |> Seq.skipWhile (fun (i, _) -> i + 1 < startLine)
            |> Seq.takeWhile (fun (i, _) -> i + 1 <= endLine)
            |> Seq.filter (fun (_, l) -> not (String.IsNullOrWhiteSpace l))
            |> Seq.truncate maxExcerpts
            |> Seq.map (fun (i, l) -> { Path = path; LineNumber = i + 1; Line = l })
            |> List.ofSeq

    /// Ranks the files under `root` against `query`, best first, at most `limit`. Cancelling `ct` stops
    /// it with `OperationCanceledException` and leaves the saved index as it was.
    let searchWith
        (ct: CancellationToken)
        (cacheDir: string)
        (embedder: Lazy<IEmbedder>)
        (progress: int -> int -> unit)
        (files: IFileSource)
        (root: string)
        (query: string)
        (limit: int)
        : ScoredDoc list =
        if String.IsNullOrWhiteSpace query then
            []
        else
            let snapshot, paths = sync ct cacheDir embedder progress files root
            ct.ThrowIfCancellationRequested()
            let q =
                phase "ferret.embed" (fun a ->
                    let before = timingsOf embedder
                    let v = (embedder.Value.EmbedBatch [| query |])[0]
                    tag a "kind" "query"
                    tagEmbedTimings a before embedder
                    v)
            let dim = snapshot.Dimension
            let top =
                phase "ferret.rank" (fun a ->
                    tag a "files" paths.Length
                    paths
                    |> List.choose (fun (path, key) ->
                        let entry = snapshot.Files[key]
                        if entry.Chunks.Length = 0 then
                            None
                        else
                            let scores =
                                Array.init entry.Chunks.Length (fun i ->
                                    TensorPrimitives.Dot(ReadOnlySpan<float32> q, ReadOnlySpan<float32>(entry.Vectors, i * dim, dim)))
                            let best = scores |> Array.indexed |> Array.maxBy snd
                            Some(path, float (snd best), entry.Chunks[fst best]))
                    |> List.sortWith (fun (pa, sa, _) (pb, sb, _) ->
                        match compare sb sa with
                        | 0 -> String.CompareOrdinal(pa, pb)
                        | c -> c)
                    |> List.truncate limit)
            phase "ferret.excerpts" (fun a ->
                tag a "files" top.Length
                top
                |> List.map (fun (path, score, chunk) ->
                    { File = path
                      Score = score
                      Excerpts = excerpt files path chunk.StartLine chunk.EndLine }))

    /// `searchWith` that cannot be cancelled.
    let search
        (cacheDir: string)
        (embedder: Lazy<IEmbedder>)
        (progress: int -> int -> unit)
        (files: IFileSource)
        (root: string)
        (query: string)
        (limit: int)
        : ScoredDoc list =
        searchWith CancellationToken.None cacheDir embedder progress files root query limit
