namespace Mig

open System
open System.Globalization
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Mig.HotMigrationPrimitives

module internal HotMigrationMetadata =
  type MigrationProgressRow =
    { lastReplayedLogId: int64
      drainCompleted: bool }

  type SchemaIdentityRow =
    { schemaHash: string
      schemaCommit: string option }

  let tableExists
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

  let countRows
    (connection: SqliteConnection)
    (transaction: SqliteTransaction option)
    (tableName: string)
    : Task<int64> =
    task {
      use cmd =
        createCommand connection transaction $"SELECT COUNT(*) FROM {quoteIdentifier tableName}"

      let! resultObj = cmd.ExecuteScalarAsync()
      return Convert.ToInt64(resultObj, CultureInfo.InvariantCulture)
    }

  let countRowsIfTableExists
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

  let readMarkerStatus
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
          createCommand connection transaction $"SELECT status FROM {quoteIdentifier tableName} WHERE id = 0 LIMIT 1"

        let! statusObj = cmd.ExecuteScalarAsync()

        if isNull statusObj then
          return None
        else
          return Some(string statusObj)
    }

  let upsertStatusRow
    (connection: SqliteConnection)
    (transaction: SqliteTransaction option)
    (tableName: string)
    (status: string)
    : Task<unit> =
    task {
      use cmd =
        createCommand
          connection
          transaction
          $"INSERT INTO {quoteIdentifier tableName}(id, status) VALUES (0, @status) ON CONFLICT(id) DO UPDATE SET status = excluded.status"

      cmd.Parameters.AddWithValue("@status", status) |> ignore
      let! _ = cmd.ExecuteNonQueryAsync()
      return ()
    }

  let upsertSchemaIdentity
    (connection: SqliteConnection)
    (transaction: SqliteTransaction option)
    (schemaHash: string)
    (schemaCommit: string option)
    : Task<unit> =
    task {
      use cmd =
        createCommand
          connection
          transaction
          "INSERT INTO _schema_identity(id, schema_hash, schema_commit, created_utc) VALUES (0, @schema_hash, @schema_commit, @created_utc) ON CONFLICT(id) DO UPDATE SET schema_hash = excluded.schema_hash, schema_commit = excluded.schema_commit, created_utc = excluded.created_utc"

      cmd.Parameters.AddWithValue("@schema_hash", schemaHash) |> ignore

      let schemaCommitValue =
        match schemaCommit with
        | Some value -> box value
        | None -> box DBNull.Value

      cmd.Parameters.AddWithValue("@schema_commit", schemaCommitValue) |> ignore

      let createdUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
      cmd.Parameters.AddWithValue("@created_utc", createdUtc) |> ignore
      let! _ = cmd.ExecuteNonQueryAsync()
      return ()
    }

  let upsertMigrationProgress
    (connection: SqliteConnection)
    (transaction: SqliteTransaction option)
    (lastReplayedLogId: int64)
    (drainCompleted: bool)
    : Task<unit> =
    task {
      use cmd =
        createCommand
          connection
          transaction
          "INSERT INTO _migration_progress(id, last_replayed_log_id, drain_completed) VALUES (0, @last_replayed, @drain_completed) ON CONFLICT(id) DO UPDATE SET last_replayed_log_id = excluded.last_replayed_log_id, drain_completed = excluded.drain_completed"

      cmd.Parameters.AddWithValue("@last_replayed", lastReplayedLogId) |> ignore

      cmd.Parameters.AddWithValue("@drain_completed", if drainCompleted then 1 else 0)
      |> ignore

      let! _ = cmd.ExecuteNonQueryAsync()
      return ()
    }

  let readMigrationProgress
    (connection: SqliteConnection)
    (transaction: SqliteTransaction option)
    : Task<MigrationProgressRow option> =
    task {
      let! hasProgressTable = tableExists connection transaction "_migration_progress"

      if not hasProgressTable then
        return None
      else
        use rowCmd =
          createCommand
            connection
            transaction
            "SELECT last_replayed_log_id, drain_completed FROM _migration_progress WHERE id = 0 LIMIT 1"

        use! reader = rowCmd.ExecuteReaderAsync()
        let! hasRow = reader.ReadAsync()

        if not hasRow then
          return None
        else
          return
            Some
              { lastReplayedLogId = reader.GetInt64 0
                drainCompleted = reader.GetInt64 1 = 1L }
    }

  let readSchemaIdentity
    (connection: SqliteConnection)
    (transaction: SqliteTransaction option)
    : Task<SchemaIdentityRow option> =
    task {
      let! hasSchemaIdentityTable = tableExists connection transaction "_schema_identity"

      if not hasSchemaIdentityTable then
        return None
      else
        use rowCmd =
          createCommand
            connection
            transaction
            "SELECT schema_hash, schema_commit FROM _schema_identity WHERE id = 0 LIMIT 1"

        use! reader = rowCmd.ExecuteReaderAsync()
        let! hasRow = reader.ReadAsync()

        if not hasRow then
          return None
        else
          return
            Some
              { schemaHash = reader.GetString 0
                schemaCommit = if reader.IsDBNull 1 then None else Some(reader.GetString 1) }
    }

  let ensureMigrationProgressRow
    (connection: SqliteConnection)
    (transaction: SqliteTransaction option)
    : Task<MigrationProgressRow> =
    task {
      use createProgressCmd =
        createCommand
          connection
          transaction
          "CREATE TABLE IF NOT EXISTS _migration_progress(id INTEGER PRIMARY KEY CHECK (id = 0), last_replayed_log_id INTEGER NOT NULL, drain_completed INTEGER NOT NULL);"

      let! _ = createProgressCmd.ExecuteNonQueryAsync()
      let! progress = readMigrationProgress connection transaction

      match progress with
      | Some row -> return row
      | None ->
        do! upsertMigrationProgress connection transaction 0L false

        return
          { lastReplayedLogId = 0L
            drainCompleted = false }
    }

  let countPendingLogEntries
    (connection: SqliteConnection)
    (transaction: SqliteTransaction option)
    (lastReplayedLogId: int64)
    : Task<int64> =
    task {
      let! hasLogTable = tableExists connection transaction "_migration_log"

      if not hasLogTable then
        return 0L
      else
        use cmd =
          createCommand connection transaction "SELECT COUNT(*) FROM _migration_log WHERE id > @last_replayed_log_id"

        cmd.Parameters.AddWithValue("@last_replayed_log_id", lastReplayedLogId)
        |> ignore

        let! countObj = cmd.ExecuteScalarAsync()
        return Convert.ToInt64(countObj, CultureInfo.InvariantCulture)
    }
