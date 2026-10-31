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

/// Reads the file format straight from a buffer. Terms are compared as UTF-8 bytes, so skipping a
/// term that no query wants allocates nothing.
type private Cursor(buf: byte[]) =
    let mutable pos = 0
    member _.Int32() =
        let v = BitConverter.ToInt32(buf, pos)
        pos <- pos + 4
        v
    member _.Byte() =
        let v = buf[pos]
        pos <- pos + 1
        v
    /// BinaryWriter prefixes a string with its byte length as a 7-bit encoded int.
    member private _.Length() =
        let mutable n = 0
        let mutable shift = 0
        let mutable more = true
        while more do
            let b = buf[pos]
            pos <- pos + 1
            n <- n ||| (int (b &&& 0x7Fuy) <<< shift)
            shift <- shift + 7
            more <- b &&& 0x80uy <> 0uy
        n
    member this.String() =
        let n = this.Length()
        let s = Encoding.UTF8.GetString(buf, pos, n)
        pos <- pos + n
        s
    member this.SkipString() =
        let n = this.Length()
        pos <- pos + n
    member _.Skip n = pos <- pos + n
    /// Reads a string and returns the index of the wanted term it equals, or -1.
    member this.Match(wanted: byte[][]) =
        let n = this.Length()
        let span = ReadOnlySpan<byte>(buf, pos, n)
        pos <- pos + n
        let mutable found = -1
        let mutable i = 0
        while found < 0 && i < wanted.Length do
            if span.SequenceEqual(ReadOnlySpan<byte> wanted[i]) then found <- i
            i <- i + 1
        found

/// Decodes like `read`, but keeps only the given terms in each entry's `TermFreq` and `FieldTerms`.
/// This is what a query sees of the entry (fields with no wanted term are left out), without
/// building a map or set for every term in the file.
let private readFor (buf: byte[]) (terms: string list) : Snapshot option =
    let c = Cursor buf
    let wanted = terms |> List.map Encoding.UTF8.GetBytes |> Array.ofList
    let names = Array.ofList terms
    if c.Int32() <> Magic then
        None
    else
        let version = c.Int32()
        if version <> Ferret.Index.version then
            None
        else
            let count = c.Int32()
            let entries = Collections.Generic.Dictionary<string, Entry>(count)
            for _ in 1 .. count do
                let path = c.String()
                let stamp = c.String()
                let length = c.Int32()
                let mutable freqs = []
                for _ in 1 .. c.Int32() do
                    let hit = c.Match wanted
                    let n = c.Int32()
                    if hit >= 0 then freqs <- (names[hit], n) :: freqs
                let mutable fields = []
                for _ in 1 .. c.Int32() do
                    let code = c.Byte()
                    let mutable hits = []
                    for _ in 1 .. c.Int32() do
                        let hit = c.Match wanted
                        if hit >= 0 then hits <- names[hit] :: hits
                    if not hits.IsEmpty then
                        fields <- (fieldCodes |> List.find (snd >> (=) code) |> fst, Set.ofList hits) :: fields
                entries[path] <-
                    { Stamp = stamp
                      Length = length
                      TermFreq = Map.ofList freqs
                      FieldTerms = Map.ofList fields }
            if c.Int32() <> Trailer then None else Some { Version = version; Entries = entries }

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

        member _.Clear() =
            try File.Delete path with :? IOException | :? UnauthorizedAccessException -> ()

    interface IQueryIndexStore with
        member _.LoadFor terms =
            try
                if File.Exists path then readFor (File.ReadAllBytes path) terms else None
            with _ ->
                None
