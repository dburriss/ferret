module Ferret.Ranking

open System
open Ferret.Domain

/// Scores candidates against a query. Returns best-first, without excerpts; callers apply limits.
type Ranker = Query -> Candidate list -> ScoredDoc list

/// BM25 with OR semantics over the query terms, boosted for matches in the path, headings and
/// frontmatter. Equal scores sort by ordinal path. Candidates
/// without a query term are dropped.
let bm25: Ranker =
    fun query candidates ->
        let docs = Array.ofList candidates
        if query.Terms.IsEmpty || docs.Length = 0 then
            []
        else
            let stats: Bm25.CorpusStats =
                { DocCount = docs.Length
                  AvgLength = docs |> Array.averageBy (fun d -> float d.Length)
                  DocFreq =
                    query.Terms
                    |> List.map (fun t -> t, docs |> Array.sumBy (fun d -> if d.TermFreq.ContainsKey t then 1 else 0))
                    |> Map.ofList }
            docs
            |> Array.map (fun d -> d, Bm25.score stats query.Terms d.TermFreq d.Length * Bm25.boost query.Terms d.FieldTerms)
            |> Array.filter (fun (_, score) -> score > 0.0)
            |> Array.sortWith (fun (a, sa) (b, sb) ->
                match compare sb sa with
                | 0 -> String.CompareOrdinal(a.Path, b.Path)
                | c -> c)
            |> Array.map (fun (d, score) ->
                { File = d.Path
                  Score = score
                  Excerpts = [] })
            |> List.ofArray

/// Reciprocal rank fusion: `rrf(d) = Σ weight / (k + rank)` over the rankings that contain `d`, with
/// 1-based ranks and no score normalisation. Each ranking is best-first with its weight. Equal scores
/// sort by ordinal path; documents scoring 0 (only in zero-weight rankings) are dropped. A document's
/// excerpts come from the first ranking that has any, so order rankings by preferred excerpt source.
let fuse (k: int) (rankings: (float * ScoredDoc list) list) : ScoredDoc list =
    let contributions =
        rankings
        |> List.collect (fun (weight, docs) ->
            docs |> List.mapi (fun i doc -> doc.File, weight / float (k + i + 1), doc.Excerpts))
    contributions
    |> List.groupBy (fun (file, _, _) -> file)
    |> List.map (fun (file, parts) ->
        { File = file
          Score = parts |> List.sumBy (fun (_, s, _) -> s)
          Excerpts = parts |> List.tryPick (fun (_, _, e) -> if e.IsEmpty then None else Some e) |> Option.defaultValue [] })
    |> List.filter (fun d -> d.Score > 0.0)
    |> List.sortWith (fun a b ->
        match compare b.Score a.Score with
        | 0 -> String.CompareOrdinal(a.File, b.File)
        | c -> c)
