namespace MigLib

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open MigLib.Util

module DbStartup =
  type StartupDatabaseState =
    | Missing
    | Ready
    | Migrating
    | Invalid of reason: string

  type StartupDatabaseDecision =
    | UseExisting of dbPath: string
    | WaitForMigration of dbPath: string
    | MigrateThisInstance of dbPath: string
    | ExitEarly of dbPath: string * reason: string

  let getStartupDatabaseState (dbPath: string) : Task<Result<StartupDatabaseState, SqliteException>> =
    task {
      match DbCore.resolveDatabasePath dbPath with
      | Error message -> return Error(SqliteException(message, 0))
      | Ok resolvedDbPath ->
        if not (File.Exists resolvedDbPath) then
          return Ok Missing
        else
          try
            use connection = DbCore.openSqliteConnection resolvedDbPath
            let! statusResult = DbCore.tryReadStatusValueFromConnection connection "_migration_status"

            match statusResult with
            | Error ex -> return Error ex
            | Ok None ->
              let! hasStatusTable = DbCore.tableExistsInConnection connection "_migration_status"

              if hasStatusTable then
                return
                  Ok(
                    Invalid
                      "Target database has a _migration_status table but no status row at id = 0. Run migration again or repair the target before serving traffic."
                  )
              else
                return Ok Ready
            | Ok(Some status) when status.Equals("ready", StringComparison.OrdinalIgnoreCase) -> return Ok Ready
            | Ok(Some status) when status.Equals("migrating", StringComparison.OrdinalIgnoreCase) -> return Ok Migrating
            | Ok(Some status) ->
              return
                Ok(
                  Invalid
                    $"Unsupported _migration_status value '{status}'. Expected 'migrating' or 'ready' before serving traffic."
                )
          with :? SqliteException as ex ->
            return Error ex
    }

  let getStartupDatabaseDecision
    (configuredDirectory: string)
    (dbFileName: string)
    : Task<Result<StartupDatabaseDecision, SqliteException>> =
    taskResult {
      let! dbPath =
        (DbCore.resolveDatabaseFilePath configuredDirectory dbFileName
         |> TaskResultEx.ofResultMapError (fun message -> SqliteException(message, 0))
        : Task<Result<string, SqliteException>>)

      let! state = (getStartupDatabaseState dbPath: Task<Result<StartupDatabaseState, SqliteException>>)

      return
        match state with
        | Missing -> MigrateThisInstance dbPath
        | Ready -> UseExisting dbPath
        | Migrating -> WaitForMigration dbPath
        | Invalid reason -> ExitEarly(dbPath, reason)
    }

  let waitForStartupDatabaseReady
    (dbPath: string)
    (pollInterval: TimeSpan)
    (cancellationToken: CancellationToken)
    : Task<Result<unit, SqliteException>> =
    task {
      try
        let interval =
          if pollInterval <= TimeSpan.Zero then
            TimeSpan.FromMilliseconds 100.0
          else
            pollInterval

        let mutable keepWaiting = true
        let mutable result = Ok()

        while keepWaiting do
          let! stateResult = getStartupDatabaseState dbPath

          match stateResult with
          | Error ex ->
            result <- Error ex
            keepWaiting <- false
          | Ok Ready ->
            result <- Ok()
            keepWaiting <- false
          | Ok Migrating -> do! Task.Delay(interval, cancellationToken)
          | Ok Missing ->
            result <- Error(SqliteException($"Target database was not found while waiting for readiness: {dbPath}", 0))
            keepWaiting <- false
          | Ok(Invalid reason) ->
            result <- Error(SqliteException(reason, 0))
            keepWaiting <- false

        return result
      with :? OperationCanceledException ->
        return Error(SqliteException("Waiting for startup database readiness was canceled.", 0))
    }
