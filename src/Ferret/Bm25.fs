module Ferret.Bm25

let k1 = 1.2
let b = 0.75

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
