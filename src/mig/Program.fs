module Mig.Program

open Argu
open System
open System.IO
open System.Security.Cryptography
open System.Text
open MigLib.HotMigration

[<CliPrefix(CliPrefix.DoubleDash)>]
type MigrateArgs =
  | [<AltCommandLine("-d")>] Dir of path: string
  | Schema of path: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Dir _ -> "directory that contains schema.fsx and <dir>-<hash>.sqlite files (default: current directory)"
      | Schema _ -> "path to the .fsx schema file (default: <dir>/schema.fsx)"

[<CliPrefix(CliPrefix.DoubleDash)>]
type DrainArgs =
  | [<AltCommandLine("-d")>] Dir of path: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Dir _ -> "directory that contains schema.fsx and <dir>-<hash>.sqlite files (default: current directory)"

[<CliPrefix(CliPrefix.DoubleDash)>]
type CutoverArgs =
  | [<AltCommandLine("-d")>] Dir of path: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Dir _ -> "directory that contains schema.fsx and <dir>-<hash>.sqlite files (default: current directory)"

[<CliPrefix(CliPrefix.DoubleDash)>]
type CleanupOldArgs =
  | [<AltCommandLine("-d")>] Dir of path: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Dir _ -> "directory that contains schema.fsx and <dir>-<hash>.sqlite files (default: current directory)"

[<CliPrefix(CliPrefix.DoubleDash)>]
type StatusArgs =
  | [<AltCommandLine("-d")>] Dir of path: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Dir _ -> "directory that contains schema.fsx and <dir>-<hash>.sqlite files (default: current directory)"

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

type private DeterministicNewDbPath =
  { schemaPath: string
    schemaHash: string
    path: string }

let private normalizeLineEndings (text: string) =
  text.Replace("\r\n", "\n").Replace("\r", "\n")

let private computeShortSchemaHash (schemaPath: string) : Result<string, string> =
  try
    let normalizedSchema = File.ReadAllText schemaPath |> normalizeLineEndings
    use sha256 = SHA256.Create()
    let schemaBytes = Encoding.UTF8.GetBytes normalizedSchema
    let hashBytes = sha256.ComputeHash schemaBytes
    Ok(Convert.ToHexString(hashBytes).ToLowerInvariant().Substring(0, 16))
  with ex ->
    Error ex.Message

let private defaultSchemaPathForCurrentDirectory (currentDirectory: string) =
  Path.Combine(currentDirectory, "schema.fsx")

let private resolveCommandDirectory (commandName: string) (candidate: string option) : Result<string, string> =
  let targetDirectory =
    candidate
    |> Option.defaultValue (Directory.GetCurrentDirectory())
    |> Path.GetFullPath

  if Directory.Exists targetDirectory then
    Ok targetDirectory
  else
    Error($"Directory does not exist for `{commandName}`: {targetDirectory}")

let private resolveDeterministicNewDbPath
  (currentDirectory: string)
  (directoryName: string)
  (schemaPath: string)
  : Result<DeterministicNewDbPath, string> =
  match computeShortSchemaHash schemaPath with
  | Error message -> Error message
  | Ok schemaHash ->
    Ok
      { schemaPath = schemaPath
        schemaHash = schemaHash
        path = Path.Combine(currentDirectory, $"{directoryName}-{schemaHash}.sqlite") }

let private isHexHashSegment (value: string) =
  value.Length = 16 && value |> Seq.forall Uri.IsHexDigit

let private isDirectoryHashNamedSqlite (directoryName: string) (path: string) =
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

let private inferOldDbFromCurrentDirectory
  (currentDirectory: string)
  (directoryName: string)
  (excludePath: string option)
  : Result<string, string> =
  let shouldExclude (path: string) =
    match excludePath with
    | Some excludedPath -> path.Equals(excludedPath, StringComparison.OrdinalIgnoreCase)
    | None -> false

  let candidates =
    Directory.GetFiles(currentDirectory, $"{directoryName}-*.sqlite")
    |> Array.filter (isDirectoryHashNamedSqlite directoryName)
    |> Array.filter (fun path -> not (shouldExclude path))
    |> Array.sort

  if candidates.Length = 1 then
    Ok candidates[0]
  elif candidates.Length > 1 then
    let candidateList = String.concat ", " candidates

    Error(
      $"Could not infer old database automatically. Found multiple candidates matching '{directoryName}-<old-hash>.sqlite': {candidateList}."
    )
  else
    Error(
      $"Could not infer old database automatically. Expected exactly one source matching '{directoryName}-<old-hash>.sqlite' in {currentDirectory}."
    )

