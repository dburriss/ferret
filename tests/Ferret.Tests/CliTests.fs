module Ferret.Tests.CliTests

open System.IO
open Xunit
open Ferret.Domain
open Ferret.FileSource
open Ferret.Cli
open Ferret.Cli.Deps
open Ferret.Cli.GrepCli

/// Creates the files (an empty body makes just the file), runs `body` with the root, then cleans up.
let private withTree (files: (string * string) list) (body: string -> unit) =
    let dir = Directory.CreateTempSubdirectory "ferret-tests-"
    try
        for rel, content in files do
            let path = Path.Combine(dir.FullName, rel)
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, content)
        body dir.FullName
    finally
        dir.Delete true

let private grep pattern paths =
    { Pattern = pattern
      Paths = paths
      Options = MatchOptions.defaults
      LineNumbers = false
      Mode = ShowLines }

/// Disk file source whose stdin is the given lines.
let private withStdin (stdin: string list) =
    let disk = DiskFileSource() :> IFileSource
    { new IFileSource with
        member _.Kind p = disk.Kind p
        member _.Enumerate r = disk.Enumerate r
        member _.ReadLines p = disk.ReadLines p
        member _.Stamp p = disk.Stamp p
        member _.ReadStdin() = Seq.ofList stdin }

/// Exit code, stdout, stderr. `stdin` is `Some lines` when stdin is piped.
let private runWith (stdin: string list option) (args: GrepArgs) =
    let out = new StringWriter()
    let err = new StringWriter()
    let deps =
        { Files = withStdin (Option.defaultValue [] stdin)
          Out = out
          Err = err
          Index = Ferret.Index.MemoryIndexStore()
          StdinIsReadable = Option.isSome stdin
          IsDebug = false }
    let code = GrepCli.run deps args
    code, out.ToString().Replace("\r\n", "\n"), err.ToString().Replace("\r\n", "\n")

let private run (args: GrepArgs) = runWith None args

let private lines (xs: string list) = String.concat "\n" xs + (if xs.IsEmpty then "" else "\n")

let private tree =
    [ "a.txt", "hello world\nnothing\nhello again\n"
      "sub/b.txt", "say hello\n"
      "sub/c.txt", "nope\n" ]

[<Fact>]
let ``directory search prefixes paths, in path order`` () =
    withTree tree (fun d ->
        let code, out, _ = run (grep "hello" [ d ])
        Assert.Equal(0, code)
        Assert.Equal(lines [ $"{d}/a.txt:hello world"; $"{d}/a.txt:hello again"; $"{d}/sub/b.txt:say hello" ], out))

[<Fact>]
let ``line numbers`` () =
    withTree tree (fun d ->
        let code, out, _ = run { grep "hello" [ d ] with LineNumbers = true }
        Assert.Equal(0, code)
        Assert.Equal(lines [ $"{d}/a.txt:1:hello world"; $"{d}/a.txt:3:hello again"; $"{d}/sub/b.txt:1:say hello" ], out))

[<Fact>]
let ``a single file has no path prefix`` () =
    withTree tree (fun d ->
        let code, out, _ = run { grep "hello" [ $"{d}/a.txt" ] with LineNumbers = true }
        Assert.Equal(0, code)
        Assert.Equal(lines [ "1:hello world"; "3:hello again" ], out))

[<Fact>]
let ``two files keep the path prefix`` () =
    withTree tree (fun d ->
        let _, out, _ = run (grep "hello" [ $"{d}/a.txt"; $"{d}/sub/b.txt" ])
        Assert.Equal(lines [ $"{d}/a.txt:hello world"; $"{d}/a.txt:hello again"; $"{d}/sub/b.txt:say hello" ], out))

[<Fact>]
let ``no match exits 1 with no output`` () =
    withTree tree (fun d ->
        let code, out, err = run (grep "zzz" [ d ])
        Assert.Equal(1, code)
        Assert.Equal("", out)
        Assert.Equal("", err))

