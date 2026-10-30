module Ferret.DiskIndexStore

open System
open System.IO
open System.Text
open Ferret.Domain
open Ferret.Index

[<Literal>]
let private Magic = 0x31545246 // "FRT1"

[<Literal>]
let private Trailer = 0x444E4546 // "FEND"

let private fieldCodes =
    [ Field.Path, 0uy
      Field.Heading, 1uy
      Field.Title, 2uy
      Field.Description, 3uy
      Field.TagList, 4uy ]

let private write (w: BinaryWriter) (snapshot: Snapshot) =
    w.Write Magic
    w.Write snapshot.Version
    w.Write snapshot.Entries.Count
    for KeyValue(path, entry) in snapshot.Entries do
        w.Write path
        w.Write entry.Stamp
        w.Write entry.Length
        w.Write entry.TermFreq.Count
        for KeyValue(term, count) in entry.TermFreq do
            w.Write term
            w.Write count
        w.Write entry.FieldTerms.Count
        for KeyValue(field, terms) in entry.FieldTerms do
            w.Write(fieldCodes |> List.find (fst >> (=) field) |> snd)
            w.Write terms.Count
            for term in terms do
                w.Write term
    w.Write Trailer

let private read (r: BinaryReader) : Snapshot option =
    if r.ReadInt32() <> Magic then
        None
    else
        let version = r.ReadInt32()
        // A snapshot from other code is useless, so skip decoding it.
        if version <> Ferret.Index.version then
            None
        else
            let entries = Collections.Generic.Dictionary<string, Entry>()
            for _ in 1 .. r.ReadInt32() do
                let path = r.ReadString()
                let stamp = r.ReadString()
                let length = r.ReadInt32()
                let termFreq =
                    [ for _ in 1 .. r.ReadInt32() ->
                        let term = r.ReadString()
                        term, r.ReadInt32() ]
                    |> Map.ofList
                let fieldTerms =
                    [ for _ in 1 .. r.ReadInt32() ->
                        let code = r.ReadByte()
                        let field = fieldCodes |> List.find (snd >> (=) code) |> fst
                        field, Set.ofList [ for _ in 1 .. r.ReadInt32() -> r.ReadString() ] ]
                    |> Map.ofList
                entries[path] <-
                    { Stamp = stamp
                      Length = length
                      TermFreq = termFreq
                      FieldTerms = fieldTerms }
            if r.ReadInt32() <> Trailer then
                None
            else
                Some { Version = version; Entries = entries }

/// Where the cache lives by default: `$FERRET_CACHE_DIR`, else `$XDG_CACHE_HOME/ferret`, else
/// the platform's per-user cache directory.
let defaultPath () : string =
    let env name =
        match Environment.GetEnvironmentVariable name with
        | null
        | "" -> None
        | value -> Some value
    let dir =
        match env "FERRET_CACHE_DIR", env "XDG_CACHE_HOME" with
        | Some dir, _ -> dir
        | None, Some xdg -> Path.Combine(xdg, "ferret")
        | None, None when OperatingSystem.IsWindows() ->
            Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.LocalApplicationData, "ferret")
        | None, None -> Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".cache", "ferret")
    Path.Combine(dir, "lexical.idx")

/// Stores the cache in one binary file. A missing, truncated, corrupt or other-version file loads
/// as `None` and is replaced on the next save. Saves go through a temp file and a move, so
/// concurrent runs never see a half-written cache. Failing to write is ignored.
type DiskIndexStore(path: string) =
    interface IIndexStore with
        member _.Load() =
            try
                if File.Exists path then
                    use r = new BinaryReader(File.OpenRead path, Encoding.UTF8)
                    read r
                else
                    None
            with _ ->
                // Disposable cache: whatever is wrong with the file, rebuild it.
                None

        member _.Save snapshot =
            let temp = $"{path}.{Environment.ProcessId}.tmp"
            try
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath path)) |> ignore
                using (new BinaryWriter(File.Create temp, Encoding.UTF8)) (fun w -> write w snapshot)
                File.Move(temp, path, true)
            with :? IOException | :? UnauthorizedAccessException ->
                try File.Delete temp with _ -> ()
