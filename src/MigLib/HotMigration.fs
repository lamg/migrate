module MigLib.HotMigration

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open MigLib.DeclarativeMigrations.DataCopy
open MigLib.DeclarativeMigrations.DrainReplay
open MigLib.DeclarativeMigrations.SchemaDiff
open MigLib.DeclarativeMigrations.Types
open MigLib.SchemaScript

type MigrationStatusReport =
  { oldMarkerStatus: string option
    migrationLogEntries: int64
    pendingReplayEntries: int64 option
    idMappingEntries: int64 option
    newMigrationStatus: string option }

type CutoverResult =
  { previousStatus: string
    idMappingDropped: bool }

type MigrateResult =
  { newDbPath: string
    copiedTables: int
    copiedRows: int64 }

type DrainResult =
  { replayedEntries: int
    remainingEntries: int64 }

type private TableInfoRow =
  { name: string
    declaredType: string
    isNotNull: bool
    defaultSql: string option
    primaryKeyOrder: int }

type private ForeignKeyRow =
  { id: int
    seq: int
    refTable: string
    fromColumn: string
    toColumn: string option
    onUpdate: string
    onDelete: string }

let private migrationTables =
  set [ "_migration_marker"; "_migration_log"; "_migration_status"; "_id_mapping" ]

let private toSqliteError (message: string) = SqliteException(message, 0)

let private createCommand
  (connection: SqliteConnection)
  (transaction: SqliteTransaction option)
  (sql: string)
  : SqliteCommand =
  match transaction with
  | Some tx -> new SqliteCommand(sql, connection, tx)
  | None -> new SqliteCommand(sql, connection)

let private quoteIdentifier (name: string) =
  let escaped = name.Replace("\"", "\"\"")
  $"\"{escaped}\""

let private exprToInt64 (expr: Expr) : int64 option =
  match expr with
  | Integer value -> Some(int64 value)
  | Value value ->
    match Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) with
    | true, int64Value -> Some int64Value
    | _ -> None
  | _ -> None

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

let private dbValueToExpr (value: obj) : Expr =
  if isNull value || Object.ReferenceEquals(value, DBNull.Value) then
    Value "NULL"
  else
    match value with
    | :? string as text -> String text
    | :? int8 as number -> Integer(int number)
    | :? int16 as number -> Integer(int number)
    | :? int as number -> Integer number
    | :? int64 as number ->
      if number >= int64 Int32.MinValue && number <= int64 Int32.MaxValue then
        Integer(int number)
      else
        Value(number.ToString(CultureInfo.InvariantCulture))
    | :? uint8 as number -> Integer(int number)
    | :? uint16 as number ->
      if number <= uint16 Int32.MaxValue then
        Integer(int number)
      else
        Value(number.ToString(CultureInfo.InvariantCulture))
    | :? uint32 as number ->
      if number <= uint32 Int32.MaxValue then
        Integer(int number)
      else
        Value(number.ToString(CultureInfo.InvariantCulture))
    | :? uint64 as number ->
      if number <= uint64 Int32.MaxValue then
        Integer(int number)
      else
        Value(number.ToString(CultureInfo.InvariantCulture))
    | :? float32 as number -> Real(float number)
    | :? float as number -> Real number
    | :? decimal as number -> Real(float number)
    | :? bool as flag -> Integer(if flag then 1 else 0)
    | :? DateTime as dt -> String(dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
    | :? (byte[]) as bytes -> Value(Convert.ToBase64String bytes)
    | _ -> Value(value.ToString())

let private parseSqlType (declaredType: string) : SqlType =
  let upper =
    if String.IsNullOrWhiteSpace declaredType then
      ""
    else
      declaredType.ToUpperInvariant()

  if upper.Contains "INT" then
    SqlInteger
  elif upper.Contains "REAL" || upper.Contains "FLOA" || upper.Contains "DOUB" then
    SqlReal
  elif upper.Contains "TIMESTAMP" || upper.Contains "DATE" || upper.Contains "TIME" then
    SqlTimestamp
  elif upper.Contains "CHAR" || upper.Contains "CLOB" || upper.Contains "TEXT" then
    SqlText
  elif upper.Contains "BLOB" then
    SqlFlexible
  else
    SqlFlexible

let private sqlTypeToSql (sqlType: SqlType) : string =
  match sqlType with
  | SqlInteger -> "INTEGER"
  | SqlText -> "TEXT"
  | SqlReal -> "REAL"
  | SqlTimestamp -> "TEXT"
  | SqlString -> "TEXT"
  | SqlFlexible -> "TEXT"

let private parseDefaultExpr (defaultSql: string) : Expr =
  let trimmed = defaultSql.Trim()

  match Int32.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture) with
  | true, number -> Integer number
  | _ ->
    match Double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture) with
    | true, number -> Real number
    | _ when trimmed.StartsWith "'" && trimmed.EndsWith "'" && trimmed.Length >= 2 ->
      let content = trimmed.[1 .. trimmed.Length - 2].Replace("''", "'")
      String content
    | _ -> Value trimmed