[<Fact>]
let ``missing path exits 2 even when another path matches`` () =
    withTree tree (fun d ->
        let code, out, err = run (grep "hello" [ $"{d}/a.txt"; $"{d}/nope" ])
        Assert.Equal(2, code)
        Assert.Equal(lines [ $"{d}/a.txt:hello world"; $"{d}/a.txt:hello again" ], out)
        Assert.Equal($"ferret: {d}/nope: No such file or directory (os error 2)\n", err))

[<Fact>]
let ``invalid regex exits 2`` () =
    withTree tree (fun d ->
        let code, out, err = run (grep "(" [ d ])
        Assert.Equal(2, code)
        Assert.Equal("", out)
        Assert.StartsWith("ferret: regex parse error:", err))

[<Fact>]
let ``files with matches`` () =
    withTree tree (fun d ->
        let code, out, _ = run { grep "hello" [ d ] with Mode = ShowPaths }
        Assert.Equal(0, code)
        Assert.Equal(lines [ $"{d}/a.txt"; $"{d}/sub/b.txt" ], out))

[<Fact>]
let ``count lists only files with matches`` () =
    withTree tree (fun d ->
        let code, out, _ = run { grep "hello" [ d ] with Mode = ShowCounts }
        Assert.Equal(0, code)
        Assert.Equal(lines [ $"{d}/a.txt:2"; $"{d}/sub/b.txt:1" ], out))

[<Fact>]
let ``count for a single file is bare`` () =
    withTree tree (fun d ->
        let _, out, _ = run { grep "hello" [ $"{d}/a.txt" ] with Mode = ShowCounts }
        Assert.Equal("2\n", out))

[<Fact>]
let ``hidden and binary files are skipped`` () =
    withTree [ "a.txt", "hello\n"; ".hidden.txt", "hello\n"; ".dot/x.txt", "hello\n" ] (fun d ->
        File.WriteAllBytes(Path.Combine(d, "bin.dat"), [| 104uy; 101uy; 0uy; 104uy; 101uy |])
        let _, out, _ = run (grep "he" [ d ])
        Assert.Equal(lines [ $"{d}/a.txt:hello" ], out))

[<Fact>]
let ``gitignore applies inside a git repository`` () =
    withTree [ ".git/HEAD", ""; ".gitignore", "*.log\nbuild/\n"; "a.txt", "x\n"; "a.log", "x\n"; "build/b.txt", "x\n" ] (fun d ->
        let _, out, _ = run (grep "x" [ d ])
        Assert.Equal(lines [ $"{d}/a.txt:x" ], out))

[<Fact>]
let ``gitignore is not used outside a git repository`` () =
    withTree [ ".gitignore", "*.log\n"; "a.txt", "x\n"; "a.log", "x\n" ] (fun d ->
        let _, out, _ = run (grep "x" [ d ])
        Assert.Equal(lines [ $"{d}/a.log:x"; $"{d}/a.txt:x" ], out))

[<Fact>]
let ``ignore and ferretignore apply without a git repository`` () =
    withTree [ ".ignore", "*.log\n"; ".ferretignore", "*.tmp\n"; "a.txt", "x\n"; "a.log", "x\n"; "a.tmp", "x\n" ] (fun d ->
        let _, out, _ = run (grep "x" [ d ])
        Assert.Equal(lines [ $"{d}/a.txt:x" ], out))

[<Fact>]
let ``ferretignore overrides gitignore`` () =
    withTree [ ".git/HEAD", ""; ".gitignore", "*.log\n"; ".ferretignore", "!keep.log\n"; "keep.log", "x\n"; "drop.log", "x\n" ] (fun d ->
        let _, out, _ = run (grep "x" [ d ])
        Assert.Equal(lines [ $"{d}/keep.log:x" ], out))

[<Fact>]
let ``a nested ignore file applies below its directory only`` () =
    withTree [ "a.tmp", "x\n"; "sub/.ignore", "*.tmp\n"; "sub/b.tmp", "x\n"; "sub/b.txt", "x\n" ] (fun d ->
        let _, out, _ = run (grep "x" [ d ])
        Assert.Equal(lines [ $"{d}/a.tmp:x"; $"{d}/sub/b.txt:x" ], out))

