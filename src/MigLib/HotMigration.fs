module MigLib.HotMigration

open System
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
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
    newMigrationStatus: string option
    idMappingTablePresent: bool option
    migrationProgressTablePresent: bool option
    schemaIdentityHash: string option
    schemaIdentityCommit: string option }

type NewDatabaseStatusReport =
  { newMigrationStatus: string option
    idMappingEntries: int64
    idMappingTablePresent: bool
    migrationProgressTablePresent: bool
    schemaIdentityHash: string option
    schemaIdentityCommit: string option }

type CutoverResult =
  { previousStatus: string
    idMappingDropped: bool
    migrationProgressDropped: bool }

type MigrateResult =
  { newDbPath: string
    copiedTables: int
    copiedRows: int64 }

type MigratePlanReport =
  { schemaHash: string
    schemaCommit: string option
    supportedDifferences: string list
    unsupportedDifferences: string list
    plannedCopyTargets: string list
    replayPrerequisites: string list
    canRunMigrate: bool }

type DrainResult =
  { replayedEntries: int
    remainingEntries: int64 }

type CleanupOldResult =
  { previousMarkerStatus: string option
    markerDropped: bool
    logDropped: bool }

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

type private MigrationProgressRow =
  { lastReplayedLogId: int64
    drainCompleted: bool }

type private SchemaIdentityRow =
  { schemaHash: string
    schemaCommit: string option }

let private migrationTables =
  set
    [ "_migration_marker"
      "_migration_log"
      "_migration_status"
      "_migration_progress"
      "_id_mapping"
      "_schema_identity" ]

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

let private normalizeLineEndings (text: string) =
  text.Replace("\r\n", "\n").Replace("\r", "\n")

let private computeSchemaHashFromScriptPath (schemaPath: string) : Result<string, SqliteException> =
  try
    let normalizedSchema = File.ReadAllText schemaPath |> normalizeLineEndings
    use sha256 = SHA256.Create()
    let schemaBytes = Encoding.UTF8.GetBytes normalizedSchema
    let hashBytes = sha256.ComputeHash schemaBytes
    Ok(Convert.ToHexString(hashBytes).ToLowerInvariant().Substring(0, 16))
  with ex ->
    Error(toSqliteError $"Could not compute schema hash from script '{schemaPath}': {ex.Message}")

let private tryResolveSchemaCommitFromGit (schemaPath: string) : string option =
  try
    let fullSchemaPath = Path.GetFullPath schemaPath
    let schemaDirectory = Path.GetDirectoryName fullSchemaPath

    if String.IsNullOrWhiteSpace schemaDirectory then
      None
    else
      let startInfo = ProcessStartInfo()
      startInfo.FileName <- "git"
      startInfo.UseShellExecute <- false
      startInfo.RedirectStandardOutput <- true
      startInfo.RedirectStandardError <- true
      startInfo.CreateNoWindow <- true
      startInfo.ArgumentList.Add "-C"
      startInfo.ArgumentList.Add schemaDirectory
      startInfo.ArgumentList.Add "rev-parse"
      startInfo.ArgumentList.Add "HEAD"

      use proc = Process.Start startInfo

      if isNull proc then
        None
      else
        let output = proc.StandardOutput.ReadToEnd()
        let _ = proc.StandardError.ReadToEnd()
        let exited = proc.WaitForExit 2000

        if exited && proc.ExitCode = 0 then
          let commit = output.Trim()

          if String.IsNullOrWhiteSpace commit then
            None
          else
            Some commit
        else
          None
  with _ ->
    None

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

let private upsertSchemaIdentity
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

let private upsertMigrationProgress
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

let private readMigrationProgress
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
              drainCompleted = reader.GetInt64(1) = 1L }
  }

let private readSchemaIdentity
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

let private ensureMigrationProgressRow
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

let private countPendingLogEntries
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

let private createNewMigrationTables
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

    use schemaIdentityCmd =
      createCommand
        newConnection
        (Some tx)
        "CREATE TABLE IF NOT EXISTS _schema_identity(id INTEGER PRIMARY KEY CHECK (id = 0), schema_hash TEXT NOT NULL, schema_commit TEXT, created_utc TEXT NOT NULL);"

    let! _ = schemaIdentityCmd.ExecuteNonQueryAsync()

    do! upsertStatusRow newConnection (Some tx) "_migration_status" "migrating"
    do! upsertSchemaIdentity newConnection (Some tx) schemaHash schemaCommit
    let! _ = ensureMigrationProgressRow newConnection (Some tx)
    do! upsertMigrationProgress newConnection (Some tx) 0L false
  }

