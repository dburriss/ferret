module Ferret.Text

open System

/// Bump when tokenizing, stop words or stemming change; part of the index cache key once an index exists.
[<Literal>]
let NormaliserVersion = 1

let stopWords : Set<string> =
    set [ "a"; "an"; "the"; "of"; "and"; "or"; "to"; "in"; "is"; "for"; "on" ]

let private isVowel c = "aeiou".IndexOf(c: char) >= 0

let private stripVerbSuffix (token: string) (suffix: string) =
    let stem = token.Substring(0, token.Length - suffix.Length)
    if stem.Length < 3 || not (Seq.exists isVowel stem) then
        None
    else
        let last = stem[stem.Length - 1]
        let doubled =
            stem.Length > 3
            && last = stem[stem.Length - 2]
            && not (isVowel last)
            && last <> 'l'
            && last <> 's'
            && last <> 'z'
        Some(if doubled then stem.Substring(0, stem.Length - 1) else stem)

/// Conservative suffix stemmer (-ing, -ed, -es, -s) so `mock` matches `mocked`. Digit-bearing and short tokens are left alone.
let stem (token: string) : string =
    if token.Length <= 3 || Seq.exists Char.IsDigit token then
        token
    elif token.EndsWith "ing" then
        stripVerbSuffix token "ing" |> Option.defaultValue token
    elif token.EndsWith "ed" then
        stripVerbSuffix token "ed" |> Option.defaultValue token
    elif token.EndsWith "ss" || token.EndsWith "us" || token.EndsWith "is" then
        token
    elif token.EndsWith "es" && ([ "ses"; "xes"; "zes"; "ches"; "shes" ] |> List.exists token.EndsWith) then
        token.Substring(0, token.Length - 2)
    elif token.EndsWith "s" then
        token.Substring(0, token.Length - 1)
    else
        token

/// Lowercased runs of letters and digits, minus stop words, stemmed. Shared by indexing and querying.
let tokenize (text: string) : string list =
    let tokens = ResizeArray<string>()
    let mutable start = -1
    for i in 0 .. text.Length do
        let isWord = i < text.Length && Char.IsLetterOrDigit text[i]
        if isWord && start < 0 then
            start <- i
        elif not isWord && start >= 0 then
            let token = text.Substring(start, i - start).ToLowerInvariant()
            if not (stopWords.Contains token) then
                tokens.Add(stem token)
            start <- -1
    List.ofSeq tokens
