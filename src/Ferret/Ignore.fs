module Ferret.Ignore

open System
open System.Text
open System.Text.RegularExpressions

/// Outcome of matching a path against ignore rules. The last matching rule wins.
type Verdict =
    | Ignored
    | Whitelisted
    | NoMatch

/// How a rule tests a path. Plain names and simple `*`/`prefix*` globs are by far the most
/// common rules and are matched with string comparison; anything else goes through a regex.
/// `Anchored` patterns apply to the whole path, the others to its last segment.
type Matcher =
    /// Anchored, no glob characters: the whole path equals the text.
    | Exact of string
    /// Unanchored, no glob characters: the last segment equals the text.
    | Name of string
    /// `*text`: the segment ends with the text.
    | EndsWith of anchored: bool * text: string
    /// `text*`: the segment starts with the text.
    | StartsWith of anchored: bool * text: string
    | Pattern of Regex

type Rule =
    { Matcher: Matcher
      Negate: bool
      DirOnly: bool }

/// Rules from the ignore files of one directory. `Base` is that directory,
/// relative to the search root with `/` separators (`""` for the root).
type Rules = { Base: string; Rules: Rule[] }

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

let private isLiteral (text: string) = text.IndexOfAny [| '*'; '?'; '['; '\\' |] < 0

/// The matcher for a glob (leading `/` already removed). `anchored` is whether the rule is
/// tied to the rules' directory.
let private matcherFor (anchored: bool) (glob: string) : Matcher =
    let plain (text: string) = isLiteral text && not (text.Contains '/')
    if isLiteral glob then
        if anchored then Exact glob else Name glob
    elif glob.Length > 0 && glob[0] = '*' && plain (glob.Substring 1) then
        EndsWith(anchored, glob.Substring 1)
    elif glob.Length > 1 && glob[glob.Length - 1] = '*' && plain (glob.Substring(0, glob.Length - 1)) then
        StartsWith(anchored, glob.Substring(0, glob.Length - 1))
    else
        let prefix = if anchored then "" else "(?:.*/)?"
        Pattern(Regex("^" + prefix + translate glob + "$", RegexOptions.CultureInvariant))

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
            Some { Matcher = matcherFor anchored line; Negate = negate; DirOnly = dirOnly }

/// Compile the lines of ignore files found in `baseDir`. Pure: the caller reads the files.
/// Lines of lower-precedence files come first (later rules win).
let compile (baseDir: string) (lines: seq<string>) : Rules =
    { Base = baseDir; Rules = lines |> Seq.choose parseLine |> Array.ofSeq }

/// `path` relative to `baseDir`, or `None` when it is not below it.
let private relativeTo (baseDir: string) (path: string) =
    if baseDir = "" then Some path
    elif
        path.Length > baseDir.Length
        && path[baseDir.Length] = '/'
        && path.StartsWith(baseDir, StringComparison.Ordinal)
    then
        Some(path.Substring(baseDir.Length + 1))
    else
        None

let private matches (matcher: Matcher) (rel: string) =
    let segment = rel.AsSpan(rel.LastIndexOf '/' + 1)
    match matcher with
    | Exact text -> rel = text
    | Name text -> segment.SequenceEqual(text.AsSpan())
    | EndsWith(true, text) -> not (rel.Contains '/') && rel.EndsWith(text, StringComparison.Ordinal)
    | EndsWith(false, text) -> segment.EndsWith(text.AsSpan(), StringComparison.Ordinal)
    | StartsWith(true, text) -> not (rel.Contains '/') && rel.StartsWith(text, StringComparison.Ordinal)
    | StartsWith(false, text) -> segment.StartsWith(text.AsSpan(), StringComparison.Ordinal)
    | Pattern regex -> regex.IsMatch rel

/// The verdict of the last matching rule of one layer.
let private checkLayer (layer: Rules) (path: string) (isDir: bool) : Verdict =
    if layer.Rules.Length = 0 then
        NoMatch
    else
        match relativeTo layer.Base path with
        | None -> NoMatch
        | Some rel ->
            let mutable verdict = NoMatch
            let mutable i = layer.Rules.Length - 1
            while verdict = NoMatch && i >= 0 do
                let rule = layer.Rules[i]
                if (not rule.DirOnly || isDir) && matches rule.Matcher rel then
                    verdict <- if rule.Negate then Whitelisted else Ignored
                i <- i - 1
            verdict

/// Match one path (relative to the search root, `/` separators) against layers ordered
/// shallow to deep. The last matching rule wins, so the deepest layer is tried first and the
/// search stops at the first match. Does not look at ancestors; see `isIgnored`.
let rec check (layers: Rules list) (path: string) (isDir: bool) : Verdict =
    match layers with
    | [] -> NoMatch
    | layer :: deeper ->
        match check deeper path isDir with
        | NoMatch -> checkLayer layer path isDir
        | verdict -> verdict

/// True when the path, or any directory above it, is ignored. A file inside an
/// ignored directory stays ignored even if a later rule re-includes it.
let isIgnored (layers: Rules list) (path: string) (isDir: bool) : bool =
    let parts = path.Split '/'
    let mutable ignored = false
    let mutable i = 0
    while not ignored && i < parts.Length do
        let prefix = String.Join("/", parts, 0, i + 1)
        let last = i = parts.Length - 1
        ignored <- check layers prefix (not last || isDir) = Ignored
        i <- i + 1
    ignored