let private parseFkAction (rawAction: string) : FkAction option =
  match rawAction.Trim().ToUpperInvariant() with
  | "CASCADE" -> Some Cascade
  | "RESTRICT" -> Some Restrict
  | "NO ACTION" -> Some NoAction
  | "SET NULL" -> Some SetNull
  | "SET DEFAULT" -> Some SetDefault
  | _ -> None

let private fkActionSql (action: FkAction) : string =
  match action with
  | Cascade -> "CASCADE"
  | Restrict -> "RESTRICT"
  | NoAction -> "NO ACTION"
  | SetNull -> "SET NULL"
  | SetDefault -> "SET DEFAULT"

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
    use cmd =
      createCommand connection transaction $"SELECT COUNT(*) FROM {quoteIdentifier tableName}"

    let! resultObj = cmd.ExecuteScalarAsync()
    return Convert.ToInt64(resultObj, CultureInfo.InvariantCulture)
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
        createCommand connection transaction $"SELECT status FROM {quoteIdentifier tableName} WHERE id = 0 LIMIT 1"

      let! statusObj = cmd.ExecuteScalarAsync()

      if isNull statusObj then
        return None
      else
        return Some(string statusObj)
  }

let private upsertStatusRow
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

let private renderColumnConstraint (constraintDef: ColumnConstraint) : string option =
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

let private renderTableConstraint (constraintDef: ColumnConstraint) : string option =
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

let private createTableSql (table: CreateTable) : string =
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

let private createIndexSql (index: CreateIndex) : string =
  let cols = index.columns |> List.map quoteIdentifier |> String.concat ", "
  $"CREATE INDEX {quoteIdentifier index.name} ON {quoteIdentifier index.table} ({cols});"

let private ensureOldRecordingTables (oldConnection: SqliteConnection) : Task<Result<unit, SqliteException>> =
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

let private setOldMarkerToDraining (oldConnection: SqliteConnection) : Task<Result<unit, SqliteException>> =
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

let private createNewMigrationTables (newConnection: SqliteConnection) (tx: SqliteTransaction) : Task<unit> =
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

    do! upsertStatusRow newConnection (Some tx) "_migration_status" "migrating"
  }

let private initializeNewDatabase
  (newConnection: SqliteConnection)
  (targetSchema: SqlFile)
  : Task<Result<unit, SqliteException>> =
  task {
    try
      use tx = newConnection.BeginTransaction()
      use fkOffCmd = createCommand newConnection (Some tx) "PRAGMA foreign_keys = OFF;"
      let! _ = fkOffCmd.ExecuteNonQueryAsync()

      for table in targetSchema.tables do
        use createTableCmd = createCommand newConnection (Some tx) (createTableSql table)
        let! _ = createTableCmd.ExecuteNonQueryAsync()
        ()

      for index in targetSchema.indexes do
        use createIndexCmd = createCommand newConnection (Some tx) (createIndexSql index)
        let! _ = createIndexCmd.ExecuteNonQueryAsync()
        ()

      for view in targetSchema.views do
        for sql in view.sqlTokens do
          use createViewCmd = createCommand newConnection (Some tx) sql
          let! _ = createViewCmd.ExecuteNonQueryAsync()
          ()

      for trigger in targetSchema.triggers do
        for sql in trigger.sqlTokens do
          use createTriggerCmd = createCommand newConnection (Some tx) sql
          let! _ = createTriggerCmd.ExecuteNonQueryAsync()
          ()

      do! createNewMigrationTables newConnection tx

      use fkOnCmd = createCommand newConnection (Some tx) "PRAGMA foreign_keys = ON;"
      let! _ = fkOnCmd.ExecuteNonQueryAsync()
      tx.Commit()
      return Ok()
    with
    | :? SqliteException as ex -> return Error ex
    | ex -> return Error(toSqliteError ex.Message)
  }

