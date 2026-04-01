namespace Mig

open System
open System.Globalization
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open MigLib.Util
open DeclarativeMigrations.DataCopy
open DeclarativeMigrations.Types
open Mig.HotMigrationPrimitives

module internal HotMigrationCopy =
  let extractValues (row: Map<string, Expr>) (columns: string list) : Result<Expr list, string> =
    columns
    |> foldResults
      (fun values columnName ->
        match row.TryFind columnName with
        | Some value -> Ok(values @ [ value ])
        | None -> Error $"Missing column '{columnName}' in source row.")
      []

  let persistIdMapping
    (tx: SqliteTransaction)
    (tableName: string)
    (sourceIdentity: Expr list)
    (targetIdentity: Expr list)
    : Task<Result<unit, SqliteException>> =
    task {
      match sourceIdentity, targetIdentity with
      | [ sourceExpr ], [ targetExpr ] ->
        match exprToInt64 sourceExpr, exprToInt64 targetExpr with
        | Some oldId, Some newId ->
          try
            use cmd =
              createCommand
                tx.Connection
                (Some tx)
                "INSERT OR REPLACE INTO _id_mapping(table_name, old_id, new_id) VALUES (@table_name, @old_id, @new_id)"

            cmd.Parameters.AddWithValue("@table_name", tableName) |> ignore
            cmd.Parameters.AddWithValue("@old_id", oldId) |> ignore
            cmd.Parameters.AddWithValue("@new_id", newId) |> ignore
            let! _ = cmd.ExecuteNonQueryAsync()
            return Ok()
          with :? SqliteException as ex ->
            return Error ex
        | _ -> return Ok()
      | _ -> return Ok()
    }

  let insertProjectedRow
    (tx: SqliteTransaction)
    (tableName: string)
    (insertColumns: string list)
    (insertValues: Expr list)
    : Task<Result<unit, SqliteException>> =
    task {
      try
        if insertColumns.IsEmpty then
          use cmd =
            createCommand tx.Connection (Some tx) $"INSERT INTO {quoteIdentifier tableName} DEFAULT VALUES"

          let! _ = cmd.ExecuteNonQueryAsync()
          return Ok()
        else
          let columnList = insertColumns |> List.map quoteIdentifier |> String.concat ", "

          let parameterList =
            insertColumns |> List.mapi (fun i _ -> $"@p{i}") |> String.concat ", "

          use cmd =
            createCommand
              tx.Connection
              (Some tx)
              $"INSERT INTO {quoteIdentifier tableName} ({columnList}) VALUES ({parameterList})"

          insertValues
          |> List.iteri (fun i value -> cmd.Parameters.AddWithValue($"@p{i}", exprToDbValue value) |> ignore)

          let! _ = cmd.ExecuteNonQueryAsync()
          return Ok()
      with :? SqliteException as ex ->
        return Error ex
    }

  let getGeneratedIdentity (tx: SqliteTransaction) (identity: TableIdentity option) : Task<Expr list option> =
    task {
      match identity with
      | Some identityInfo when identityInfo.targetAutoincrementColumn.IsSome ->
        use idCmd = createCommand tx.Connection (Some tx) "SELECT last_insert_rowid()"
        let! idObj = idCmd.ExecuteScalarAsync()
        let idValue = Convert.ToInt64(idObj, CultureInfo.InvariantCulture)

        let idExpr =
          if idValue >= int64 Int32.MinValue && idValue <= int64 Int32.MaxValue then
            Integer(int idValue)
          else
            Value(idValue.ToString CultureInfo.InvariantCulture)

        return Some [ idExpr ]
      | _ -> return None
    }

  let readSourceRow (reader: SqliteDataReader) (step: TableCopyStep) : Map<string, Expr> =
    step.sourceTableDef.columns
    |> List.mapi (fun index column ->
      let value =
        if reader.IsDBNull index then
          Value "NULL"
        else
          dbValueToExpr (reader.GetValue index)

      column.name, value)
    |> Map.ofList

  let syncCopiedIdentityMapping
    (tx: SqliteTransaction)
    (step: TableCopyStep)
    (sourceRow: Map<string, Expr>)
    (mappings: IdMappingStore)
    (persistToTable: bool)
    : Task<Result<unit, SqliteException>> =
    task {
      if not persistToTable then
        return Ok()
      else
        match step.identity with
        | None -> return Ok()
        | Some identity ->
          match extractValues sourceRow identity.sourceKeyColumns with
          | Error message -> return Error(toSqliteError message)
          | Ok sourceIdentity ->
            match lookupMappedIdentity step.mapping.targetTable sourceIdentity mappings with
            | Error message -> return Error(toSqliteError message)
            | Ok targetIdentity -> return! persistIdMapping tx step.mapping.targetTable sourceIdentity targetIdentity
    }

  let copyTableRows
    (oldConnection: SqliteConnection)
    (newTx: SqliteTransaction)
    (step: TableCopyStep)
    (initialMappings: IdMappingStore)
    (persistIdMappings: bool)
    : Task<Result<IdMappingStore * int64, SqliteException>> =
    task {
      try
        let sourceColumns = step.sourceTableDef.columns |> List.map _.name
        let selectColumns = sourceColumns |> List.map quoteIdentifier |> String.concat ", "

        use selectCmd =
          createCommand oldConnection None $"SELECT {selectColumns} FROM {quoteIdentifier step.mapping.sourceTable}"

        use! reader = selectCmd.ExecuteReaderAsync()

        let mutable mappings = initialMappings
        let mutable copiedRows = 0L
        let mutable keepReading = true

        let isOk =
          function
          | Ok _ -> true
          | Error _ -> false

        let mutable result: Result<IdMappingStore * int64, SqliteException> =
          Ok(mappings, copiedRows)

        while keepReading && isOk result do
          let! hasRow = reader.ReadAsync()

          if hasRow then
            let sourceRow = readSourceRow reader step

            match projectRowForInsert step sourceRow mappings with
            | Error message ->
              result <-
                Error(
                  toSqliteError
                    $"Bulk copy projection failed for source table '{step.mapping.sourceTable}' -> target '{step.mapping.targetTable}': {message}"
                )
            | Ok(targetRow, insertColumns, insertValues) ->
              let! insertResult = insertProjectedRow newTx step.mapping.targetTable insertColumns insertValues

              match insertResult with
              | Error ex -> result <- Error ex
              | Ok() ->
                let! generatedIdentity = getGeneratedIdentity newTx step.identity

                match recordIdMapping step sourceRow targetRow generatedIdentity mappings with
                | Error message ->
                  result <-
                    Error(
                      toSqliteError
                        $"Bulk copy ID mapping failed for source table '{step.mapping.sourceTable}' -> target '{step.mapping.targetTable}': {message}"
                    )
                | Ok updatedMappings ->
                  let! persistResult = syncCopiedIdentityMapping newTx step sourceRow updatedMappings persistIdMappings

                  match persistResult with
                  | Error ex -> result <- Error ex
                  | Ok() ->
                    mappings <- updatedMappings
                    copiedRows <- copiedRows + 1L
                    result <- Ok(mappings, copiedRows)
          else
            keepReading <- false

        return result
      with :? SqliteException as ex ->
        return Error ex
    }

  let executeBulkCopy
    (oldConnection: SqliteConnection)
    (newConnection: SqliteConnection)
    (plan: BulkCopyPlan)
    (persistIdMappings: bool)
    : Task<Result<IdMappingStore * int64, SqliteException>> =
    task {
      try
        use tx = newConnection.BeginTransaction()
        let mutable mappings = emptyIdMappings
        let mutable totalRows = 0L
        let mutable stepIndex = 0

        let isOk =
          function
          | Ok _ -> true
          | Error _ -> false

        let mutable result: Result<IdMappingStore * int64, SqliteException> =
          Ok(mappings, totalRows)

        while stepIndex < plan.steps.Length && isOk result do
          let step = plan.steps[stepIndex]
          let! tableResult = copyTableRows oldConnection tx step mappings persistIdMappings

          match tableResult with
          | Error ex -> result <- Error ex
          | Ok(updatedMappings, copiedRows) ->
            mappings <- updatedMappings
            totalRows <- totalRows + copiedRows
            stepIndex <- stepIndex + 1
            result <- Ok(mappings, totalRows)

        match result with
        | Ok _ ->
          tx.Commit()
          return result
        | Error _ ->
          tx.Rollback()
          return result
      with :? SqliteException as ex ->
        return Error ex
    }
