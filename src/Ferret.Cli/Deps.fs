module Ferret.Cli.Deps

open System
open System.IO
open Microsoft.Win32.SafeHandles
open Ferret.FileSource

/// Adapters built once in `main` and passed to every command.
type AdapterDeps =
    { Files: IFileSource
      Out: TextWriter
      Err: TextWriter
      /// True when stdin carries data to search (a pipe or non-empty file), not a terminal or `/dev/null`.
      StdinIsReadable: bool
      IsDebug: bool }

/// Mirrors rg's check: search stdin only when it is a pipe, socket or file, so a terminal or
/// `/dev/null` (common under CI and agents) falls back to searching the current directory.
/// .NET cannot stat fd 0 portably, so on Unix a seekable, empty stdin counts as `/dev/null`.
/// That also treats `< emptyfile` as no stdin, where rg would search the empty input.
let private stdinIsReadable () =
    if not Console.IsInputRedirected then
        false
    elif OperatingSystem.IsWindows() then
        true
    else
        try
            use stdin = new FileStream(new SafeFileHandle(nativeint 0, false), FileAccess.Read, 1, false)
            not (stdin.CanSeek && stdin.Length = 0L)
        with _ ->
            true

module AdapterDeps =
    let create (isDebug: bool) : AdapterDeps =
        { Files = DiskFileSource()
          Out = new StreamWriter(Console.OpenStandardOutput(), Text.UTF8Encoding(false), AutoFlush = false)
          Err = Console.Error
          StdinIsReadable = stdinIsReadable ()
          IsDebug = isDebug }