let private resolveDefaultNewDbFromCurrentSchema
  (commandName: string)
  (currentDirectory: string)
  (directoryName: string)
  : Result<DeterministicNewDbPath, string> =
  let schemaPath = defaultSchemaPathForCurrentDirectory currentDirectory

  match resolveDeterministicNewDbPath currentDirectory directoryName schemaPath with
  | Ok resolved -> Ok resolved
  | Error message ->
    Error($"Could not infer new database automatically from schema '{schemaPath}' for `{commandName}`: {message}.")

let migrate (args: ParseResults<MigrateArgs>) =
  match resolveCommandDirectory "migrate" (args.TryGetResult MigrateArgs.Dir) with
  | Error message ->
    eprintfn $"migrate failed: {message}"
    1
  | Ok currentDirectory ->
    let directoryName = DirectoryInfo(currentDirectory).Name

    let schemaPath =
      args.TryGetResult MigrateArgs.Schema
      |> Option.map (fun path ->
        if Path.IsPathRooted path then
          path
        else
          Path.Combine(currentDirectory, path))
      |> Option.defaultValue (defaultSchemaPathForCurrentDirectory currentDirectory)

    match resolveDeterministicNewDbPath currentDirectory directoryName schemaPath with
    | Error message ->
      eprintfn
        $"migrate failed: Could not resolve deterministic new database path from schema '{schemaPath}': {message}"

      1
    | Ok resolvedNew ->
      let newDb = resolvedNew.path

      let oldResult =
        match inferOldDbFromCurrentDirectory currentDirectory directoryName (Some newDb) with
        | Ok inferredOld -> Ok(Some inferredOld)
        | Error _ when File.Exists newDb -> Ok None
        | Error message -> Error($"{message} Excluding target '{newDb}'. Use `-d` to select a different directory.")

      match oldResult with
      | Error message ->
        eprintfn $"migrate failed: {message}"
        1
      | Ok None ->
        printfn "Migrate skipped."
        printfn $"Schema script: {schemaPath}"
        printfn $"Schema hash: {resolvedNew.schemaHash}"
        printfn $"Database already present for current schema: {newDb}"
        0
      | Ok(Some old) ->
        match runMigrate old schemaPath newDb |> fun t -> t.Result with
        | Ok result ->
          printfn "Migrate complete."
          printfn $"Old database: {old}"
          printfn $"Schema script: {schemaPath}"
          printfn $"Schema hash: {resolvedNew.schemaHash}"
          printfn $"New database: {result.newDbPath}"
          printfn $"Copied tables: {result.copiedTables}"
          printfn $"Copied rows: {result.copiedRows}"
          0
        | Error ex ->
          eprintfn $"migrate failed: {ex.Message}"
          1

