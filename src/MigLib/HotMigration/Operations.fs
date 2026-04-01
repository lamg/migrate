namespace Mig

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open MigLib.Db
open MigLib.Util
open DeclarativeMigrations.DataCopy
open DeclarativeMigrations.DrainReplay
open DeclarativeMigrations.SchemaDiff
open DeclarativeMigrations.Types
open Mig.HotMigrationPrimitives
open Mig.HotMigrationMetadata
open Mig.HotMigrationSchemaBootstrap
open Mig.HotMigrationSchemaIntrospection
open Mig.HotMigrationCopy
open Mig.HotMigrationPlanning

module HotMigration =
  type MigrationStatusReport =
    { oldMarkerStatus: string option
      migrationLogEntries: int64
      pendingReplayEntries: int64 option
      idMappingEntries: int64 option
      newMigrationStatus: string option
      idMappingTablePresent: bool option
      migrationProgressTablePresent: bool option
      schemaIdentityHash: string option
      schemaIdentityCommit: string option }

  type OldDatabaseStatusReport =
    { oldMarkerStatus: string option
      migrationLogEntries: int64
      migrationLogTablePresent: bool }

  type NewDatabaseStatusReport =
    { newMigrationStatus: string option
      idMappingEntries: int64
      idMappingTablePresent: bool
      migrationProgressTablePresent: bool
      schemaIdentityHash: string option
      schemaIdentityCommit: string option }

  type CutoverResult =
    { previousStatus: string
      idMappingDropped: bool
      migrationProgressDropped: bool }

  type MigrateResult =
    { newDbPath: string
      copiedTables: int
      copiedRows: int64 }

  type InitResult =
    { newDbPath: string; seededRows: int64 }

  type SchemaIdentity =
    { schemaHash: string
      schemaCommit: string option }

  type MigratePlanReport =
    { schemaHash: string
      schemaCommit: string option
      supportedDifferences: string list
      unsupportedDifferences: string list
      plannedCopyTargets: string list
      replayPrerequisites: string list
      canRunMigrate: bool }

  type DrainResult =
    { replayedEntries: int
      remainingEntries: int64 }

  type ArchiveOldResult =
    { previousMarkerStatus: string option
      archivePath: string
      replacedExistingArchive: bool }

  type ResetMigrationResult =
    { previousOldMarkerStatus: string option
      oldMarkerDropped: bool
      oldLogDropped: bool
      previousNewStatus: string option
      newDatabaseExisted: bool
      newDatabaseDeleted: bool }

  type ResetMigrationPlan =
    { previousOldMarkerStatus: string option
      oldMarkerPresent: bool
      oldLogPresent: bool
      previousNewStatus: string option
      newDatabaseExisted: bool
      willDropOldMarker: bool
      willDropOldLog: bool
      willDeleteNewDatabase: bool
      canApplyReset: bool
      blockedReason: string option }

  let private migrationTables =
    set
      [ "_migration_marker"
        "_migration_log"
        "_migration_status"
        "_migration_progress"
        "_id_mapping"
        "_schema_identity" ]

  let private readyResetBlockedMessage (newDbPath: string) =
    $"Refusing reset because new database status is ready at '{newDbPath}'. This command is only for failed or aborted migrations."

  let internal analyzeNonTableConsistency =
    HotMigrationPlanning.analyzeNonTableConsistency

  let getMigratePlanWithSchema
    (oldDbPath: string)
    (schemaIdentity: SchemaIdentity)
    (targetSchema: SqlFile)
    (newDbPath: string)
    : Task<Result<MigratePlanReport, SqliteException>> =
    taskResult {
      try
        if not (File.Exists oldDbPath) then
          return! Error(toSqliteError $"Old database was not found: {oldDbPath}")
        else
          use oldConnection = openSqliteConnection oldDbPath

          let! sourceSchema = loadSchemaFromDatabase oldConnection migrationTables
          let nonTableConsistency = analyzeNonTableConsistency targetSchema
          let schemaPlanResult = buildSchemaCopyPlan sourceSchema targetSchema

          let tableDifferences =
            match schemaPlanResult with
            | Ok schemaPlan -> describeSupportedDifferences schemaPlan
            | Error _ -> []

          let supportedDifferences = tableDifferences @ nonTableConsistency.supportedLines

          let plannerResult = buildBulkCopyPlan sourceSchema targetSchema

          let plannedCopyTargets, plannerUnsupported =
            match plannerResult with
            | Ok plan -> plan.steps |> List.map _.mapping.targetTable, []
            | Error message -> [], [ message ]

          let schemaPlanUnsupported =
            match schemaPlanResult with
            | Ok _ -> []
            | Error message -> [ message ]

          let unsupportedDifferences =
            nonTableConsistency.unsupportedLines
            @ schemaPlanUnsupported
            @ plannerUnsupported

          let! oldMarkerStatus = readMarkerStatus oldConnection None "_migration_marker"
          let! oldMigrationLogTablePresent = tableExists oldConnection None "_migration_log"
          let! oldMigrationLogEntries = countRowsIfTableExists oldConnection None "_migration_log"

          let markerPrerequisite =
            match oldMarkerStatus with
            | Some status -> $"_migration_marker is present with status '{status}' (migrate will set it to recording)."
            | None -> "_migration_marker is absent (migrate will create it in recording mode)."

          let logPrerequisite =
            if oldMigrationLogTablePresent then
              $"_migration_log is present with {oldMigrationLogEntries} entries (migrate will recreate it)."
            else
              "_migration_log is absent (migrate will create it)."

          let newDatabaseAlreadyExists = File.Exists newDbPath

          let targetPrerequisite =
            if newDatabaseAlreadyExists then
              $"target database already exists: {newDbPath}"
            else
              $"target database path is available: {newDbPath}"

          let driftPrerequisite =
            if unsupportedDifferences.IsEmpty then
              "schema preflight checks pass."
            else
              $"schema preflight has {unsupportedDifferences.Length} blocking issue(s)."

          let replayPrerequisites =
            [ markerPrerequisite; logPrerequisite; targetPrerequisite; driftPrerequisite ]

          let canRunMigrate = not newDatabaseAlreadyExists && unsupportedDifferences.IsEmpty

          return
            { schemaHash = schemaIdentity.schemaHash
              schemaCommit = schemaIdentity.schemaCommit
              supportedDifferences = supportedDifferences
              unsupportedDifferences = unsupportedDifferences
              plannedCopyTargets = plannedCopyTargets
              replayPrerequisites = replayPrerequisites
              canRunMigrate = canRunMigrate }
      with
      | :? SqliteException as ex -> return! Error ex
      | ex -> return! Error(toSqliteError ex.Message)
    }

  let getStatus (oldDbPath: string) (newDbPath: string option) : Task<Result<MigrationStatusReport, SqliteException>> =
    task {
      try
        use oldConnection = openSqliteConnection oldDbPath

        let! oldMarkerStatus = readMarkerStatus oldConnection None "_migration_marker"
        let! migrationLogEntries = countRowsIfTableExists oldConnection None "_migration_log"

        match newDbPath with
        | None ->
          return
            Ok
              { oldMarkerStatus = oldMarkerStatus
                migrationLogEntries = migrationLogEntries
                pendingReplayEntries = None
                idMappingEntries = None
                newMigrationStatus = None
                idMappingTablePresent = None
                migrationProgressTablePresent = None
                schemaIdentityHash = None
                schemaIdentityCommit = None }
        | Some newPath ->
          use newConnection = openSqliteConnection newPath

          let! idMappingTablePresent = tableExists newConnection None "_id_mapping"

          let! idMappingEntries =
            if idMappingTablePresent then
              countRows newConnection None "_id_mapping"
            else
              Task.FromResult 0L

          let! newMigrationStatus = readMarkerStatus newConnection None "_migration_status"
          let! migrationProgressTablePresent = tableExists newConnection None "_migration_progress"
          let! progress = readMigrationProgress newConnection None
          let! schemaIdentity = readSchemaIdentity newConnection None

          let isReady =
            newMigrationStatus
            |> Option.exists (fun status -> status.Equals("ready", StringComparison.OrdinalIgnoreCase))

          let! pendingReplayEntries =
            if isReady then
              Task.FromResult 0L
            else
              match progress with
              | Some row -> countPendingLogEntries oldConnection None row.lastReplayedLogId
              | None -> Task.FromResult migrationLogEntries

          return
            Ok
              { oldMarkerStatus = oldMarkerStatus
                migrationLogEntries = migrationLogEntries
                pendingReplayEntries = Some pendingReplayEntries
                idMappingEntries = Some idMappingEntries
                newMigrationStatus = newMigrationStatus
                idMappingTablePresent = Some idMappingTablePresent
                migrationProgressTablePresent = Some migrationProgressTablePresent
                schemaIdentityHash = schemaIdentity |> Option.map _.schemaHash
                schemaIdentityCommit = schemaIdentity |> Option.bind _.schemaCommit }
      with
      | :? SqliteException as ex -> return Error ex
      | ex -> return Error(toSqliteError ex.Message)
    }

  let getNewDatabaseStatus (newDbPath: string) : Task<Result<NewDatabaseStatusReport, SqliteException>> =
    task {
      try
        use newConnection = openSqliteConnection newDbPath

        let! idMappingTablePresent = tableExists newConnection None "_id_mapping"

        let! idMappingEntries =
          if idMappingTablePresent then
            countRows newConnection None "_id_mapping"
          else
            Task.FromResult 0L

        let! newMigrationStatus = readMarkerStatus newConnection None "_migration_status"
        let! migrationProgressTablePresent = tableExists newConnection None "_migration_progress"
        let! schemaIdentity = readSchemaIdentity newConnection None

        return
          Ok
            { newMigrationStatus = newMigrationStatus
              idMappingEntries = idMappingEntries
              idMappingTablePresent = idMappingTablePresent
              migrationProgressTablePresent = migrationProgressTablePresent
              schemaIdentityHash = schemaIdentity |> Option.map _.schemaHash
              schemaIdentityCommit = schemaIdentity |> Option.bind _.schemaCommit }
      with
      | :? SqliteException as ex -> return Error ex
      | ex -> return Error(toSqliteError ex.Message)
    }

  let getOldDatabaseStatus (oldDbPath: string) : Task<Result<OldDatabaseStatusReport, SqliteException>> =
    task {
      try
        use oldConnection = openSqliteConnection oldDbPath

        let! oldMarkerStatus = readMarkerStatus oldConnection None "_migration_marker"
        let! migrationLogTablePresent = tableExists oldConnection None "_migration_log"

        let! migrationLogEntries =
          if migrationLogTablePresent then
            countRows oldConnection None "_migration_log"
          else
            Task.FromResult 0L

        return
          Ok
            { oldMarkerStatus = oldMarkerStatus
              migrationLogEntries = migrationLogEntries
              migrationLogTablePresent = migrationLogTablePresent }
      with
      | :? SqliteException as ex -> return Error ex
      | ex -> return Error(toSqliteError ex.Message)
    }

  let private runMigrateInternal
    (oldDbPath: string)
    (schemaIdentity: SchemaIdentity)
    (targetSchema: SqlFile)
    (newDbPath: string)
    (prepareOldDatabase: SqliteConnection -> Task<Result<unit, SqliteException>>)
    (initializeTargetDatabase:
      SqliteConnection -> SqlFile -> string -> string option -> Task<Result<unit, SqliteException>>)
    (persistIdMappings: bool)
    : Task<Result<MigrateResult, SqliteException>> =
    taskResult {
      try
        if not (File.Exists oldDbPath) then
          return! Error(toSqliteError $"Old database was not found: {oldDbPath}")
        elif File.Exists newDbPath then
          return! Error(toSqliteError $"New database already exists: {newDbPath}")
        else
          use oldConnection = openSqliteConnection oldDbPath
          let! sourceSchema = loadSchemaFromDatabase oldConnection migrationTables
          let! copyPlan = buildCopyPlan sourceSchema targetSchema
          do! prepareOldDatabase oldConnection

          let newDirectory = Path.GetDirectoryName newDbPath

          if not (String.IsNullOrWhiteSpace newDirectory) then
            Directory.CreateDirectory newDirectory |> ignore

          use newConnection = openSqliteConnection newDbPath

          do! initializeTargetDatabase newConnection targetSchema schemaIdentity.schemaHash schemaIdentity.schemaCommit

          let! _, copiedRows = executeBulkCopy oldConnection newConnection copyPlan persistIdMappings

          return
            { newDbPath = newDbPath
              copiedTables = copyPlan.steps.Length
              copiedRows = copiedRows }
      with
      | :? SqliteException as ex -> return! Error ex
      | ex -> return! Error(toSqliteError ex.Message)
    }

  let runMigrateWithSchema
    (oldDbPath: string)
    (schemaIdentity: SchemaIdentity)
    (targetSchema: SqlFile)
    (newDbPath: string)
    : Task<Result<MigrateResult, SqliteException>> =
    runMigrateInternal
      oldDbPath
      schemaIdentity
      targetSchema
      newDbPath
      ensureOldRecordingTables
      initializeNewDatabase
      true

  let runOfflineMigrateWithSchema
    (oldDbPath: string)
    (schemaIdentity: SchemaIdentity)
    (targetSchema: SqlFile)
    (newDbPath: string)
    : Task<Result<MigrateResult, SqliteException>> =
    runMigrateInternal
      oldDbPath
      schemaIdentity
      targetSchema
      newDbPath
      (fun _ -> Task.FromResult(Ok()))
      initializeOfflineDatabase
      false

  let runInitWithSchema (targetSchema: SqlFile) (newDbPath: string) : Task<Result<InitResult, SqliteException>> =
    task {
      try
        if File.Exists newDbPath then
          return Error(toSqliteError $"Database already exists: {newDbPath}")
        else
          let newDirectory = Path.GetDirectoryName newDbPath

          if not (String.IsNullOrWhiteSpace newDirectory) then
            Directory.CreateDirectory newDirectory |> ignore

          use newConnection = openSqliteConnection newDbPath

          let! initResult = initializeDatabaseFromSchemaOnly newConnection targetSchema

          match initResult with
          | Error ex -> return Error ex
          | Ok seededRows ->
            return
              Ok
                { newDbPath = newDbPath
                  seededRows = seededRows }
      with
      | :? SqliteException as ex -> return Error ex
      | ex -> return Error(toSqliteError ex.Message)
    }

  let inferPreviousDatabasePath
    (sqliteDirectory: string)
    (dbFileName: string)
    : Result<string option, SqliteException> =
    result {
      if String.IsNullOrWhiteSpace sqliteDirectory then
        return! Error(SqliteException("Configured database path is empty.", 0))
      elif String.IsNullOrWhiteSpace dbFileName then
        return! Error(SqliteException("Configured database file name is empty.", 0))
      elif Path.IsPathRooted dbFileName || dbFileName <> Path.GetFileName dbFileName then
        return!
          Error(SqliteException($"Configured database file name must be a file name only, not a path: {dbFileName}", 0))
      else
        let resolvedDirectory = Path.GetFullPath sqliteDirectory

        if not (Directory.Exists resolvedDirectory) then
          return! Error(SqliteException($"Database directory '{resolvedDirectory}' does not exist", 0))
        else
          let currentDbPath = Path.Combine(resolvedDirectory, dbFileName)

          let candidatePaths =
            Directory.GetFiles(resolvedDirectory, "*.sqlite")
            |> Array.filter (fun path -> not (String.Equals(path, currentDbPath, StringComparison.OrdinalIgnoreCase)))
            |> Array.sort
            |> Array.toList

          match candidatePaths with
          | [] -> return None
          | [ candidatePath ] -> return Some candidatePath
          | many ->
            let rendered = String.concat ", " many

            return!
              Error(
                SqliteException(
                  $"Could not infer previous database automatically. Found multiple sqlite files besides the target '{currentDbPath}': {rendered}.",
                  0
                )
              )
    }

  let startServiceWithPolling
    (sqliteDirectory: string)
    (dbFileName: string)
    (schemaIdentity: SchemaIdentity)
    (targetSchema: SqlFile)
    (pollInterval: TimeSpan)
    (cancellationToken: CancellationToken)
    : Task<Result<DbTxnBuilder, SqliteException>> =
    taskResult {
      let! decision = getStartupDatabaseDecision sqliteDirectory dbFileName

      match decision with
      | UseExisting dbPath -> return dbTxn dbPath
      | WaitForMigration dbPath ->
        do! waitForStartupDatabaseReady dbPath pollInterval cancellationToken
        return dbTxn dbPath
      | MigrateThisInstance newDbPath ->
        let! oldDbPath = inferPreviousDatabasePath sqliteDirectory dbFileName

        match oldDbPath with
        | None ->
          let! (_: InitResult) = (runInitWithSchema targetSchema newDbPath: Task<Result<InitResult, SqliteException>>)
          return dbTxn newDbPath
        | Some oldDbPath ->
          let! (_: MigrateResult) =
            (runMigrateWithSchema oldDbPath schemaIdentity targetSchema newDbPath
            : Task<Result<MigrateResult, SqliteException>>)

          return dbTxn newDbPath
      | ExitEarly(_, reason) -> return! Error(SqliteException(reason, 0))
    }

  let startService
    (sqliteDirectory: string)
    (dbFileName: string)
    (schemaIdentity: SchemaIdentity)
    (targetSchema: SqlFile)
    (cancellationToken: CancellationToken)
    : Task<Result<DbTxnBuilder, SqliteException>> =
    startServiceWithPolling
      sqliteDirectory
      dbFileName
      schemaIdentity
      targetSchema
      (TimeSpan.FromSeconds 1.0)
      cancellationToken

  let runDrain (oldDbPath: string) (newDbPath: string) : Task<Result<DrainResult, SqliteException>> =
    taskResult {
      try
        if not (File.Exists oldDbPath) then
          return! Error(toSqliteError $"Old database was not found: {oldDbPath}")
        elif not (File.Exists newDbPath) then
          return! Error(toSqliteError $"New database was not found: {newDbPath}")
        else
          use oldConnection = openSqliteConnection oldDbPath
          use newConnection = openSqliteConnection newDbPath

          do! setOldMarkerToDraining oldConnection
          let! sourceSchema = loadSchemaFromDatabase oldConnection migrationTables
          let! targetSchema = loadSchemaFromDatabase newConnection migrationTables
          let! plan = buildCopyPlan sourceSchema targetSchema
          let! initialMappings = loadIdMappings newConnection
          let! progressRow = ensureMigrationProgressRow newConnection None
          let mutable mappings = initialMappings
          let mutable replayedEntries = 0
          let mutable lastConsumedLogId = progressRow.lastReplayedLogId
          let mutable keepDraining = true

          let isOk =
            function
            | Ok _ -> true
            | Error _ -> false

          let mutable result: Result<DrainResult, SqliteException> =
            Ok
              { replayedEntries = 0
                remainingEntries = 0L }

          while keepDraining && isOk result do
            let! batchResult = readMigrationLogEntries oldConnection lastConsumedLogId

            match batchResult with
            | Error ex -> result <- Error ex
            | Ok [] -> keepDraining <- false
            | Ok entries ->
              let! replayResult = replayDrainEntries newConnection plan entries mappings

              match replayResult with
              | Error ex -> result <- Error ex
              | Ok updatedMappings ->
                mappings <- updatedMappings
                replayedEntries <- replayedEntries + entries.Length

                let batchMaxLogId = entries |> List.map _.id |> List.max
                lastConsumedLogId <- batchMaxLogId
                do! upsertMigrationProgress newConnection None lastConsumedLogId false

          match result with
          | Error ex -> return! Error ex
          | Ok _ ->
            let! remainingEntries = countPendingLogEntries oldConnection None lastConsumedLogId
            do! upsertMigrationProgress newConnection None lastConsumedLogId (remainingEntries = 0L)

            return
              { replayedEntries = replayedEntries
                remainingEntries = remainingEntries }
      with
      | :? SqliteException as ex -> return! Error ex
      | ex -> return! Error(toSqliteError ex.Message)
    }

  let runArchiveOld (projectDirectory: string) (oldDbPath: string) : Task<Result<ArchiveOldResult, SqliteException>> =
    task {
      try
        let resolvedProjectDirectory = Path.GetFullPath projectDirectory
        let resolvedOldDbPath = Path.GetFullPath oldDbPath

        if not (Directory.Exists resolvedProjectDirectory) then
          return Error(toSqliteError $"Project directory was not found: {resolvedProjectDirectory}")
        elif not (File.Exists resolvedOldDbPath) then
          return Error(toSqliteError $"Old database was not found: {oldDbPath}")
        else
          let! markerStatus =
            task {
              use connection = openSqliteConnection resolvedOldDbPath
              use transaction = connection.BeginTransaction()
              let! markerStatus = readMarkerStatus connection (Some transaction) "_migration_marker"

              let markerIsRecording =
                markerStatus
                |> Option.exists (fun status -> status.Equals("recording", StringComparison.OrdinalIgnoreCase))

              if markerIsRecording then
                transaction.Rollback()

                return
                  Error(
                    toSqliteError
                      "Old database is still in recording mode. Run `mig drain` and `mig cutover` before cleanup."
                  )
              else
                transaction.Commit()
                return Ok markerStatus
            }

          match markerStatus with
          | Error ex -> return Error ex
          | Ok markerStatus ->
            let archiveDirectory = Path.Combine(resolvedProjectDirectory, "archive")
            Directory.CreateDirectory archiveDirectory |> ignore

            let archivePath = Path.Combine(archiveDirectory, Path.GetFileName resolvedOldDbPath)
            let replacedExistingArchive = File.Exists archivePath

            if replacedExistingArchive then
              File.Delete archivePath

            File.Move(resolvedOldDbPath, archivePath)

            return
              Ok
                { previousMarkerStatus = markerStatus
                  archivePath = archivePath
                  replacedExistingArchive = replacedExistingArchive }
      with
      | :? SqliteException as ex -> return Error ex
      | ex -> return Error(toSqliteError ex.Message)
    }

  let getResetMigrationPlan
    (oldDbPath: string)
    (newDbPath: string)
    : Task<Result<ResetMigrationPlan, SqliteException>> =
    taskResult {
      try
        if not (File.Exists oldDbPath) then
          return! Error(toSqliteError $"Old database was not found: {oldDbPath}")
        else
          let newDatabaseExisted = File.Exists newDbPath

          let! (previousNewStatus: string option) =
            if newDatabaseExisted then
              taskResult {
                try
                  use newConnection = openSqliteConnection newDbPath
                  return! readMarkerStatus newConnection None "_migration_status"
                with
                | :? SqliteException as ex -> return! Error ex
                | ex -> return! Error(toSqliteError ex.Message)
              }
            else
              Task.FromResult(Ok None)

          use oldConnection = openSqliteConnection oldDbPath

          let! previousOldMarkerStatus = readMarkerStatus oldConnection None "_migration_marker"
          let! oldHasMarker = tableExists oldConnection None "_migration_marker"
          let! oldHasLog = tableExists oldConnection None "_migration_log"

          let newIsReady =
            previousNewStatus
            |> Option.exists (fun status -> status.Equals("ready", StringComparison.OrdinalIgnoreCase))

          let blockedReason =
            if newIsReady then
              Some(readyResetBlockedMessage newDbPath)
            else
              None

          let canApplyReset = blockedReason.IsNone

          return
            { previousOldMarkerStatus = previousOldMarkerStatus
              oldMarkerPresent = oldHasMarker
              oldLogPresent = oldHasLog
              previousNewStatus = previousNewStatus
              newDatabaseExisted = newDatabaseExisted
              willDropOldMarker = oldHasMarker
              willDropOldLog = oldHasLog
              willDeleteNewDatabase = newDatabaseExisted && canApplyReset
              canApplyReset = canApplyReset
              blockedReason = blockedReason }
      with
      | :? SqliteException as ex -> return! Error ex
      | ex -> return! Error(toSqliteError ex.Message)
    }

  let runResetMigration (oldDbPath: string) (newDbPath: string) : Task<Result<ResetMigrationResult, SqliteException>> =
    taskResult {
      try
        let! plan = getResetMigrationPlan oldDbPath newDbPath

        if not plan.canApplyReset then
          let message = plan.blockedReason |> Option.defaultValue "Reset cannot be applied."
          return! Error(toSqliteError message)
        else
          use oldConnection = openSqliteConnection oldDbPath

          use transaction = oldConnection.BeginTransaction()
          let! previousOldMarkerStatus = readMarkerStatus oldConnection (Some transaction) "_migration_marker"
          let! oldHasMarker = tableExists oldConnection (Some transaction) "_migration_marker"
          let! oldHasLog = tableExists oldConnection (Some transaction) "_migration_log"

          if oldHasMarker then
            use dropMarkerCmd =
              createCommand oldConnection (Some transaction) "DROP TABLE _migration_marker"

            let! _ = dropMarkerCmd.ExecuteNonQueryAsync()
            ()

          if oldHasLog then
            use dropLogCmd =
              createCommand oldConnection (Some transaction) "DROP TABLE _migration_log"

            let! _ = dropLogCmd.ExecuteNonQueryAsync()
            ()

          transaction.Commit()

          let newDatabaseDeleted =
            if plan.newDatabaseExisted then
              File.Delete newDbPath
              true
            else
              false

          return
            { previousOldMarkerStatus = previousOldMarkerStatus
              oldMarkerDropped = oldHasMarker
              oldLogDropped = oldHasLog
              previousNewStatus = plan.previousNewStatus
              newDatabaseExisted = plan.newDatabaseExisted
              newDatabaseDeleted = newDatabaseDeleted }
      with
      | :? SqliteException as ex -> return! Error ex
      | ex -> return! Error(toSqliteError ex.Message)
    }

  let private ensureOldStateSafeForCutover
    (oldDbPath: string)
    (newDbPath: string)
    : Task<Result<unit, SqliteException>> =
    taskResult {
      try
        if not (File.Exists oldDbPath) then
          return! Error(toSqliteError $"Old database was not found: {oldDbPath}")
        elif not (File.Exists newDbPath) then
          return! Error(toSqliteError $"New database was not found: {newDbPath}")
        else
          use newConnection = openSqliteConnection newDbPath
          let! newMigrationStatus = readMarkerStatus newConnection None "_migration_status"

          let newIsMigrating =
            newMigrationStatus
            |> Option.exists (fun status -> status.Equals("migrating", StringComparison.OrdinalIgnoreCase))

          if not newIsMigrating then
            return ()
          else
            let! progress = readMigrationProgress newConnection None

            match progress with
            | None ->
              return!
                Error(
                  toSqliteError
                    "_migration_progress is missing in the new database. Run `mig drain` before `mig cutover`."
                )
            | Some progressRow when not progressRow.drainCompleted ->
              return!
                Error(
                  toSqliteError
                    "Drain is not complete. Pending replay entries still exist. Run `mig drain` again before `mig cutover`."
                )
            | Some progressRow ->
              use oldConnection = openSqliteConnection oldDbPath
              let! oldMarkerStatus = readMarkerStatus oldConnection None "_migration_marker"
              let! hasMigrationLog = tableExists oldConnection None "_migration_log"

              if not hasMigrationLog then
                return!
                  Error(
                    toSqliteError
                      "Cutover blocked: _migration_log is missing in the old database. Replay divergence risk cannot be ruled out."
                  )
              else
                match oldMarkerStatus with
                | None ->
                  return!
                    Error(
                      toSqliteError
                        "Cutover blocked: _migration_marker is missing in the old database. Replay divergence risk cannot be ruled out."
                    )
                | Some markerStatus when markerStatus.Equals("recording", StringComparison.OrdinalIgnoreCase) ->
                  return!
                    Error(
                      toSqliteError
                        "Cutover blocked: old marker status is recording, so new writes may still be accumulating in _migration_log. Run `mig drain` again before `mig cutover`."
                    )
                | Some markerStatus when markerStatus.Equals("draining", StringComparison.OrdinalIgnoreCase) ->
                  let! pendingEntries = countPendingLogEntries oldConnection None progressRow.lastReplayedLogId

                  if pendingEntries > 0L then
                    let entryNoun = if pendingEntries = 1L then "entry" else "entries"

                    return!
                      Error(
                        toSqliteError
                          $"Cutover blocked: old _migration_log has {pendingEntries} unreplayed {entryNoun} beyond checkpoint {progressRow.lastReplayedLogId}. Run `mig drain` again before `mig cutover`."
                      )
                  else
                    return ()
                | Some markerStatus ->
                  return!
                    Error(
                      toSqliteError
                        $"Cutover blocked: old marker status is '{markerStatus}'. Expected 'draining' before cutover to avoid replay divergence."
                    )
      with
      | :? SqliteException as ex -> return! Error ex
      | ex -> return! Error(toSqliteError ex.Message)
    }

  let runCutover (newDbPath: string) : Task<Result<CutoverResult, SqliteException>> =
    task {
      try
        use connection = openSqliteConnection newDbPath
        use transaction = connection.BeginTransaction()

        let! hasMigrationStatus = tableExists connection (Some transaction) "_migration_status"

        if not hasMigrationStatus then
          transaction.Rollback()

          return
            Error(
              toSqliteError
                "_migration_status table is missing in the new database. Run `mig migrate` before `mig cutover`."
            )
        else
          let! previousStatusOption = readMarkerStatus connection (Some transaction) "_migration_status"

          let previousStatus =
            match previousStatusOption with
            | Some status -> Ok status
            | None ->
              Error(
                toSqliteError
                  "_migration_status row with id = 0 is missing in the new database. Run `mig migrate` before `mig cutover`."
              )

          match previousStatus with
          | Error error ->
            transaction.Rollback()
            return Error error
          | Ok status ->
            let isMigrating = status.Equals("migrating", StringComparison.OrdinalIgnoreCase)
            let isReady = status.Equals("ready", StringComparison.OrdinalIgnoreCase)

            if not isMigrating && not isReady then
              transaction.Rollback()

              return
                Error(
                  toSqliteError
                    $"Unsupported _migration_status value '{status}'. Expected 'migrating' or 'ready' before cutover."
                )
            else
              let! readyForCutover =
                if isMigrating then
                  task {
                    let! progress = readMigrationProgress connection (Some transaction)

                    match progress with
                    | None ->
                      return
                        Error(
                          toSqliteError
                            "_migration_progress is missing in the new database. Run `mig drain` before `mig cutover`."
                        )
                    | Some progressRow when not progressRow.drainCompleted ->
                      return
                        Error(
                          toSqliteError
                            "Drain is not complete. Pending replay entries still exist. Run `mig drain` again before `mig cutover`."
                        )
                    | Some _ -> return Ok()
                  }
                else
                  Task.FromResult(Ok())

              match readyForCutover with
              | Error error ->
                transaction.Rollback()
                return Error error
              | Ok() ->
                let! hasIdMapping = tableExists connection (Some transaction) "_id_mapping"

                if hasIdMapping then
                  use dropIdMappingCmd =
                    createCommand connection (Some transaction) "DROP TABLE _id_mapping"

                  let! _ = dropIdMappingCmd.ExecuteNonQueryAsync()
                  ()

                let! hasMigrationProgress = tableExists connection (Some transaction) "_migration_progress"

                if hasMigrationProgress then
                  use dropProgressCmd =
                    createCommand connection (Some transaction) "DROP TABLE _migration_progress"

                  let! _ = dropProgressCmd.ExecuteNonQueryAsync()
                  ()

                do! upsertStatusRow connection (Some transaction) "_migration_status" "ready"
                transaction.Commit()

                return
                  Ok
                    { previousStatus = status
                      idMappingDropped = hasIdMapping
                      migrationProgressDropped = hasMigrationProgress }
      with
      | :? SqliteException as ex -> return Error ex
      | ex -> return Error(toSqliteError ex.Message)
    }

  let runCutoverWithOldSafety (oldDbPath: string) (newDbPath: string) : Task<Result<CutoverResult, SqliteException>> =
    taskResult {
      do! ensureOldStateSafeForCutover oldDbPath newDbPath
      let! (cutoverResult: CutoverResult) = runCutover newDbPath
      return cutoverResult
    }
