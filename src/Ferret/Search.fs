module Ferret.Search

open System
open Ferret.Domain
open Ferret.FileSource

/// Excerpt lines shown per file.
let maxExcerpts = 3

/// The tokens of the path (extension excluded) and, for Markdown, of headings and the
/// frontmatter title, description and tags, per field. Fields without tokens are left out.
let private fieldTokens (path: string) (lines: seq<string>) : Map<Field, Set<string>> =
    let markdown = if Markdown.isMarkdown path then Markdown.fieldText lines else []
    (Field.Path, IO.Path.ChangeExtension(path, null)) :: markdown
    |> List.groupBy fst
    |> List.map (fun (field, texts) -> field, texts |> List.collect (snd >> Text.tokenize) |> Set.ofList)
    |> List.filter (fun (_, found) -> not found.IsEmpty)
    |> Map.ofList

/// Which query terms appear in each field. Fields without a match are left out.
let private onlyTerms (terms: Set<string>) (fields: Map<Field, Set<string>>) : Map<Field, Set<string>> =
    fields
    |> Map.map (fun _ found -> Set.intersect found terms)
    |> Map.filter (fun _ found -> not found.IsEmpty)

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
          FieldTerms = fieldTokens path lines |> onlyTerms terms })

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

let private rank
    (ranker: Ranking.Ranker)
    (files: IFileSource)
    (query: string)
    (terms: string list)
    (limit: int)
    (candidates: Candidate list)
    : ScoredDoc list =
    let termSet = Set.ofList terms
    ranker { Text = query; Terms = terms } candidates
    |> List.truncate limit
    |> List.map (fun doc -> { doc with Excerpts = excerpts files termSet doc.File })

/// Ranks the files under `roots` against `query` with `ranker` and returns the best `limit`, each
/// with excerpts of the lines that contain a query term. Reads every file on every call.
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
        roots
        |> Seq.collect files.Enumerate
        |> Seq.distinct
        |> Seq.choose (readCandidate files termSet)
        |> List.ofSeq
        |> rank ranker files query terms limit

/// Everything about a file that does not depend on the query.
let private buildEntry (files: IFileSource) (path: string) : Index.Entry option =
    // Stamp first: a file changed while being read then looks stale on the next run.
    files.Stamp path
    |> Option.bind (fun stamp ->
        files.ReadLines path
        |> Option.map (fun lines ->
            let lines = if Markdown.isMarkdown path then Seq.cache lines else lines
            let mutable length = 0
            let counts = Collections.Generic.Dictionary<string, int>()
            for line in lines do
                for token in Text.tokenize line do
                    length <- length + 1
                    counts[token] <- (match counts.TryGetValue token with | true, n -> n + 1 | _ -> 1)
            { Stamp = stamp
              Length = length
              TermFreq = counts |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
              FieldTerms = fieldTokens path lines }))

let private candidateOf (terms: string list) (termSet: Set<string>) (path: string) (entry: Index.Entry) : Candidate =
    { Path = path
      Length = entry.Length
      TermFreq =
        terms
        |> List.choose (fun t -> Map.tryFind t entry.TermFreq |> Option.map (fun n -> t, n))
        |> Map.ofList
      FieldTerms = onlyTerms termSet entry.FieldTerms }

/// The full path an entry is stored under, so one cache serves every way of naming a root.
let private entryKey (path: string) = IO.Path.GetFullPath path

/// Whether the entry at `key` lies under a searched root (a file root is just that file).
let private isUnder (roots: (string * bool) list) (key: string) =
    roots
    |> List.exists (fun (root, isDir) ->
        if isDir then key.StartsWith(root.TrimEnd IO.Path.DirectorySeparatorChar + string IO.Path.DirectorySeparatorChar)
        else key = root)

/// Same results as `search`, but reads only files that are new or changed since the last run and
/// keeps what it learned in `store`. Entries for files that have gone from the searched roots are
/// dropped; entries elsewhere are kept. A snapshot from other code (`Index.version`) is ignored.
let searchIndexed
    (store: Index.IIndexStore)
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
        let entries =
            match store.Load() |> Option.filter (fun s -> s.Version = Index.version) with
            | Some s -> Collections.Generic.Dictionary<string, Index.Entry>(s.Entries)
            | None -> Collections.Generic.Dictionary<string, Index.Entry>()
        let seen = Collections.Generic.HashSet<string>()
        let mutable changed = false
        let candidates =
            roots
            |> Seq.collect files.Enumerate
            |> Seq.distinct
            |> Seq.choose (fun path ->
                let key = entryKey path
                let stamp = files.Stamp path
                match entries.TryGetValue key with
                | true, entry when stamp = Some entry.Stamp ->
                    seen.Add key |> ignore
                    Some(candidateOf terms termSet path entry)
                | cached, _ ->
                    changed <- true
                    match buildEntry files path with
                    | Some entry ->
                        seen.Add key |> ignore
                        entries[key] <- entry
                        Some(candidateOf terms termSet path entry)
                    | None ->
                        if cached then entries.Remove key |> ignore
                        None)
            |> List.ofSeq
        let searched =
            roots |> List.map (fun r -> entryKey (if r = "" then "." else r), files.Kind r = IsDirectory)
        let gone = entries.Keys |> Seq.filter (fun k -> not (seen.Contains k) && isUnder searched k) |> List.ofSeq
        for key in gone do
            entries.Remove key |> ignore
        if changed || not gone.IsEmpty then
            store.Save { Version = Index.version; Entries = entries }
        rank ranker files query terms limit candidates
