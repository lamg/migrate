namespace Mig.DeclarativeMigrations

open System
open System.Globalization
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Mig.DeclarativeMigrations.Types
open DrainReplayTypes

module internal DrainReplayParsing =
  let private exprFromJsonNode (node: JsonNode) : Expr =
    if isNull node then
      Value "NULL"
    else
      let mutable intValue = 0
      let mutable int64Value = 0L
      let mutable doubleValue = 0.0
      let mutable boolValue = false
      let mutable stringValue = ""

      if node.GetValueKind() = System.Text.Json.JsonValueKind.Null then
        Value "NULL"
      elif node.AsValue().TryGetValue<int>(&intValue) then
        Integer intValue
      elif node.AsValue().TryGetValue<int64>(&int64Value) then
        if int64Value >= int64 Int32.MinValue && int64Value <= int64 Int32.MaxValue then
          Integer(int int64Value)
        else
          Value(int64Value.ToString(CultureInfo.InvariantCulture))
      elif node.AsValue().TryGetValue<double>(&doubleValue) then
        Real doubleValue
      elif node.AsValue().TryGetValue<bool>(&boolValue) then
        Integer(if boolValue then 1 else 0)
      elif node.AsValue().TryGetValue<string>(&stringValue) then
        String stringValue
      else
        Value(node.ToJsonString())

  let private parseRowData (rowDataJson: string) : Result<Map<string, Expr>, string> =
    try
      let parsed = JsonNode.Parse(rowDataJson)

      if isNull parsed then
        Ok Map.empty
      else
        match parsed with
        | :? JsonObject as obj ->
          obj
          |> Seq.map (fun pair -> pair.Key, exprFromJsonNode pair.Value)
          |> Map.ofSeq
          |> Ok
        | _ -> Error $"row_data must be a JSON object, received: {parsed.GetValueKind()}"
    with ex ->
      Error $"Failed to parse row_data JSON: {ex.Message}"

  let private parseOperation (value: string) : Result<ReplayOperation, string> =
    match value.ToLowerInvariant() with
    | "insert" -> Ok Insert
    | "update" -> Ok Update
    | "delete" -> Ok Delete
    | other -> Error $"Unsupported migration operation '{other}'."

  let groupEntriesByTransaction (entries: MigrationLogEntry list) : (int64 * MigrationLogEntry list) list =
    entries
    |> List.sortBy (fun entry -> entry.txnId, entry.ordering, entry.id)
    |> List.groupBy _.txnId
    |> List.map (fun (txnId, txnEntries) -> txnId, (txnEntries |> List.sortBy (fun entry -> entry.ordering, entry.id)))

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

  let readMigrationLogEntries
    (connection: SqliteConnection)
    (minLogIdExclusive: int64)
    : Task<Result<MigrationLogEntry list, SqliteException>> =
    task {
      try
        let! hasLogTable = tableExists connection None "_migration_log"

        if not hasLogTable then
          return Ok []
        else
          use cmd =
            new SqliteCommand(
              "SELECT id, txn_id, ordering, operation, table_name, row_data FROM _migration_log WHERE id > @min_id ORDER BY txn_id, ordering, id",
              connection
            )

          cmd.Parameters.AddWithValue("@min_id", minLogIdExclusive) |> ignore
          use! reader = cmd.ExecuteReaderAsync()
          let results = ResizeArray<MigrationLogEntry>()
          let mutable keepReading = true

          while keepReading do
            let! hasRow = reader.ReadAsync()

            if hasRow then
              let id = reader.GetInt64(0)
              let txnId = reader.GetInt64(1)
              let ordering = reader.GetInt64(2)
              let operationRaw = reader.GetString(3)
              let tableName = reader.GetString(4)
              let rowDataJson = reader.GetString(5)

              match parseOperation operationRaw, parseRowData rowDataJson with
              | Ok operation, Ok rowData ->
                results.Add
                  { id = id
                    txnId = txnId
                    ordering = ordering
                    operation = operation
                    sourceTable = tableName
                    rowData = rowData }
              | Error operationError, _ -> raise (toSqliteError operationError)
              | _, Error rowDataError -> raise (toSqliteError rowDataError)
            else
              keepReading <- false

          return Ok(results |> Seq.toList)
      with :? SqliteException as ex ->
        return Error ex
    }
