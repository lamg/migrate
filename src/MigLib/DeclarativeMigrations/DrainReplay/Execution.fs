namespace Mig.DeclarativeMigrations

open System
open System.Globalization
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Mig.DeclarativeMigrations.DataCopy
open Mig.DeclarativeMigrations.Types
open DrainReplayTypes
open DrainReplayParsing
open DrainReplayState

module internal DrainReplayExecution =
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

  let private executeUpdateCommand
    (tx: SqliteTransaction)
    (targetTable: string)
    (setColumns: string list)
    (setValues: Expr list)
    (targetKeyColumns: string list)
    (targetIdentity: Expr list)
    (idMappings: IdMappingStore)
    : Task<Result<IdMappingStore, SqliteException>> =
    task {
      try
        let setClause =
          setColumns
          |> List.mapi (fun i columnName -> $"{columnName} = @s{i}")
          |> String.concat ", "

        let whereClause =
          targetKeyColumns
          |> List.mapi (fun i columnName -> $"{columnName} = @w{i}")
          |> String.concat " AND "

        use cmd =
          new SqliteCommand($"UPDATE {targetTable} SET {setClause} WHERE {whereClause}", tx.Connection, tx)

        setValues
        |> List.iteri (fun i value -> cmd.Parameters.AddWithValue($"@s{i}", exprToDbValue value) |> ignore)

        targetIdentity
        |> List.iteri (fun i value -> cmd.Parameters.AddWithValue($"@w{i}", exprToDbValue value) |> ignore)

        let! _ = cmd.ExecuteNonQueryAsync()
        return Ok idMappings
      with :? SqliteException as ex ->
        return Error ex
    }

  let private executeUpdate
    (entry: MigrationLogEntry)
    (tx: SqliteTransaction)
    (step: TableCopyStep)
    (idMappings: IdMappingStore)
    : Task<Result<IdMappingStore, SqliteException>> =
    match step.identity with
    | None ->
      Task.FromResult(
        Error(
          replayOperationError entry $"Table '{step.mapping.targetTable}' has no identity mapping for update replay."
        )
      )
    | Some identity ->
      match projectRowForInsert step entry.rowData idMappings with
      | Error message -> Task.FromResult(Error(replayOperationError entry message))
      | Ok(targetRow, _, _) ->
        match extractValues entry.rowData identity.sourceKeyColumns with
        | Error message -> Task.FromResult(Error(replayOperationError entry message))
        | Ok sourceIdentity ->
          match lookupMappedIdentity step.mapping.targetTable sourceIdentity idMappings with
          | Error message -> Task.FromResult(Error(replayOperationError entry message))
          | Ok targetIdentity ->
            let pkSet = identity.targetKeyColumns |> Set.ofList

            let setColumns =
              step.targetTableDef.columns
              |> List.map _.name
              |> List.filter (fun name -> not (pkSet.Contains name))

            match extractValues targetRow setColumns with
            | Error message -> Task.FromResult(Error(replayOperationError entry message))
            | Ok setValues ->
              if setColumns.IsEmpty then
                Task.FromResult(Ok idMappings)
              else
                executeUpdateCommand
                  tx
                  step.mapping.targetTable
                  setColumns
                  setValues
                  identity.targetKeyColumns
                  targetIdentity
                  idMappings

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

  let replayDrainEntries
    (connection: SqliteConnection)
    (bulkPlan: BulkCopyPlan)
    (entries: MigrationLogEntry list)
    (initialIdMappings: IdMappingStore)
    : Task<Result<IdMappingStore, SqliteException>> =
    task {
      let groups = groupEntriesByTransaction entries

      let isOk =
        function
        | Ok _ -> true
        | Error _ -> false

      let mutable mappings = initialIdMappings
      let mutable result: Result<IdMappingStore, SqliteException> = Ok mappings
      let mutable index = 0

      while index < groups.Length && isOk result do
        let _, groupEntries = groups[index]
        use tx = connection.BeginTransaction()
        let mutable groupResult: Result<IdMappingStore, SqliteException> = Ok mappings
        let mutable opIndex = 0

        while opIndex < groupEntries.Length && isOk groupResult do
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
