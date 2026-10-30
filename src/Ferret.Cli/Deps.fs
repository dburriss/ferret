module Ferret.Cli.Deps

open System
open System.IO
open Ferret.FileSource

/// Adapters built once in `main` and passed to every command.
type AdapterDeps =
    { Files: IFileSource
      Out: TextWriter
      Err: TextWriter
      IsDebug: bool }

module AdapterDeps =
    let create (isDebug: bool) : AdapterDeps =
        { Files = DiskFileSource()
          Out = new StreamWriter(Console.OpenStandardOutput(), Text.UTF8Encoding(false), AutoFlush = false)
          Err = Console.Error
          IsDebug = isDebug }
