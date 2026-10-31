module Ferret.Cli.GrepCli

open System
open Ferret
open Ferret.Domain
open Ferret.FileSource
open Ferret.Cli.Deps

type OutputMode =
    | ShowLines
    | ShowPaths
    | ShowCounts

type GrepArgs =
    { Pattern: string
      Paths: string list
      Options: MatchOptions
      LineNumbers: bool
      Mode: OutputMode }

/// A path argument of `-` means stdin, as in `rg`.
let private stdinPath = "-"

/// Name shown for stdin in output (rg's `--with-filename` form).
let private stdinName = "<stdin>"

/// Plain ripgrep-compatible search. Returns the exit code: 0 on match, 1 on none, 2 on error.
let run (deps: AdapterDeps) (args: GrepArgs) : int =
    match (try Ok(Grep.compile args.Options args.Pattern) with :? ArgumentException as ex -> Error ex.Message) with
    | Error message ->
        deps.Err.WriteLine $"ferret: regex parse error: {message}"
        2
    | Ok regex ->
        // no path: stdin when it carries data, otherwise the current directory
        let roots =
            match args.Paths with
            | [] when deps.StdinIsReadable -> [ stdinPath ]
            | [] -> [ "" ]
            | paths -> paths
        let kinds = roots |> List.map (fun r -> r, (if r = stdinPath then IsFile else deps.Files.Kind r))
        let withFilename =
            match kinds with
            | [ (_, IsFile) ] -> false
            | _ -> true
        let mutable matched = false
        let mutable failed = false

        /// Writes the result for `path` from its matching lines, per the output mode.
        let report (path: string) (hits: (int * string) list) =
            match args.Mode with
            | ShowPaths -> Output.writePath deps.Out path
            | ShowCounts -> Output.writeCount deps.Out withFilename path hits.Length
            | ShowLines ->
                for number, line in hits do
                    Output.writeHit deps.Out withFilename args.LineNumbers { Path = path; LineNumber = number; Line = line }

        /// Writes the hits of `ferret.Grep` as they arrive. Hits come grouped by file, so `-l` and `-c`
        /// buffer only the file being read (`-l` just its first hit).
        let searchDisk (root: string) =
            use ferret = AdapterDeps.client deps [ root ] true
            let mutable current = null
            let pending = ResizeArray<int * string>()
            let flush () =
                if pending.Count > 0 then
                    matched <- true
                    report current (List.ofSeq pending)
                    pending.Clear()
            for hit in ferret.Grep(args.Pattern, args.Options) do
                if args.Mode = ShowLines then
                    matched <- true
                    Output.writeHit deps.Out withFilename args.LineNumbers hit
                else
                    if hit.Path <> current then
                        flush ()
                        current <- hit.Path
                    if args.Mode = ShowCounts || pending.Count = 0 then pending.Add((hit.LineNumber, hit.Line))
            flush ()

        let mutable stdinUsed = false

        for root, kind in kinds do
            if root = stdinPath then
                // stdin can only be read once
                if not stdinUsed then
                    stdinUsed <- true
                    let hits = Grep.matchLines regex (deps.Files.ReadStdin()) |> List.ofSeq
                    if not hits.IsEmpty then
                        matched <- true
                        report stdinName hits
            elif kind = Missing then
                deps.Err.WriteLine $"ferret: {root}: No such file or directory (os error 2)"
                failed <- true
            else
                searchDisk root

        deps.Out.Flush()
        if failed then 2
        elif matched then 0
        else 1