let private readTableList
  (connection: SqliteConnection)
  (excludedTables: Set<string>)
  : Task<Result<(string * string option) list, SqliteException>> =
  task {
    try
      use cmd =
        createCommand
          connection
          None
          "SELECT name, sql FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"

      use! reader = cmd.ExecuteReaderAsync()
      let tables = ResizeArray<string * string option>()
      let mutable keepReading = true

      while keepReading do
        let! hasRow = reader.ReadAsync()

        if hasRow then
          let tableName = reader.GetString 0

          if not (excludedTables.Contains tableName) then
            let sql = if reader.IsDBNull 1 then None else Some(reader.GetString 1)

            tables.Add(tableName, sql)
        else
          keepReading <- false

      return Ok(tables |> Seq.toList)
    with :? SqliteException as ex ->
      return Error ex
  }

let private readTableInfoRows
  (connection: SqliteConnection)
  (tableName: string)
  : Task<Result<TableInfoRow list, SqliteException>> =
  task {
    try
      use cmd =
        createCommand connection None $"PRAGMA table_info({quoteIdentifier tableName});"

      use! reader = cmd.ExecuteReaderAsync()
      let rows = ResizeArray<TableInfoRow>()
      let mutable keepReading = true

      while keepReading do
        let! hasRow = reader.ReadAsync()

        if hasRow then
          rows.Add
            { name = reader.GetString 1
              declaredType = if reader.IsDBNull 2 then "" else reader.GetString 2
              isNotNull = reader.GetInt32(3) = 1
              defaultSql = if reader.IsDBNull 4 then None else Some(reader.GetString 4)
              primaryKeyOrder = reader.GetInt32 5 }
        else
          keepReading <- false

      return Ok(rows |> Seq.toList)
    with :? SqliteException as ex ->
      return Error ex
  }

let private readForeignKeyRows
  (connection: SqliteConnection)
  (tableName: string)
  : Task<Result<ForeignKeyRow list, SqliteException>> =
  task {
    try
      use cmd =
        createCommand connection None $"PRAGMA foreign_key_list({quoteIdentifier tableName});"

      use! reader = cmd.ExecuteReaderAsync()
      let rows = ResizeArray<ForeignKeyRow>()
      let mutable keepReading = true

      while keepReading do
        let! hasRow = reader.ReadAsync()

        if hasRow then
          rows.Add
            { id = reader.GetInt32 0
              seq = reader.GetInt32 1
              refTable = reader.GetString 2
              fromColumn = reader.GetString 3
              toColumn = if reader.IsDBNull 4 then None else Some(reader.GetString 4)
              onUpdate = if reader.IsDBNull 5 then "" else reader.GetString 5
              onDelete = if reader.IsDBNull 6 then "" else reader.GetString 6 }
        else
          keepReading <- false

      return Ok(rows |> Seq.toList)
    with :? SqliteException as ex ->
      return Error ex
  }

let private addColumnConstraint
  (columnName: string)
  (constraintDef: ColumnConstraint)
  (columns: ColumnDef list)
  : ColumnDef list =
  columns
  |> List.map (fun column ->
    if column.name.Equals(columnName, StringComparison.OrdinalIgnoreCase) then
      { column with
          constraints = column.constraints @ [ constraintDef ] }
    else
      column)

let private buildForeignKeyConstraint (rows: ForeignKeyRow list) (columns: string list) : ColumnConstraint =
  let orderedRows = rows |> List.sortBy _.seq
  let head = orderedRows.Head

  let refColumns =
    orderedRows
    |> List.choose _.toColumn
    |> List.filter (String.IsNullOrWhiteSpace >> not)

  ForeignKey
    { columns = columns
      refTable = head.refTable
      refColumns = refColumns
      onDelete = parseFkAction head.onDelete
      onUpdate = parseFkAction head.onUpdate }

