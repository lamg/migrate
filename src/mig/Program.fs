module Mig.Program

open Argu
open System
open System.IO
open System.Security.Cryptography
open System.Text
open MigLib.CodeGen.CodeGen
open MigLib.HotMigration

[<CliPrefix(CliPrefix.DoubleDash)>]
type MigrateArgs =
  | [<AltCommandLine("-d")>] Dir of path: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Dir _ -> "directory that contains schema.fsx and <dir>-<hash>.sqlite files (default: current directory)"

[<CliPrefix(CliPrefix.DoubleDash)>]
type InitArgs =
  | [<AltCommandLine("-d")>] Dir of path: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Dir _ -> "directory that contains schema.fsx and <dir>-<hash>.sqlite files (default: current directory)"

[<CliPrefix(CliPrefix.DoubleDash)>]
type PlanArgs =
  | [<AltCommandLine("-d")>] Dir of path: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Dir _ -> "directory that contains schema.fsx and <dir>-<hash>.sqlite files (default: current directory)"

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
type ResetArgs =
  | [<AltCommandLine("-d")>] Dir of path: string
  | Dry_Run

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Dir _ -> "directory that contains schema.fsx and <dir>-<hash>.sqlite files (default: current directory)"
      | Dry_Run -> "print reset impact without dropping old migration tables or deleting the new database"

[<CliPrefix(CliPrefix.DoubleDash)>]
type StatusArgs =
  | [<AltCommandLine("-d")>] Dir of path: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Dir _ -> "directory that contains schema.fsx and <dir>-<hash>.sqlite files (default: current directory)"

[<CliPrefix(CliPrefix.DoubleDash)>]
type CodegenArgs =
  | [<AltCommandLine("-d")>] Dir of path: string
  | [<AltCommandLine("-m")>] Module of name: string
  | [<AltCommandLine("-o")>] Output of path: string

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Dir _ -> "directory that contains schema.fsx and generated source files (default: current directory)"
      | Module _ -> "module name for generated F# code (default: Schema)"
      | Output _ -> "output file name in the schema directory (default: Schema.fs)"

type Command =
  | [<AltCommandLine("-v")>] Version
  | [<CliPrefix(CliPrefix.None)>] Init of ParseResults<InitArgs>
  | [<CliPrefix(CliPrefix.None)>] Codegen of ParseResults<CodegenArgs>
  | [<CliPrefix(CliPrefix.None)>] Migrate of ParseResults<MigrateArgs>
  | [<CliPrefix(CliPrefix.None)>] Plan of ParseResults<PlanArgs>
  | [<CliPrefix(CliPrefix.None)>] Drain of ParseResults<DrainArgs>
  | [<CliPrefix(CliPrefix.None)>] Cutover of ParseResults<CutoverArgs>
  | [<CliPrefix(CliPrefix.None); CustomCommandLine("cleanup-old")>] CleanupOld of ParseResults<CleanupOldArgs>
  | [<CliPrefix(CliPrefix.None)>] Reset of ParseResults<ResetArgs>
  | [<CliPrefix(CliPrefix.None)>] Status of ParseResults<StatusArgs>

  interface IArgParserTemplate with
    member this.Usage =
      match this with
      | Version -> "print mig version"
      | Init _ -> "initialize a schema-matched database from schema.fsx without requiring a source database"
      | Codegen _ -> "generate F# query helpers from schema.fsx"
      | Migrate _ -> "create new database and copy data from old"
      | Plan _ -> "show dry-run migration plan without mutating databases"
      | Drain _ -> "stop writes on old database and replay accumulated changes"
      | Cutover _ -> "mark new database as ready for serving"
      | CleanupOld _ -> "drop old-database migration tables after cutover"
      | Reset _ -> "reset failed migration artifacts on old/new databases"
      | Status _ -> "show current migration state"

let private getVersionText () =
  let version = typeof<Command>.Assembly.GetName().Version

  if isNull version then
    "unknown"
  else
    $"{version.Major}.{version.Minor}.{version.Build}"

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

