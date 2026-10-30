module Ferret.Cli.Deps

open System
open System.IO
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles
open Ferret.FileSource

/// Adapters built once in `main` and passed to every command.
type AdapterDeps =
    { Files: IFileSource
      Out: TextWriter
      Err: TextWriter
      /// True when stdin is a pipe, socket or file to search, not a terminal or `/dev/null`.
      StdinIsReadable: bool
      IsDebug: bool }

/// rg's rule: stdin is searched only when it is a regular file, pipe or socket. A terminal or
/// `/dev/null` (a character device, common under CI and agents) is not. `mode` is `st_mode`.
let stdinModeIsSearchable (mode: uint32) : bool =
    match mode &&& 0o170000u with
    | 0o100000u // regular file
    | 0o010000u // fifo
    | 0o140000u -> true // socket
    | _ -> false

module private Posix =
    [<DllImport("libc", EntryPoint = "fstat")>]
    extern int fstat(int fd, byte[] buf)

    /// glibc before 2.33 only exports `__fxstat`.
    [<DllImport("libc", EntryPoint = "__fxstat")>]
    extern int fxstat(int version, int fd, byte[] buf)

/// Where `st_mode` sits in `struct stat`. .NET has no portable way to stat a descriptor, so the
/// offsets are listed per platform. `None` for platforms not listed here.
let private statLayout () =
    if OperatingSystem.IsMacOS() then
        Some(4, 2, 0) // both architectures (the legacy 32-bit inode struct agrees)
    elif OperatingSystem.IsLinux() then
        match RuntimeInformation.ProcessArchitecture with
        | Architecture.X64 -> Some(24, 4, 1)
        | Architecture.Arm64 -> Some(16, 4, 0)
        | _ -> None
    else
        None

/// `st_mode` of fd 0, or `None` when it cannot be read.
let private stdinMode () : uint32 option =
    match statLayout () with
    | None -> None
    | Some(offset, size, fxstatVersion) ->
        let buf = Array.zeroCreate<byte> 512
        let attempt call =
            try
                Some(call ())
            with
            | :? DllNotFoundException
            | :? EntryPointNotFoundException -> None
        let status =
            match attempt (fun () -> Posix.fstat (0, buf)) with
            | Some status -> Some status
            | None when OperatingSystem.IsLinux() -> attempt (fun () -> Posix.fxstat (fxstatVersion, 0, buf))
            | None -> None
        match status with
        | Some 0 ->
            if size = 2 then Some(uint32 (BitConverter.ToUInt16(buf, offset)))
            else Some(BitConverter.ToUInt32(buf, offset))
        | _ -> None

/// Used when fd 0 cannot be stat'ed: a seekable, empty stdin is taken to be `/dev/null`.
/// This wrongly treats `< emptyfile` as no stdin.
let private stdinIsReadableFallback () =
    try
        use stdin = new FileStream(new SafeFileHandle(nativeint 0, false), FileAccess.Read, 1, false)
        not (stdin.CanSeek && stdin.Length = 0L)
    with _ ->
        true

let private stdinIsReadable () =
    if not Console.IsInputRedirected then false
    elif OperatingSystem.IsWindows() then true
    else
        match stdinMode () with
        | Some mode -> stdinModeIsSearchable mode
        | None -> stdinIsReadableFallback ()

module AdapterDeps =
    let create (isDebug: bool) : AdapterDeps =
        { Files = DiskFileSource()
          Out = new StreamWriter(Console.OpenStandardOutput(), Text.UTF8Encoding(false), AutoFlush = false)
          Err = Console.Error
          StdinIsReadable = stdinIsReadable ()
          IsDebug = isDebug }
