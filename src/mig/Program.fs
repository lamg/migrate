module Mig.Program

open Argu
open System
open System.IO
open System.Security.Cryptography
open System.Text
open MigLib.HotMigration

[<CliPrefix(CliPrefix.DoubleDash)>]
type MigrateArgs =
  | Old of path: string
  | Schema of path: string
  | Schema_Commit of value: string
  | New of path: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Old _ -> "path to the old database (default: auto-detect <dir>-<old-hash>.sqlite in current directory)"
      | Schema _ -> "path to the .fsx schema file (default: ./schema.fsx)"
      | Schema_Commit _ -> "optional schema commit metadata (default: MIG_SCHEMA_COMMIT env var when set)"
      | New _ -> "path for the new database (default: ./<dir>-<schema-hash>.sqlite)"

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
  | [<CliPrefix(CliPrefix.None); CustomCommandLine("cleanup-old")>] CleanupOld of ParseResults<CleanupOldArgs>
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
  let normalizeLineEndings (text: string) =
    text.Replace("\r\n", "\n").Replace("\r", "\n")

  let currentDirectory = Directory.GetCurrentDirectory()
  let directoryName = DirectoryInfo(currentDirectory).Name

  let isHexHashSegment (value: string) =
    value.Length = 16 && value |> Seq.forall Uri.IsHexDigit

  let isDirectoryHashNamedSqlite (path: string) =
    if not (Path.GetExtension(path).Equals(".sqlite", StringComparison.OrdinalIgnoreCase)) then
      false
    else
      let fileStem = Path.GetFileNameWithoutExtension path
      let prefix = $"{directoryName}-"

      if fileStem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
        let hashSegment = fileStem.Substring(prefix.Length)
        isHexHashSegment hashSegment
      else
        false

  let schemaPath =
    args.TryGetResult MigrateArgs.Schema
    |> Option.defaultValue (Path.Combine(currentDirectory, "schema.fsx"))

  let schemaCommit =
    let fromArg = args.TryGetResult MigrateArgs.Schema_Commit

    match fromArg with
    | Some commitValue when not (String.IsNullOrWhiteSpace commitValue) -> Some commitValue
    | _ ->
      let fromEnvironment = Environment.GetEnvironmentVariable "MIG_SCHEMA_COMMIT"

      if isNull fromEnvironment || String.IsNullOrWhiteSpace fromEnvironment then
        None
      else
        Some fromEnvironment

  let schemaHashResult =
    try
      let normalizedSchema = File.ReadAllText schemaPath |> normalizeLineEndings
      use sha256 = SHA256.Create()
      let schemaBytes = Encoding.UTF8.GetBytes normalizedSchema
      let hashBytes = sha256.ComputeHash schemaBytes
      Ok(Convert.ToHexString(hashBytes).ToLowerInvariant().Substring(0, 16))
    with ex ->
      Error ex.Message

  match schemaHashResult with
  | Error message ->
    eprintfn $"migrate failed: Could not resolve deterministic new database path from schema '{schemaPath}': {message}"
    1
  | Ok schemaHash ->
    let autoNewDbPath =
      Path.Combine(currentDirectory, $"{directoryName}-{schemaHash}.sqlite")

    let newDb, usedDeterministicDefault =
      match args.TryGetResult MigrateArgs.New with
      | Some explicitPath -> explicitPath, false
      | None -> autoNewDbPath, true

    let oldResult =
      match args.TryGetResult MigrateArgs.Old with
      | Some explicitOld -> Ok(Some explicitOld)
      | None ->
        let candidates =
          Directory.GetFiles(currentDirectory, $"{directoryName}-*.sqlite")
          |> Array.filter (fun path -> not (path.Equals(newDb, StringComparison.OrdinalIgnoreCase)))
          |> Array.filter isDirectoryHashNamedSqlite
          |> Array.sort

        if candidates.Length = 1 then
          Ok(Some candidates[0])
        elif candidates.Length > 1 then
          let candidateList = String.concat ", " candidates

          Error($"Could not infer old database automatically. Found multiple candidates: {candidateList}. Use --old.")
        elif File.Exists(newDb) then
          Ok None
        else
          Error(
            $"Could not infer old database automatically. Expected exactly one source matching '{directoryName}-<old-hash>.sqlite' in {currentDirectory}, excluding target '{newDb}'. Use --old."
          )

    match oldResult with
    | Error message ->
      eprintfn $"migrate failed: {message}"
      1
    | Ok None ->
      printfn "Migrate skipped."
      printfn $"Schema script: {schemaPath}"
      printfn $"Schema hash: {schemaHash}"
      printfn $"Database already present for current schema: {newDb}"
      0
    | Ok(Some old) ->
      match
        runMigrateWithSchemaCommit old schemaPath newDb schemaCommit
        |> fun t -> t.Result
      with
      | Ok result ->
        printfn "Migrate complete."
        printfn $"Old database: {old}"
        printfn $"Schema script: {schemaPath}"

        if usedDeterministicDefault then
          printfn $"Schema hash: {schemaHash}"

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

      match report.schemaIdentityHash with
      | Some schemaHash -> printfn $"Schema hash: {schemaHash}"
      | None -> ()

      match report.schemaIdentityCommit with
      | Some schemaCommit -> printfn $"Schema commit: {schemaCommit}"
      | None -> ()

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