let private ensureSchemaScriptExists (schemaPath: string) : Result<unit, string> =
  if File.Exists schemaPath then
    Ok()
  else
    Error($"Schema script was not found: {schemaPath}")

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
  match ensureSchemaScriptExists schemaPath with
  | Error message -> Error message
  | Ok() ->
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

  let sqliteFiles =
    Directory.GetFiles(currentDirectory, "*.sqlite")
    |> Array.filter (fun path -> not (shouldExclude path))
    |> Array.sort

  let candidates =
    sqliteFiles |> Array.filter (isDirectoryHashNamedSqlite directoryName)

  if candidates.Length = 1 then
    Ok candidates[0]
  elif candidates.Length > 1 then
    let candidateList = String.concat ", " candidates

    Error(
      $"Could not infer old database automatically. Found multiple candidates matching '{directoryName}-<old-hash>.sqlite': {candidateList}."
    )
  elif sqliteFiles.Length > 0 then
    let discoveredList = String.concat ", " sqliteFiles

    Error(
      $"Could not infer old database automatically. Found sqlite files that do not match '{directoryName}-<old-hash>.sqlite': {discoveredList}."
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

let private printMigrateRecoveryGuidance (oldDbPath: string) (newDbPath: string) =
  let oldSnapshot = getOldDatabaseStatus oldDbPath |> fun t -> t.Result
  let newDbPresent = File.Exists newDbPath

  let newSnapshot =
    if newDbPresent then
      Some(getNewDatabaseStatus newDbPath |> fun t -> t.Result)
    else
      None

  eprintfn "Recovery snapshot:"

  match oldSnapshot with
  | Ok report ->
    let oldMarkerStatus = report.oldMarkerStatus |> Option.defaultValue "no marker"

    let oldMigrationLogState =
      if report.migrationLogTablePresent then
        $"present ({report.migrationLogEntries} entries)"
      else
        "absent"

    eprintfn $"  Old marker status: {oldMarkerStatus}"
    eprintfn $"  Old _migration_log: {oldMigrationLogState}"
  | Error ex -> eprintfn $"  Old database snapshot unavailable: {ex.Message}"

  let newDbState = if newDbPresent then "present" else "absent"
  eprintfn $"  New database file: {newDbState} ({newDbPath})"

  match newSnapshot with
  | Some(Ok report) ->
    let newStatus = report.newMigrationStatus |> Option.defaultValue "no status marker"

    let idMappingState =
      if report.idMappingTablePresent then
        $"present ({report.idMappingEntries} entries)"
      else
        "absent"

    let migrationProgressState =
      if report.migrationProgressTablePresent then
        "present"
      else
        "absent"

    eprintfn $"  New migration status: {newStatus}"
    eprintfn $"  New _id_mapping: {idMappingState}"
    eprintfn $"  New _migration_progress: {migrationProgressState}"
  | Some(Error ex) -> eprintfn $"  New database snapshot unavailable: {ex.Message}"
  | None -> ()

  let hasRecordingMarker =
    match oldSnapshot with
    | Ok report ->
      report.oldMarkerStatus
      |> Option.exists (fun status -> status.Equals("recording", StringComparison.OrdinalIgnoreCase))
    | Error _ -> false

  let hasOldMigrationLog =
    match oldSnapshot with
    | Ok report -> report.migrationLogTablePresent
    | Error _ -> false

  let safeImmediateRerun =
    not newDbPresent && not hasRecordingMarker && not hasOldMigrationLog

  let guidance = ResizeArray<string>()
  guidance.Add "Keep the old database as source of truth; do not run drain/cutover after a failed migrate."

  if hasRecordingMarker then
    guidance.Add "Old marker is recording; new writes may be accumulating in _migration_log."

  if hasOldMigrationLog || hasRecordingMarker then
    guidance.Add "If restarting from scratch: stop writes first, then clear _migration_marker and _migration_log."

  if newDbPresent then
    guidance.Add $"Delete failed target database before rerun: {newDbPath}."
  else
    guidance.Add "No target database file was created."

  guidance.Add "Run `mig plan` to confirm inferred paths and preflight status."

  if safeImmediateRerun then
    guidance.Add "Current snapshot indicates immediate rerun is safe."
  else
    guidance.Add "Rerun `mig migrate` only after cleanup/reset conditions above are satisfied."

  eprintfn "Recovery guidance:"

  guidance |> Seq.iteri (fun index line -> eprintfn $"  {index + 1}. {line}")

let private resolveCodegenOutputPath (currentDirectory: string) (candidate: string option) : Result<string, string> =
  let outputFileName = candidate |> Option.defaultValue "Schema.fs"

  if String.IsNullOrWhiteSpace outputFileName then
    Error "Output file name for `codegen` cannot be empty."
  elif Path.IsPathRooted outputFileName then
    Error "Output file for `codegen` must be a file name, not an absolute path."
  elif not (outputFileName.Equals(Path.GetFileName outputFileName, StringComparison.Ordinal)) then
    Error "Output file for `codegen` must be in the same directory as schema.fsx (no subdirectories)."
  else
    Ok(Path.Combine(currentDirectory, outputFileName))

let codegen (args: ParseResults<CodegenArgs>) =
  match resolveCommandDirectory "codegen" (args.TryGetResult CodegenArgs.Dir) with
  | Error message ->
    eprintfn $"codegen failed: {message}"
    1
  | Ok currentDirectory ->
    let schemaPath = defaultSchemaPathForCurrentDirectory currentDirectory

    match ensureSchemaScriptExists schemaPath with
    | Error message ->
      eprintfn $"codegen failed: {message}"
      1
    | Ok() ->
      let moduleName =
        args.TryGetResult CodegenArgs.Module |> Option.defaultValue "Schema"

      if String.IsNullOrWhiteSpace moduleName then
        eprintfn "codegen failed: module name cannot be empty."
        1
      else
        match resolveCodegenOutputPath currentDirectory (args.TryGetResult CodegenArgs.Output) with
        | Error message ->
          eprintfn $"codegen failed: {message}"
          1
        | Ok outputPath ->
          match generateCodeFromScript moduleName schemaPath outputPath with
          | Error message ->
            eprintfn $"codegen failed: {message}"
            1
          | Ok stats ->
            printfn "Code generation complete."
            printfn $"Schema script: {schemaPath}"
            printfn $"Module: {moduleName}"
            printfn $"Output file: {outputPath}"
            printfn $"Normalized tables (DU): {stats.NormalizedTables}"
            printfn $"Regular tables (records): {stats.RegularTables}"
            printfn $"Views: {stats.Views}"
            printfn "Generated files:"
            stats.GeneratedFiles |> List.iter (fun file -> printfn $"  {file}")
            0

let migrate (args: ParseResults<MigrateArgs>) =
  match resolveCommandDirectory "migrate" (args.TryGetResult MigrateArgs.Dir) with
  | Error message ->
    eprintfn $"migrate failed: {message}"
    1
  | Ok currentDirectory ->
    let directoryName = DirectoryInfo(currentDirectory).Name

    let schemaPath = defaultSchemaPathForCurrentDirectory currentDirectory

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
          printMigrateRecoveryGuidance old newDb
          1

let init (args: ParseResults<InitArgs>) =
  match resolveCommandDirectory "init" (args.TryGetResult InitArgs.Dir) with
  | Error message ->
    eprintfn $"init failed: {message}"
    1
  | Ok currentDirectory ->
    let directoryName = DirectoryInfo(currentDirectory).Name
    let schemaPath = defaultSchemaPathForCurrentDirectory currentDirectory

    match resolveDeterministicNewDbPath currentDirectory directoryName schemaPath with
    | Error message ->
      eprintfn $"init failed: Could not resolve deterministic new database path from schema '{schemaPath}': {message}"

      1
    | Ok resolvedNew ->
      let newDb = resolvedNew.path

      if File.Exists newDb then
        printfn "Init skipped."
        printfn $"Schema script: {schemaPath}"
        printfn $"Schema hash: {resolvedNew.schemaHash}"
        printfn $"Database already present for current schema: {newDb}"
        0
      else
        match runInit schemaPath newDb |> fun t -> t.Result with
        | Ok result ->
          printfn "Init complete."
          printfn $"Schema script: {schemaPath}"
          printfn $"Schema hash: {resolvedNew.schemaHash}"
          printfn $"Database: {result.newDbPath}"
          printfn $"Seeded rows: {result.seededRows}"
          0
        | Error ex ->
          eprintfn $"init failed: {ex.Message}"
          1

let plan (args: ParseResults<PlanArgs>) =
  match resolveCommandDirectory "plan" (args.TryGetResult PlanArgs.Dir) with
  | Error message ->
    eprintfn $"plan failed: {message}"
    1
  | Ok currentDirectory ->
    let directoryName = DirectoryInfo(currentDirectory).Name
    let schemaPath = defaultSchemaPathForCurrentDirectory currentDirectory

    match resolveDeterministicNewDbPath currentDirectory directoryName schemaPath with
    | Error message ->
      eprintfn $"plan failed: Could not resolve deterministic new database path from schema '{schemaPath}': {message}"

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
        eprintfn $"plan failed: {message}"
        1
      | Ok None ->
        printfn "Plan skipped."
        printfn $"Schema script: {schemaPath}"
        printfn $"Schema hash: {resolvedNew.schemaHash}"
        printfn $"Database already present for current schema: {newDb}"
        0
      | Ok(Some old) ->
        match getMigratePlan old schemaPath newDb |> fun t -> t.Result with
        | Error ex ->
          eprintfn $"plan failed: {ex.Message}"
          1
        | Ok report ->
          let canRunMigrate = if report.canRunMigrate then "yes" else "no"

          let printLines header lines =
            printfn $"{header}"

            match lines with
            | [] -> printfn "  - none"
            | values -> values |> List.iter (fun line -> printfn $"  - {line}")

          printfn "Migration plan."
          printfn $"Old database: {old}"
          printfn $"Schema script: {schemaPath}"
          printfn $"Schema hash: {report.schemaHash}"

          match report.schemaCommit with
          | Some schemaCommit -> printfn $"Schema commit: {schemaCommit}"
          | None -> ()

          printfn $"New database: {newDb}"
          printfn $"Can run migrate now: {canRunMigrate}"

          printLines "Planned copy targets (execution order):" report.plannedCopyTargets
          printLines "Supported differences:" report.supportedDifferences
          printLines "Unsupported differences:" report.unsupportedDifferences
          printLines "Replay prerequisites:" report.replayPrerequisites

          if report.canRunMigrate then 0 else 1

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
      let oldResult =
        inferOldDbFromCurrentDirectory currentDirectory directoryName (Some newDb)

      let cutoverResult =
        match oldResult with
        | Ok oldDb -> runCutoverWithOldSafety oldDb newDb |> fun t -> t.Result
        | Error _ -> runCutover newDb |> fun t -> t.Result

      match cutoverResult with
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

let reset (args: ParseResults<ResetArgs>) =
  let isDryRun = args.Contains ResetArgs.Dry_Run

  match resolveCommandDirectory "reset" (args.TryGetResult ResetArgs.Dir) with
  | Error message ->
    eprintfn $"reset failed: {message}"
    1
  | Ok currentDirectory ->
    let directoryName = DirectoryInfo(currentDirectory).Name

    let newResult =
      resolveDefaultNewDbFromCurrentSchema "reset" currentDirectory directoryName
      |> Result.map _.path

    match newResult with
    | Error message ->
      eprintfn $"reset failed: {message}"
      1
    | Ok newDb ->
      let oldResult =
        inferOldDbFromCurrentDirectory currentDirectory directoryName (Some newDb)
        |> Result.mapError (fun message ->
          $"{message} Excluding target '{newDb}'. Use `-d` to select a different directory.")

      match oldResult with
      | Error message ->
        eprintfn $"reset failed: {message}"
        1
      | Ok old ->
        if isDryRun then
          match getResetMigrationPlan old newDb |> fun t -> t.Result with
          | Error ex ->
            eprintfn $"reset failed: {ex.Message}"
            1
          | Ok plan ->
            let previousOldMarkerStatus =
              plan.previousOldMarkerStatus |> Option.defaultValue "no marker"

            let wouldDropOldMarker = if plan.willDropOldMarker then "yes" else "no"
            let wouldDropOldLog = if plan.willDropOldLog then "yes" else "no"

            let previousNewStatus =
              plan.previousNewStatus |> Option.defaultValue "no status marker"

            let newDatabaseExisted = if plan.newDatabaseExisted then "yes" else "no"
            let wouldDeleteNewDatabase = if plan.willDeleteNewDatabase then "yes" else "no"
            let resetCanApply = if plan.canApplyReset then "yes" else "no"

            printfn "Migration reset dry run."
            printfn $"Old database: {old}"
            printfn $"Previous old marker status: {previousOldMarkerStatus}"
            printfn $"Would drop _migration_marker: {wouldDropOldMarker}"
            printfn $"Would drop _migration_log: {wouldDropOldLog}"
            printfn $"New database: {newDb}"
            printfn $"New database existed: {newDatabaseExisted}"

            if plan.newDatabaseExisted then
              printfn $"Previous new migration status: {previousNewStatus}"

            printfn $"Would delete new database: {wouldDeleteNewDatabase}"
            printfn $"Reset can be applied: {resetCanApply}"

            if not plan.canApplyReset then
              let blockedReason = plan.blockedReason |> Option.defaultValue "Reset is blocked."
              printfn $"Blocked reason: {blockedReason}"

            if plan.canApplyReset then 0 else 1
        else
          match runResetMigration old newDb |> fun t -> t.Result with
          | Error ex ->
            eprintfn $"reset failed: {ex.Message}"
            1
          | Ok result ->
            let previousOldMarkerStatus =
              result.previousOldMarkerStatus |> Option.defaultValue "no marker"

            let droppedOldMarker = if result.oldMarkerDropped then "yes" else "no"
            let droppedOldLog = if result.oldLogDropped then "yes" else "no"

            let previousNewStatus =
              result.previousNewStatus |> Option.defaultValue "no status marker"

            let newDatabaseExisted = if result.newDatabaseExisted then "yes" else "no"
            let newDatabaseDeleted = if result.newDatabaseDeleted then "yes" else "no"

            printfn "Migration reset complete."
            printfn $"Old database: {old}"
            printfn $"Previous old marker status: {previousOldMarkerStatus}"
            printfn $"Dropped _migration_marker: {droppedOldMarker}"
            printfn $"Dropped _migration_log: {droppedOldLog}"
            printfn $"New database: {newDb}"
            printfn $"New database existed: {newDatabaseExisted}"

            if result.newDatabaseExisted then
              printfn $"Previous new migration status: {previousNewStatus}"

            printfn $"Deleted new database: {newDatabaseDeleted}"
            0

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

    if results.Contains Version then
      printfn $"{getVersionText ()}"
      0
    else
      match results.GetSubCommand() with
      | Init args -> init args
      | Codegen args -> codegen args
      | Migrate args -> migrate args
      | Plan args -> plan args
      | Drain args -> drain args
      | Cutover args -> cutover args
      | CleanupOld args -> cleanupOld args
      | Reset args -> reset args
      | Status args -> status args
      | Version ->
        printfn $"{getVersionText ()}"
        0
  with :? ArguParseException as ex ->
    printfn $"%s{ex.Message}"
    1
