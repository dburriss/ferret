namespace Ferret.Semantic

open System
open System.IO
open System.Net.Http
open System.Security.Cryptography

/// Where the embedding model comes from.
type ModelSource =
    /// Model and vocab files on disk.
    | Files of onnx: string * vocab: string
    /// Resources embedded in this build (`-p:BundleModel=true`).
    | Bundled

/// The model is missing and could not be fetched. The message says what to do about it.
exception ModelUnavailable of string

/// Finds the model, downloading it on first use. Order: `FERRET_MODEL_DIR`, the cache directory, the
/// bundled resources, then a download into the cache (unless offline: `FERRET_OFFLINE=1` or `offline`).
module ModelLocator =

    /// Identifies the model in the cache path and in the index, so a different model rebuilds the index.
    [<Literal>]
    let ModelId = "bge-small-en-v1.5-int8"

    let private onnxName = "model_quantized.onnx"
    let private vocabName = "vocab.txt"

    let private baseUrl = "https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/main"

    /// (url, file name, SHA-256) of each file of the model.
    let private files =
        [ $"{baseUrl}/onnx/model_quantized.onnx", onnxName, "6c9c6101a956d62dfb5e7190c538226c0c5bb9cb27b651234b6df063ee7dbfe4"
          $"{baseUrl}/vocab.txt", vocabName, "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3" ]

    let private env name =
        match Environment.GetEnvironmentVariable name with
        | null
        | "" -> None
        | value -> Some value

    /// `<cache dir>/models/<model id>`, where `cacheDir` is Ferret's cache directory.
    let modelDir (cacheDir: string) = Path.Combine(cacheDir, "models", ModelId)

    let private present (dir: string) =
        let onnx, vocab = Path.Combine(dir, onnxName), Path.Combine(dir, vocabName)
        if File.Exists onnx && File.Exists vocab then Some(Files(onnx, vocab)) else None

    let private sha256 (path: string) =
        use stream = File.OpenRead path
        Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant()

    /// Downloads the model into `dir`: each file goes to a temp name, is checked against its SHA-256 and
    /// moved into place, so an interrupted download never leaves a half-written model.
    let download (progress: string -> unit) (dir: string) : ModelSource =
        Directory.CreateDirectory dir |> ignore
        use client = new HttpClient(Timeout = TimeSpan.FromMinutes 10.0)
        for url, name, hash in files do
            let target = Path.Combine(dir, name)
            if not (File.Exists target) then
                progress $"downloading {name} from {url}"
                let temp = target + $".{Guid.NewGuid():N}.tmp"
                try
                    use response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).Result.EnsureSuccessStatusCode()
                    use source = response.Content.ReadAsStream()
                    use dest = File.Create temp
                    source.CopyTo dest
                    dest.Close()
                    if sha256 temp <> hash then
                        failwith $"checksum mismatch for {name}"
                    File.Move(temp, target, true)
                finally
                    if File.Exists temp then File.Delete temp
        (present dir).Value

    /// The model to use, downloading it if it is nowhere else. Fails with a hint when offline.
    let resolve (offline: bool) (progress: string -> unit) (cacheDir: string) : ModelSource =
        let configured = env "FERRET_MODEL_DIR" |> Option.bind present
        let cached = present (modelDir cacheDir)
        match configured, cached with
        | Some source, _
        | None, Some source -> source
        | None, None when OnnxEmbedder.HasBundledModel -> Bundled
        | None, None when offline || env "FERRET_OFFLINE" = Some "1" ->
            raise (ModelUnavailable $"semantic model not found in {modelDir cacheDir} and offline mode is on (FerretOptions.Offline or FERRET_OFFLINE=1); run `ferret --model download` or set FERRET_MODEL_DIR")
        | None, None ->
            try
                download progress (modelDir cacheDir)
            with ex ->
                raise (ModelUnavailable $"could not download the semantic model ({ex.GetBaseException().Message}); set FERRET_MODEL_DIR to a directory holding {onnxName} and {vocabName}")

    /// An embedder for `source`.
    let load (source: ModelSource) : IEmbedder =
        match source with
        | Files(onnx, vocab) -> OnnxEmbedder.FromFiles(onnx, vocab) :> IEmbedder
        | Bundled -> OnnxEmbedder.FromBundled() :> IEmbedder
