module Ferret.FileSource

open System
open System.IO

type PathKind =
    | Missing
    | IsFile
    | IsDirectory

/// Port: enumerate and read files under a root.
type IFileSource =
    /// What `path` is. `""` means the current directory.
    abstract Kind: path: string -> PathKind
    /// Files to search under `root`, in deterministic order. Paths keep `root` as a prefix
    /// (nothing is prefixed for `""`). A file root is returned as is.
    abstract Enumerate: root: string -> seq<string>
    /// Lines of a file, or `None` when it is binary or unreadable.
    abstract ReadLines: path: string -> seq<string> option
    /// A cheap fingerprint that changes when the file does (size and modified time on disk), or
    /// `None` when the file cannot be read. Index entries are reused while it stays the same.
    abstract Stamp: path: string -> string option
    /// Lines of standard input. Single use: the sequence consumes the stream.
    abstract ReadStdin: unit -> seq<string>

let private real (path: string) = if path = "" then "." else path

let private inGitRepo (dir: string) =
    let rec up (d: DirectoryInfo) =
        if isNull d then false
        else
            let git = Path.Combine(d.FullName, ".git")
            Directory.Exists git || File.Exists git || up d.Parent
    up (DirectoryInfo(Path.GetFullPath dir))

/// Lowest precedence first: later files override earlier ones.
let private readRules (dir: string) (rel: string) (useGitignore: bool) =
    let names =
        [ if useGitignore then ".gitignore"
          ".ignore"
          ".ferretignore" ]
    let lines =
        names
        |> List.collect (fun name ->
            let file = Path.Combine(dir, name)
            if File.Exists file then List.ofSeq (File.ReadLines file) else [])
    Ignore.compile rel lines

let private entries (dir: string) =
    try
        DirectoryInfo(dir).EnumerateFileSystemInfos()
        |> Seq.filter (fun e -> isNull e.LinkTarget && not (e.Name.StartsWith '.'))
        |> Seq.sortWith (fun a b -> String.CompareOrdinal(a.Name, b.Name))
        |> Array.ofSeq
    with :? IOException | :? UnauthorizedAccessException ->
        [||]

let rec private walk (useGitignore: bool) (dir: string) (rel: string) (display: string) (layers: Ignore.Rules list) =
    seq {
        let layers = layers @ [ readRules dir rel useGitignore ]
        for e in entries dir do
            let childRel = if rel = "" then e.Name else rel + "/" + e.Name
            let isDir = e.Attributes.HasFlag FileAttributes.Directory
            if Ignore.check layers childRel isDir <> Ignore.Ignored then
                let childDisplay = if display = "" then e.Name else display.TrimEnd '/' + "/" + e.Name
                if isDir then
                    yield! walk useGitignore e.FullName childRel childDisplay layers
                else
                    yield childDisplay
    }

/// Walks the disk like ripgrep: skips hidden files, symlinks and binary files, and honours
/// `.gitignore` (inside a git repository), `.ignore` and `.ferretignore`. Ignored directories
/// are not entered. Explicit file roots are searched regardless of ignore rules.
type DiskFileSource() =
    interface IFileSource with
        member _.Kind path =
            let p = real path
            if Directory.Exists p then IsDirectory
            elif File.Exists p then IsFile
            else Missing

        member _.Enumerate root =
            let p = real root
            if File.Exists p then
                Seq.singleton root
            elif Directory.Exists p then
                walk (inGitRepo p) p "" root []
            else
                Seq.empty

        member _.ReadLines path =
            try
                use stream = File.OpenRead path
                let buffer = Array.zeroCreate<byte> 8000
                let read = stream.Read(buffer, 0, buffer.Length)
                if Array.IndexOf(buffer, 0uy, 0, read) >= 0 then None
                else Some(File.ReadLines path)
            with :? IOException | :? UnauthorizedAccessException ->
                None

        member _.Stamp path =
            try
                let info = FileInfo path
                if info.Exists then Some $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" else None
            with :? IOException | :? UnauthorizedAccessException ->
                None

        member _.ReadStdin() =
            let reader = new StreamReader(Console.OpenStandardInput(), Text.UTF8Encoding(false))
            Seq.unfold
                (fun () ->
                    match reader.ReadLine() with
                    | null -> None
                    | line -> Some(line, ()))
                ()
