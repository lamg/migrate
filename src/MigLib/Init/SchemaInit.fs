module internal MigLib.Init.SchemaInit

open System
open System.Threading.Tasks

open Microsoft.Data.Sqlite

open MigLib.Schema.Types
open MigLib.Schema.Sql
open MigLib.Types
open MigLib.TaskResult
open MigLib.Sqlite
open MigLib.Codegen

let private exprToSql =
  function
  | String value ->
    let escaped = value.Replace("'", "''")
    $"'{escaped}'"
  | Integer value -> string value
  | Real value -> value.ToString("R", Globalization.CultureInfo.InvariantCulture)
  | Value value -> value

let private exprToDbValue =
  function
  | String value -> box value
  | Integer value -> box value
  | Real value -> box value
  | Value value -> box value

let private renderForeignKeyTail (fk: ForeignKey) =
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

let private renderColumnConstraint =
  function
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

let private renderTableConstraint =
  function
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

let private createTableSql (table: CreateTable) =
  let columnDefs =
    table.columns
    |> List.map (fun column ->
      let constraints =
        column.constraints |> List.choose renderColumnConstraint |> String.concat " "

      if String.IsNullOrWhiteSpace constraints then
        $"{quoteIdentifier column.name} {sqlTypeStorageName column.columnType}"
      else
        $"{quoteIdentifier column.name} {sqlTypeStorageName column.columnType} {constraints}")

  let tableConstraints = table.constraints |> List.choose renderTableConstraint
  let body = columnDefs @ tableConstraints |> String.concat ",\n  "
  $"CREATE TABLE {quoteIdentifier table.name} (\n  {body}\n);"

let private createIndexSql (index: CreateIndex) =
  let cols = index.columns |> List.map quoteIdentifier |> String.concat ", "
  $"CREATE INDEX {quoteIdentifier index.name} ON {quoteIdentifier index.table} ({cols});"

let private createSchemaObjects (connection: SqliteConnection) (tx: SqliteTransaction) (targetSchema: SqlFile) =
  task {
    for table in targetSchema.tables do
      use createTableCmd = createCommand connection tx (createTableSql table)
      let! _ = createTableCmd.ExecuteNonQueryAsync()
      ()

    for index in targetSchema.indexes do
      use createIndexCmd = createCommand connection tx (createIndexSql index)
      let! _ = createIndexCmd.ExecuteNonQueryAsync()
      ()

    let orderedViews =
      match ViewDependencies.orderViews targetSchema.tables targetSchema.views with
      | Ok views -> views
      | Error error -> invalidOp error

    for view in orderedViews do
      use createViewCmd = createCommand connection tx view.sql
      let! _ = createViewCmd.ExecuteNonQueryAsync()
      ()

    for trigger in targetSchema.triggers do
      use createTriggerCmd = createCommand connection tx trigger.sql
      let! _ = createTriggerCmd.ExecuteNonQueryAsync()
      ()
  }

let private validateSeedInserts (targetSchema: SqlFile) =
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

type private SeedConflictHandling =
  | Strict
  | IgnoreConflicts

let private seedInsertPrefix =
  function
  | Strict -> "INSERT INTO"
  | IgnoreConflicts -> "INSERT OR IGNORE INTO"

let private applySeedInserts
  conflictHandling
  (connection: SqliteConnection)
  (tx: SqliteTransaction)
  (targetSchema: SqlFile)
  =
  task {
    let mutable seededRows = 0L

    for insert in targetSchema.inserts do
      let escapedColumns =
        insert.columns |> List.map quoteIdentifier |> String.concat ", "

      let parameterNames =
        insert.columns |> List.mapi (fun i _ -> $"@p{i}") |> String.concat ", "

      let insertSql =
        $"{seedInsertPrefix conflictHandling} {quoteIdentifier insert.table} ({escapedColumns}) VALUES ({parameterNames})"

      for rowValues in insert.values do
        use insertCmd = createCommand connection tx insertSql

        rowValues
        |> List.iteri (fun i value -> insertCmd.Parameters.AddWithValue($"@p{i}", exprToDbValue value) |> ignore)

        let! rows = insertCmd.ExecuteNonQueryAsync()
        seededRows <- seededRows + int64 rows

    return seededRows
  }

let initializeDatabaseFromSchemaOnly
  (newConnection: SqliteConnection)
  (targetSchema: SqlFile)
  : Task<Result<unit, MigError>> =
  task {
    try
      use tx = newConnection.BeginTransaction()
      use fkOffCmd = createCommand newConnection tx "PRAGMA foreign_keys = OFF;"
      let! _ = fkOffCmd.ExecuteNonQueryAsync()

      do! createSchemaObjects newConnection tx targetSchema
      do tx.Commit()
      return Ok()
    with
    | :? SqliteException as ex -> return Error(MigError.Sqlite ex)
    | ex -> return Error(MigError.Other ex)
  }

let private seedDatabaseWith
  conflictHandling
  (conn: SqliteConnection)
  (targetSchema: SqlFile)
  : Task<Result<int64, MigError>> =
  task {
    try
      use tx = conn.BeginTransaction()

      match validateSeedInserts targetSchema with
      | Some message ->
        tx.Rollback()
        return Error(MigError.Regular message)
      | None ->
        let! seededRows = applySeedInserts conflictHandling conn tx targetSchema

        use fkOnCmd = createCommand conn tx "PRAGMA foreign_keys = ON;"
        let! _ = fkOnCmd.ExecuteNonQueryAsync()

        tx.Commit()
        return Ok seededRows
    with
    | :? SqliteException as ex -> return Error(MigError.Sqlite ex)
    | ex -> return Error(MigError.Other ex)
  }

let seedDatabase (conn: SqliteConnection) (targetSchema: SqlFile) : Task<Result<int64, MigError>> =
  seedDatabaseWith Strict conn targetSchema

let seedDatabaseIgnoringConflicts (conn: SqliteConnection) (targetSchema: SqlFile) : Task<Result<int64, MigError>> =
  seedDatabaseWith IgnoreConflicts conn targetSchema
