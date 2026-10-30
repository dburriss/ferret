module Ferret.Cli.Args

open Argu

/// Flat, no subcommands: like `rg`, everything is a flag and the first positional is the pattern.
type FerretArgs =
    | [<MainCommand>] Positional of pattern_then_paths: string
    | [<AltCommandLine("-i")>] Ignore_Case
    | [<AltCommandLine("-F")>] Fixed_Strings
    | [<AltCommandLine("-n")>] Line_Number
    | [<AltCommandLine("-l")>] Files_With_Matches
    | [<AltCommandLine("-c")>] Count
    | [<AltCommandLine("-w")>] Word_Regexp
    | [<AltCommandLine("-V")>] Version
    | Lex
    | Limit of count: int
    | Scores
    | Jsonl
    | No_Index
    | Debug

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Positional _ -> "pattern to search for, followed by the files or directories to search (default: current directory)."
            | Ignore_Case -> "case insensitive search."
            | Fixed_Strings -> "treat the pattern as a literal string, not a regex."
            | Line_Number -> "show line numbers."
            | Files_With_Matches -> "print only the paths of files with matches."
            | Count -> "print the number of matching lines per file."
            | Word_Regexp -> "only match whole words."
            | Version -> "print the version."
            | Lex -> "ranked lexical (BM25) search: the first positional is the query terms, not a regex."
            | Limit _ -> "with --lex, the maximum number of files to return (default: 100)."
            | Scores -> "with --lex, prefix each result line with its relevance score."
            | Jsonl -> "with --lex, print one JSON object per result line (file, line, score, text)."
            | No_Index -> "with --lex, ignore the persisted index and read every file."
            | Debug -> "print stack traces for unexpected errors."

let private clusterable = set "inlcwF"

/// Expand clustered short flags (`-in` becomes `-i -n`) like `rg`. Argu does not do this itself.
let expandShortFlags (argv: string[]) : string[] =
    let rec go afterSeparator (args: string list) =
        match args with
        | [] -> []
        | "--" :: rest -> "--" :: go true rest
        | arg :: rest when
            not afterSeparator
            && arg.Length > 2
            && arg[0] = '-'
            && arg[1] <> '-'
            && arg.Substring 1 |> Seq.forall clusterable.Contains
            ->
            [ for c in arg.Substring 1 -> $"-{c}" ] @ go afterSeparator rest
        | arg :: rest -> arg :: go afterSeparator rest
    go false (List.ofArray argv) |> Array.ofList

/// Split argv at the first `--`: Argu has no end-of-options marker, so everything after it is
/// returned as literal positionals (pattern or paths) and never parsed as flags.
let splitSeparator (argv: string[]) : string[] * string list =
    match Array.tryFindIndex ((=) "--") argv with
    | Some i -> argv[.. i - 1], List.ofArray argv[i + 1 ..]
    | None -> argv, []

/// Argu hands unrecognised flags to the main command as positionals. rg rejects them, so we do too.
let tryFindUnrecognizedFlag (positionals: string list) : string option =
    positionals |> List.tryFind (fun p -> p.Length > 1 && p[0] = '-')
