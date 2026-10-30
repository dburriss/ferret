module Ferret.Ignore

open System.Text
open System.Text.RegularExpressions

/// Outcome of matching a path against ignore rules. The last matching rule wins.
type Verdict =
    | Ignored
    | Whitelisted
    | NoMatch

type Rule =
    { Regex: Regex
      Negate: bool
      DirOnly: bool }

/// Rules from the ignore files of one directory. `Base` is that directory,
/// relative to the search root with `/` separators (`""` for the root).
type Rules = { Base: string; Rules: Rule list }

/// Translate a gitignore glob (no leading `/`, no trailing `/`) to a regex body.
let private translate (glob: string) : string =
    let sb = StringBuilder()
    let n = glob.Length
    let mutable i = 0

    while i < n do
        match glob[i] with
        | '*' when i + 1 < n && glob[i + 1] = '*' ->
            let atStart = i = 0 || glob[i - 1] = '/'
            if atStart && i + 2 < n && glob[i + 2] = '/' then
                sb.Append("(?:.*/)?") |> ignore
                i <- i + 3
            elif atStart && i + 2 = n then
                sb.Append(".*") |> ignore
                i <- i + 2
            else
                sb.Append("[^/]*") |> ignore
                i <- i + 2
        | '*' ->
            sb.Append("[^/]*") |> ignore
            i <- i + 1
        | '?' ->
            sb.Append("[^/]") |> ignore
            i <- i + 1
        | '[' ->
            let negated = i + 1 < n && (glob[i + 1] = '!' || glob[i + 1] = '^')
            let start = if negated then i + 2 else i + 1
            // a `]` directly after the opening is a literal member
            let close = glob.IndexOf(']', min n (start + 1))
            if start >= n || close < 0 then
                sb.Append(@"\[") |> ignore
                i <- i + 1
            else
                let members =
                    glob.Substring(start, close - start)
                    |> String.collect (fun c ->
                        match c with
                        | '\\' | '[' | ']' | '^' -> $"\\{c}"
                        | _ -> string c)
                sb.Append('[').Append(if negated then "^/" else "").Append(members).Append(']') |> ignore
                i <- close + 1
        | '\\' when i + 1 < n ->
            sb.Append(Regex.Escape(string glob[i + 1])) |> ignore
            i <- i + 2
        | c ->
            sb.Append(Regex.Escape(string c)) |> ignore
            i <- i + 1

    sb.ToString()

let private trimTrailingSpaces (line: string) =
    let mutable e = line.Length
    while e > 0 && line[e - 1] = ' ' && not (e >= 2 && line[e - 2] = '\\') do
        e <- e - 1
    line.Substring(0, e)

let private parseLine (raw: string) : Rule option =
    let line = trimTrailingSpaces (raw.TrimEnd '\r')
    if line = "" || line[0] = '#' then
        None
    else
        let negate = line[0] = '!'
        let line = if negate then line.Substring 1 else line
        let dirOnly = line.EndsWith '/'
        let line = if dirOnly then line.Substring(0, line.Length - 1) else line
        if line = "" then
            None
        else
            // a slash anywhere but the end anchors the pattern to the rules' directory
            let anchored = line.Contains '/'
            let line = if line[0] = '/' then line.Substring 1 else line
            let prefix = if anchored then "" else "(?:.*/)?"
            let regex = Regex("^" + prefix + translate line + "$", RegexOptions.CultureInvariant)
            Some { Regex = regex; Negate = negate; DirOnly = dirOnly }

/// Compile the lines of ignore files found in `baseDir`. Pure: the caller reads the files.
/// Lines of lower-precedence files come first (later rules win).
let compile (baseDir: string) (lines: seq<string>) : Rules =
    { Base = baseDir; Rules = lines |> Seq.choose parseLine |> List.ofSeq }

let private relativeTo (baseDir: string) (path: string) =
    if baseDir = "" then Some path
    elif path.StartsWith(baseDir + "/") then Some(path.Substring(baseDir.Length + 1))
    else None

/// Match one path (relative to the search root, `/` separators) against layers ordered
/// shallow to deep. The last matching rule wins. Does not look at ancestors; see `isIgnored`.
let check (layers: Rules list) (path: string) (isDir: bool) : Verdict =
    let mutable verdict = NoMatch
    for layer in layers do
        match relativeTo layer.Base path with
        | None -> ()
        | Some rel ->
            for rule in layer.Rules do
                if (not rule.DirOnly || isDir) && rule.Regex.IsMatch rel then
                    verdict <- if rule.Negate then Whitelisted else Ignored
    verdict

/// True when the path, or any directory above it, is ignored. A file inside an
/// ignored directory stays ignored even if a later rule re-includes it.
let isIgnored (layers: Rules list) (path: string) (isDir: bool) : bool =
    let parts = path.Split '/'
    let mutable ignored = false
    let mutable i = 0
    while not ignored && i < parts.Length do
        let prefix = System.String.Join("/", parts, 0, i + 1)
        let last = i = parts.Length - 1
        ignored <- check layers prefix (not last || isDir) = Ignored
        i <- i + 1
    ignored
