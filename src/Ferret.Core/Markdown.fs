module Ferret.Markdown

open System
open System.Text.RegularExpressions
open Flexi.Yaml
open Flexi.Yaml.YamlExtensions
open Ferret.Domain

let private extensions = [ ".md"; ".markdown"; ".mdx" ]

let isMarkdown (path: string) : bool =
    extensions |> List.exists (fun e -> path.EndsWith(e, StringComparison.OrdinalIgnoreCase))

let private heading = Regex(@"^ {0,3}#{1,6}\s", RegexOptions.Compiled)

type private Mode =
    | Start
    | Front
    | Body

/// Frontmatter lines and headings are newest first.
type private State =
    { Mode: Mode
      Fenced: bool
      Front: string list
      Headings: string list }

let private body (s: State) (line: string) : State =
    let t = line.TrimStart()
    if t.StartsWith "```" || t.StartsWith "~~~" then
        { s with Fenced = not s.Fenced }
    elif not s.Fenced && heading.IsMatch line then
        { s with Headings = line :: s.Headings }
    else
        s

let private step (s: State) (line: string) : State =
    match s.Mode with
    | Start ->
        if line.TrimEnd() = "---" then
            { s with Mode = Front }
        else
            body { s with Mode = Body } line
    | Front ->
        let t = line.TrimEnd()
        if t = "---" || t = "..." then
            { s with Mode = Body }
        else
            { s with Front = line :: s.Front }
    | Body -> body s line

/// Every piece of scalar text in a YAML value: scalars as written, sequences element by element,
/// mappings by value.
let rec private flatten (value: YamlValue) : string list =
    match value with
    | YamlValue.String s -> [ s ]
    | YamlValue.Number n -> [ string n ]
    | YamlValue.Float f -> [ string f ]
    | YamlValue.Boolean b -> [ string b ]
    | YamlValue.Timestamp t -> [ t.ToString "yyyy-MM-dd" ]
    | YamlValue.Null -> []
    | YamlValue.Sequence xs -> xs |> List.ofArray |> List.collect flatten
    | YamlValue.Mapping ps -> ps |> List.ofArray |> List.collect (snd >> flatten)

let private frontmatterFields = [ "title", Field.Title; "description", Field.Description; "tags", Field.TagList ]

/// The title, description and tags of a frontmatter block. Empty when it is not valid YAML or
/// not a mapping.
let private frontmatter (lines: string list) : (Field * string) list =
    match YamlValue.TryParse(String.Join("\n", lines)) with
    | None -> []
    | Some doc ->
        frontmatterFields
        |> List.collect (fun (name, field) ->
            match doc.TryGetProperty name with
            | Some value -> flatten value |> List.map (fun text -> field, text)
            | None -> [])

/// The text of each boostable part of a Markdown file: frontmatter `title`, `description` and
/// `tags` values, then headings (outside fenced code). Frontmatter is a leading `---` block parsed
/// as YAML; an unterminated or invalid block yields no frontmatter fields.
let fieldText (lines: seq<string>) : (Field * string) list =
    let start =
        { Mode = Start
          Fenced = false
          Front = []
          Headings = [] }
    let final = Seq.fold step start lines
    // An unterminated block is not frontmatter.
    let front = if final.Mode = Front then [] else List.rev final.Front
    frontmatter front @ (List.rev final.Headings |> List.map (fun h -> Field.Heading, h))
