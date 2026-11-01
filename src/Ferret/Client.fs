namespace Ferret

open System
open System.IO
open System.Threading.Tasks
open Ferret.Domain
open Ferret.FileSource
open Ferret.Index
open Ferret.Semantic

/// How files are ranked.
type SearchMode =
    /// BM25 over the words in each file.
    | Lexical
    /// Embedding similarity: finds files by meaning. Needs the model, which downloads on first use.
    | Semantic
    /// BM25 and semantic rankings fused with reciprocal rank fusion.
    | Hybrid

/// Settings for a `FerretClient`. Every property has a default, so `FerretOptions()` is enough.
[<AllowNullLiteral>]
type FerretOptions() =
    /// Folders (or files) to search. `""` is the current directory. Default: the current directory.
    member val Roots: string[] = [| "" |] with get, set
    /// Where indexes and the downloaded model live. Default: Ferret's cache directory, which the `ferret`
    /// tool shares, so both see the same indexes. `FERRET_CACHE_DIR` overrides it.
    member val CacheDir: string = null with get, set
    /// A directory holding `model_quantized.onnx` and `vocab.txt`, to use instead of downloading the model.
    /// Default: `FERRET_MODEL_DIR`, else the cache, else a download on first use.
    member val ModelDir: string = null with get, set
    /// The mode `Search` uses when none is given.
    member val DefaultMode: SearchMode = Lexical with get, set
    /// Lexical search keeps a persisted index (true, the default) or reads every file on each search.
    member val UseIndex: bool = true with get, set
    /// Never download the model; a missing model throws `ModelUnavailable`. `FERRET_OFFLINE=1` has the same effect.
    member val Offline: bool = false with get, set
    /// Gitignore-style patterns applied to every root, after its ignore files, so they win ties and `!` re-includes.
    /// Ignored when a custom `IFileSource` is passed to the `FerretClient` constructor.
    member val IgnorePatterns: string[] = [||] with get, set
    /// Receives status messages: model download and embedding progress. Default: none.
    member val Progress: Action<string> = null with get, set

