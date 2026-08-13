module Mig.Program

open System
open System.Reflection
open MigLib
open MigLib.Codegen

let private writeOut (s: string) = Console.WriteLine s
let private writeErr (s: string) = Console.Error.WriteLine s

let private usage () =
  writeOut "USAGE: mig <command> [options]"
  writeOut ""
  writeOut "Commands:"
  writeOut "  codegen   Generate F# modules from an annotated schema directory"
  writeOut "  version   Print version and exit"
  writeOut "  help      Show this help"
  writeOut ""
  writeOut "codegen options:"
  writeOut "  -m, --migrations <dir>   Schema directory (snapshot *.sql + optional _migration.sql)"
  writeOut "  -o, --output <dir>       Output directory for generated modules (required)"
  writeOut "  -n, --namespace <name>   F# namespace prefix, e.g. MyApp.Db (required)"

let private getVersionText () =
  let asm = Assembly.GetExecutingAssembly()

  let informational =
    asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    |> Option.ofObj
    |> Option.map _.InformationalVersion

  match informational with
  | Some v when not (String.IsNullOrWhiteSpace v) -> "mig " + v
  | _ ->
    let v = asm.GetName().Version

    if isNull v then "mig 0.0.0" else "mig " + v.ToString()

let private parseCodegenArgs (args: string list) : Result<string * string * string, string> =
  let mutable migrations = None
  let mutable output = None
  let mutable ns = None
  let mutable error = None
  let mutable rest = args

  while error.IsNone && not rest.IsEmpty do
    match rest with
    | ("-m" | "--migrations") :: value :: tail when not (value.StartsWith("-", StringComparison.Ordinal)) ->
      migrations <- Some value
      rest <- tail
    | ("-o" | "--output") :: value :: tail when not (value.StartsWith("-", StringComparison.Ordinal)) ->
      output <- Some value
      rest <- tail
    | ("-n" | "--namespace") :: value :: tail when not (value.StartsWith("-", StringComparison.Ordinal)) ->
      ns <- Some value
      rest <- tail
    | unknown :: _ -> error <- Some("unrecognized argument: " + unknown)
    | [] -> ()

  match error with
  | Some msg -> Error msg
  | None ->
    match migrations, output, ns with
    | Some m, Some o, Some n -> Ok(m, o, n)
    | _ -> Error "codegen requires -m|--migrations, -o|--output, and -n|--namespace"

let private executeCodegen (migrations: string) (output: string) (ns: string) =
  match generate migrations output ns with
  | Ok result ->
    writeOut (
      "generated "
      + string result.relationCount
      + " relation(s) -> "
      + result.outputDir
      + " (namespace "
      + result.namespaceName
      + ")"
    )

    for path in result.generatedFiles do
      writeOut ("  " + path)

    0
  | Error message ->
    writeErr ("codegen failed: " + message)
    1

[<EntryPoint>]
let main argv =
  let args = argv |> Array.toList

  match args with
  | []
  | [ "help" ]
  | [ "--help" ]
  | [ "-h" ] ->
    usage ()
    0
  | "version" :: _ ->
    writeOut (getVersionText ())
    0
  | "codegen" :: rest ->
    match parseCodegenArgs rest with
    | Error msg ->
      writeErr msg
      usage ()
      1
    | Ok(migrations, output, ns) -> executeCodegen migrations output ns
  | unknown :: _ ->
    writeErr ("unrecognized command: " + unknown)
    usage ()
    1
