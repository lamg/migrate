namespace Mig.DeclarativeMigrations

open System
open System.Globalization
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Mig.DeclarativeMigrations.DataCopy
open Mig.DeclarativeMigrations.Types
open MigLib.Util
open DrainReplayTypes

module internal DrainReplayState =
  let private tableExists
    (connection: SqliteConnection)
    (transaction: SqliteTransaction option)
    (tableName: string)
    : Task<bool> =
    task {
      use cmd =
        match transaction with
        | Some tx ->
          new SqliteCommand("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1", connection, tx)
        | None ->
          new SqliteCommand("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1", connection)

      cmd.Parameters.AddWithValue("@name", tableName) |> ignore
      let! result = cmd.ExecuteScalarAsync()
      return not (isNull result)
    }

  let loadIdMappings (connection: SqliteConnection) : Task<Result<IdMappingStore, SqliteException>> =
    task {
      try
        let! hasMappingTable = tableExists connection None "_id_mapping"

        if not hasMappingTable then
          return Ok emptyIdMappings
        else
          use cmd =
            new SqliteCommand(
              "SELECT table_name, old_id, new_id FROM _id_mapping ORDER BY table_name, old_id",
              connection
            )

          use! reader = cmd.ExecuteReaderAsync()
          let mutable mappings = emptyIdMappings
          let mutable keepReading = true

          while keepReading do
            let! hasRow = reader.ReadAsync()

            if hasRow then
              let tableName = reader.GetString(0)
              let oldId = reader.GetInt64(1)
              let newId = reader.GetInt64(2)

              let oldExpr =
                if oldId >= int64 Int32.MinValue && oldId <= int64 Int32.MaxValue then
                  Integer(int oldId)
                else
                  Value(oldId.ToString(CultureInfo.InvariantCulture))

              let newExpr =
                if newId >= int64 Int32.MinValue && newId <= int64 Int32.MaxValue then
                  Integer(int newId)
                else
                  Value(newId.ToString(CultureInfo.InvariantCulture))

              mappings <- putMappedIdentity tableName [ oldExpr ] [ newExpr ] mappings
            else
              keepReading <- false

          return Ok mappings
      with :? SqliteException as ex ->
        return Error ex
    }

  let exprToDbValue (expr: Expr) : obj =
    match expr with
    | String value -> box value
    | Integer value -> box value
    | Real value -> box value
    | Value value when value.Equals("NULL", StringComparison.OrdinalIgnoreCase) -> box DBNull.Value
    | Value value ->
      match Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) with
      | true, int64Value -> box int64Value
      | _ ->
        match Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, doubleValue -> box doubleValue
        | _ -> box value

  let exprToInt64 (expr: Expr) : int64 option =
    match expr with
    | Integer value -> Some(int64 value)
    | Value value ->
      match Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) with
      | true, int64Value -> Some int64Value
      | _ -> None
    | _ -> None

  let extractValues (row: Map<string, Expr>) (columns: string list) : Result<Expr list, string> =
    columns
    |> foldResults
      (fun values columnName ->
        match row.TryFind columnName with
        | Some value -> Ok(values @ [ value ])
        | None -> Error $"Missing column '{columnName}' in replay row.")
      []

  let replayOperationError (entry: MigrationLogEntry) (message: string) =
    toSqliteError
      $"Replay failed for txn {entry.txnId}, operation {entry.ordering} on table '{entry.sourceTable}': {message}"

  let findStepForSourceTable (bulkPlan: BulkCopyPlan) (sourceTable: string) : Result<TableCopyStep, string> =
    bulkPlan.steps
    |> List.tryFind (fun step -> step.mapping.sourceTable.Equals(sourceTable, StringComparison.OrdinalIgnoreCase))
    |> function
      | Some step -> Ok step
      | None -> Error $"No replay mapping exists for source table '{sourceTable}'."

  let persistIdMapping
    (tx: SqliteTransaction)
    (tableName: string)
    (sourceIdentity: Expr list)
    (targetIdentity: Expr list)
    : Task<Result<unit, SqliteException>> =
    task {
      try
        let! hasMappingTable = tableExists tx.Connection (Some tx) "_id_mapping"

        if not hasMappingTable then
          return Ok()
        else
          match sourceIdentity, targetIdentity with
          | [ sourceExpr ], [ targetExpr ] ->
            match exprToInt64 sourceExpr, exprToInt64 targetExpr with
            | Some oldId, Some newId ->
              use cmd =
                new SqliteCommand(
                  "INSERT OR REPLACE INTO _id_mapping(table_name, old_id, new_id) VALUES (@table_name, @old_id, @new_id)",
                  tx.Connection,
                  tx
                )

              cmd.Parameters.AddWithValue("@table_name", tableName) |> ignore
              cmd.Parameters.AddWithValue("@old_id", oldId) |> ignore
              cmd.Parameters.AddWithValue("@new_id", newId) |> ignore
              let! _ = cmd.ExecuteNonQueryAsync()
              return Ok()
            | _ -> return Ok()
          | _ -> return Ok()
      with :? SqliteException as ex ->
        return Error ex
    }
