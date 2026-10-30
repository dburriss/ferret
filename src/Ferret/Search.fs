module Ferret.Search

open System
open Ferret.Domain
open Ferret.FileSource

/// Excerpt lines shown per file.
let maxExcerpts = 3

/// Which query terms appear in the path (extension excluded) and, for Markdown, in headings and
/// the frontmatter title, description and tags. Fields without a match are left out.
let private fieldTerms (terms: Set<string>) (path: string) (lines: seq<string>) : Map<Field, Set<string>> =
    let inTerms text = Text.tokenize text |> List.filter terms.Contains
    let markdown = if Markdown.isMarkdown path then Markdown.fieldText lines else []
    (Field.Path, IO.Path.ChangeExtension(path, null)) :: markdown
    |> List.groupBy fst
    |> List.map (fun (field, texts) -> field, texts |> List.collect (snd >> inTerms) |> Set.ofList)
    |> List.filter (fun (_, found) -> not found.IsEmpty)
    |> Map.ofList

let private readCandidate (files: IFileSource) (terms: Set<string>) (path: string) : Candidate option =
    files.ReadLines path
    |> Option.map (fun lines ->
        // Markdown is read twice (body, then fields); cache so the file is read once.
        let lines = if Markdown.isMarkdown path then Seq.cache lines else lines
        let mutable length = 0
        let counts = Collections.Generic.Dictionary<string, int>()
        for line in lines do
            for token in Text.tokenize line do
                length <- length + 1
                if terms.Contains token then
                    counts[token] <- (match counts.TryGetValue token with | true, n -> n + 1 | _ -> 1)
        { Path = path
          Length = length
          TermFreq = counts |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
          FieldTerms = fieldTerms terms path lines })

let private excerpts (files: IFileSource) (terms: Set<string>) (path: string) : Hit list =
    match files.ReadLines path with
    | None -> []
    | Some lines ->
        lines
        |> Seq.indexed
        |> Seq.filter (fun (_, line) -> Text.tokenize line |> List.exists terms.Contains)
        |> Seq.truncate maxExcerpts
        |> Seq.map (fun (i, line) -> { Path = path; LineNumber = i + 1; Line = line })
        |> List.ofSeq

/// Ranks the files under `roots` against `query` with `ranker` and returns the best `limit`, each
/// with excerpts of the lines that contain a query term.
let search
    (ranker: Ranking.Ranker)
    (files: IFileSource)
    (roots: string list)
    (query: string)
    (limit: int)
    : ScoredDoc list =
    let terms = Text.tokenize query |> List.distinct
    if terms.IsEmpty then
        []
    else
        let termSet = Set.ofList terms
        let candidates =
            roots
            |> Seq.collect files.Enumerate
            |> Seq.distinct
            |> Seq.choose (readCandidate files termSet)
            |> List.ofSeq
        ranker { Text = query; Terms = terms } candidates
        |> List.truncate limit
        |> List.map (fun doc -> { doc with Excerpts = excerpts files termSet doc.File })
