module internal MigLib.Migrate.SchemaIntrospection

open System
open System.Threading.Tasks

open Microsoft.Data.Sqlite

open MigLib.Schema.Types
open MigLib.Types

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

let private quoteIdentifier (identifier: string) =
  let escaped = identifier.Replace("\"", "\"\"")
  $"\"{escaped}\""

let private parseSqlType (declaredType: string) =
  let normalized = declaredType.Trim().ToUpperInvariant()

  if normalized.Contains "INT" then
    SqlInteger
  elif
    normalized.Contains "REAL"
    || normalized.Contains "FLOA"
    || normalized.Contains "DOUB"
  then
    SqlReal
  elif
    normalized.Contains "TEXT"
    || normalized.Contains "CHAR"
    || normalized.Contains "CLOB"
  then
    SqlText
  else
    SqlString

let private trimOuterParens (value: string) =
  let trimmed = value.Trim()

  if trimmed.StartsWith "(" && trimmed.EndsWith ")" && trimmed.Length >= 2 then
    trimmed.Substring(1, trimmed.Length - 2).Trim()
  else
    trimmed

let private parseDefaultExpr (defaultSql: string) =
  let trimmed = trimOuterParens defaultSql

  if trimmed.StartsWith "'" && trimmed.EndsWith "'" && trimmed.Length >= 2 then
    String(trimmed.Substring(1, trimmed.Length - 2).Replace("''", "'"))
  else
    match Int32.TryParse trimmed with
    | true, value -> Integer value
    | false, _ -> Value trimmed

let private parseFkAction (value: string) =
  match value.Trim().ToUpperInvariant() with
  | "CASCADE" -> Some Cascade
  | "RESTRICT" -> Some Restrict
  | "NO ACTION" -> Some NoAction
  | "SET NULL" -> Some SetNull
  | "SET DEFAULT" -> Some SetDefault
  | _ -> None

let private readTableList (connection: SqliteConnection) =
  task {
    use cmd =
      new SqliteCommand(
        "SELECT name, sql FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;",
        connection
      )

    use! reader = cmd.ExecuteReaderAsync()
    let tables = ResizeArray<string * string option>()
    let mutable keepReading = true

    while keepReading do
      let! hasRow = reader.ReadAsync()

      if hasRow then
        let tableName = reader.GetString 0
        let sql = if reader.IsDBNull 1 then None else Some(reader.GetString 1)
        tables.Add(tableName, sql)
      else
        keepReading <- false

    return tables |> Seq.toList
  }

let private readTableInfoRows (connection: SqliteConnection) (tableName: string) =
  task {
    use cmd =
      new SqliteCommand($"PRAGMA table_info({quoteIdentifier tableName});", connection)

    use! reader = cmd.ExecuteReaderAsync()
    let rows = ResizeArray<TableInfoRow>()
    let mutable keepReading = true

    while keepReading do
      let! hasRow = reader.ReadAsync()

      if hasRow then
        rows.Add
          { name = reader.GetString 1
            declaredType = if reader.IsDBNull 2 then "" else reader.GetString 2
            isNotNull = reader.GetInt32 3 = 1
            defaultSql = if reader.IsDBNull 4 then None else Some(reader.GetString 4)
            primaryKeyOrder = reader.GetInt32 5 }
      else
        keepReading <- false

    return rows |> Seq.toList
  }

let private readForeignKeyRows (connection: SqliteConnection) (tableName: string) =
  task {
    use cmd =
      new SqliteCommand($"PRAGMA foreign_key_list({quoteIdentifier tableName});", connection)

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

    return rows |> Seq.toList
  }

let private addColumnConstraint (columnName: string) (constraintDef: ColumnConstraint) (columns: ColumnDef list) =
  columns
  |> List.map (fun column ->
    if column.name.Equals(columnName, StringComparison.OrdinalIgnoreCase) then
      { column with
          constraints = column.constraints @ [ constraintDef ] }
    else
      column)

let private buildForeignKeyConstraint (rows: ForeignKeyRow list) (columns: string list) =
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
  =
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

  let mutable columns: ColumnDef list =
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
        previousName = None
        columnType = parseSqlType row.declaredType
        constraints = constraints |> Seq.toList
        enumLikeDu = None
        unitOfMeasure = None })

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

    match orderedRows with
    | [ row ] ->
      let fk = buildForeignKeyConstraint orderedRows []
      columns <- columns |> addColumnConstraint row.fromColumn fk
    | _ -> ()

  let tableConstraints =
    if primaryKeys.Length > 1 then
      PrimaryKey
        { constraintName = None
          columns = primaryKeys
          isAutoincrement = false }
      :: tableConstraints
    else
      tableConstraints

  { name = tableName
    previousName = None
    dropColumns = []
    columns = columns
    constraints = tableConstraints
    queryByAnnotations = []
    queryLikeAnnotations = []
    queryByOrCreateAnnotations = []
    selectOneAnnotations = []
    insertOrIgnoreAnnotations = []
    deleteAllAnnotations = []
    upsertAnnotations = [] }

let loadSchemaFromDatabase (connection: SqliteConnection) : Task<Result<SqlFile, MigError>> =
  task {
    try
      let! tableList = readTableList connection
      let tables = ResizeArray<CreateTable>()

      for tableName, createSql in tableList do
        let! tableInfoRows = readTableInfoRows connection tableName
        let! foreignKeyRows = readForeignKeyRows connection tableName
        tables.Add(buildTableDefinition tableName createSql tableInfoRows foreignKeyRows)

      return
        Ok
          { measureTypes = []
            inserts = []
            views = []
            tables = tables |> Seq.toList
            indexes = []
            triggers = [] }
    with
    | :? SqliteException as ex -> return Error(MigError.Sqlite ex)
    | ex -> return Error(MigError.Other ex)
  }