let private initializeNewDatabase
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

      do! createNewMigrationTables newConnection tx schemaHash schemaCommit

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

let private parseSchemaFromScript (schemaPath: string) : Result<SqlFile, SqliteException> =
  match buildSchemaFromScript schemaPath with
  | Ok schema -> Ok schema
  | Error message -> Error(toSqliteError message)

let private joinOrNone (items: string list) =
  match items with
  | [] -> "none"
  | values -> String.concat ", " values

let private formatRenamePairs (pairs: (string * string) list) =
  match pairs with
  | [] -> "none"
  | values ->
    values
    |> List.map (fun (sourceName, targetName) -> $"{sourceName}->{targetName}")
    |> String.concat ", "

let private formatTableMappingDelta (mapping: TableCopyMapping) : string option =
  let deltas =
    [ if not (mapping.sourceTable.Equals(mapping.targetTable, StringComparison.OrdinalIgnoreCase)) then
        yield $"table rename {mapping.sourceTable}->{mapping.targetTable}"

      if not mapping.renamedColumns.IsEmpty then
        let renamedColumns =
          mapping.renamedColumns
          |> List.map (fun (sourceName, targetName) -> $"{sourceName}->{targetName}")
          |> String.concat ", "

        yield $"renamed columns [{renamedColumns}]"

      if not mapping.addedTargetColumns.IsEmpty then
        let addedTargetColumns = mapping.addedTargetColumns |> String.concat ", "
        yield $"added target columns [{addedTargetColumns}]"

      if not mapping.droppedSourceColumns.IsEmpty then
        let droppedSourceColumns = mapping.droppedSourceColumns |> String.concat ", "
        yield $"dropped source columns [{droppedSourceColumns}]" ]

  match deltas with
  | [] -> None
  | changes ->
    let changeSummary = changes |> String.concat "; "
    Some $"table '{mapping.targetTable}': {changeSummary}"

type internal NonTableConsistencyReport =
  { supportedLines: string list
    unsupportedLines: string list }

let private summarizeIndexDefinitions (indexes: CreateIndex list) =
  match indexes with
  | [] -> "target indexes: none"
  | values ->
    let details =
      values
      |> List.map (fun index ->
        let columns = index.columns |> String.concat ", "
        $"{index.name} ({index.table}: {columns})")
      |> String.concat ", "

    $"target indexes: {details}"

let private summarizeViewDefinitions (views: CreateView list) =
  match views with
  | [] -> "target views: none"
  | values ->
    let details =
      values
      |> List.map (fun view ->
        let dependencies = view.dependencies |> joinOrNone
        $"{view.name} (deps: {dependencies})")
      |> String.concat ", "

    $"target views: {details}"

let private summarizeTriggerDefinitions (triggers: CreateTrigger list) =
  match triggers with
  | [] -> "target triggers: none"
  | values ->
    let details =
      values
      |> List.map (fun trigger ->
        let dependencies = trigger.dependencies |> joinOrNone
        $"{trigger.name} (deps: {dependencies})")
      |> String.concat ", "

    $"target triggers: {details}"

let private detectDuplicateObjectNames (objectType: string) (names: string list) =
  names
  |> List.groupBy id
  |> List.choose (fun (name, values) ->
    if values.Length > 1 then
      Some $"Target {objectType} '{name}' is declared {values.Length} times."
    else
      None)

let private validateIndexConsistency (targetSchema: SqlFile) =
  let tablesByName =
    targetSchema.tables |> List.map (fun table -> table.name, table) |> Map.ofList

  targetSchema.indexes
  |> List.collect (fun index ->
    match tablesByName.TryFind index.table with
    | None -> [ $"Index '{index.name}' references missing table '{index.table}'." ]
    | Some tableDef ->
      let tableColumns = tableDef.columns |> List.map _.name |> Set.ofList

      let missingColumns =
        index.columns
        |> List.filter (fun columnName -> not (tableColumns.Contains columnName))

      if missingColumns.IsEmpty then
        []
      else
        let missingColumnText = missingColumns |> String.concat ", "
        [ $"Index '{index.name}' references missing columns on '{index.table}': {missingColumnText}." ])

