namespace Mig

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open MigLib.Db
open MigLib.Util
open DeclarativeMigrations.DrainReplay
open DeclarativeMigrations.Types
open Mig.HotMigrationTypes
open Mig.HotMigrationPrimitives
open Mig.HotMigrationMetadata
open Mig.HotMigrationSchemaBootstrap
open Mig.HotMigrationSchemaIntrospection
open Mig.HotMigrationCopy
open Mig.HotMigrationPlanning
open Mig.HotMigrationShared

module internal HotMigrationMigration =
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
