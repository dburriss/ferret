module Ferret.Bm25

open Ferret.Domain

let k1 = 1.2
let b = 0.75

/// How much a document's score grows when every query term appears in `field`.
let fieldBoost (field: Field) : float =
    match field with
    | Field.Path -> 0.3
    | Field.Heading -> 0.5
    | Field.Title -> 0.5
    | Field.Description -> 0.3
    | Field.TagList -> 0.5

/// Corpus-wide numbers BM25 needs. `DocFreq` holds the query terms only.
type CorpusStats =
    { DocCount: int
      /// Mean document length in tokens.
      AvgLength: float
      DocFreq: Map<string, int> }

/// Never negative, even for a term in every document.
let idf (docCount: int) (docFreq: int) : float =
    log (1.0 + (float docCount - float docFreq + 0.5) / (float docFreq + 0.5))

/// Sum of the BM25 term scores of `terms` for one document. `termFreq` holds the term counts
/// and `length` the document length in tokens.
let score (stats: CorpusStats) (terms: string list) (termFreq: Map<string, int>) (length: int) : float =
    let norm = 1.0 - b + b * float length / stats.AvgLength
    terms
    |> List.sumBy (fun term ->
        match Map.tryFind term termFreq, Map.tryFind term stats.DocFreq with
        | Some tf, Some df when tf > 0 ->
            let tf = float tf
            idf stats.DocCount df * tf * (k1 + 1.0) / (tf + k1 * norm)
        | _ -> 0.0)

/// Multiplier for a document's BM25 score: 1 plus each field's boost scaled by the share of
/// `terms` found in that field. 1.0 when nothing matched outside the body.
let boost (terms: string list) (fieldTerms: Map<Field, Set<string>>) : float =
    if terms.IsEmpty then
        1.0
    else
        let share (found: Set<string>) =
            float (terms |> List.filter found.Contains |> List.length) / float terms.Length
        fieldTerms |> Map.fold (fun acc field found -> acc + fieldBoost field * share found) 1.0