[<Fact>]
let ``an explicit file is searched even when ignored`` () =
    withTree [ ".ignore", "*.log\n"; "a.log", "x\n" ] (fun d ->
        let code, out, _ = run (grep "x" [ $"{d}/a.log" ])
        Assert.Equal(0, code)
        Assert.Equal("x\n", out))

[<Fact>]
let ``short flags are expanded`` () =
    Assert.Equal<string[]>([| "-i"; "-n"; "foo"; "-w" |], Args.expandShortFlags [| "-in"; "foo"; "-w" |])

[<Fact>]
let ``unknown clusters and long flags are left alone`` () =
    Assert.Equal<string[]>([| "-xy"; "--ignore-case"; "-i" |], Args.expandShortFlags [| "-xy"; "--ignore-case"; "-i" |])

[<Fact>]
let ``nothing is expanded after the separator`` () =
    Assert.Equal<string[]>([| "-i"; "--"; "-in" |], Args.expandShortFlags [| "-i"; "--"; "-in" |])

[<Fact>]
let ``argv is split at the first separator`` () =
    let flags, rest = Args.splitSeparator [| "-i"; "--"; "-F"; "--"; "x" |]
    Assert.Equal<string[]>([| "-i" |], flags)
    Assert.Equal<string list>([ "-F"; "--"; "x" ], rest)
    let flags, rest = Args.splitSeparator [| "-i"; "foo" |]
    Assert.Equal<string[]>([| "-i"; "foo" |], flags)
    Assert.Empty rest

[<Fact>]
let ``dash-prefixed positionals are unrecognized flags`` () =
    Assert.Equal(Some "--bogus", Args.tryFindUnrecognizedFlag [ "foo"; "--bogus" ])
    Assert.Equal(None, Args.tryFindUnrecognizedFlag [ "foo"; "docs"; "-" ])

let private piped = [ "alpha"; "hello stdin"; "omega" ]

[<Fact>]
let ``piped stdin is searched when no path is given`` () =
    let code, out, _ = runWith (Some piped) { grep "hello" [] with LineNumbers = true }
    Assert.Equal(0, code)
    Assert.Equal("2:hello stdin\n", out)

[<Fact>]
let ``no match on stdin exits 1`` () =
    let code, out, _ = runWith (Some piped) (grep "zzz" [])
    Assert.Equal(1, code)
    Assert.Equal("", out)

[<Fact>]
let ``a dash path reads stdin`` () =
    let code, out, _ = runWith (Some piped) (grep "hello" [ "-" ])
    Assert.Equal(0, code)
    Assert.Equal("hello stdin\n", out)

[<Fact>]
let ``a dash path reads stdin even when stdin is not detected as piped`` () =
    let disk = withStdin piped
    let out = new StringWriter()
    let deps = { Files = disk; Index = Ferret.Index.MemoryIndexStore(); Out = out; Err = new StringWriter(); StdinIsReadable = false; IsDebug = false }
    Assert.Equal(0, GrepCli.run deps (grep "hello" [ "-" ]))
    Assert.Equal("hello stdin\n", out.ToString().Replace("\r\n", "\n"))

[<Fact>]
let ``an explicit path wins over piped stdin`` () =
    withTree tree (fun d ->
        let code, out, _ = runWith (Some piped) (grep "hello" [ $"{d}/a.txt" ])
        Assert.Equal(0, code)
        Assert.Equal(lines [ "hello world"; "hello again" ], out))

[<Fact>]
let ``stdin and a file together get path prefixes`` () =
    withTree tree (fun d ->
        let _, out, _ = runWith (Some piped) (grep "hello" [ "-"; $"{d}/sub/b.txt" ])
        Assert.Equal(lines [ "<stdin>:hello stdin"; $"{d}/sub/b.txt:say hello" ], out))

[<Fact>]
let ``stdin is named in files-with-matches`` () =
    withTree tree (fun d ->
        let _, out, _ = runWith (Some piped) { grep "hello" [ "-"; $"{d}/sub/b.txt" ] with Mode = ShowPaths }
        Assert.Equal(lines [ "<stdin>"; $"{d}/sub/b.txt" ], out))