let drain (args: ParseResults<DrainArgs>) =
  match resolveCommandDirectory "drain" (args.TryGetResult DrainArgs.Dir) with
  | Error message ->
    eprintfn $"drain failed: {message}"
    1
  | Ok currentDirectory ->
    let directoryName = DirectoryInfo(currentDirectory).Name

    let newResult =
      resolveDefaultNewDbFromCurrentSchema "drain" currentDirectory directoryName
      |> Result.map _.path

    match newResult with
    | Error message ->
      eprintfn $"drain failed: {message}"
      1
    | Ok newDb ->
      let oldResult =
        inferOldDbFromCurrentDirectory currentDirectory directoryName (Some newDb)
        |> Result.mapError (fun message ->
          $"{message} Excluding target '{newDb}'. Use `-d` to select a different directory.")

      match oldResult with
      | Error message ->
        eprintfn $"drain failed: {message}"
        1
      | Ok old ->
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
  match resolveCommandDirectory "cutover" (args.TryGetResult CutoverArgs.Dir) with
  | Error message ->
    eprintfn $"cutover failed: {message}"
    1
  | Ok currentDirectory ->
    let directoryName = DirectoryInfo(currentDirectory).Name

    let newResult =
      resolveDefaultNewDbFromCurrentSchema "cutover" currentDirectory directoryName
      |> Result.map _.path

    match newResult with
    | Error message ->
      eprintfn $"cutover failed: {message}"
      1
    | Ok newDb ->
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
  match resolveCommandDirectory "cleanup-old" (args.TryGetResult CleanupOldArgs.Dir) with
  | Error message ->
    eprintfn $"cleanup-old failed: {message}"
    1
  | Ok currentDirectory ->
    let directoryName = DirectoryInfo(currentDirectory).Name

    let inferredNew =
      match resolveDefaultNewDbFromCurrentSchema "cleanup-old" currentDirectory directoryName with
      | Ok inferredTarget -> Some inferredTarget.path
      | Error _ -> None

    let oldResult =
      inferOldDbFromCurrentDirectory currentDirectory directoryName inferredNew
      |> Result.mapError (fun message ->
        match inferredNew with
        | Some inferredTarget ->
          $"{message} Excluding inferred target '{inferredTarget}'. Use `-d` to select a different directory."
        | None -> $"{message} Use `-d` to select a different directory.")

    match oldResult with
    | Error message ->
      eprintfn $"cleanup-old failed: {message}"
      1
    | Ok old ->
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
  match resolveCommandDirectory "status" (args.TryGetResult StatusArgs.Dir) with
  | Error message ->
    eprintfn $"status failed: {message}"
    1
  | Ok currentDirectory ->
    let directoryName = DirectoryInfo(currentDirectory).Name

    let inferredNew =
      match resolveDefaultNewDbFromCurrentSchema "status" currentDirectory directoryName with
      | Ok resolvedNew when File.Exists resolvedNew.path -> Some resolvedNew.path
      | _ -> None

    let inferredOld =
      inferOldDbFromCurrentDirectory currentDirectory directoryName inferredNew

    match inferredOld, inferredNew with
    | Ok oldPath, _ ->
      match getStatus oldPath inferredNew |> fun t -> t.Result with
      | Error ex ->
        eprintfn $"status failed: {ex.Message}"
        1
      | Ok report ->
        let markerStatus = report.oldMarkerStatus |> Option.defaultValue "no marker"
        printfn $"Old database: {oldPath}"
        printfn $"Marker status: {markerStatus}"
        printfn $"Migration log entries: {report.migrationLogEntries}"

        match inferredNew with
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
    | Error _, Some newPath ->
      match getNewDatabaseStatus newPath |> fun t -> t.Result with
      | Error ex ->
        eprintfn $"status failed: {ex.Message}"
        1
      | Ok report ->
        let migrationStatus =
          report.newMigrationStatus |> Option.defaultValue "no status marker"

        printfn "Old database: n/a (not inferred)"
        printfn "Marker status: n/a"
        printfn "Migration log entries: n/a"
        printfn $"New database: {newPath}"
        printfn $"Migration status: {migrationStatus}"

        match report.schemaIdentityHash with
        | Some schemaHash -> printfn $"Schema hash: {schemaHash}"
        | None -> ()

        match report.schemaIdentityCommit with
        | Some schemaCommit -> printfn $"Schema commit: {schemaCommit}"
        | None -> ()

        printfn "Pending replay entries: n/a (old database unavailable)"

        match report.idMappingTablePresent with
        | false -> printfn "_id_mapping: removed"
        | true -> printfn $"_id_mapping entries: {report.idMappingEntries}"

        match report.migrationProgressTablePresent with
        | false -> printfn "_migration_progress: removed"
        | true -> printfn "_migration_progress: present"

        0
    | Error message, None ->
      eprintfn $"status failed: {message} Use `-d` to select a different directory."
      1

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