let private buildTableDefinition
  (tableName: string)
  (createSql: string option)
  (tableInfoRows: TableInfoRow list)
  (foreignKeyRows: ForeignKeyRow list)
  : CreateTable =
  let primaryKeys =
    tableInfoRows
    |> List.filter (fun row -> row.primaryKeyOrder > 0)
    |> List.sortBy _.primaryKeyOrder
    |> List.map _.name

  let hasAutoincrement =
    match createSql with
    | None -> false
    | Some sql -> sql.ToUpperInvariant().Contains "AUTOINCREMENT"

  let singlePrimaryKey =
    if primaryKeys.Length = 1 then
      Some primaryKeys.Head
    else
      None

  let mutable columns =
    tableInfoRows
    |> List.map (fun row ->
      let constraints = ResizeArray<ColumnConstraint>()

      match singlePrimaryKey with
      | Some pk when pk.Equals(row.name, StringComparison.OrdinalIgnoreCase) ->
        constraints.Add(
          PrimaryKey
            { constraintName = None
              columns = []
              isAutoincrement = hasAutoincrement }
        )
      | _ -> ()

      if row.isNotNull then
        constraints.Add NotNull

      match row.defaultSql with
      | Some defaultSql when not (String.IsNullOrWhiteSpace defaultSql) ->
        constraints.Add(Default(parseDefaultExpr defaultSql))
      | _ -> ()

      { name = row.name
        columnType = parseSqlType row.declaredType
        constraints = constraints |> Seq.toList })

  let fkGroups = foreignKeyRows |> List.groupBy _.id

  let tableConstraints =
    fkGroups
    |> List.choose (fun (_, rows) ->
      let orderedRows = rows |> List.sortBy _.seq

      if orderedRows.Length > 1 then
        let fkColumns = orderedRows |> List.map _.fromColumn
        Some(buildForeignKeyConstraint orderedRows fkColumns)
      else
        None)

  for _, rows in fkGroups do
    let orderedRows = rows |> List.sortBy _.seq

    if orderedRows.Length = 1 then
      let row = orderedRows.Head
      let constraintDef = buildForeignKeyConstraint orderedRows []
      columns <- addColumnConstraint row.fromColumn constraintDef columns

  let tablePrimaryKeyConstraint =
    if primaryKeys.Length > 1 then
      [ PrimaryKey
          { constraintName = None
            columns = primaryKeys
            isAutoincrement = false } ]
    else
      []

  { name = tableName
    columns = columns
    constraints = tablePrimaryKeyConstraint @ tableConstraints
    queryByAnnotations = []
    queryLikeAnnotations = []
    queryByOrCreateAnnotations = []
    insertOrIgnoreAnnotations = [] }

let private loadSchemaFromDatabase
  (connection: SqliteConnection)
  (excludedTables: Set<string>)
  : Task<Result<SqlFile, SqliteException>> =
  task {
    let! tableListResult = readTableList connection excludedTables

    match tableListResult with
    | Error ex -> return Error ex
    | Ok tableList ->
      let tables = ResizeArray<CreateTable>()
      let mutable index = 0
      let mutable result: Result<SqlFile, SqliteException> = Ok emptyFile

      while index < tableList.Length && result.IsOk do
        let tableName, createSql = tableList[index]
        let! tableInfoResult = readTableInfoRows connection tableName

        match tableInfoResult with
        | Error ex -> result <- Error ex
        | Ok tableInfoRows ->
          let! fkRowsResult = readForeignKeyRows connection tableName

          match fkRowsResult with
          | Error ex -> result <- Error ex
          | Ok fkRows ->
            let table = buildTableDefinition tableName createSql tableInfoRows fkRows
            tables.Add table
            index <- index + 1

      match result with
      | Error ex -> return Error ex
      | Ok _ ->
        return
          Ok
            { emptyFile with
                tables = tables |> Seq.toList }
  }

let private extractValues (row: Map<string, Expr>) (columns: string list) : Result<Expr list, string> =
  columns
  |> foldResults
    (fun values columnName ->
      match row.TryFind columnName with
      | Some value -> Ok(values @ [ value ])
      | None -> Error $"Missing column '{columnName}' in source row.")
    []

let private persistIdMapping
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

let private insertProjectedRow
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

let private getGeneratedIdentity (tx: SqliteTransaction) (identity: TableIdentity option) : Task<Expr list option> =
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
          Value(idValue.ToString(CultureInfo.InvariantCulture))

      return Some [ idExpr ]
    | _ -> return None
  }

