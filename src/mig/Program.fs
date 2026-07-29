module Mig.Program

open System
open System.Reflection
open Argu
open MigLib

type CodegenArgs =
  | [<Mandatory; AltCommandLine("-m")>] Migrations of path: string
  | [<Mandatory; AltCommandLine("-o")>] Output of path: string
  | [<Mandatory; AltCommandLine("-n")>] Namespace of name: string
  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Migrations _ -> "directory containing ordered DbUp *.sql migration scripts"
      | Output _ -> "directory for generated modules (one .fs file per relation)"
      | Namespace _ -> "F# namespace prefix for modules (e.g. MyApp.Data.Stores)"

type Command =
  | [<CliPrefix(CliPrefix.None)>] Codegen of ParseResults<CodegenArgs>
  | [<CliPrefix(CliPrefix.None)>] Version
  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Codegen _ -> "generate F# types and queries from annotated SQL migrations"
      | Version -> "print version and exit"

let private getVersionText () =
  let asm = Assembly.GetExecutingAssembly()

  let version =
    asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    |> Option.ofObj
    |> Option.map _.InformationalVersion
    |> Option.defaultValue (
      let v = asm.GetName().Version
      if isNull v then "0.0.0" else v.ToString()
    )

  $"mig {version}"

let private runCodegen (args: ParseResults<CodegenArgs>) =
  let migrations = args.GetResult Migrations
  let output = args.GetResult Output
  let ns = args.GetResult Namespace

  match generate migrations output ns with
  | Ok result ->
    printfn
      "generated %d relation(s) -> %s (namespace %s)"
      result.relationCount
      result.outputDir
      result.namespaceName

    for path in result.generatedFiles do
      printfn "  %s" path

    0
  | Error message ->
    eprintfn "codegen failed: %s" message
    1

[<EntryPoint>]
let main argv =
  let parser = ArgumentParser.Create<Command>(programName = "mig")

  try
    let results = parser.ParseCommandLine(inputs = argv, raiseOnUsage = true)

    match results.GetSubCommand() with
    | Codegen args -> runCodegen args
    | Version ->
      printfn "%s" (getVersionText ())
      0
  with :? ArguParseException as ex ->
    eprintfn "%s" ex.Message
    1
