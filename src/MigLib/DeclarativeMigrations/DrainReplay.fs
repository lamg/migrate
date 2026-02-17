module MigLib.DeclarativeMigrations.DrainReplay

open System
open System.Globalization
open System.Text.Json.Nodes
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open Microsoft.Data.Sqlite
open MigLib.DeclarativeMigrations.DataCopy
open MigLib.DeclarativeMigrations.Types

type internal ReplayOperation =
  | Insert
  | Update
  | Delete

type internal MigrationLogEntry =
  { id: int64
    txnId: int64
    ordering: int64
    operation: ReplayOperation
    sourceTable: string
    rowData: Map<string, Expr> }

let private toSqliteError (message: string) = SqliteException(message, 0)

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

let internal groupEntriesByTransaction (entries: MigrationLogEntry list) : (int64 * MigrationLogEntry list) list =
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

let internal readMigrationLogEntries
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

let internal loadIdMappings (connection: SqliteConnection) : Task<Result<IdMappingStore, SqliteException>> =
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

let private exprToDbValue (expr: Expr) : obj =
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

let private exprToInt64 (expr: Expr) : int64 option =
  match expr with
  | Integer value -> Some(int64 value)
  | Value value ->
    match Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) with
    | true, int64Value -> Some int64Value
    | _ -> None
  | _ -> None

let private extractValues (row: Map<string, Expr>) (columns: string list) : Result<Expr list, string> =
  columns
  |> foldResults
    (fun values columnName ->
      match row.TryFind columnName with
      | Some value -> Ok(values @ [ value ])
      | None -> Error $"Missing column '{columnName}' in replay row.")
    []

let private replayOperationError (entry: MigrationLogEntry) (message: string) =
  toSqliteError
    $"Replay failed for txn {entry.txnId}, operation {entry.ordering} on table '{entry.sourceTable}': {message}"

let private findStepForSourceTable (bulkPlan: BulkCopyPlan) (sourceTable: string) : Result<TableCopyStep, string> =
  bulkPlan.steps
  |> List.tryFind (fun step -> step.mapping.sourceTable.Equals(sourceTable, StringComparison.OrdinalIgnoreCase))
  |> function
    | Some step -> Ok step
    | None -> Error $"No replay mapping exists for source table '{sourceTable}'."

let private persistIdMapping
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

let private executeInsert
  (entry: MigrationLogEntry)
  (tx: SqliteTransaction)
  (step: TableCopyStep)
  (idMappings: IdMappingStore)
  : Task<Result<IdMappingStore, SqliteException>> =
  task {
    match projectRowForInsert step entry.rowData idMappings with
    | Error message -> return Error(replayOperationError entry message)
    | Ok(targetRow, insertColumns, insertValues) ->
      try
        let columnList = String.concat ", " insertColumns

        let paramNames =
          insertColumns |> List.mapi (fun i _ -> $"@p{i}") |> String.concat ", "

        use cmd =
          new SqliteCommand(
            $"INSERT INTO {step.mapping.targetTable} ({columnList}) VALUES ({paramNames})",
            tx.Connection,
            tx
          )

        insertValues
        |> List.iteri (fun i value -> cmd.Parameters.AddWithValue($"@p{i}", exprToDbValue value) |> ignore)

        let! _ = cmd.ExecuteNonQueryAsync()

        let! generatedIdentity =
          match step.identity with
          | Some identity when identity.targetAutoincrementColumn.IsSome ->
            task {
              use idCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
              let! idObj = idCmd.ExecuteScalarAsync()
              let idValue = Convert.ToInt64(idObj, CultureInfo.InvariantCulture)

              let idExpr =
                if idValue >= int64 Int32.MinValue && idValue <= int64 Int32.MaxValue then
                  Integer(int idValue)
                else
                  Value(idValue.ToString(CultureInfo.InvariantCulture))

              return Some [ idExpr ]
            }
          | _ -> Task.FromResult None

        match recordIdMapping step entry.rowData targetRow generatedIdentity idMappings with
        | Error message -> return Error(replayOperationError entry message)
        | Ok updatedMappings ->
          match step.identity with
          | Some identity ->
            match extractValues entry.rowData identity.sourceKeyColumns with
            | Error message -> return Error(replayOperationError entry message)
            | Ok sourceIdentity ->
              match lookupMappedIdentity step.mapping.targetTable sourceIdentity updatedMappings with
              | Error message -> return Error(replayOperationError entry message)
              | Ok targetIdentity ->
                let! persistResult = persistIdMapping tx step.mapping.targetTable sourceIdentity targetIdentity

                match persistResult with
                | Ok() -> return Ok updatedMappings
                | Error ex -> return Error ex
          | None -> return Ok updatedMappings
      with :? SqliteException as ex ->
        return Error ex
  }

