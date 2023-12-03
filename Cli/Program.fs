// Copyright 2023 Luis Ángel Méndez Gort

// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at

//     http://www.apache.org/licenses/LICENSE-2.0

// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

open System.Reflection
open Argu
open System
open System.Diagnostics
open Migrate
open Migrate.Types

type MigArgs =
  | [<CliPrefix(CliPrefix.None)>] Version of ParseResults<VersionArgs>
  | [<CliPrefix(CliPrefix.None)>] Log of ParseResults<LogArgs>
  | [<CliPrefix(CliPrefix.None)>] Commit of ParseResults<CommitArgs>
  | [<CliPrefix(CliPrefix.None)>] Pull of ParseResults<PullArgs>
  | [<CliPrefix(CliPrefix.None)>] Report of ParseResults<ReportArgs>
  | [<CliPrefix(CliPrefix.None)>] Init of ParseResults<ReportArgs>
  | [<CliPrefix(CliPrefix.None)>] Status of ParseResults<StatusArgs>
  | [<CliPrefix(CliPrefix.None)>] DbSchema of ParseResults<DumpSchemaArgs>
  | [<CliPrefix(CliPrefix.None)>] Relations of ParseResults<RelationsArgs>
  | [<CliPrefix(CliPrefix.None)>] Export of ParseResults<ExportArgs>

  interface IArgParserTemplate with
    member s.Usage =
      match s with
      | Version _ -> "Prints mig version"
      | Commit _ -> "run the migration instead of just printing it"
      | Log _ -> "shows previous migrations"
      | Pull _ -> "pull database from cloud"
      | Report _ -> "show and generate reports"
      | Init _ -> "initializes an empty project"
      | Status _ -> "show the differences between the current database schema and the schema in source files"
      | DbSchema _ -> "shows the current schema in the DB"
      | Relations _ -> "shows the relations (tables + views) type signatures in the database or project"
      | Export _ -> "exports the content of a relation as an insert statement"

and VersionArgs =
  | [<NoCommandLine>] Dummy

  interface IArgParserTemplate with
    member _.Usage = "Prints mig version"

and RelationsArgs =
  | [<AltCommandLine("-db")>] Database
  | [<AltCommandLine("-p")>] Project

  interface IArgParserTemplate with
    member s.Usage =
      match s with
      | Database -> "Print type signatures of relations (tables + views) in database"
      | Project -> "Print type signatures of relations (tables + views) in project"

and DumpSchemaArgs =
  | [<AltCommandLine("-f")>] DbFile of string
  | [<AltCommandLine("-o")>] Output of string

  interface IArgParserTemplate with
    member s.Usage =
      match s with
      | DbFile _ -> "get the schema of a specific database file"
      | Output _ -> "output file"

and StatusArgs =
  | [<NoCommandLine>] Dummy

  interface IArgParserTemplate with
    member _.Usage =
      "show the differences between the current database schema and the schema in source files"

and CommitArgs =
  | [<AltCommandLine("-m")>] Manual

  interface IArgParserTemplate with
    member _.Usage = "introduce a manual migration using standard input"

and LogArgs =
  | [<AltCommandLine("-c")>] CommitHash of string
  | [<AltCommandLine("-s")>] LastShort
  | [<AltCommandLine("-l")>] Last

  interface IArgParserTemplate with
    member s.Usage =
      match s with
      | CommitHash _ -> "the hash of the commit to see"
      | LastShort -> "shows the last migration with steps summarized"
      | Last -> "shows the last migration with detailed steps"

and PullArgs =
  | [<NoCommandLine>] Dummy

  interface IArgParserTemplate with
    member _.Usage = "pull"

and ReportArgs =
  | [<AltCommandLine("-s")>] SyncReports
  | [<AltCommandLine("-o")>] ShowReports

  interface IArgParserTemplate with
    member s.Usage =
      match s with
      | SyncReports -> "synchronize reports"
      | ShowReports -> "show reports"