[<Fact>]
let ``count on stdin alone is bare`` () =
    let _, out, _ = runWith (Some [ "a"; "a"; "b" ]) { grep "a" [] with Mode = ShowCounts }
    Assert.Equal("2\n", out)

[<Fact>]
let ``stdin is read once when given twice`` () =
    let _, out, _ = runWith (Some piped) (grep "hello" [ "-"; "-" ])
    Assert.Equal("<stdin>:hello stdin\n", out)

[<Fact>]
let ``regular files, pipes and sockets are searchable stdin`` () =
    Assert.True(Deps.stdinModeIsSearchable 0o100644u) // regular file
    Assert.True(Deps.stdinModeIsSearchable 0o010600u) // fifo
    Assert.True(Deps.stdinModeIsSearchable 0o140755u) // socket

[<Fact>]
let ``character devices, block devices and directories are not`` () =
    Assert.False(Deps.stdinModeIsSearchable 0o020666u) // /dev/null, a tty
    Assert.False(Deps.stdinModeIsSearchable 0o060660u) // block device
    Assert.False(Deps.stdinModeIsSearchable 0o040755u) // directory

let private ranked query paths limit : RankedCli.RankedArgs =
    { Query = query
      Paths = paths
      Limit = limit
      Scores = false
      Jsonl = false
      NoIndex = false }

let private runRanked (args: RankedCli.RankedArgs) =
    let out = new StringWriter()
    let err = new StringWriter()
    let deps =
        { Files = withStdin []
          Index = Ferret.Index.MemoryIndexStore()
          Out = out
          Err = err
          StdinIsReadable = false
          IsDebug = false }
    let code = RankedCli.run deps args
    code, out.ToString().Replace("\r\n", "\n"), err.ToString().Replace("\r\n", "\n")

let private rankedTree =
    [ "many.txt", "apple apple apple\nbanana\n"
      "once.txt", "apple\n"
      "none.txt", "cherry\n" ]

[<Fact>]
let ``lex ranks by relevance and skips files without the term`` () =
    withTree rankedTree (fun d ->
        let code, out, _ = runRanked (ranked "apple" [ d ] 100)
        Assert.Equal(0, code)
        Assert.Equal(lines [ $"{d}/many.txt:1:apple apple apple"; $"{d}/once.txt:1:apple" ], out))

[<Fact>]
let ``lex limit caps the number of files`` () =
    withTree rankedTree (fun d ->
        let code, out, _ = runRanked (ranked "apple" [ d ] 1)
        Assert.Equal(0, code)
        Assert.Equal(lines [ $"{d}/many.txt:1:apple apple apple" ], out))

[<Fact>]
let ``lex uses OR semantics over terms`` () =
    withTree rankedTree (fun d ->
        let code, out, _ = runRanked (ranked "banana cherry" [ d ] 100)
        Assert.Equal(0, code)
        Assert.Contains($"{d}/many.txt:2:banana", out)
        Assert.Contains($"{d}/none.txt:1:cherry", out))

[<Fact>]
let ``lex shows at most three excerpt lines per file`` () =
    withTree [ "a.txt", "x\nx\nx\nx\n" ] (fun d ->
        let _, out, _ = runRanked (ranked "x" [ d ] 100)
        Assert.Equal(lines [ $"{d}/a.txt:1:x"; $"{d}/a.txt:2:x"; $"{d}/a.txt:3:x" ], out))

[<Fact>]
let ``lex ties break by path`` () =
    withTree [ "b.txt", "x\n"; "a.txt", "x\n" ] (fun d ->
        let _, out, _ = runRanked (ranked "x" [ d ] 100)
        Assert.Equal(lines [ $"{d}/a.txt:1:x"; $"{d}/b.txt:1:x" ], out))

[<Fact>]
let ``lex ignores ignored files`` () =
    withTree [ "a.txt", "apple\n"; "skip.txt", "apple apple\n"; ".ignore", "skip.txt\n" ] (fun d ->
        let _, out, _ = runRanked (ranked "apple" [ d ] 100)
        Assert.Equal(lines [ $"{d}/a.txt:1:apple" ], out))