let private validateViewConsistency (targetSchema: SqlFile) =
  let tableNames = targetSchema.tables |> List.map _.name |> Set.ofList
  let viewNames = targetSchema.views |> List.map _.name |> Set.ofList
  let knownDependencies = Set.union tableNames viewNames

  targetSchema.views
  |> List.collect (fun view ->
    let missingDependencies =
      view.dependencies
      |> List.filter (fun dependency -> not (knownDependencies.Contains dependency))

    let dependencyErrors =
      if missingDependencies.IsEmpty then
        []
      else
        let missingDependencyText = missingDependencies |> String.concat ", "
        [ $"View '{view.name}' references missing dependencies: {missingDependencyText}." ]

    let tokenErrors =
      if view.sqlTokens |> Seq.isEmpty then
        [ $"View '{view.name}' has no SQL tokens." ]
      else
        []

    dependencyErrors @ tokenErrors)

let private validateTriggerConsistency (targetSchema: SqlFile) =
  let tableNames = targetSchema.tables |> List.map _.name |> Set.ofList
  let viewNames = targetSchema.views |> List.map _.name |> Set.ofList
  let knownDependencies = Set.union tableNames viewNames

  targetSchema.triggers
  |> List.collect (fun trigger ->
    let missingDependencies =
      trigger.dependencies
      |> List.filter (fun dependency -> not (knownDependencies.Contains dependency))

    let dependencyErrors =
      if missingDependencies.IsEmpty then
        []
      else
        let missingDependencyText = missingDependencies |> String.concat ", "
        [ $"Trigger '{trigger.name}' references missing dependencies: {missingDependencyText}." ]

    let tokenErrors =
      if trigger.sqlTokens |> Seq.isEmpty then
        [ $"Trigger '{trigger.name}' has no SQL tokens." ]
      else
        []

    dependencyErrors @ tokenErrors)

let internal analyzeNonTableConsistency (targetSchema: SqlFile) : NonTableConsistencyReport =
  let duplicateIndexNames =
    targetSchema.indexes |> List.map _.name |> detectDuplicateObjectNames "index"

  let duplicateViewNames =
    targetSchema.views |> List.map _.name |> detectDuplicateObjectNames "view"

  let duplicateTriggerNames =
    targetSchema.triggers |> List.map _.name |> detectDuplicateObjectNames "trigger"

  let unsupportedLines =
    duplicateIndexNames
    @ duplicateViewNames
    @ duplicateTriggerNames
    @ validateIndexConsistency targetSchema
    @ validateViewConsistency targetSchema
    @ validateTriggerConsistency targetSchema

  let supportedLines =
    [ summarizeIndexDefinitions targetSchema.indexes
      summarizeViewDefinitions targetSchema.views
      summarizeTriggerDefinitions targetSchema.triggers
      if unsupportedLines.IsEmpty then
        "non-table consistency checks: passed"
      else
        "non-table consistency checks: found unsupported target-schema issues" ]

  { supportedLines = supportedLines
    unsupportedLines = unsupportedLines }

let private describeSupportedDifferences (schemaPlan: SchemaCopyPlan) : string list =
  let diff = schemaPlan.diff

  let tableLevelSummary =
    [ $"added tables: {joinOrNone diff.addedTables}"
      $"removed tables: {joinOrNone diff.removedTables}"
      $"renamed tables: {formatRenamePairs diff.renamedTables}" ]

  let mappingDeltas = schemaPlan.tableMappings |> List.choose formatTableMappingDelta

  if mappingDeltas.IsEmpty then
    tableLevelSummary @ [ "column/table mapping deltas: none" ]
  else
    tableLevelSummary @ mappingDeltas

let private renderPreflightReport (supported: string list) (unsupported: string list) =
  let renderSection (header: string) (lines: string list) =
    let normalizedLines =
      match lines with
      | [] -> [ "none" ]
      | values -> values

    header :: (normalizedLines |> List.map (fun line -> $"  - {line}"))

  [ "Schema preflight report:"
    yield! renderSection "Supported differences:" supported
    yield! renderSection "Unsupported differences:" unsupported ]
  |> String.concat Environment.NewLine

