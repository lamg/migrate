namespace Mig

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open MigLib.Db
open MigLib.Util
open Mig.HotMigrationTypes
open Mig.HotMigrationPrimitives
open Mig.HotMigrationMetadata
open Mig.HotMigrationShared

module internal HotMigrationAdmin =
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