let private readSourceRow (reader: SqliteDataReader) (step: TableCopyStep) : Map<string, Expr> =
  step.sourceTableDef.columns
  |> List.mapi (fun index column ->
    let value =
      if reader.IsDBNull index then
        Value "NULL"
      else
        dbValueToExpr (reader.GetValue index)

    column.name, value)
  |> Map.ofList

let private syncCopiedIdentityMapping
  (tx: SqliteTransaction)
  (step: TableCopyStep)
  (sourceRow: Map<string, Expr>)
  (mappings: IdMappingStore)
  : Task<Result<unit, SqliteException>> =
  task {
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

let private copyTableRows
  (oldConnection: SqliteConnection)
  (newTx: SqliteTransaction)
  (step: TableCopyStep)
  (initialMappings: IdMappingStore)
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

      let mutable result: Result<IdMappingStore * int64, SqliteException> =
        Ok(mappings, copiedRows)

      while keepReading && result.IsOk do
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
                let! persistResult = syncCopiedIdentityMapping newTx step sourceRow updatedMappings

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

let private executeBulkCopy
  (oldConnection: SqliteConnection)
  (newConnection: SqliteConnection)
  (plan: BulkCopyPlan)
  : Task<Result<IdMappingStore * int64, SqliteException>> =
  task {
    try
      use tx = newConnection.BeginTransaction()
      let mutable mappings = emptyIdMappings
      let mutable totalRows = 0L
      let mutable stepIndex = 0

      let mutable result: Result<IdMappingStore * int64, SqliteException> =
        Ok(mappings, totalRows)

      while stepIndex < plan.steps.Length && result.IsOk do
        let step = plan.steps[stepIndex]
        let! tableResult = copyTableRows oldConnection tx step mappings

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

let private deleteMigrationLogEntriesUpTo
  (oldConnection: SqliteConnection)
  (maxLogIdInclusive: int64)
  : Task<Result<unit, SqliteException>> =
  task {
    try
      use cmd =
        createCommand oldConnection None "DELETE FROM _migration_log WHERE id <= @max_id"

      cmd.Parameters.AddWithValue("@max_id", maxLogIdInclusive) |> ignore
      let! _ = cmd.ExecuteNonQueryAsync()
      return Ok()
    with :? SqliteException as ex ->
      return Error ex
  }

let private parseSchemaFromScript (schemaPath: string) : Result<SqlFile, SqliteException> =
  match buildSchemaFromScript schemaPath with
  | Ok schema -> Ok schema
  | Error message -> Error(toSqliteError message)

let private buildCopyPlan (sourceSchema: SqlFile) (targetSchema: SqlFile) : Result<BulkCopyPlan, SqliteException> =
  match buildBulkCopyPlan sourceSchema targetSchema with
  | Ok plan -> Ok plan
  | Error message -> Error(toSqliteError message)

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

let runMigrate
  (oldDbPath: string)
  (schemaPath: string)
  (newDbPath: string)
  : Task<Result<MigrateResult, SqliteException>> =
  task {
    try
      if not (File.Exists oldDbPath) then
        return Error(toSqliteError $"Old database was not found: {oldDbPath}")
      elif File.Exists newDbPath then
        return Error(toSqliteError $"New database already exists: {newDbPath}")
      else
        let! targetSchema =
          match parseSchemaFromScript schemaPath with
          | Ok schema -> Task.FromResult(Ok schema)
          | Error ex -> Task.FromResult(Error ex)

        match targetSchema with
        | Error ex -> return Error ex
        | Ok expectedSchema ->
          use oldConnection = new SqliteConnection($"Data Source={oldDbPath}")
          do! oldConnection.OpenAsync()

          let! sourceSchemaResult = loadSchemaFromDatabase oldConnection migrationTables

          match sourceSchemaResult with
          | Error ex -> return Error ex
          | Ok sourceSchema ->
            let! copyPlan =
              match buildCopyPlan sourceSchema expectedSchema with
              | Ok plan -> Task.FromResult(Ok plan)
              | Error ex -> Task.FromResult(Error ex)

            match copyPlan with
            | Error ex -> return Error ex
            | Ok plan ->
              let! oldSetupResult = ensureOldRecordingTables oldConnection

              match oldSetupResult with
              | Error ex -> return Error ex
              | Ok() ->
                let newDirectory = Path.GetDirectoryName newDbPath

                if not (String.IsNullOrWhiteSpace newDirectory) then
                  Directory.CreateDirectory newDirectory |> ignore

                use newConnection = new SqliteConnection($"Data Source={newDbPath}")
                do! newConnection.OpenAsync()
                let! initResult = initializeNewDatabase newConnection expectedSchema

                match initResult with
                | Error ex -> return Error ex
                | Ok() ->
                  let! copyResult = executeBulkCopy oldConnection newConnection plan

                  match copyResult with
                  | Error ex -> return Error ex
                  | Ok(_, copiedRows) ->
                    return
                      Ok
                        { newDbPath = newDbPath
                          copiedTables = plan.steps.Length
                          copiedRows = copiedRows }
    with
    | :? SqliteException as ex -> return Error ex
    | ex -> return Error(toSqliteError ex.Message)
  }

let runDrain (oldDbPath: string) (newDbPath: string) : Task<Result<DrainResult, SqliteException>> =
  task {
    try
      if not (File.Exists oldDbPath) then
        return Error(toSqliteError $"Old database was not found: {oldDbPath}")
      elif not (File.Exists newDbPath) then
        return Error(toSqliteError $"New database was not found: {newDbPath}")
      else
        use oldConnection = new SqliteConnection($"Data Source={oldDbPath}")
        do! oldConnection.OpenAsync()

        use newConnection = new SqliteConnection($"Data Source={newDbPath}")
        do! newConnection.OpenAsync()

        let! setDrainResult = setOldMarkerToDraining oldConnection

        match setDrainResult with
        | Error ex -> return Error ex
        | Ok() ->
          let! sourceSchemaResult = loadSchemaFromDatabase oldConnection migrationTables
          let! targetSchemaResult = loadSchemaFromDatabase newConnection migrationTables

          match sourceSchemaResult, targetSchemaResult with
          | Error ex, _ -> return Error ex
          | _, Error ex -> return Error ex
          | Ok sourceSchema, Ok targetSchema ->
            let! planResult =
              match buildCopyPlan sourceSchema targetSchema with
              | Ok plan -> Task.FromResult(Ok plan)
              | Error ex -> Task.FromResult(Error ex)

            match planResult with
            | Error ex -> return Error ex
            | Ok plan ->
              let! initialMappingsResult = loadIdMappings newConnection

              match initialMappingsResult with
              | Error ex -> return Error ex
              | Ok initialMappings ->
                let mutable mappings = initialMappings
                let mutable replayedEntries = 0
                let mutable lastConsumedLogId = 0L
                let mutable keepDraining = true

                let mutable result: Result<DrainResult, SqliteException> =
                  Ok
                    { replayedEntries = 0
                      remainingEntries = 0L }

                while keepDraining && result.IsOk do
                  let! batchResult = readMigrationLogEntries oldConnection lastConsumedLogId

                  match batchResult with
                  | Error ex -> result <- Error ex
                  | Ok [] -> keepDraining <- false
                  | Ok entries ->
                    let! replayResult = replayDrainEntries newConnection plan entries mappings

                    match replayResult with
                    | Error ex -> result <- Error ex
                    | Ok updatedMappings ->
                      mappings <- updatedMappings
                      replayedEntries <- replayedEntries + entries.Length

                      let batchMaxLogId = entries |> List.map _.id |> List.max

                      let! deleteResult = deleteMigrationLogEntriesUpTo oldConnection batchMaxLogId

                      match deleteResult with
                      | Error ex -> result <- Error ex
                      | Ok() -> lastConsumedLogId <- batchMaxLogId

                match result with
                | Error ex -> return Error ex
                | Ok _ ->
                  let! remainingEntries = countRowsIfTableExists oldConnection None "_migration_log"

                  return
                    Ok
                      { replayedEntries = replayedEntries
                        remainingEntries = remainingEntries }
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

            do! upsertStatusRow connection (Some transaction) "_migration_status" "ready"
            transaction.Commit()

            return
              Ok
                { previousStatus = status
                  idMappingDropped = hasIdMapping }
    with
    | :? SqliteException as ex -> return Error ex
    | ex -> return Error(toSqliteError ex.Message)
  }
