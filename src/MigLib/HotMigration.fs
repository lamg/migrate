module MigLib.HotMigration

open System
open System.Globalization
open System.Threading.Tasks
open Microsoft.Data.Sqlite

type MigrationStatusReport =
  { oldMarkerStatus: string option
    migrationLogEntries: int64
    pendingReplayEntries: int64 option
    idMappingEntries: int64 option
    newMigrationStatus: string option }

type CutoverResult =
  { previousStatus: string
    idMappingDropped: bool }

let private toSqliteError (message: string) = SqliteException(message, 0)

let private createCommand
  (connection: SqliteConnection)
  (transaction: SqliteTransaction option)
  (sql: string)
  : SqliteCommand =
  match transaction with
  | Some tx -> new SqliteCommand(sql, connection, tx)
  | None -> new SqliteCommand(sql, connection)

let private tableExists
  (connection: SqliteConnection)
  (transaction: SqliteTransaction option)
  (tableName: string)
  : Task<bool> =
  task {
    use cmd =
      createCommand connection transaction "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1"

    cmd.Parameters.AddWithValue("@name", tableName) |> ignore
    let! result = cmd.ExecuteScalarAsync()
    return not (isNull result)
  }

let private countRows
  (connection: SqliteConnection)
  (transaction: SqliteTransaction option)
  (tableName: string)
  : Task<int64> =
  task {
    use cmd = createCommand connection transaction $"SELECT COUNT(*) FROM {tableName}"
    let! resultObj = cmd.ExecuteScalarAsync()
    let result = Convert.ToInt64(resultObj, CultureInfo.InvariantCulture)
    return result
  }

let private countRowsIfTableExists
  (connection: SqliteConnection)
  (transaction: SqliteTransaction option)
  (tableName: string)
  : Task<int64> =
  task {
    let! exists = tableExists connection transaction tableName

    if exists then
      return! countRows connection transaction tableName
    else
      return 0L
  }

let private readMarkerStatus
  (connection: SqliteConnection)
  (transaction: SqliteTransaction option)
  (tableName: string)
  : Task<string option> =
  task {
    let! exists = tableExists connection transaction tableName

    if not exists then
      return None
    else
      use cmd =
        createCommand connection transaction $"SELECT status FROM {tableName} WHERE id = 0 LIMIT 1"

      let! statusObj = cmd.ExecuteScalarAsync()

      if isNull statusObj then
        return None
      else
        return Some(string statusObj)
  }

let getStatus (oldDbPath: string) (newDbPath: string option) : Task<Result<MigrationStatusReport, SqliteException>> =
  task {
    try
      use oldConnection = new SqliteConnection($"Data Source={oldDbPath}")
      do! oldConnection.OpenAsync()

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
              newMigrationStatus = None }
      | Some newPath ->
        use newConnection = new SqliteConnection($"Data Source={newPath}")
        do! newConnection.OpenAsync()

        let! idMappingEntries = countRowsIfTableExists newConnection None "_id_mapping"
        let! newMigrationStatus = readMarkerStatus newConnection None "_migration_status"

        return
          Ok
            { oldMarkerStatus = oldMarkerStatus
              migrationLogEntries = migrationLogEntries
              pendingReplayEntries = Some migrationLogEntries
              idMappingEntries = Some idMappingEntries
              newMigrationStatus = newMigrationStatus }
    with
    | :? SqliteException as ex -> return Error ex
    | ex -> return Error(toSqliteError ex.Message)
  }

let runCutover (newDbPath: string) : Task<Result<CutoverResult, SqliteException>> =
  task {
    try
      use connection = new SqliteConnection($"Data Source={newDbPath}")
      do! connection.OpenAsync()
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
            let! hasIdMapping = tableExists connection (Some transaction) "_id_mapping"

            if hasIdMapping then
              use dropCmd = createCommand connection (Some transaction) "DROP TABLE _id_mapping"
              let! _ = dropCmd.ExecuteNonQueryAsync()
              ()

            use upsertCmd =
              createCommand
                connection
                (Some transaction)
                "INSERT INTO _migration_status(id, status) VALUES (0, @status) ON CONFLICT(id) DO UPDATE SET status = excluded.status"

            upsertCmd.Parameters.AddWithValue("@status", "ready") |> ignore
            let! _ = upsertCmd.ExecuteNonQueryAsync()
            transaction.Commit()

            return
              Ok
                { previousStatus = status
                  idMappingDropped = hasIdMapping }
    with
    | :? SqliteException as ex -> return Error ex
    | ex -> return Error(toSqliteError ex.Message)
  }