let private buildCopyPlan (sourceSchema: SqlFile) (targetSchema: SqlFile) : Result<BulkCopyPlan, SqliteException> =
  let schemaPlan = buildSchemaCopyPlan sourceSchema targetSchema
  let tableDifferences = describeSupportedDifferences schemaPlan
  let nonTableConsistency = analyzeNonTableConsistency targetSchema
  let supportedDifferences = tableDifferences @ nonTableConsistency.supportedLines

  match buildBulkCopyPlan sourceSchema targetSchema with
  | Ok plan ->
    if nonTableConsistency.unsupportedLines.IsEmpty then
      Ok plan
    else
      let report =
        renderPreflightReport supportedDifferences nonTableConsistency.unsupportedLines

      Error(toSqliteError report)
  | Error message ->
    let unsupported = nonTableConsistency.unsupportedLines @ [ message ]
    let report = renderPreflightReport supportedDifferences unsupported
    Error(toSqliteError report)

let getMigratePlan
  (oldDbPath: string)
  (schemaPath: string)
  (newDbPath: string)
  : Task<Result<MigratePlanReport, SqliteException>> =
  task {
    try
      if not (File.Exists oldDbPath) then
        return Error(toSqliteError $"Old database was not found: {oldDbPath}")
      else
        let schemaHashResult = computeSchemaHashFromScriptPath schemaPath

        match schemaHashResult with
        | Error ex -> return Error ex
        | Ok schemaHash ->
          let schemaCommit = tryResolveSchemaCommitFromGit schemaPath

          let targetSchemaResult =
            match parseSchemaFromScript schemaPath with
            | Ok schema -> Ok schema
            | Error ex -> Error ex

          match targetSchemaResult with
          | Error ex -> return Error ex
          | Ok targetSchema ->
            use oldConnection = new SqliteConnection($"Data Source={oldDbPath}")
            do! oldConnection.OpenAsync()

            let! sourceSchemaResult = loadSchemaFromDatabase oldConnection migrationTables

            match sourceSchemaResult with
            | Error ex -> return Error ex
            | Ok sourceSchema ->
              let schemaPlan = buildSchemaCopyPlan sourceSchema targetSchema
              let tableDifferences = describeSupportedDifferences schemaPlan
              let nonTableConsistency = analyzeNonTableConsistency targetSchema
              let supportedDifferences = tableDifferences @ nonTableConsistency.supportedLines

              let plannerResult = buildBulkCopyPlan sourceSchema targetSchema

              let plannedCopyTargets, plannerUnsupported =
                match plannerResult with
                | Ok plan -> plan.steps |> List.map _.mapping.targetTable, []
                | Error message -> [], [ message ]

              let unsupportedDifferences =
                nonTableConsistency.unsupportedLines @ plannerUnsupported

              let! oldMarkerStatus = readMarkerStatus oldConnection None "_migration_marker"
              let! oldMigrationLogTablePresent = tableExists oldConnection None "_migration_log"
              let! oldMigrationLogEntries = countRowsIfTableExists oldConnection None "_migration_log"

              let markerPrerequisite =
                match oldMarkerStatus with
                | Some status ->
                  $"_migration_marker is present with status '{status}' (migrate will set it to recording)."
                | None -> "_migration_marker is absent (migrate will create it in recording mode)."

              let logPrerequisite =
                if oldMigrationLogTablePresent then
                  $"_migration_log is present with {oldMigrationLogEntries} entries (migrate will recreate it)."
                else
                  "_migration_log is absent (migrate will create it)."

              let newDatabaseAlreadyExists = File.Exists newDbPath

              let targetPrerequisite =
                if newDatabaseAlreadyExists then
                  $"target database already exists: {newDbPath}"
                else
                  $"target database path is available: {newDbPath}"

              let driftPrerequisite =
                if unsupportedDifferences.IsEmpty then
                  "schema preflight checks pass."
                else
                  $"schema preflight has {unsupportedDifferences.Length} blocking issue(s)."

              let replayPrerequisites =
                [ markerPrerequisite; logPrerequisite; targetPrerequisite; driftPrerequisite ]

              let canRunMigrate = not newDatabaseAlreadyExists && unsupportedDifferences.IsEmpty

              return
                Ok
                  { schemaHash = schemaHash
                    schemaCommit = schemaCommit
                    supportedDifferences = supportedDifferences
                    unsupportedDifferences = unsupportedDifferences
                    plannedCopyTargets = plannedCopyTargets
                    replayPrerequisites = replayPrerequisites
                    canRunMigrate = canRunMigrate }
    with
    | :? SqliteException as ex -> return Error ex
    | ex -> return Error(toSqliteError ex.Message)
  }

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
              newMigrationStatus = None
              idMappingTablePresent = None
              migrationProgressTablePresent = None
              schemaIdentityHash = None
              schemaIdentityCommit = None }
      | Some newPath ->
        use newConnection = new SqliteConnection($"Data Source={newPath}")
        do! newConnection.OpenAsync()

        let! idMappingTablePresent = tableExists newConnection None "_id_mapping"

        let! idMappingEntries =
          if idMappingTablePresent then
            countRows newConnection None "_id_mapping"
          else
            Task.FromResult 0L

        let! newMigrationStatus = readMarkerStatus newConnection None "_migration_status"
        let! migrationProgressTablePresent = tableExists newConnection None "_migration_progress"
        let! progress = readMigrationProgress newConnection None
        let! schemaIdentity = readSchemaIdentity newConnection None

        let isReady =
          newMigrationStatus
          |> Option.exists (fun status -> status.Equals("ready", StringComparison.OrdinalIgnoreCase))

        let! pendingReplayEntries =
          if isReady then
            Task.FromResult 0L
          else
            match progress with
            | Some row -> countPendingLogEntries oldConnection None row.lastReplayedLogId
            | None -> Task.FromResult migrationLogEntries

        return
          Ok
            { oldMarkerStatus = oldMarkerStatus
              migrationLogEntries = migrationLogEntries
              pendingReplayEntries = Some pendingReplayEntries
              idMappingEntries = Some idMappingEntries
              newMigrationStatus = newMigrationStatus
              idMappingTablePresent = Some idMappingTablePresent
              migrationProgressTablePresent = Some migrationProgressTablePresent
              schemaIdentityHash = schemaIdentity |> Option.map _.schemaHash
              schemaIdentityCommit = schemaIdentity |> Option.bind _.schemaCommit }
    with
    | :? SqliteException as ex -> return Error ex
    | ex -> return Error(toSqliteError ex.Message)
  }

