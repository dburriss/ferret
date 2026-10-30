module Ferret.Search

open System
open System.Diagnostics
open Ferret.Domain
open Ferret.FileSource

/// Phase timing. Listeners attach to the source named "Ferret"; with none, `StartActivity` returns
/// null and a phase costs nothing. One activity per phase, never per file.
[<Literal>]
let ActivitySourceName = "Ferret"

let private activitySource = new ActivitySource(ActivitySourceName)

/// Runs `body` inside an activity called `name`.
let private phase (name: string) (body: Activity -> 'a) : 'a =
    use activity = activitySource.StartActivity name
    body activity

let private tag (activity: Activity) (key: string) (value: obj) =
    if not (isNull activity) then activity.SetTag(key, value) |> ignore

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
    let top =
        phase "ferret.rank" (fun a ->
            tag a "candidates" candidates.Length
            ranker { Text = query; Terms = terms } candidates |> List.truncate limit)
    phase "ferret.excerpts" (fun a ->
        tag a "files" top.Length
        top |> List.map (fun doc -> { doc with Excerpts = excerpts files termSet doc.File }))

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
        let paths =
            phase "ferret.enumerate" (fun a ->
                let paths = roots |> Seq.collect files.Enumerate |> Seq.distinct |> List.ofSeq
                tag a "files" paths.Length
                paths)
        let candidates =
            phase "ferret.read" (fun a ->
                let candidates = paths |> List.choose (readCandidate files termSet)
                tag a "files" candidates.Length
                candidates)
        rank ranker files query terms limit candidates

/// Everything about a file that does not depend on the query.
let private buildEntry (files: IFileSource) (path: string) (stamp: string) : Index.Entry option =
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
          FieldTerms = fieldTokens path lines })

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
            phase "ferret.index.load" (fun a ->
                let loaded = store.Load() |> Option.filter (fun s -> s.Version = Index.version)
                tag a "loaded" loaded.IsSome
                match loaded with
                | Some s ->
                    tag a "entries" s.Entries.Count
                    Collections.Generic.Dictionary<string, Index.Entry>(s.Entries)
                | None -> Collections.Generic.Dictionary<string, Index.Entry>())
        // Stamp first, then read: a file changed while being read then looks stale on the next run.
        let paths =
            phase "ferret.enumerate" (fun a ->
                let paths = roots |> Seq.collect files.Enumerate |> Seq.distinct |> List.ofSeq
                tag a "files" paths.Length
                paths)
        let walked =
            phase "ferret.stamp" (fun a ->
                let walked = paths |> List.map (fun path -> path, entryKey path, files.Stamp path)
                tag a "files" walked.Length
                walked)
        let seen = Collections.Generic.HashSet<string>()
        let mutable changed = false
        let mutable reused = 0
        let mutable built = 0
        let candidates =
            phase "ferret.index.build" (fun a ->
                let candidates =
                    walked
                    |> List.choose (fun (path, key, stamp) ->
                        match entries.TryGetValue key with
                        | true, entry when stamp = Some entry.Stamp ->
                            seen.Add key |> ignore
                            reused <- reused + 1
                            Some(candidateOf terms termSet path entry)
                        | cached, _ ->
                            match stamp |> Option.bind (buildEntry files path) with
                            | Some entry ->
                                seen.Add key |> ignore
                                entries[key] <- entry
                                changed <- true
                                built <- built + 1
                                Some(candidateOf terms termSet path entry)
                            | None ->
                                // A file that cannot be read and has no entry changes nothing.
                                if cached then
                                    entries.Remove key |> ignore
                                    changed <- true
                                None)
                tag a "files.total" walked.Length
                tag a "files.reused" reused
                tag a "files.built" built
                candidates)
        let searched =
            roots |> List.map (fun r -> entryKey (if r = "" then "." else r), files.Kind r = IsDirectory)
        let gone = entries.Keys |> Seq.filter (fun k -> not (seen.Contains k) && isUnder searched k) |> List.ofSeq
        for key in gone do
            entries.Remove key |> ignore
        phase "ferret.index.save" (fun a ->
            let save = changed || not gone.IsEmpty
            tag a "saved" save
            tag a "entries" entries.Count
            tag a "files.removed" gone.Length
            if save then
                store.Save { Version = Index.version; Entries = entries })
        rank ranker files query terms limit candidates
