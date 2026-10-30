namespace Ferret.Semantic

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text

/// One chunk of an indexed file: its line range and where its vector sits in the file's `Vectors`.
type ChunkRef = { StartLine: int; EndLine: int }

/// What the index knows about one file: its stamp, its chunks and their unit-length vectors (chunk
/// `i` is `Vectors[i * dim .. (i + 1) * dim - 1]`).
type FileEntry =
    { Stamp: string
      Chunks: ChunkRef[]
      Vectors: float32[] }

/// The index of one search root, keyed by full path.
type Snapshot =
    { ModelId: string
      Dimension: int
      Files: Map<string, FileEntry> }

/// One file per search root under `<cache dir>/semantic/`, named by a hash of the root's full path.
/// Layout: magic, version, model id, dimension, then per file (path, stamp, chunk count, line ranges),
/// then every vector as one contiguous block of little-endian float32. Saves go through a temp file and
/// a move. A missing, truncated, corrupt or other-version file loads as `None`.
module VectorStore =
    let private magic = 0x58525346 // "FSRX"
    let private version = 1

    let semanticDir (cacheDir: string) = Path.Combine(cacheDir, "semantic")

    /// The index file for the search root `root` (any way of naming it).
    let pathFor (cacheDir: string) (root: string) =
        let full = Path.GetFullPath(if root = "" then "." else root)
        let hash = SHA256.HashData(Encoding.UTF8.GetBytes full)
        Path.Combine(semanticDir cacheDir, Convert.ToHexString(hash, 0, 8).ToLowerInvariant() + ".idx")

    let save (path: string) (snapshot: Snapshot) : unit =
        try
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            let temp = path + $".{Guid.NewGuid():N}.tmp"
            try
                (use w = new BinaryWriter(new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 <<< 16))
                 w.Write magic
                 w.Write version
                 w.Write snapshot.ModelId
                 w.Write snapshot.Dimension
                 w.Write snapshot.Files.Count
                 for KeyValue(p, f) in snapshot.Files do
                     w.Write p
                     w.Write f.Stamp
                     w.Write f.Chunks.Length
                     for c in f.Chunks do
                         w.Write c.StartLine
                         w.Write c.EndLine
                 for KeyValue(_, f) in snapshot.Files do
                     w.Write(MemoryMarshal.AsBytes(ReadOnlySpan<float32>(f.Vectors))))
                File.Move(temp, path, true)
            finally
                if File.Exists temp then File.Delete temp
        with _ ->
            () // an unwritable cache is ignored

    let load (path: string) : Snapshot option =
        try
            if not (File.Exists path) then
                None
            else
                use r = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 <<< 16))
                if r.ReadInt32() <> magic || r.ReadInt32() <> version then
                    None
                else
                    let modelId = r.ReadString()
                    let dim = r.ReadInt32()
                    let headers =
                        [ for _ in 1 .. r.ReadInt32() do
                              let p = r.ReadString()
                              let stamp = r.ReadString()
                              let chunks = [| for _ in 1 .. r.ReadInt32() -> { StartLine = r.ReadInt32(); EndLine = r.ReadInt32() } |]
                              p, stamp, chunks ]
                    let files =
                        headers
                        |> List.map (fun (p, stamp, chunks) ->
                            let vectors = Array.zeroCreate<float32> (chunks.Length * dim)
                            r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(Span<float32>(vectors)))
                            p, { Stamp = stamp; Chunks = chunks; Vectors = vectors })
                        |> Map.ofList
                    Some { ModelId = modelId; Dimension = dim; Files = files }
        with _ ->
            None

    /// Removes every semantic index.
    let clear (cacheDir: string) : unit =
        try
            let dir = semanticDir cacheDir
            if Directory.Exists dir then Directory.Delete(dir, true)
        with _ ->
            ()
