namespace MigLib

open System
open System.Globalization
open System.Threading.Tasks
open Microsoft.Data.Sqlite

module DbRecording =
  let ensureNewDatabaseReadyForTransactions (tx: SqliteTransaction) : Task<Result<unit, SqliteException>> =
    task {
      let! statusResult = DbCore.tryReadStatusValue tx "_migration_status"

      match statusResult with
      | Error ex -> return Error ex
      | Ok None ->
        let! hasStatusTable = DbCore.tableExists tx "_migration_status"

        if hasStatusTable then
          return
            Error(
              SqliteException(
                "Target database has a _migration_status table but no status row at id = 0. Run `mig migrate` again or repair the target before serving traffic.",
                0
              )
            )
        else
          return Ok()
      | Ok(Some status) when status.Equals("ready", StringComparison.OrdinalIgnoreCase) -> return Ok()
      | Ok(Some status) when status.Equals("migrating", StringComparison.OrdinalIgnoreCase) ->
        return
          Error(SqliteException("Target database is still migrating. Run `mig cutover` before serving requests.", 0))
      | Ok(Some status) ->
        return
          Error(
            SqliteException(
              $"Unsupported _migration_status value '{status}'. Expected 'migrating' or 'ready' before serving requests.",
              0
            )
          )
    }

  let detectMigrationMode (tx: SqliteTransaction) : Task<DbCore.MigrationMode> =
    task {
      let! markerExists = DbCore.tableExists tx "_migration_marker"

      if not markerExists then
        return DbCore.Normal
      else
        use cmd =
          new SqliteCommand("SELECT status FROM _migration_marker WHERE id = 0 LIMIT 1", tx.Connection, tx)

        let! statusObj = cmd.ExecuteScalarAsync()

        if isNull statusObj then
          return DbCore.Normal
        else
          match string statusObj with
          | "recording" -> return DbCore.Recording
          | "draining" -> return DbCore.Draining
          | _ -> return DbCore.Normal
    }

  let flushRecordedWrites (context: DbCore.TxnContext) : Task<Result<unit, SqliteException>> =
    task {
      try
        match context.mode with
        | DbCore.Recording when context.writes.Count > 0 ->
          let! logExists = DbCore.tableExists context.tx "_migration_log"

          if not logExists then
            return
              Error(SqliteException("Migration marker is set to recording, but _migration_log table is missing.", 0))
          else
            use txnIdCmd =
              new SqliteCommand(
                "SELECT COALESCE(MAX(txn_id), 0) + 1 FROM _migration_log",
                context.tx.Connection,
                context.tx
              )

            let! txnIdObj = txnIdCmd.ExecuteScalarAsync()
            let txnId = Convert.ToInt64(txnIdObj, CultureInfo.InvariantCulture)

            for index in 0 .. context.writes.Count - 1 do
              let entry = context.writes[index]

              use insertCmd =
                new SqliteCommand(
                  "INSERT INTO _migration_log (txn_id, ordering, operation, table_name, row_data) VALUES (@txn_id, @ordering, @operation, @table_name, @row_data)",
                  context.tx.Connection,
                  context.tx
                )

              insertCmd.Parameters.AddWithValue("@txn_id", txnId) |> ignore
              insertCmd.Parameters.AddWithValue("@ordering", index + 1) |> ignore
              insertCmd.Parameters.AddWithValue("@operation", entry.operation) |> ignore
              insertCmd.Parameters.AddWithValue("@table_name", entry.tableName) |> ignore
              insertCmd.Parameters.AddWithValue("@row_data", entry.rowDataJson) |> ignore

              let! _ = insertCmd.ExecuteNonQueryAsync()
              ()

            return Ok()
        | _ -> return Ok()
      with :? SqliteException as ex ->
        return Error ex
    }

module MigrationLog =
  let ensureWriteAllowed (tx: SqliteTransaction) : unit =
    match DbCore.getMatchingContext tx with
    | Some context when context.mode = DbCore.Draining ->
      raise (SqliteException("Writes are unavailable while migration drain is in progress.", 0))
    | _ -> ()

  let private recordWrite
    (tx: SqliteTransaction)
    (operation: string)
    (tableName: string)
    (rowData: (string * obj) list)
    =
    ensureWriteAllowed tx

    match DbCore.getMatchingContext tx with
    | Some context when context.mode = DbCore.Recording ->
      context.writes.Add
        { operation = operation
          tableName = tableName
          rowDataJson = DbCore.serializeRowData rowData }
    | _ -> ()

  let recordInsert (tx: SqliteTransaction) (tableName: string) (rowData: (string * obj) list) : unit =
    recordWrite tx "insert" tableName rowData

  let recordUpdate (tx: SqliteTransaction) (tableName: string) (rowData: (string * obj) list) : unit =
    recordWrite tx "update" tableName rowData

  let recordDelete (tx: SqliteTransaction) (tableName: string) (rowData: (string * obj) list) : unit =
    recordWrite tx "delete" tableName rowData