and ExportArgs =
  | [<AltCommandLine("-r")>] Relation of string

  interface IArgParserTemplate with
    member s.Usage =
      match s with
      | Relation _ -> "relation name"

let showLog (p: Project) (args: ParseResults<LogArgs>) =
  match args.TryGetResult CommitHash, args.TryGetResult Last, args.TryGetResult LastShort with
  | Some hash, _, _ -> Cli.logDetailed p hash
  | _, Some Last, _ -> Cli.lastLogDetailed p
  | _, _, Some LastShort -> Cli.lastLogShort p
  | _ -> Cli.log p

let syncReports (p: Project) (args: ParseResults<ReportArgs>) =
  match args.TryGetResult SyncReports, args.TryGetResult ShowReports with
  | Some SyncReports, _ -> Cli.syncReports p
  | _, Some ShowReports -> Cli.showReports p
  | _ ->
    Print.printRed "unrecognized report command"
    1

let commit (p: Project) (args: ParseResults<CommitArgs>) =
  match args.TryGetResult Manual with
  | Some _ -> Cli.manualMigration p
  | None -> Cli.commit p

let pullDb (p: Project) =
  let doPull scriptPath =
    let script = System.IO.File.ReadAllText scriptPath
    let processInfo = ProcessStartInfo "bash"
    processInfo.RedirectStandardInput <- true
    processInfo.UseShellExecute <- false
    let proc = Process.Start processInfo
    let outputStream = proc.StandardInput
    outputStream.WriteLine script
    outputStream.Close()
    proc.WaitForExit()

  match p.pullScript with
  | Some scriptPath ->
    doPull scriptPath
    0
  | None ->
    Print.printRed "no pull script configured"
    1

let dumpSchema (p: Project) =
  printfn $"{Cli.dumpDbSchema p}"
  0

let relations (p: Project) (args: ParseResults<RelationsArgs>) =
  match args.TryGetResult Database, args.TryGetResult Project with
  | Some _, _ -> Cli.printDbRelations p
  | _, Some _ -> Cli.printProjectRelations p
  | _ ->
    Print.printRed "unrecognized relations command"
    1

let exportRelation (p: Project) (args: ParseResults<ExportArgs>) =
  match args.TryGetResult Relation with
  | Some relName -> Cli.exportRelation p relName
  | None ->
    Print.printRed "unrecognized export command"
    1

[<EntryPoint>]
let main args =
  dotenv.net.DotEnv.Load()

  let errorHandler =
    ProcessExiter(
      colorizer =
        function
        | ErrorCode.HelpText -> None
        | _ -> Some ConsoleColor.Red
    )

  let parser =
    ArgumentParser.Create<MigArgs>(programName = "mig", errorHandler = errorHandler)

  let results = parser.ParseCommandLine(inputs = args, raiseOnUsage = true)

  let command = results.TryGetSubCommand()

  try
    match command, results.Contains Version with
    | Some(Init _), _ -> Cli.initProject ()
    | _, true ->
      let asm = Assembly.GetExecutingAssembly()
      let version = asm.GetName().Version.ToString()
      printfn $"{version}"
      0
    | _ ->
      let p = Cli.loadProject ()

      match command with
      | Some(DbSchema _) -> dumpSchema p
      | Some(Commit flags) -> commit p flags
      | Some(Log flags) -> showLog p flags
      | Some(Pull _) -> pullDb p
      | Some(Report flags) -> syncReports p flags
      | Some(Init _) -> failwith "violated project init precondition"
      | Some(Status _) -> Cli.status p
      | Some(Relations flags) -> relations p flags
      | Some(Version _) ->
        Assembly.GetExecutingAssembly().GetName().Version.ToString() |> printfn "%s"
        0
      | Some(Export args) -> exportRelation p args
      | None ->
        Print.printRed "no command given"
        1
  with MalformedProject e ->
    Print.printRed e
    1
