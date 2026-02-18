module Mig.Program

open Argu
open System
open MigLib.HotMigration

[<CliPrefix(CliPrefix.DoubleDash)>]
type MigrateArgs =
  | [<Mandatory>] Old of path: string
  | [<Mandatory>] Schema of path: string
  | New of path: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Old _ -> "path to the old database"
      | Schema _ -> "path to the .fsx schema file"
      | New _ -> "path for the new database (default: <old>.new)"

[<CliPrefix(CliPrefix.DoubleDash)>]
type DrainArgs =
  | [<Mandatory>] Old of path: string
  | [<Mandatory>] New of path: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Old _ -> "path to the old database"
      | New _ -> "path to the new database"

[<CliPrefix(CliPrefix.DoubleDash)>]
type CutoverArgs =
  | [<Mandatory>] New of path: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | New _ -> "path to the new database"

[<CliPrefix(CliPrefix.DoubleDash)>]
type CleanupOldArgs =
  | [<Mandatory>] Old of path: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Old _ -> "path to the old database"

[<CliPrefix(CliPrefix.DoubleDash)>]
type StatusArgs =
  | [<Mandatory>] Old of path: string
  | New of path: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Old _ -> "path to the old database"
      | New _ -> "path to the new database"

type Command =
  | [<CliPrefix(CliPrefix.None)>] Migrate of ParseResults<MigrateArgs>
  | [<CliPrefix(CliPrefix.None)>] Drain of ParseResults<DrainArgs>
  | [<CliPrefix(CliPrefix.None)>] Cutover of ParseResults<CutoverArgs>
  | [<CliPrefix(CliPrefix.None)>] CleanupOld of ParseResults<CleanupOldArgs>
  | [<CliPrefix(CliPrefix.None)>] Status of ParseResults<StatusArgs>

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Migrate _ -> "create new database and copy data from old"
      | Drain _ -> "stop writes on old database and replay accumulated changes"
      | Cutover _ -> "mark new database as ready for serving"
      | CleanupOld _ -> "drop old-database migration tables after cutover"
      | Status _ -> "show current migration state"

let migrate (args: ParseResults<MigrateArgs>) =
  let old = args.GetResult MigrateArgs.Old
  let schema = args.GetResult MigrateArgs.Schema
  let newDb = args.TryGetResult MigrateArgs.New |> Option.defaultValue $"{old}.new"

  match runMigrate old schema newDb |> fun t -> t.Result with
  | Ok result ->
    printfn "Migrate complete."
    printfn $"Old database: {old}"
    printfn $"Schema script: {schema}"
    printfn $"New database: {result.newDbPath}"
    printfn $"Copied tables: {result.copiedTables}"
    printfn $"Copied rows: {result.copiedRows}"
    0
  | Error ex ->
    eprintfn $"migrate failed: {ex.Message}"
    1

let drain (args: ParseResults<DrainArgs>) =
  let old = args.GetResult DrainArgs.Old
  let newDb = args.GetResult DrainArgs.New

  match runDrain old newDb |> fun t -> t.Result with
  | Ok result ->
    printfn "Drain complete."
    printfn $"Old database: {old}"
    printfn $"New database: {newDb}"
    printfn $"Replayed entries: {result.replayedEntries}"
    printfn $"Remaining log entries: {result.remainingEntries}"
    printfn "Run `mig cutover` when ready."
    0
  | Error ex ->
    eprintfn $"drain failed: {ex.Message}"
    1

let cutover (args: ParseResults<CutoverArgs>) =
  let newDb = args.GetResult CutoverArgs.New

  match runCutover newDb |> fun t -> t.Result with
  | Ok result ->
    let droppedIdMapping = if result.idMappingDropped then "yes" else "no"

    let droppedMigrationProgress =
      if result.migrationProgressDropped then "yes" else "no"

    printfn "Cutover complete."
    printfn $"New database: {newDb}"
    printfn $"Previous migration status: {result.previousStatus}"
    printfn "Current migration status: ready"
    printfn $"Dropped _id_mapping: {droppedIdMapping}"
    printfn $"Dropped _migration_progress: {droppedMigrationProgress}"
    0
  | Error ex ->
    eprintfn $"cutover failed: {ex.Message}"
    1

let cleanupOld (args: ParseResults<CleanupOldArgs>) =
  let old = args.GetResult CleanupOldArgs.Old

  match runCleanupOld old |> fun t -> t.Result with
  | Ok result ->
    let previousMarkerStatus =
      result.previousMarkerStatus |> Option.defaultValue "no marker"

    let droppedMarker = if result.markerDropped then "yes" else "no"
    let droppedLog = if result.logDropped then "yes" else "no"

    printfn "Old database cleanup complete."
    printfn $"Old database: {old}"
    printfn $"Previous marker status: {previousMarkerStatus}"
    printfn $"Dropped _migration_marker: {droppedMarker}"
    printfn $"Dropped _migration_log: {droppedLog}"
    0
  | Error ex ->
    eprintfn $"cleanup-old failed: {ex.Message}"
    1

let status (args: ParseResults<StatusArgs>) =
  let old = args.GetResult StatusArgs.Old
  let newDb = args.TryGetResult StatusArgs.New

  match getStatus old newDb |> fun t -> t.Result with
  | Error ex ->
    eprintfn $"status failed: {ex.Message}"
    1
  | Ok report ->
    let markerStatus = report.oldMarkerStatus |> Option.defaultValue "no marker"
    printfn $"Old database: {old}"
    printfn $"Marker status: {markerStatus}"
    printfn $"Migration log entries: {report.migrationLogEntries}"

    match newDb with
    | Some newPath ->
      let migrationStatus =
        report.newMigrationStatus |> Option.defaultValue "no status marker"

      let isReady =
        report.newMigrationStatus
        |> Option.exists (fun status -> status.Equals("ready", StringComparison.OrdinalIgnoreCase))

      let pendingReplayText =
        match report.pendingReplayEntries with
        | Some pending when isReady -> $"{pending} (cutover complete)"
        | Some pending -> $"{pending}"
        | None -> "n/a"

      printfn $"New database: {newPath}"
      printfn $"Migration status: {migrationStatus}"
      printfn $"Pending replay entries: {pendingReplayText}"

      match report.idMappingTablePresent, report.idMappingEntries with
      | Some false, _ -> printfn "_id_mapping: removed"
      | Some true, Some entries -> printfn $"_id_mapping entries: {entries}"
      | _ -> ()

      match report.migrationProgressTablePresent with
      | Some false -> printfn "_migration_progress: removed"
      | Some true -> printfn "_migration_progress: present"
      | None -> ()
    | None -> ()

    0

[<EntryPoint>]
let main argv =
  let parser = ArgumentParser.Create<Command>(programName = "mig")

  try
    let results = parser.ParseCommandLine(inputs = argv, raiseOnUsage = true)

    match results.GetSubCommand() with
    | Migrate args -> migrate args
    | Drain args -> drain args
    | Cutover args -> cutover args
    | CleanupOld args -> cleanupOld args
    | Status args -> status args
  with :? ArguParseException as ex ->
    printfn $"%s{ex.Message}"
    1
