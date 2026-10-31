namespace Ferret.Semantic

open System
open System.Diagnostics
open Ferret
open Ferret.Domain

/// Hybrid search: BM25 and semantic file rankings fused with reciprocal rank fusion. The arms are the
/// caller's (they differ per mode and per root); this owns the depth, the fusion and its timing.
module HybridSearch =

    /// How many files each ranker contributes before fusion.
    [<Literal>]
    let ArmDepth = 100

    /// RRF constant.
    [<Literal>]
    let DefaultK = 60

    let private activitySource = new ActivitySource(Search.ActivitySourceName)

    /// Best-first merge of per-root results: score descending, then ordinal path.
    let mergeByScore (limit: int) (results: ScoredDoc list) : ScoredDoc list =
        results
        |> List.sortWith (fun a b ->
            match compare b.Score a.Score with
            | 0 -> String.CompareOrdinal(a.File, b.File)
            | c -> c)
        |> List.truncate limit

    /// Fuses the lexical and semantic rankings (already limited to `ArmDepth`) and keeps the first `limit`.
    /// Excerpts are the lexical matching lines when the file has them, otherwise the best semantic chunk's.
    let fuse (k: int) (lexicalWeight: float) (semanticWeight: float) (limit: int) (lexical: ScoredDoc list) (semantic: ScoredDoc list) =
        use activity = activitySource.StartActivity "ferret.fuse"
        let fused = Ranking.fuse k [ lexicalWeight, lexical; semanticWeight, semantic ] |> List.truncate limit
        if not (isNull activity) then
            activity.SetTag("lexical", lexical.Length) |> ignore
            activity.SetTag("semantic", semantic.Length) |> ignore
            activity.SetTag("fused", fused.Length) |> ignore
        fused
