module Ferret.Search

open System
open Ferret.Domain
open Ferret.FileSource

/// Excerpt lines shown per file.
let maxExcerpts = 3

type private Doc =
    { Path: string
      Length: int
      TermFreq: Map<string, int> }

let private readDoc (files: IFileSource) (terms: Set<string>) (path: string) : Doc option =
    files.ReadLines path
    |> Option.map (fun lines ->
        let mutable length = 0
        let counts = Collections.Generic.Dictionary<string, int>()
        for line in lines do
            for token in Text.tokenize line do
                length <- length + 1
                if terms.Contains token then
                    counts[token] <- (match counts.TryGetValue token with | true, n -> n + 1 | _ -> 1)
        { Path = path
          Length = length
          TermFreq = counts |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq })

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

/// Ranks the files under `roots` against `query` with BM25 (OR semantics over its terms) and
/// returns the best `limit`. Equal scores sort by ordinal path. Files without a query term are
/// not returned.
let search (files: IFileSource) (roots: string list) (query: string) (limit: int) : ScoredDoc list =
    let terms = Text.tokenize query |> List.distinct
    let termSet = Set.ofList terms
    let docs =
        roots
        |> Seq.collect files.Enumerate
        |> Seq.distinct
        |> Seq.choose (readDoc files termSet)
        |> Array.ofSeq
    if terms.IsEmpty || docs.Length = 0 then
        []
    else
        let stats: Bm25.CorpusStats =
            { DocCount = docs.Length
              AvgLength = docs |> Array.averageBy (fun d -> float d.Length)
              DocFreq =
                terms
                |> List.map (fun t -> t, docs |> Array.sumBy (fun d -> if d.TermFreq.ContainsKey t then 1 else 0))
                |> Map.ofList }
        docs
        |> Array.map (fun d -> d, Bm25.score stats terms d.TermFreq d.Length)
        |> Array.filter (fun (_, score) -> score > 0.0)
        |> Array.sortWith (fun (a, sa) (b, sb) ->
            match compare sb sa with
            | 0 -> String.CompareOrdinal(a.Path, b.Path)
            | c -> c)
        |> Array.truncate limit
        |> Array.map (fun (d, score) ->
            { File = d.Path
              Score = score
              Excerpts = excerpts files termSet d.Path })
        |> List.ofArray
