namespace Mig

open System
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open DeclarativeMigrations.SchemaDiff
open DeclarativeMigrations.Types
open Mig.HotMigrationPrimitives
open Mig.HotMigrationMetadata

module internal HotMigrationSchemaBootstrap =
  let renderForeignKeyTail (fk: ForeignKey) =
    let refCols =
      if fk.refColumns.IsEmpty then
        ""
      else
        let refs = fk.refColumns |> List.map quoteIdentifier |> String.concat ", "
        $"({refs})"

    let onDelete =
      match fk.onDelete with
      | Some action -> $" ON DELETE {fkActionSql action}"
      | None -> ""

    let onUpdate =
      match fk.onUpdate with
      | Some action -> $" ON UPDATE {fkActionSql action}"
      | None -> ""

    $"REFERENCES {quoteIdentifier fk.refTable}{refCols}{onDelete}{onUpdate}"

  let renderColumnConstraint (constraintDef: ColumnConstraint) : string option =
    match constraintDef with
    | PrimaryKey pk when pk.columns.IsEmpty ->
      if pk.isAutoincrement then
        Some "PRIMARY KEY AUTOINCREMENT"
      else
        Some "PRIMARY KEY"
    | NotNull -> Some "NOT NULL"
    | Unique columns when columns.IsEmpty -> Some "UNIQUE"
    | Default expr -> Some $"DEFAULT {exprToSql expr}"
    | Check tokens ->
      let body = String.concat " " tokens
      Some $"CHECK ({body})"
    | ForeignKey fk when fk.columns.IsEmpty -> Some(renderForeignKeyTail fk)
    | Autoincrement -> Some "AUTOINCREMENT"
    | _ -> None

  let renderTableConstraint (constraintDef: ColumnConstraint) : string option =
    match constraintDef with
    | PrimaryKey pk when not pk.columns.IsEmpty ->
      let cols = pk.columns |> List.map quoteIdentifier |> String.concat ", "

      if pk.isAutoincrement && pk.columns.Length = 1 then
        Some $"PRIMARY KEY ({cols}) AUTOINCREMENT"
      else
        Some $"PRIMARY KEY ({cols})"
    | Unique columns when not columns.IsEmpty ->
      let cols = columns |> List.map quoteIdentifier |> String.concat ", "
      Some $"UNIQUE ({cols})"
    | ForeignKey fk when not fk.columns.IsEmpty ->
      let cols = fk.columns |> List.map quoteIdentifier |> String.concat ", "
      Some $"FOREIGN KEY ({cols}) {renderForeignKeyTail fk}"
    | Check tokens ->
      let body = String.concat " " tokens
      Some $"CHECK ({body})"
    | _ -> None

  let createTableSql (table: CreateTable) : string =
    let columnDefs =
      table.columns
      |> List.map (fun column ->
        let constraints =
          column.constraints |> List.choose renderColumnConstraint |> String.concat " "

        if String.IsNullOrWhiteSpace constraints then
          $"{quoteIdentifier column.name} {sqlTypeToSql column.columnType}"
        else
          $"{quoteIdentifier column.name} {sqlTypeToSql column.columnType} {constraints}")

    let tableConstraints = table.constraints |> List.choose renderTableConstraint
    let body = columnDefs @ tableConstraints |> String.concat ",\n  "
    $"CREATE TABLE {quoteIdentifier table.name} (\n  {body}\n);"

  let createIndexSql (index: CreateIndex) : string =
    let cols = index.columns |> List.map quoteIdentifier |> String.concat ", "
    $"CREATE INDEX {quoteIdentifier index.name} ON {quoteIdentifier index.table} ({cols});"

  let ensureOldRecordingTables (oldConnection: SqliteConnection) : Task<Result<unit, SqliteException>> =
    task {
      try
        use tx = oldConnection.BeginTransaction()

        use markerCmd =
          createCommand
            oldConnection
            (Some tx)
            "CREATE TABLE IF NOT EXISTS _migration_marker(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"

        let! _ = markerCmd.ExecuteNonQueryAsync()
        do! upsertStatusRow oldConnection (Some tx) "_migration_marker" "recording"

        use dropLogCmd =
          createCommand oldConnection (Some tx) "DROP TABLE IF EXISTS _migration_log;"

        let! _ = dropLogCmd.ExecuteNonQueryAsync()

        use createLogCmd =
          createCommand
            oldConnection
            (Some tx)
            "CREATE TABLE _migration_log(id INTEGER PRIMARY KEY AUTOINCREMENT, txn_id INTEGER NOT NULL, ordering INTEGER NOT NULL, operation TEXT NOT NULL, table_name TEXT NOT NULL, row_data TEXT NOT NULL);"

        let! _ = createLogCmd.ExecuteNonQueryAsync()
        tx.Commit()
        return Ok()
      with
      | :? SqliteException as ex -> return Error ex
      | ex -> return Error(toSqliteError ex.Message)
    }

  let setOldMarkerToDraining (oldConnection: SqliteConnection) : Task<Result<unit, SqliteException>> =
    task {
      try
        use tx = oldConnection.BeginTransaction()
        let! hasMarker = tableExists oldConnection (Some tx) "_migration_marker"
        let! hasLog = tableExists oldConnection (Some tx) "_migration_log"

        if not hasMarker then
          tx.Rollback()
          return Error(toSqliteError "_migration_marker table is missing in the old database. Run `mig migrate` first.")
        elif not hasLog then
          tx.Rollback()
          return Error(toSqliteError "_migration_log table is missing in the old database. Run `mig migrate` first.")
        else
          do! upsertStatusRow oldConnection (Some tx) "_migration_marker" "draining"
          tx.Commit()
          return Ok()
      with
      | :? SqliteException as ex -> return Error ex
      | ex -> return Error(toSqliteError ex.Message)
    }

  let createSchemaIdentityTable (connection: SqliteConnection) (tx: SqliteTransaction) : Task<unit> =
    task {
      use schemaIdentityCmd =
        createCommand
          connection
          (Some tx)
          "CREATE TABLE IF NOT EXISTS _schema_identity(id INTEGER PRIMARY KEY CHECK (id = 0), schema_hash TEXT NOT NULL, schema_commit TEXT, created_utc TEXT NOT NULL);"

      let! _ = schemaIdentityCmd.ExecuteNonQueryAsync()
      return ()
    }

  let createNewMigrationTables
    (newConnection: SqliteConnection)
    (tx: SqliteTransaction)
    (schemaHash: string)
    (schemaCommit: string option)
    : Task<unit> =
    task {
      use idMapCmd =
        createCommand
          newConnection
          (Some tx)
          "CREATE TABLE IF NOT EXISTS _id_mapping(table_name TEXT NOT NULL, old_id INTEGER NOT NULL, new_id INTEGER NOT NULL, PRIMARY KEY(table_name, old_id));"

      let! _ = idMapCmd.ExecuteNonQueryAsync()

      use statusCmd =
        createCommand
          newConnection
          (Some tx)
          "CREATE TABLE IF NOT EXISTS _migration_status(id INTEGER PRIMARY KEY CHECK (id = 0), status TEXT NOT NULL);"

      let! _ = statusCmd.ExecuteNonQueryAsync()

      do! createSchemaIdentityTable newConnection tx
      do! upsertStatusRow newConnection (Some tx) "_migration_status" "migrating"
      do! upsertSchemaIdentity newConnection (Some tx) schemaHash schemaCommit
      let! _ = ensureMigrationProgressRow newConnection (Some tx)
      do! upsertMigrationProgress newConnection (Some tx) 0L false
    }

  let createSchemaObjects (connection: SqliteConnection) (tx: SqliteTransaction) (targetSchema: SqlFile) : Task<unit> =
    task {
      for table in targetSchema.tables do
        use createTableCmd = createCommand connection (Some tx) (createTableSql table)
        let! _ = createTableCmd.ExecuteNonQueryAsync()
        ()

      for index in targetSchema.indexes do
        use createIndexCmd = createCommand connection (Some tx) (createIndexSql index)
        let! _ = createIndexCmd.ExecuteNonQueryAsync()
        ()

      for view in targetSchema.views do
        for sql in view.sqlTokens do
          use createViewCmd = createCommand connection (Some tx) sql
          let! _ = createViewCmd.ExecuteNonQueryAsync()
          ()

      for trigger in targetSchema.triggers do
        for sql in trigger.sqlTokens do
          use createTriggerCmd = createCommand connection (Some tx) sql
          let! _ = createTriggerCmd.ExecuteNonQueryAsync()
          ()
    }

  let initializeNewDatabase
    (newConnection: SqliteConnection)
    (targetSchema: SqlFile)
    (schemaHash: string)
    (schemaCommit: string option)
    : Task<Result<unit, SqliteException>> =
    task {
      try
        use tx = newConnection.BeginTransaction()
        use fkOffCmd = createCommand newConnection (Some tx) "PRAGMA foreign_keys = OFF;"
        let! _ = fkOffCmd.ExecuteNonQueryAsync()

        do! createSchemaObjects newConnection tx targetSchema
        do! createNewMigrationTables newConnection tx schemaHash schemaCommit

        use fkOnCmd = createCommand newConnection (Some tx) "PRAGMA foreign_keys = ON;"
        let! _ = fkOnCmd.ExecuteNonQueryAsync()
        tx.Commit()
        return Ok()
      with
      | :? SqliteException as ex -> return Error ex
      | ex -> return Error(toSqliteError ex.Message)
    }

  let initializeOfflineDatabase
    (newConnection: SqliteConnection)
    (targetSchema: SqlFile)
    (schemaHash: string)
    (schemaCommit: string option)
    : Task<Result<unit, SqliteException>> =
    task {
      try
        use tx = newConnection.BeginTransaction()
        use fkOffCmd = createCommand newConnection (Some tx) "PRAGMA foreign_keys = OFF;"
        let! _ = fkOffCmd.ExecuteNonQueryAsync()

        do! createSchemaObjects newConnection tx targetSchema
        do! createSchemaIdentityTable newConnection tx
        do! upsertSchemaIdentity newConnection (Some tx) schemaHash schemaCommit

        use fkOnCmd = createCommand newConnection (Some tx) "PRAGMA foreign_keys = ON;"
        let! _ = fkOnCmd.ExecuteNonQueryAsync()
        tx.Commit()
        return Ok()
      with
      | :? SqliteException as ex -> return Error ex
      | ex -> return Error(toSqliteError ex.Message)
    }

  let initializeDatabaseFromSchemaOnly
    (newConnection: SqliteConnection)
    (targetSchema: SqlFile)
    : Task<Result<int64, SqliteException>> =
    task {
      try
        use tx = newConnection.BeginTransaction()
        use fkOffCmd = createCommand newConnection (Some tx) "PRAGMA foreign_keys = OFF;"
        let! _ = fkOffCmd.ExecuteNonQueryAsync()

        do! createSchemaObjects newConnection tx targetSchema
        use fkOnCmd = createCommand newConnection (Some tx) "PRAGMA foreign_keys = ON;"
        let! _ = fkOnCmd.ExecuteNonQueryAsync()

        let validationError =
          targetSchema.inserts
          |> List.tryPick (fun insert ->
            if insert.columns.IsEmpty then
              Some $"Seed insert for table '{insert.table}' has no columns. Use explicit fields in seed records."
            else
              insert.values
              |> List.tryPick (fun rowValues ->
                if rowValues.Length = insert.columns.Length then
                  None
                else
                  Some
                    $"Seed insert for table '{insert.table}' has {rowValues.Length} value(s) but {insert.columns.Length} column(s)."))

        match validationError with
        | Some message -> raise (toSqliteError message)
        | None -> ()

        let mutable seededRows = 0L

        for insert in targetSchema.inserts do
          let escapedColumns =
            insert.columns |> List.map quoteIdentifier |> String.concat ", "

          let parameterNames =
            insert.columns |> List.mapi (fun i _ -> $"@p{i}") |> String.concat ", "

          let insertSql =
            $"INSERT INTO {quoteIdentifier insert.table} ({escapedColumns}) VALUES ({parameterNames})"

          for rowValues in insert.values do
            use insertCmd = createCommand newConnection (Some tx) insertSql

            rowValues
            |> List.iteri (fun i value -> insertCmd.Parameters.AddWithValue($"@p{i}", exprToDbValue value) |> ignore)

            let! _ = insertCmd.ExecuteNonQueryAsync()
            seededRows <- seededRows + 1L

        tx.Commit()
        return Ok seededRows
      with
      | :? SqliteException as ex -> return Error ex
      | ex -> return Error(toSqliteError ex.Message)
    }
