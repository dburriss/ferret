module Ferret.Ranking

open System
open Ferret.Domain

/// Scores candidates against a query. Returns best-first, without excerpts; callers apply limits.
type Ranker = Query -> Candidate list -> ScoredDoc list

/// BM25 with OR semantics over the query terms. Equal scores sort by ordinal path. Candidates
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
            |> Array.map (fun d -> d, Bm25.score stats query.Terms d.TermFreq d.Length)
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
