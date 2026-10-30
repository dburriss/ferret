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

/// Plain ripgrep-compatible search. Returns the exit code: 0 on match, 1 on none, 2 on error.
let run (deps: AdapterDeps) (args: GrepArgs) : int =
    match (try Ok(Grep.compile args.Options args.Pattern) with :? ArgumentException as ex -> Error ex.Message) with
    | Error message ->
        deps.Err.WriteLine $"ferret: regex parse error: {message}"
        2
    | Ok regex ->
        let roots = if args.Paths.IsEmpty then [ "" ] else args.Paths
        let kinds = roots |> List.map (fun r -> r, deps.Files.Kind r)
        let withFilename =
            match kinds with
            | [ (_, IsFile) ] -> false
            | _ -> true
        let mutable matched = false
        let mutable failed = false

        for root, kind in kinds do
            if kind = Missing then
                deps.Err.WriteLine $"ferret: {root}: No such file or directory (os error 2)"
                failed <- true
            else
                for path in deps.Files.Enumerate root do
                    match deps.Files.ReadLines path with
                    | None -> ()
                    | Some lines ->
                        let hits = Grep.matchLines regex lines
                        match args.Mode with
                        | ShowPaths ->
                            if Seq.isEmpty hits |> not then
                                matched <- true
                                Output.writePath deps.Out path
                        | ShowCounts ->
                            let count = Seq.length hits
                            if count > 0 then
                                matched <- true
                                Output.writeCount deps.Out withFilename path count
                        | ShowLines ->
                            for number, line in hits do
                                matched <- true
                                Output.writeHit deps.Out withFilename args.LineNumbers
                                    { Path = path; LineNumber = number; Line = line }

        deps.Out.Flush()
        if failed then 2
        elif matched then 0
        else 1
