// Downloads a BEIR dataset into corpus/<name>/ (gitignored): docs/<id>.txt, queries.tsv, qrels.tsv.
// Usage: dotnet fsi scripts/fetch-corpus.fsx [scifact|nfcorpus]...   (default: both)
open System
open System.IO
open System.IO.Compression
open System.Net.Http
open System.Text.Json

let root = Path.Combine(__SOURCE_DIRECTORY__, "..", "corpus") |> Path.GetFullPath
let baseUrl = "https://public.ukp.informatik.tu-darmstadt.de/thakur/BEIR/datasets"

let datasets =
    match fsi.CommandLineArgs |> Array.skip 1 |> List.ofArray with
    | [] -> [ "scifact"; "nfcorpus" ]
    | names -> names

let jsonl (path: string) =
    File.ReadLines path
    |> Seq.filter (fun l -> l.Trim() <> "")
    |> Seq.map (fun l -> JsonDocument.Parse(l).RootElement)

let str (e: JsonElement) (name: string) =
    match e.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
    | _ -> ""

let oneLine (s: string) = s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')

let fetch (name: string) =
    let target = Path.Combine(root, name)
    if Directory.Exists(Path.Combine(target, "docs")) && File.Exists(Path.Combine(target, "qrels.tsv")) then
        printfn "%s: already fetched" name
    else
        Directory.CreateDirectory target |> ignore
        let zip = Path.Combine(target, $"{name}.zip")
        printfn "%s: downloading" name
        use http = new HttpClient()
        File.WriteAllBytes(zip, http.GetByteArrayAsync($"{baseUrl}/{name}.zip").Result)
        let raw = Path.Combine(target, "raw")
        if Directory.Exists raw then Directory.Delete(raw, true)
        ZipFile.ExtractToDirectory(zip, raw)
        let src = Path.Combine(raw, name)

        let docs = Path.Combine(target, "docs")
        Directory.CreateDirectory docs |> ignore
        for d in jsonl (Path.Combine(src, "corpus.jsonl")) do
            let id = str d "_id"
            File.WriteAllText(Path.Combine(docs, id + ".txt"), str d "title" + "\n" + str d "text" + "\n")

        // Test qrels only; queries without judgments are dropped.
        let qrels =
            File.ReadLines(Path.Combine(src, "qrels", "test.tsv"))
            |> Seq.skip 1
            |> Seq.filter (fun l -> l.Trim() <> "")
            |> Seq.map (fun l -> l.Split '\t')
            |> Seq.filter (fun p -> int p[2] > 0)
            |> List.ofSeq
        File.WriteAllLines(Path.Combine(target, "qrels.tsv"), qrels |> List.map (String.concat "\t"))
        let judged = qrels |> List.map (fun p -> p[0]) |> Set.ofList
        let queries =
            jsonl (Path.Combine(src, "queries.jsonl"))
            |> Seq.filter (fun q -> judged.Contains(str q "_id"))
            |> Seq.map (fun q -> str q "_id" + "\t" + oneLine (str q "text"))
            |> List.ofSeq
        File.WriteAllLines(Path.Combine(target, "queries.tsv"), queries)

        Directory.Delete(raw, true)
        File.Delete zip
        printfn "%s: %d docs, %d queries" name (Directory.GetFiles docs).Length queries.Length

for name in datasets do
    fetch name
