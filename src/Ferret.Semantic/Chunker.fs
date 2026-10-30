namespace Ferret.Semantic

open System
open System.Text.RegularExpressions

/// A contiguous run of lines from one file (1-based, inclusive). `Text` is the raw lines; `EmbedText`
/// is what gets embedded: `Text` behind a `title > heading > subheading` breadcrumb.
type Chunk =
    { StartLine: int
      EndLine: int
      Text: string
      EmbedText: string }

module Chunker =

    type private Line = int * string

    let private headingLevel (line: string) =
        let m = Regex.Match(line, @"^(#{1,6})\s+(.*)$")
        if m.Success then Some(m.Groups[1].Length, m.Groups[2].Value.Trim()) else None

    let private isHeading (line: string) = (headingLevel line).IsSome
    let private blank (_, l: string) = String.IsNullOrWhiteSpace l

    let private text (lines: Line[]) = lines |> Array.map snd |> String.concat "\n"

    let private trimBlank (lines: Line[]) =
        lines |> Array.skipWhile blank |> Array.rev |> Array.skipWhile blank |> Array.rev

    /// Splits at headings outside fenced code blocks. Lines before the first heading (front matter,
    /// an intro) are the first section.
    let private sections (lines: Line[]) : Line[] list =
        let mutable inFence = false
        let result = ResizeArray<Line[]>()
        let mutable cur = ResizeArray<Line>()
        for (n, l) in lines do
            if l.TrimStart().StartsWith "```" then
                inFence <- not inFence
            if not inFence && isHeading l && cur.Count > 0 then
                result.Add(cur.ToArray())
                cur <- ResizeArray()
            cur.Add((n, l))
        if cur.Count > 0 then
            result.Add(cur.ToArray())
        result |> Seq.filter (fun s -> not (Array.forall blank s)) |> List.ofSeq

    /// A section with nothing to embed on its own: only headings, or only front matter. Such chunks
    /// win topic queries but make poor excerpts, so they are merged into the section after them.
    let private isWeak (first: bool) (section: Line[]) =
        let body = section |> Array.filter (blank >> not)
        let frontMatter = first && body.Length > 0 && (snd body[0]).Trim() = "---" && not (body |> Array.exists (snd >> isHeading))
        frontMatter || body |> Array.forall (snd >> isHeading)

    let private mergeWeak (sections: Line[] list) : Line[] list =
        let merged, pending =
            sections
            |> List.indexed
            |> List.fold
                (fun (acc, pending: Line[]) (i, s) ->
                    let s = Array.append pending s
                    if isWeak (i = 0) s then acc, s else s :: acc, [||])
                ([], [||])
        List.rev (if pending.Length > 0 then pending :: merged else merged)

    /// Packs a section into chunks of at most `maxTokens`: paragraphs first, then lines, then words.
    let private pack (count: string -> int) (maxTokens: int) (lines: Line[]) : Line[] list =
        if count (text lines) <= maxTokens then
            [ lines ]
        else
            let paragraphs =
                lines
                |> Array.fold
                    (fun (acc: Line[] list) l ->
                        match acc with
                        | cur :: rest when not (blank l) -> Array.append cur [| l |] :: rest
                        | _ when blank l -> [||] :: acc
                        | _ -> [| l |] :: acc)
                    []
                |> List.rev
                |> List.filter (fun p -> p.Length > 0)
            // Break oversize paragraphs into line groups that fit; hard-split lines that are themselves too long.
            let pieces =
                paragraphs
                |> List.collect (fun p ->
                    if count (text p) <= maxTokens then
                        [ p ]
                    else
                        p
                        |> Array.collect (fun (n, l) ->
                            if count l <= maxTokens then
                                [| (n, l) |]
                            else
                                let words = l.Split ' '
                                let size = max 1 (words.Length * maxTokens / (count l + 1))
                                words |> Array.chunkBySize size |> Array.map (fun w -> (n, String.Join(" ", w))))
                        |> Array.fold
                            (fun (acc: Line[] list) l ->
                                match acc with
                                | cur :: rest when count (text (Array.append cur [| l |])) <= maxTokens ->
                                    Array.append cur [| l |] :: rest
                                | _ -> [| l |] :: acc)
                            []
                        |> List.rev)
            pieces
            |> List.fold
                (fun (acc: Line[] list) p ->
                    match acc with
                    | cur :: rest when count (text (Array.concat [ cur; [| (0, "") |]; p ])) <= maxTokens ->
                        Array.concat [ cur; p ] :: rest
                    | _ -> p :: acc)
                []
            |> List.rev

    /// Headings in `lines` (outside code fences) pushed onto the heading path `stack` (outermost first).
    let private pushHeadings (stack: (int * string) list) (lines: Line[]) =
        let mutable inFence = false
        let mutable stack = stack
        for (_, l) in lines do
            if l.TrimStart().StartsWith "```" then
                inFence <- not inFence
            match (if inFence then None else headingLevel l) with
            | Some(level, title) -> stack <- (stack |> List.filter (fun (lv, _) -> lv < level)) @ [ (level, title) ]
            | None -> ()
        stack

    /// Chunks `content` by heading, merging heading-only and front-matter sections into the next one;
    /// sections over `maxTokens` fall back to paragraph packing. `title` (the file's name) and the
    /// heading path prefix each chunk's `EmbedText`. `count` measures text in tokens.
    let chunk (title: string) (count: string -> int) (maxTokens: int) (content: string) : Chunk list =
        let lines = content.Replace("\r\n", "\n").Split '\n' |> Array.mapi (fun i l -> (i + 1, l))
        let _, chunks =
            sections lines
            |> mergeWeak
            |> List.fold
                (fun (stack, acc) section ->
                    let stack = pushHeadings stack section
                    let crumb = String.Join(" > ", title :: (stack |> List.map snd))
                    let made =
                        pack count (maxTokens - count crumb) section
                        |> List.map trimBlank
                        |> List.filter (fun l -> l.Length > 0)
                        |> List.map (fun l ->
                            { StartLine = fst l[0]
                              EndLine = fst l[l.Length - 1]
                              Text = text l
                              EmbedText = crumb + "\n" + text l })
                    stack, acc @ made)
                ([], [])
        chunks