let private executeUpdate
  (entry: MigrationLogEntry)
  (tx: SqliteTransaction)
  (step: TableCopyStep)
  (idMappings: IdMappingStore)
  : Task<Result<IdMappingStore, SqliteException>> =
  task {
    match step.identity with
    | None ->
      return
        Error(
          replayOperationError entry $"Table '{step.mapping.targetTable}' has no identity mapping for update replay."
        )
    | Some identity ->
      match projectRowForInsert step entry.rowData idMappings with
      | Error message -> return Error(replayOperationError entry message)
      | Ok(targetRow, _, _) ->
        match extractValues entry.rowData identity.sourceKeyColumns with
        | Error message -> return Error(replayOperationError entry message)
        | Ok sourceIdentity ->
          match lookupMappedIdentity step.mapping.targetTable sourceIdentity idMappings with
          | Error message -> return Error(replayOperationError entry message)
          | Ok targetIdentity ->
            let pkSet = identity.targetKeyColumns |> Set.ofList

            let setColumns =
              step.targetTableDef.columns
              |> List.map _.name
              |> List.filter (fun name -> not (pkSet.Contains name))

            match extractValues targetRow setColumns with
            | Error message -> return Error(replayOperationError entry message)
            | Ok setValues ->
              try
                if setColumns.IsEmpty then
                  return Ok idMappings
                else
                  let setClause =
                    setColumns
                    |> List.mapi (fun i columnName -> $"{columnName} = @s{i}")
                    |> String.concat ", "

                  let whereClause =
                    identity.targetKeyColumns
                    |> List.mapi (fun i columnName -> $"{columnName} = @w{i}")
                    |> String.concat " AND "

                  use cmd =
                    new SqliteCommand(
                      $"UPDATE {step.mapping.targetTable} SET {setClause} WHERE {whereClause}",
                      tx.Connection,
                      tx
                    )

                  setValues
                  |> List.iteri (fun i value -> cmd.Parameters.AddWithValue($"@s{i}", exprToDbValue value) |> ignore)

                  targetIdentity
                  |> List.iteri (fun i value -> cmd.Parameters.AddWithValue($"@w{i}", exprToDbValue value) |> ignore)

                  let! _ = cmd.ExecuteNonQueryAsync()
                  return Ok idMappings
              with :? SqliteException as ex ->
                return Error ex
  }

let private executeDelete
  (entry: MigrationLogEntry)
  (tx: SqliteTransaction)
  (step: TableCopyStep)
  (idMappings: IdMappingStore)
  : Task<Result<IdMappingStore, SqliteException>> =
  task {
    match step.identity with
    | None ->
      return
        Error(
          replayOperationError entry $"Table '{step.mapping.targetTable}' has no identity mapping for delete replay."
        )
    | Some identity ->
      match extractValues entry.rowData identity.sourceKeyColumns with
      | Error message -> return Error(replayOperationError entry message)
      | Ok sourceIdentity ->
        match lookupMappedIdentity step.mapping.targetTable sourceIdentity idMappings with
        | Error message -> return Error(replayOperationError entry message)
        | Ok targetIdentity ->
          try
            let whereClause =
              identity.targetKeyColumns
              |> List.mapi (fun i columnName -> $"{columnName} = @w{i}")
              |> String.concat " AND "

            use cmd =
              new SqliteCommand($"DELETE FROM {step.mapping.targetTable} WHERE {whereClause}", tx.Connection, tx)

            targetIdentity
            |> List.iteri (fun i value -> cmd.Parameters.AddWithValue($"@w{i}", exprToDbValue value) |> ignore)

            let! _ = cmd.ExecuteNonQueryAsync()
            return Ok idMappings
          with :? SqliteException as ex ->
            return Error ex
  }

let private executeReplayEntry
  (bulkPlan: BulkCopyPlan)
  (entry: MigrationLogEntry)
  (tx: SqliteTransaction)
  (idMappings: IdMappingStore)
  : Task<Result<IdMappingStore, SqliteException>> =
  task {
    match findStepForSourceTable bulkPlan entry.sourceTable with
    | Error message -> return Error(replayOperationError entry message)
    | Ok step ->
      match entry.operation with
      | Insert -> return! executeInsert entry tx step idMappings
      | Update -> return! executeUpdate entry tx step idMappings
      | Delete -> return! executeDelete entry tx step idMappings
  }

let internal replayDrainEntries
  (connection: SqliteConnection)
  (bulkPlan: BulkCopyPlan)
  (entries: MigrationLogEntry list)
  (initialIdMappings: IdMappingStore)
  : Task<Result<IdMappingStore, SqliteException>> =
  task {
    let groups = groupEntriesByTransaction entries
    let mutable mappings = initialIdMappings
    let mutable result: Result<IdMappingStore, SqliteException> = Ok mappings
    let mutable index = 0

    while index < groups.Length && result.IsOk do
      let _, groupEntries = groups[index]
      use tx = connection.BeginTransaction()
      let mutable groupResult: Result<IdMappingStore, SqliteException> = Ok mappings
      let mutable opIndex = 0

      while opIndex < groupEntries.Length && groupResult.IsOk do
        let entry = groupEntries[opIndex]
        let! replayResult = executeReplayEntry bulkPlan entry tx mappings
        groupResult <- replayResult

        match groupResult with
        | Ok updated ->
          mappings <- updated
          opIndex <- opIndex + 1
        | Error _ -> ()

      match groupResult with
      | Ok _ ->
        tx.Commit()
        result <- Ok mappings
      | Error ex ->
        tx.Rollback()
        result <- Error ex

      index <- index + 1

    return result
  }