/// Searches files by regex (like `rg`), keyword, meaning or both. One client holds the loaded model, so reuse it across
/// searches and dispose it when done.
///
///     use ferret = new FerretClient(FerretOptions(Roots = [| "docs" |]))
///     let hits = ferret.Search("how do I install it", 10, Semantic)
///
/// Semantic and hybrid searches build or update their index as needed.
type FerretClient(options: FerretOptions, files: IFileSource, index: IIndexStore) =
    static let defaultCacheDir () = Path.GetDirectoryName(DiskIndexStore.defaultPath ())

    let cacheDir = if String.IsNullOrEmpty options.CacheDir then defaultCacheDir () else options.CacheDir
    let roots = List.ofArray options.Roots
    let log (message: string) =
        if not (isNull options.Progress) then options.Progress.Invoke message

    let progress (done': int) (total: int) =
        if done' = 0 then log $"embedding {total} chunks"
        elif total >= 200 && done' % (total / 10) = 0 then log $"embedded {done'}/{total} chunks"

    let modelSource () =
        match options.ModelDir with
        | null
        | "" -> ModelLocator.resolve options.Offline log cacheDir
        | dir ->
            let onnx, vocab = Path.Combine(dir, "model_quantized.onnx"), Path.Combine(dir, "vocab.txt")
            if File.Exists onnx && File.Exists vocab then
                Files(onnx, vocab)
            else
                raise (ModelUnavailable $"model_quantized.onnx and vocab.txt not found in {dir}")

    let embedder = lazy (ModelLocator.load (modelSource ()))

    let checkedRoots () =
        for root in roots do
            if files.Kind root = Missing then
                raise (DirectoryNotFoundException $"{root}: No such file or directory")
        roots

    let lexical (query: string) (limit: int) =
        if options.UseIndex then
            Search.searchIndexed index Ranking.bm25 files (checkedRoots ()) query limit
        else
            Search.search Ranking.bm25 files (checkedRoots ()) query limit

    let semantic (query: string) (limit: int) =
        checkedRoots ()
        |> List.collect (fun root -> SemanticSearch.search cacheDir embedder progress files root query limit)
        |> HybridSearch.mergeByScore limit

    new(options: FerretOptions) =
        let cache = if String.IsNullOrEmpty options.CacheDir then defaultCacheDir () else options.CacheDir
        new FerretClient(options, DiskFileSource(List.ofArray (if isNull options.IgnorePatterns then [||] else options.IgnorePatterns)), Ferret.DiskIndexStore.DiskIndexStore(Path.Combine(cache, "lexical.idx")))

    new() = new FerretClient(FerretOptions())

    /// Makes sure the semantic model is available, downloading it if it is not, so later searches can run
    /// offline. Returns the directory it lives in (`"bundled"` when compiled in).
    member _.EnsureModel() : string =
        match modelSource () with
        | Files(onnx, _) -> Path.GetDirectoryName onnx
        | Bundled -> "bundled"

    /// `EnsureModel` without blocking the caller.
    member this.EnsureModelAsync() : Task<string> = Task.Run(fun () -> this.EnsureModel())

    /// Regex search like `rg`: the matching lines of every file under the roots, in file order, honouring
    /// ignore files. `options` sets case, fixed-string and whole-word matching; use `MatchOptions.defaults`
    /// for none. Lazy: files are read as the result is enumerated. A bad pattern throws `ArgumentException`.
    member _.Grep(pattern: string, options: MatchOptions) : Hit seq =
        let regex = Grep.compile options pattern
        let roots = checkedRoots ()
        seq {
            for root in roots do
                for path in files.Enumerate root do
                    match files.ReadLines path with
                    | None -> ()
                    | Some lines ->
                        for number, line in Grep.matchLines regex lines do
                            { Path = path; LineNumber = number; Line = line }
        }

    /// `Grep` with the default options (case-sensitive regex).
    member this.Grep(pattern: string) : Hit seq = this.Grep(pattern, MatchOptions.defaults)

    /// Ranks the files under the roots against `query`, best first, at most `limit`, each with excerpts.
    member _.Search(query: string, limit: int, mode: SearchMode) : ScoredDoc list =
        if limit < 1 then invalidArg (nameof limit) "limit must be at least 1"
        match mode with
        | Lexical -> lexical query limit
        | Semantic -> semantic query limit
        | Hybrid ->
            let depth = HybridSearch.ArmDepth
            HybridSearch.fuse HybridSearch.DefaultK 1.0 1.0 limit (lexical query depth) (semantic query depth)

    /// `Search` in the default mode.
    member this.Search(query: string, limit: int) : ScoredDoc list = this.Search(query, limit, options.DefaultMode)

    /// `Search` in the default mode, returning at most 10 files.
    member this.Search(query: string) : ScoredDoc list = this.Search(query, 10)

    /// Builds or updates the index for `mode` without searching, so the first search is fast. `Hybrid` builds
    /// both. Returns the number of files indexed (for `Hybrid`, in the lexical index).
    member _.BuildIndex(mode: SearchMode) : int =
        let roots = checkedRoots ()
        let lexicalCount () = Search.buildIndex index files roots
        let semanticCount () = roots |> List.sumBy (SemanticSearch.build cacheDir embedder progress files)
        match mode with
        | Lexical -> lexicalCount ()
        | Semantic -> semanticCount ()
        | Hybrid ->
            let count = lexicalCount ()
            semanticCount () |> ignore
            count

    /// `BuildIndex` in the default mode.
    member this.BuildIndex() : int = this.BuildIndex options.DefaultMode

    /// Deletes the persisted indexes for `mode` (`Hybrid` deletes both). The model stays.
    member _.Clean(mode: SearchMode) : unit =
        match mode with
        | Lexical -> index.Clear()
        | Semantic -> VectorStore.clear cacheDir
        | Hybrid ->
            index.Clear()
            VectorStore.clear cacheDir

    interface IDisposable with
        member _.Dispose() =
            if embedder.IsValueCreated then embedder.Value.Dispose()
