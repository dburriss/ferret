module Ferret.Text

open System

/// Lowercased runs of letters and digits. Shared by indexing and querying.
let tokenize (text: string) : string list =
    let tokens = ResizeArray<string>()
    let mutable start = -1
    for i in 0 .. text.Length do
        let isWord = i < text.Length && Char.IsLetterOrDigit text[i]
        if isWord && start < 0 then
            start <- i
        elif not isWord && start >= 0 then
            tokens.Add(text.Substring(start, i - start).ToLowerInvariant())
            start <- -1
    List.ofSeq tokens
