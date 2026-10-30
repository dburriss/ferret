module Ferret.Cli.Program

open System
open System.Reflection
open Argu
open Ferret.Domain
open Ferret.Cli.Args
open Ferret.Cli.Deps
open Ferret.Cli.GrepCli
open Ferret.Cli.RankedCli

let private (|VersionCmd|_|) (r: ParseResults<FerretArgs>) =
    if r.Contains Version then Some() else None

let private toGrepArgs (r: ParseResults<FerretArgs>) (afterSeparator: string list) =
    match r.GetResults Positional @ afterSeparator with
    | [] -> None
    | pattern :: paths ->
        let mode =
            if r.Contains Files_With_Matches then ShowPaths
            elif r.Contains Count then ShowCounts
            else ShowLines
        Some
            { Pattern = pattern
              Paths = paths
              Options =
                { IgnoreCase = r.Contains Ignore_Case
                  FixedStrings = r.Contains Fixed_Strings
                  WordRegexp = r.Contains Word_Regexp }
              LineNumbers = r.Contains Line_Number
              Mode = mode }

let private defaultLimit = 100

/// Ranked search arguments, or a usage error. Regex-only flags make no sense with terms.
let private toRankedArgs (r: ParseResults<FerretArgs>) (afterSeparator: string list) =
    let regexOnly =
        [ r.Contains Ignore_Case, "-i"
          r.Contains Fixed_Strings, "-F"
          r.Contains Word_Regexp, "-w"
          r.Contains Files_With_Matches, "-l"
          r.Contains Count, "-c" ]
        |> List.tryFind fst
    match regexOnly, r.TryGetResult Limit, r.GetResults Positional @ afterSeparator with
    | Some(_, name), _, _ -> Error $"{name} cannot be used with --lex"
    | _, Some limit, _ when limit < 1 -> Error "--limit must be at least 1"
    | _, _, [] -> Error "--lex needs query terms"
    | _, limit, query :: paths ->
        Ok
            { Query = query
              Paths = paths
              Limit = defaultArg limit defaultLimit
              Scores = r.Contains Scores }

let private version () =
    let assembly = Assembly.GetExecutingAssembly()
    let info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    let v = if isNull info then assembly.GetName().Version.ToString() else info.InformationalVersion
    printfn "ferret %s" (v.Split('+')[0])
    0

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<FerretArgs>(programName = "ferret")
    try
        let flags, afterSeparator = splitSeparator argv
        let parsed = parser.ParseCommandLine(expandShortFlags flags)
        let isDebug = parsed.Contains Debug

        match tryFindUnrecognizedFlag (parsed.GetResults Positional) with
        | Some flag ->
            eprintfn "ferret: unrecognized flag %s" flag
            2
        | None ->
            match parsed with
            | VersionCmd() -> version ()
            | _ when parsed.Contains Lex ->
                match toRankedArgs parsed afterSeparator with
                | Ok cmd -> RankedCli.run (AdapterDeps.create isDebug) cmd
                | Error message ->
                    eprintfn "ferret: %s" message
                    2
            | _ when parsed.Contains Limit ->
                eprintfn "ferret: --limit requires --lex"
                2
            | _ when parsed.Contains Scores ->
                eprintfn "ferret: --scores requires --lex"
                2
            | _ ->
                match toGrepArgs parsed afterSeparator with
                | Some cmd -> GrepCli.run (AdapterDeps.create isDebug) cmd
                | None ->
                    printfn "%s" (parser.PrintUsage())
                    2
    with
    | :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        2
    | ex ->
        let isDebug = argv |> Array.contains "--debug"
        eprintfn "ferret: unexpected error: %s" ex.Message
        if isDebug then eprintfn "%s" (ex.ToString())
        else eprintfn "run with --debug for the full stack trace"
        2
