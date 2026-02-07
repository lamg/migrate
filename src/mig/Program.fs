open System.IO
open Argu
open migrate.DeclarativeMigrations
open migrate.Execution
open migrate.CodeGen
open FsToolkit.ErrorHandling

type Args =
  | [<CliPrefix(CliPrefix.None)>] Status of ParseResults<GenArgs>
  | [<CliPrefix(CliPrefix.None)>] Commit of ParseResults<ExecArgs>
  | [<CliPrefix(CliPrefix.None)>] Schema of ParseResults<SchemaArgs>
  | [<CliPrefix(CliPrefix.None)>] Codegen of ParseResults<CodegenArgs>
  | [<CliPrefix(CliPrefix.None)>] Log of ParseResults<LogArgs>
  | [<CliPrefix(CliPrefix.None)>] Init of ParseResults<InitArgs>
  | [<CliPrefix(CliPrefix.None)>] Seed of ParseResults<SeedArgs>
  | [<AltCommandLine("-nc")>] NoColors
  | [<AltCommandLine("-nl")>] NoLog
  | [<AltCommandLine("-v")>] Version

  interface IArgParserTemplate with
    member s.Usage =
      match s with
      | Status _ -> "generates a migration script"
      | Commit _ -> "generates and executes step by step a migration script"
      | Schema _ -> "show the database schema"
      | Codegen _ -> "generate F# types and queries from SQL schema files"
      | Seed _ -> "execute seed statements (INSERT OR REPLACE) from SQL files"
      | NoColors -> "when present deactivates the SQL syntax highlighting"
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

and CodegenArgs =
  | [<AltCommandLine("-d")>] Directory of string
  | [<AltCommandLine("-a")>] Async

  interface IArgParserTemplate with
    member s.Usage =
      match s with
      | Directory _ -> "Directory containing SQL schema files (defaults to current directory)"
      | Async -> "Generate async methods using Task and task computation expression"

and LogArgs =
  | [<AltCommandLine("-s")>] StepsId of string

  interface IArgParserTemplate with
    member s.Usage =
      match s with
      | StepsId _ -> "Show migration steps by ID (a date shown by `mig log`)"

and SeedArgs =
  | [<NoCommandLine>] Dummy

  interface IArgParserTemplate with
    member s.Usage = ""

let generate withColors =
  withColors
  |> Exec.generateMigrationScript
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
      let! statements = Exec.migrationStatements false
      let! results = Exec.executeMigration statements
      return results
    }
    |> printExecResult

  let execAndLog (flags: ParseResults<ExecArgs>) =
    let message = flags.TryGetResult Message

    result {
      let! statements = Exec.migrationStatements true
      let! results = Exec.executeMigrations (message, statements)
      return results
    }
    |> printExecResult

let log (flags: ParseResults<LogArgs>) =
  match flags.TryGetResult StepsId with
  | Some id ->
    Exec.showSteps id
    |> List.map (FormatSql.format true)
    |> String.concat "\n\n"
    |> printfn "%s"

    0
  | None ->
    Exec.log () |> List.iter (printfn "%s\n")
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

let codegen (flags: ParseResults<CodegenArgs>) =
  let directory =
    match flags.TryGetResult Directory with
    | Some dir -> dir
    | None -> "."

  let useAsync = flags.Contains Async

  CodeGen.generateCode useAsync directory
  |> function
    | Ok stats ->
      printfn "Code generation complete!"
      printfn ""
      printfn "Statistics:"
      printfn $"  Normalized tables (DU): {stats.NormalizedTables}"
      printfn $"  Regular tables (records): {stats.RegularTables}"
      printfn $"  Views: {stats.Views}"
      printfn ""
      printfn "Generated files:"

      for file in stats.GeneratedFiles do
        printfn $"  {file}"

      0
    | Error e ->
      eprintfn $"Code generation error: {e}"
      1

let init () =
  let sql = "CREATE TABLE student(id integer PRIMARY KEY);"
  File.WriteAllText("schema.sql", sql)
  0

let seed () =
  match Exec.seedStatements () with
  | Ok statements ->
    if statements.IsEmpty then
      printfn "No seed statements found (only tables with primary keys are seeded)"
      0
    else
      match Exec.executeSeed statements with
      | Ok results ->
        results |> List.iter (printfn "%s")
        printfn $"\n✅ Successfully seeded {statements.Length} table(s)"
        0
      | Error e ->
        eprintfn $"Seed execution failed: {e}"
        1
  | Error e ->
    eprintfn $"Seed generation failed: {e}"
    1

let version () =
  let asm = System.Reflection.Assembly.GetExecutingAssembly()

  let version = asm.GetName().Version
  printfn $"{version.Major}.{version.Minor}.{version.Build}"
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
    | _ when results.Contains Version -> version ()
    | Some(Status _) -> generate withColors
    | Some(Args.Commit flags) when withLog -> Exec.execAndLog flags
    | Some(Args.Commit _) -> Exec.exec ()
    | Some(Schema _) -> schema withColors
    | Some(Codegen flags) -> codegen flags
    | Some(Log flags) -> log flags
    | Some(Init _) -> init ()
    | Some(Seed _) -> seed ()
    | _ ->
      eprintfn $"{parser.PrintUsage()}"
      1
  with e ->
    eprintfn $"{e.Message}"
    1