let getNewDatabaseStatus (newDbPath: string) : Task<Result<NewDatabaseStatusReport, SqliteException>> =
  task {
    try
      use newConnection = new SqliteConnection($"Data Source={newDbPath}")
      do! newConnection.OpenAsync()

      let! idMappingTablePresent = tableExists newConnection None "_id_mapping"

      let! idMappingEntries =
        if idMappingTablePresent then
          countRows newConnection None "_id_mapping"
        else
          Task.FromResult 0L

      let! newMigrationStatus = readMarkerStatus newConnection None "_migration_status"
      let! migrationProgressTablePresent = tableExists newConnection None "_migration_progress"
      let! schemaIdentity = readSchemaIdentity newConnection None

      return
        Ok
          { newMigrationStatus = newMigrationStatus
            idMappingEntries = idMappingEntries
            idMappingTablePresent = idMappingTablePresent
            migrationProgressTablePresent = migrationProgressTablePresent
            schemaIdentityHash = schemaIdentity |> Option.map _.schemaHash
            schemaIdentityCommit = schemaIdentity |> Option.bind _.schemaCommit }
    with
    | :? SqliteException as ex -> return Error ex
    | ex -> return Error(toSqliteError ex.Message)
  }

let private runMigrateInternal
  (oldDbPath: string)
  (schemaPath: string)
  (newDbPath: string)
  (schemaCommit: string option)
  : Task<Result<MigrateResult, SqliteException>> =
  task {
    try
      if not (File.Exists oldDbPath) then
        return Error(toSqliteError $"Old database was not found: {oldDbPath}")
      elif File.Exists newDbPath then
        return Error(toSqliteError $"New database already exists: {newDbPath}")
      else
        let schemaHashResult = computeSchemaHashFromScriptPath schemaPath

        match schemaHashResult with
        | Error ex -> return Error ex
        | Ok schemaHash ->
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

                  let! initResult = initializeNewDatabase newConnection expectedSchema schemaHash schemaCommit

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

let runMigrate
  (oldDbPath: string)
  (schemaPath: string)
  (newDbPath: string)
  : Task<Result<MigrateResult, SqliteException>> =
  let schemaCommit = tryResolveSchemaCommitFromGit schemaPath
  runMigrateInternal oldDbPath schemaPath newDbPath schemaCommit

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
                let! progressRow = ensureMigrationProgressRow newConnection None
                let mutable mappings = initialMappings
                let mutable replayedEntries = 0
                let mutable lastConsumedLogId = progressRow.lastReplayedLogId
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
                      lastConsumedLogId <- batchMaxLogId
                      do! upsertMigrationProgress newConnection None lastConsumedLogId false

                match result with
                | Error ex -> return Error ex
                | Ok _ ->
                  let! remainingEntries = countPendingLogEntries oldConnection None lastConsumedLogId

                  do! upsertMigrationProgress newConnection None lastConsumedLogId (remainingEntries = 0L)

                  return
                    Ok
                      { replayedEntries = replayedEntries
                        remainingEntries = remainingEntries }
    with
    | :? SqliteException as ex -> return Error ex
    | ex -> return Error(toSqliteError ex.Message)
  }

