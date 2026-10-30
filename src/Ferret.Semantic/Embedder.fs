namespace Ferret.Semantic

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Threading.Tasks
open Microsoft.ML.OnnxRuntime
open Microsoft.ML.OnnxRuntime.Tensors
open Microsoft.ML.Tokenizers

/// Cumulative time an embedder has spent tokenizing and running the model, in `Stopwatch` ticks, and
/// the tokens it has encoded. Callers take a snapshot before and after a phase and subtract.
type EmbedTimings =
    { TokenizeTicks: int64
      InferenceTicks: int64
      Tokens: int }

    static member Zero = { TokenizeTicks = 0L; InferenceTicks = 0L; Tokens = 0 }

/// Turns text into unit-length vectors, so cosine similarity is a dot product.
type IEmbedder =
    inherit IDisposable
    abstract Dimension: int
    /// Cumulative tokenize and inference time so far; see `EmbedTimings`.
    abstract Timings: EmbedTimings
    /// Token count the text would use (including the two special tokens), uncapped.
    abstract CountTokens: text: string -> int
    abstract EmbedBatch: texts: string[] -> float32[][]

/// WordPiece tokenizer, ONNX encoder, masked mean pooling and L2 normalisation (bge-small-en-v1.5).
type OnnxEmbedder(loadModel: unit -> byte[], openVocab: unit -> Stream) =
    let maxTokens = 512

    // Parsing the 30k-entry vocab overlaps with reading the model and creating the session.
    let tokenizerTask =
        Task.Run(fun () ->
            use vocab = openVocab ()
            BertTokenizer.Create(vocab, BertOptions(LowerCaseBeforeTokenization = true)))

    let session = new InferenceSession(loadModel ())
    let inputNames = session.InputMetadata.Keys |> Set.ofSeq
    let tokenizer () = tokenizerTask.Result
    let mutable tokenizeTicks = 0L
    let mutable inferenceTicks = 0L
    let mutable tokenCount = 0

    static let readResource (name: string) =
        use s = Assembly.GetExecutingAssembly().GetManifestResourceStream name
        if isNull s then failwith $"Embedded resource '{name}' not found"
        use ms = new MemoryStream()
        s.CopyTo ms
        ms.ToArray()

    /// Whether this build carries the model (`-p:BundleModel=true`).
    static member HasBundledModel =
        let names = Assembly.GetExecutingAssembly().GetManifestResourceNames()
        names |> Array.contains "model.onnx" && names |> Array.contains "vocab.txt"

    static member FromBundled() =
        new OnnxEmbedder((fun () -> readResource "model.onnx"), (fun () -> new MemoryStream(readResource "vocab.txt") :> Stream))

    static member FromFiles(modelPath: string, vocabPath: string) =
        new OnnxEmbedder((fun () -> File.ReadAllBytes modelPath), (fun () -> File.OpenRead vocabPath :> Stream))

    member private _.Tokenize(text: string) : int[] =
        let mutable normalized: string = null
        let mutable consumed = 0
        (tokenizer ()).EncodeToIds(text, maxTokens, true, &normalized, &consumed) |> Seq.toArray

    interface IEmbedder with
        member _.Dimension = 384

        member _.Timings =
            { TokenizeTicks = tokenizeTicks
              InferenceTicks = inferenceTicks
              Tokens = tokenCount }

        member _.CountTokens text =
            let start = Stopwatch.GetTimestamp()
            let n = (tokenizer ()).CountTokens text + 2
            tokenizeTicks <- tokenizeTicks + (Stopwatch.GetTimestamp() - start)
            n

        member this.EmbedBatch texts =
            let tokenizeStart = Stopwatch.GetTimestamp()
            let tokens = texts |> Array.map this.Tokenize
            tokenizeTicks <- tokenizeTicks + (Stopwatch.GetTimestamp() - tokenizeStart)
            tokenCount <- tokenCount + (tokens |> Array.sumBy Array.length)
            let b = tokens.Length
            let n = tokens |> Array.map Array.length |> Array.max
            let ids = Array.zeroCreate<int64> (b * n) // 0 = [PAD]
            let mask = Array.zeroCreate<int64> (b * n)
            tokens
            |> Array.iteri (fun i t ->
                t |> Array.iteri (fun j id -> ids[i * n + j] <- int64 id)
                Array.fill mask (i * n) t.Length 1L)
            let tensor (data: int64[]) = DenseTensor<int64>(Memory<int64>(data), ReadOnlySpan<int>([| b; n |]))
            let inputs =
                [ NamedOnnxValue.CreateFromTensor("input_ids", tensor ids)
                  NamedOnnxValue.CreateFromTensor("attention_mask", tensor mask)
                  if inputNames.Contains "token_type_ids" then
                      NamedOnnxValue.CreateFromTensor("token_type_ids", tensor (Array.zeroCreate (b * n))) ]
            let inferenceStart = Stopwatch.GetTimestamp()
            use results = session.Run inputs
            inferenceTicks <- inferenceTicks + (Stopwatch.GetTimestamp() - inferenceStart)
            let hidden = (results |> Seq.head).AsTensor<float32>()
            let dim = hidden.Dimensions[2]
            // Read the flat buffer: indexing the 3-D tensor per element is far slower than span access.
            let data = (hidden :?> DenseTensor<float32>).Buffer
            tokens
            |> Array.mapi (fun i t ->
                // Masked sum over real tokens; normalising the sum equals normalising the mean.
                let pooled = Array.zeroCreate<float32> dim
                let span = data.Span
                for j in 0 .. t.Length - 1 do
                    let offset = (i * n + j) * dim
                    for d in 0 .. dim - 1 do
                        pooled[d] <- pooled[d] + span[offset + d]
                let norm = pooled |> Array.sumBy (fun v -> v * v) |> sqrt |> float32
                pooled |> Array.map (fun v -> v / norm))

        member _.Dispose() = session.Dispose()
