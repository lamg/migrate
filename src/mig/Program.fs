open System.IO
open Argu
open migrate.DeclarativeMigrations
open migrate.ImportGoose
open migrate.MigrationLog
open migrate.Execution
open FsToolkit.ErrorHandling

type Args =
  | [<CliPrefix(CliPrefix.None)>] Gen of ParseResults<GenArgs>
  | [<CliPrefix(CliPrefix.None)>] Exec of ParseResults<ExecArgs>
  | [<CliPrefix(CliPrefix.None)>] Schema of ParseResults<SchemaArgs>
  | [<CliPrefix(CliPrefix.None)>] Import of ParseResults<ImportArgs>
  | [<CliPrefix(CliPrefix.None)>] Log of ParseResults<LogArgs>
  | [<CliPrefix(CliPrefix.None)>] Init of ParseResults<InitArgs>
  | [<AltCommandLine("-nc")>] NoColors
  | [<AltCommandLine("-nl")>] NoLog
  | [<AltCommandLine("-v")>] Version

  interface IArgParserTemplate with
    member s.Usage =
      match s with
      | Gen _ -> "generates a migration script"
      | Exec _ -> "generates and executes step by step a migration script"
      | Schema _ -> "show the database schema"
      | NoColors -> "when present deactivates the SQL syntax highlighting"
      | Import _ -> "imports Goose migrations from a directory"
      | Log _ -> "print the migration log"
      | NoLog -> "do not log the migration"
      | Init _ -> "Initialize a project in the current directory with example definitions"
      | Version -> "Prints mig's version"

and InitArgs =
  | [<NoCommandLine>] Dummy

  interface IArgParserTemplate with
    member s.Usage = ""

and GenArgs =
  | [<NoCommandLine>] Dummy

  interface IArgParserTemplate with
    member s.Usage = ""

and ExecArgs =
  | [<AltCommandLine("-m")>] Message of string

  interface IArgParserTemplate with
    member s.Usage = ""

and SchemaArgs =
  | [<NoCommandLine>] Dummy

  interface IArgParserTemplate with
    member s.Usage = ""

and ImportArgs =
  | [<AltCommandLine("-d")>] Directory of string
  | [<AltCommandLine("-g")>] GenerateScript
  | [<AltCommandLine("-e")>] Exec

  interface IArgParserTemplate with
    member s.Usage =
      match s with
      | Directory _ -> "Goose directory with a list of SQL migration scripts"
      | GenerateScript -> "Generate script from Goose migrations without executing them"
      | Exec -> "Execute imported Goose migrations in the current directory"

and LogArgs =
  | [<AltCommandLine("-s")>] StepsId of string

  interface IArgParserTemplate with
    member s.Usage =
      match s with
      | StepsId _ -> "Show migration steps by ID (a date shown by `mig log`)"

let generate withColors =
  withColors
  |> ExecAndLog.generateMigrationScript
  |> function
    | Ok script ->
      printfn $"{script}"
      0
    | Error e ->
      eprintfn $"{e}"
      1

module Exec =
  let printExecResult (r: Result<string list, Types.MigrationError>) =
    match r with
    | Ok xs ->
      xs |> List.iter (printfn "%s")
      0
    | Error(Types.FailedSteps xs) ->
      xs |> List.iter (eprintfn "%s")
      1
    | Error e ->
      eprintfn $"error: {e}"
      1

  let exec () =
    result {
      let! statements = Exec.migrationStatements ()
      let! results = Exec.executeMigration statements
      return results
    }
    |> printExecResult

  let execAndLog (flags: ParseResults<ExecArgs>) =
    let message = flags.TryGetResult Message

    result {
      let! statements = ExecAndLog.migrationStatements ()
      let! results = ExecAndLog.executeMigrations (message, statements)
      return results
    }
    |> printExecResult

let log (flags: ParseResults<LogArgs>) =
  match flags.TryGetResult StepsId with
  | Some id ->
    ExecAndLog.showSteps id
    |> List.map (FormatSql.format true)
    |> String.concat "\n\n"
    |> printfn "%s"

    0
  | None ->
    ExecAndLog.log () |> List.iter (printfn "%s\n")
    0

let schema withColors =
  Exec.getDbSql withColors
  |> function
    | Ok sql ->
      printfn $"{sql}"
      0
    | Error e ->
      eprintfn $"{e}"
      1

module Goose =
  type Importer = string -> Result<string list, Types.MigrationError>

  let baseImport importer (withColors, flags: ParseResults<ImportArgs>) =
    match flags.TryGetResult Directory with
    | Some dir ->
      match flags.Contains GenerateScript, flags.Contains ImportArgs.Exec with
      | _, true ->
        match importer dir with
        | Ok steps ->
          steps |> List.iter (printfn "%s")
          0
        | Error(e: Types.MigrationError) ->
          eprintfn $"import error: {e}"
          1

      | _ ->
        match ImportGoose.scriptFromGoose (withColors, dir) with
        | Ok script ->
          printfn $"{script}"
          0
        | Error e ->
          eprintfn $"error generating import script: {e}"
          1
    | None ->
      eprintfn "A directory is required to import Goose migrations"
      eprintfn $"{flags.Parser.PrintUsage()}"
      1

  let import = baseImport ImportGoose.execGooseImport
  let importLog = baseImport ImportGoose.execGooseImportLog

let init () =
  let sql = "CREATE TABLE student(id integer PRIMARY KEY);"
  File.WriteAllText("schema.sql", sql)
  0

let version () =
  let asm = System.Reflection.Assembly.GetExecutingAssembly()
  let version = asm.GetName().Version
  printfn $"{version.Major}.{version.Minor}.{version.Revision}"
  0

[<EntryPoint>]
let main args =

  let errorHandler =
    ProcessExiter(
      colorizer =
        function
        | ErrorCode.HelpText -> None
        | _ -> Some System.ConsoleColor.Red
    )

  let parser =
    ArgumentParser.Create<Args>(programName = "mig", errorHandler = errorHandler)

  let results = parser.ParseCommandLine(inputs = args, raiseOnUsage = true)

  let command = results.TryGetSubCommand()
  let withColors = results.Contains NoColors |> not
  let withLog = results.Contains NoLog |> not

  try
    match command with
    | _ when results.Contains Version -> version()
    | Some(Gen _) -> generate withColors
    | Some(Args.Exec flags) when withLog -> Exec.execAndLog flags
    | Some(Args.Exec _) -> Exec.exec ()
    | Some(Schema _) -> schema withColors
    | Some(Import flags) when withLog -> Goose.importLog (withColors, flags)
    | Some(Import flags) -> Goose.import (withColors, flags)
    | Some(Log flags) -> log flags
    | Some(Init _) -> init ()
    | _ ->
      eprintfn $"{parser.PrintUsage()}"
      1
  with e ->
    eprintfn $"{e.Message}"
    1