let runCleanupOld (oldDbPath: string) : Task<Result<CleanupOldResult, SqliteException>> =
  task {
    try
      if not (File.Exists oldDbPath) then
        return Error(toSqliteError $"Old database was not found: {oldDbPath}")
      else
        use connection = new SqliteConnection($"Data Source={oldDbPath}")
        do! connection.OpenAsync()
        use transaction = connection.BeginTransaction()
        let! markerStatus = readMarkerStatus connection (Some transaction) "_migration_marker"

        let markerIsRecording =
          markerStatus
          |> Option.exists (fun status -> status.Equals("recording", StringComparison.OrdinalIgnoreCase))

        if markerIsRecording then
          transaction.Rollback()

          return
            Error(
              toSqliteError "Old database is still in recording mode. Run `mig drain` and `mig cutover` before cleanup."
            )
        else
          let! hasMarker = tableExists connection (Some transaction) "_migration_marker"
          let! hasLog = tableExists connection (Some transaction) "_migration_log"

          if hasMarker then
            use dropMarkerCmd =
              createCommand connection (Some transaction) "DROP TABLE _migration_marker"

            let! _ = dropMarkerCmd.ExecuteNonQueryAsync()
            ()

          if hasLog then
            use dropLogCmd =
              createCommand connection (Some transaction) "DROP TABLE _migration_log"

            let! _ = dropLogCmd.ExecuteNonQueryAsync()
            ()

          transaction.Commit()

          return
            Ok
              { previousMarkerStatus = markerStatus
                markerDropped = hasMarker
                logDropped = hasLog }
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
            let! readyForCutover =
              if isMigrating then
                task {
                  let! progress = readMigrationProgress connection (Some transaction)

                  match progress with
                  | None ->
                    return
                      Error(
                        toSqliteError
                          "_migration_progress is missing in the new database. Run `mig drain` before `mig cutover`."
                      )
                  | Some progressRow when not progressRow.drainCompleted ->
                    return
                      Error(
                        toSqliteError
                          "Drain is not complete. Pending replay entries still exist. Run `mig drain` again before `mig cutover`."
                      )
                  | Some _ -> return Ok()
                }
              else
                Task.FromResult(Ok())

            match readyForCutover with
            | Error error ->
              transaction.Rollback()
              return Error error
            | Ok() ->
              let! hasIdMapping = tableExists connection (Some transaction) "_id_mapping"

              if hasIdMapping then
                use dropIdMappingCmd =
                  createCommand connection (Some transaction) "DROP TABLE _id_mapping"

                let! _ = dropIdMappingCmd.ExecuteNonQueryAsync()
                ()

              let! hasMigrationProgress = tableExists connection (Some transaction) "_migration_progress"

              if hasMigrationProgress then
                use dropProgressCmd =
                  createCommand connection (Some transaction) "DROP TABLE _migration_progress"

                let! _ = dropProgressCmd.ExecuteNonQueryAsync()
                ()

              do! upsertStatusRow connection (Some transaction) "_migration_status" "ready"
              transaction.Commit()

              return
                Ok
                  { previousStatus = status
                    idMappingDropped = hasIdMapping
                    migrationProgressDropped = hasMigrationProgress }
    with
    | :? SqliteException as ex -> return Error ex
    | ex -> return Error(toSqliteError ex.Message)
  }