[<Fact>]
let ``lex with no match exits 1`` () =
    withTree rankedTree (fun d ->
        let code, out, _ = runRanked (ranked "zebra" [ d ] 100)
        Assert.Equal(1, code)
        Assert.Equal("", out))

[<Fact>]
let ``lex with a missing path exits 2`` () =
    withTree rankedTree (fun d ->
        let code, _, err = runRanked (ranked "apple" [ $"{d}/nope" ] 100)
        Assert.Equal(2, code)
        Assert.Contains("No such file or directory", err))

[<Fact>]
let ``lex scores prefix every excerpt line`` () =
    withTree rankedTree (fun d ->
        let _, plain, _ = runRanked (ranked "apple" [ d ] 100)
        let _, out, _ = runRanked { ranked "apple" [ d ] 100 with Scores = true }
        let outLines = out.TrimEnd('\n').Split('\n')
        Assert.Equal(2, outLines.Length)
        for line in outLines do
            Assert.Matches(@"^\[\d+\.\d{4}\] ", line)
        let stripped = outLines |> Array.map (fun l -> l.Substring(l.IndexOf("] ") + 2))
        Assert.Equal(plain, lines (List.ofArray stripped)))

[<Fact>]
let ``lex scores are non-increasing down the output`` () =
    withTree rankedTree (fun d ->
        let _, out, _ = runRanked { ranked "apple banana" [ d ] 100 with Scores = true }
        let scores =
            out.TrimEnd('\n').Split('\n')
            |> Array.map (fun l -> float (l.Substring(1, l.IndexOf ']' - 1)))
        Assert.Equal<float[]>(Array.sortDescending scores, scores))

let private parseJsonl (out: string) =
    out.TrimEnd('\n').Split('\n')
    |> Array.map (fun l -> System.Text.Json.JsonDocument.Parse(l).RootElement)

[<Fact>]
let ``lex jsonl writes one object per excerpt with file, line, score and text`` () =
    withTree rankedTree (fun d ->
        let code, out, _ = runRanked { ranked "apple" [ d ] 100 with Jsonl = true }
        Assert.Equal(0, code)
        let objs = parseJsonl out
        Assert.Equal(2, objs.Length)
        Assert.Equal($"{d}/many.txt", objs[0].GetProperty("file").GetString())
        Assert.Equal(1, objs[0].GetProperty("line").GetInt32())
        Assert.Equal("apple apple apple", objs[0].GetProperty("text").GetString())
        Assert.Matches(@"""score"":\d+\.\d{4}[,}]", out)
        Assert.True(objs[0].GetProperty("score").GetDouble() >= objs[1].GetProperty("score").GetDouble()))

[<Fact>]
let ``lex jsonl escapes quotes, backslashes and control characters`` () =
    withTree [ "a.txt", "apple \"q\" back\\slash\ttab\n" ] (fun d ->
        let _, out, _ = runRanked { ranked "apple" [ d ] 100 with Jsonl = true }
        let objs = parseJsonl out
        Assert.Equal(1, objs.Length)
        Assert.Equal("apple \"q\" back\\slash\ttab", objs[0].GetProperty("text").GetString()))

[<Fact>]
let ``lex jsonl ignores scores`` () =
    withTree rankedTree (fun d ->
        let _, withFlag, _ = runRanked { ranked "apple" [ d ] 100 with Jsonl = true; Scores = true }
        let _, without, _ = runRanked { ranked "apple" [ d ] 100 with Jsonl = true }
        Assert.Equal(without, withFlag))

[<Fact>]
let ``lex jsonl with no match exits 1 and prints nothing`` () =
    withTree rankedTree (fun d ->
        let code, out, _ = runRanked { ranked "zebra" [ d ] 100 with Jsonl = true }
        Assert.Equal(1, code)
        Assert.Equal("", out))

[<Fact>]
let ``lex gives the same output with and without the index`` () =
    withTree rankedTree (fun d ->
        let args = { ranked "apple banana" [ d ] 100 with Scores = true }
        let _, indexed, _ = runRanked args
        let _, direct, _ = runRanked { args with NoIndex = true }
        Assert.Equal(direct, indexed))
