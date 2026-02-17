/// Module for generating CRUD query methods for normalized schemas with discriminated unions.
/// Generates async methods with pattern matching for DU cases.
module internal MigLib.CodeGen.NormalizedQueryGenerator

open MigLib.DeclarativeMigrations.Types
open MigLib.CodeGen.AstExprBuilders
open Fabulous.AST
open type Fabulous.AST.Ast

// =============================================================================
// Helper Functions
// =============================================================================

/// Helper to get reader method name from F# type
let private readerMethod (t: string) =
  t.Replace("int64", "Int64").Replace("string", "String").Replace("float", "Double").Replace("DateTime", "DateTime")

/// Get the non-PK columns from a table (for INSERT)
let private getInsertColumns (table: CreateTable) : ColumnDef list =
  table.columns
  |> List.filter (fun col ->
    // Exclude auto-increment primary key columns
    not (
      col.constraints
      |> List.exists (fun c ->
        match c with
        | PrimaryKey pk -> pk.isAutoincrement
        | _ -> false)
    ))

/// Generate SQL INSERT statement for a table with specific columns
let private generateInsertSql (tableName: string) (columns: ColumnDef list) : string =
  let columnNames = columns |> List.map (fun c -> c.name) |> String.concat ", "
  let paramNames = columns |> List.map (fun c -> $"@{c.name}") |> String.concat ", "
  $"INSERT INTO {tableName} ({columnNames}) VALUES ({paramNames})"

/// Generate SQL INSERT OR IGNORE statement for a table with specific columns
let private generateInsertOrIgnoreSql (tableName: string) (columns: ColumnDef list) : string =
  let columnNames = columns |> List.map (fun c -> c.name) |> String.concat ", "
  let paramNames = columns |> List.map (fun c -> $"@{c.name}") |> String.concat ", "
  $"INSERT OR IGNORE INTO {tableName} ({columnNames}) VALUES ({paramNames})"

/// Generate positional pattern for pattern matching (e.g., "name, age")
/// Note: Named patterns (Name = name) are F# 5+ and not supported by Fantomas parser
let private generateFieldPattern (columns: ColumnDef list) : string =
  columns
  |> List.map (fun col ->
    let fieldName = TypeGenerator.toPascalCase col.name

    fieldName.ToLower().[0..0]
    + (if fieldName.Length > 1 then fieldName.[1..] else ""))
  |> String.concat ", "

/// Generate positional pattern to extract a single field from a list of columns
/// Returns (pattern, varName) where pattern uses _ for other positions
let private generateSingleFieldPattern (columns: ColumnDef list) (targetColName: string) : string * string =
  let targetColLower = targetColName.ToLowerInvariant()

  let parts =
    columns
    |> List.map (fun col ->
      if col.name.ToLowerInvariant() = targetColLower then
        let fieldName = TypeGenerator.toPascalCase col.name

        let varName =
          fieldName.ToLower().[0..0]
          + (if fieldName.Length > 1 then fieldName.[1..] else "")

        varName
      else
        "_")

  let pattern = parts |> String.concat ", "

  let varName =
    columns
    |> List.find (fun c -> c.name.ToLowerInvariant() = targetColLower)
    |> fun col ->
        let fieldName = TypeGenerator.toPascalCase col.name

        fieldName.ToLower().[0..0]
        + (if fieldName.Length > 1 then fieldName.[1..] else "")

  (pattern, varName)

/// Generate parameter binding code for columns using variable names
let private generateParamBindings (columns: ColumnDef list) (cmdVarName: string) : string list =
  columns
  |> List.map (fun col ->
    let fieldName = TypeGenerator.toPascalCase col.name

    let varName =
      fieldName.ToLower().[0..0]
      + (if fieldName.Length > 1 then fieldName.[1..] else "")

    let isNullable = TypeGenerator.isColumnNullable col

    if isNullable then
      $"{cmdVarName}.Parameters.AddWithValue(\"@{col.name}\", match {varName} with Some v -> box v | None -> box DBNull.Value) |> ignore"
    else
      $"{cmdVarName}.Parameters.AddWithValue(\"@{col.name}\", {varName}) |> ignore")

let private getSinglePrimaryKeyColumn (table: CreateTable) : ColumnDef option =
  let tableLevelPk =
    table.constraints
    |> List.tryPick (fun c ->
      match c with
      | PrimaryKey pk when pk.columns.Length > 0 -> Some pk.columns
      | _ -> None)

  let pkColumns =
    match tableLevelPk with
    | Some cols -> table.columns |> List.filter (fun col -> List.contains col.name cols)
    | None ->
      table.columns
      |> List.filter (fun col ->
        col.constraints
        |> List.exists (fun c ->
          match c with
          | PrimaryKey _ -> true
          | _ -> false))

  match pkColumns with
  | [ singlePk ] -> Some singlePk
  | _ -> None

let private isAutoIncrementPrimaryKey (column: ColumnDef) : bool =
  column.constraints
  |> List.exists (fun constraintDef ->
    match constraintDef with
    | PrimaryKey pk -> pk.isAutoincrement
    | _ -> false)

let private getColumnVarName (column: ColumnDef) : string =
  let fieldName = TypeGenerator.toPascalCase column.name

  fieldName.ToLower().[0..0]
  + (if fieldName.Length > 1 then fieldName.[1..] else "")

/// Generate the Base case insert (single table)
let private generateBaseCase (baseTable: CreateTable) (typeName: string) : string =
  let insertColumns = getInsertColumns baseTable
  let insertSql = generateInsertSql baseTable.name insertColumns
  let fieldPattern = generateFieldPattern insertColumns

  let asyncParamBindings =
    generateParamBindings insertColumns "cmd" |> String.concat "\n          "

  $"""        | New{typeName}.Base({fieldPattern}) ->
          // Single INSERT into base table
          use cmd = new SqliteCommand("{insertSql}", tx.Connection, tx)
          {asyncParamBindings}
          MigrationLog.ensureWriteAllowed tx
          let! _ = cmd.ExecuteNonQueryAsync()
          use lastIdCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
          let! lastId = lastIdCmd.ExecuteScalarAsync()
          let {baseTable.name}Id = lastId |> unbox<int64>
          return Ok {baseTable.name}Id"""

/// Generate the Base case insert-or-ignore (single table)
let private generateBaseCaseInsertOrIgnore (baseTable: CreateTable) (typeName: string) : string =
  let insertColumns = getInsertColumns baseTable
  let insertSql = generateInsertOrIgnoreSql baseTable.name insertColumns
  let fieldPattern = generateFieldPattern insertColumns

  let asyncParamBindings =
    generateParamBindings insertColumns "cmd" |> String.concat "\n          "

  $"""        | New{typeName}.Base({fieldPattern}) ->
          // Single INSERT OR IGNORE into base table
          use cmd = new SqliteCommand("{insertSql}", tx.Connection, tx)
          {asyncParamBindings}
          MigrationLog.ensureWriteAllowed tx
          let! rows = cmd.ExecuteNonQueryAsync()
          if rows = 0 then
            return Ok None
          else
            use lastIdCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
            let! lastId = lastIdCmd.ExecuteScalarAsync()
            let {baseTable.name}Id = lastId |> unbox<int64>
            return Ok (Some {baseTable.name}Id)"""

/// Generate an extension case insert (multi-table)
let private generateExtensionCase (baseTable: CreateTable) (extension: ExtensionTable) (typeName: string) : string =
  let caseName = TypeGenerator.toPascalCase extension.aspectName
  let baseInsertColumns = getInsertColumns baseTable
  let baseInsertSql = generateInsertSql baseTable.name baseInsertColumns
  let basePkColumn = getSinglePrimaryKeyColumn baseTable

  let extensionFkValueExpr =
    match basePkColumn with
    | Some pkCol when not (isAutoIncrementPrimaryKey pkCol) -> getColumnVarName pkCol
    | _ -> $"{baseTable.name}Id"

  // Extension columns excluding the FK column
  let extensionInsertColumns =
    extension.table.columns
    |> List.filter (fun col -> col.name <> extension.fkColumn)

  // Combine all columns for the field pattern
  let allColumns = baseInsertColumns @ extensionInsertColumns
  let fieldPattern = generateFieldPattern allColumns

  let asyncBaseParamBindings =
    generateParamBindings baseInsertColumns "cmd1" |> String.concat "\n          "

  let asyncExtensionParamBindings =
    generateParamBindings extensionInsertColumns "cmd2"
    |> String.concat "\n          "

  $"""        | New{typeName}.With{caseName}({fieldPattern}) ->
          // Two inserts in same transaction (atomic)
          use cmd1 = new SqliteCommand("{baseInsertSql}", tx.Connection, tx)
          {asyncBaseParamBindings}
          MigrationLog.ensureWriteAllowed tx
          let! _ = cmd1.ExecuteNonQueryAsync()

          use lastIdCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
          let! lastId = lastIdCmd.ExecuteScalarAsync()
          let {baseTable.name}Id = lastId |> unbox<int64>

          use cmd2 = new SqliteCommand("INSERT INTO {extension.table.name} ({extension.fkColumn}, {extensionInsertColumns |> List.map (fun c -> c.name) |> String.concat ", "}) VALUES (@{extension.fkColumn}, {extensionInsertColumns |> List.map (fun c -> $"@{c.name}") |> String.concat ", "})", tx.Connection, tx)
          cmd2.Parameters.AddWithValue("@{extension.fkColumn}", {extensionFkValueExpr}) |> ignore
          {asyncExtensionParamBindings}
          MigrationLog.ensureWriteAllowed tx
          let! _ = cmd2.ExecuteNonQueryAsync()
          return Ok {baseTable.name}Id"""

/// Generate an extension case insert-or-ignore (multi-table)
let private generateExtensionCaseInsertOrIgnore
  (baseTable: CreateTable)
  (extension: ExtensionTable)
  (typeName: string)
  : string =
  let caseName = TypeGenerator.toPascalCase extension.aspectName
  let baseInsertColumns = getInsertColumns baseTable
  let baseInsertSql = generateInsertOrIgnoreSql baseTable.name baseInsertColumns
  let basePkColumn = getSinglePrimaryKeyColumn baseTable

  let extensionFkValueExpr =
    match basePkColumn with
    | Some pkCol when not (isAutoIncrementPrimaryKey pkCol) -> getColumnVarName pkCol
    | _ -> $"{baseTable.name}Id"

  // Extension columns excluding the FK column
  let extensionInsertColumns =
    extension.table.columns
    |> List.filter (fun col -> col.name <> extension.fkColumn)

  // Combine all columns for the field pattern
  let allColumns = baseInsertColumns @ extensionInsertColumns
  let fieldPattern = generateFieldPattern allColumns

  let asyncBaseParamBindings =
    generateParamBindings baseInsertColumns "cmd1" |> String.concat "\n          "

  let asyncExtensionParamBindings =
    generateParamBindings extensionInsertColumns "cmd2"
    |> String.concat "\n          "

  $"""        | New{typeName}.With{caseName}({fieldPattern}) ->
          // Base INSERT OR IGNORE then extension INSERT
          use cmd1 = new SqliteCommand("{baseInsertSql}", tx.Connection, tx)
          {asyncBaseParamBindings}
          MigrationLog.ensureWriteAllowed tx
          let! rows = cmd1.ExecuteNonQueryAsync()
          if rows = 0 then
            return Ok None
          else
            use lastIdCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
            let! lastId = lastIdCmd.ExecuteScalarAsync()
            let {baseTable.name}Id = lastId |> unbox<int64>

            use cmd2 = new SqliteCommand("INSERT INTO {extension.table.name} ({extension.fkColumn}, {extensionInsertColumns |> List.map (fun c -> c.name) |> String.concat ", "}) VALUES (@{extension.fkColumn}, {extensionInsertColumns |> List.map (fun c -> $"@{c.name}") |> String.concat ", "})", tx.Connection, tx)
            cmd2.Parameters.AddWithValue("@{extension.fkColumn}", {extensionFkValueExpr}) |> ignore
            {asyncExtensionParamBindings}
            MigrationLog.ensureWriteAllowed tx
            let! _ = cmd2.ExecuteNonQueryAsync()
            return Ok (Some {baseTable.name}Id)"""

/// Generate the Insert method for a normalized table
let generateInsert (normalized: NormalizedTable) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name

  // Generate Base case
  let baseCase = generateBaseCase normalized.baseTable typeName

  // Generate extension cases
  let extensionCases =
    normalized.extensions
    |> List.map (fun ext -> generateExtensionCase normalized.baseTable ext typeName)
    |> String.concat "\n\n"

  let allCases =
    if normalized.extensions.IsEmpty then
      baseCase
    else
      $"{baseCase}\n\n{extensionCases}"

  $"""  static member Insert (item: New{typeName}) (tx: SqliteTransaction)
    : Task<Result<int64, SqliteException>> =
    task {{
      try
        match item with
{allCases}
      with
      | :? SqliteException as ex -> return Error ex
    }}"""

/// Generate the InsertOrIgnore method for a normalized table
let generateInsertOrIgnore (normalized: NormalizedTable) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name

  // Generate Base case
  let baseCase = generateBaseCaseInsertOrIgnore normalized.baseTable typeName

  // Generate extension cases
  let extensionCases =
    normalized.extensions
    |> List.map (fun ext -> generateExtensionCaseInsertOrIgnore normalized.baseTable ext typeName)
    |> String.concat "\n\n"

  let allCases =
    if normalized.extensions.IsEmpty then
      baseCase
    else
      $"{baseCase}\n\n{extensionCases}"

  $"""  static member InsertOrIgnore (item: New{typeName}) (tx: SqliteTransaction)
    : Task<Result<int64 option, SqliteException>> =
    task {{
      try
        match item with
{allCases}
      with
      | :? SqliteException as ex -> return Error ex
    }}"""

/// Get the primary key column(s) from a table
let private getPrimaryKeyColumns (table: CreateTable) : ColumnDef list =
  // Check for table-level primary keys first
  let tableLevelPk =
    table.constraints
    |> List.tryPick (fun c ->
      match c with
      | PrimaryKey pk when pk.columns.Length > 0 -> Some pk.columns
      | _ -> None)

  match tableLevelPk with
  | Some cols -> table.columns |> List.filter (fun col -> List.contains col.name cols)
  | None ->
    // Check for column-level primary keys
    table.columns
    |> List.filter (fun col ->
      col.constraints
      |> List.exists (fun c ->
        match c with
        | PrimaryKey _ -> true
        | _ -> false))

/// Generate LEFT JOIN clauses for all extension tables
let private generateLeftJoins (baseTable: CreateTable) (extensions: ExtensionTable list) : string =
  let basePkColumnName =
    getSinglePrimaryKeyColumn baseTable
    |> Option.map (fun pk -> pk.name)
    |> Option.defaultValue "id"

  extensions
  |> List.mapi (fun i ext ->
    let alias = $"ext{i}"
    $"LEFT JOIN {ext.table.name} {alias} ON {baseTable.name}.{basePkColumnName} = {alias}.{ext.fkColumn}")
  |> String.concat "\n         "

/// Generate column list for SELECT with proper aliases
let private generateSelectColumns (baseTable: CreateTable) (extensions: ExtensionTable list) : string =
  let baseColumns =
    baseTable.columns
    |> List.map (fun c -> $"{baseTable.name}.{c.name}")
    |> String.concat ", "

  let extensionColumns =
    extensions
    |> List.mapi (fun i ext ->
      ext.table.columns
      |> List.filter (fun col -> col.name <> ext.fkColumn)
      |> List.map (fun c -> $"ext{i}.{c.name}")
      |> String.concat ", ")
    |> List.filter (fun s -> s <> "")
    |> String.concat ", "

  if extensionColumns = "" then
    baseColumns
  else
    $"{baseColumns}, {extensionColumns}"

/// Generate field reading code for base table columns
let private generateBaseFieldReads (baseTable: CreateTable) (startIndex: int) : string list =
  baseTable.columns
  |> List.mapi (fun i col ->
    let fieldName = TypeGenerator.toPascalCase col.name
    let colIndex = startIndex + i
    let isNullable = TypeGenerator.isColumnNullable col
    let readerMethod = TypeGenerator.mapSqlType col.columnType false |> readerMethod

    if isNullable then
      $"{fieldName} = if reader.IsDBNull {colIndex} then None else Some(reader.Get{readerMethod} {colIndex})"
    else
      $"{fieldName} = reader.Get{readerMethod} {colIndex}")

/// Generate field reading code for extension columns (excluding FK)
let private generateExtensionFieldReads (extension: ExtensionTable) (startIndex: int) : string list =
  extension.table.columns
  |> List.filter (fun col -> col.name <> extension.fkColumn)
  |> List.mapi (fun i col ->
    let fieldName = TypeGenerator.toPascalCase col.name
    let colIndex = startIndex + i
    let isNullable = TypeGenerator.isColumnNullable col
    let readerMethod = TypeGenerator.mapSqlType col.columnType false |> readerMethod

    if isNullable then
      $"{fieldName} = if reader.IsDBNull {colIndex} then None else Some(reader.Get{readerMethod} {colIndex})"
    else
      $"{fieldName} = reader.Get{readerMethod} {colIndex}")

/// Generate pattern matching for case selection based on NULL checks
/// baseIndent is the number of spaces for the base indentation (12 or 14 for async)
let private generateCaseSelection
  (baseIndent: int)
  (baseTable: CreateTable)
  (extensions: ExtensionTable list)
  (typeName: string)
  : string =
  let indent = String.replicate baseIndent " "
  let indent2 = String.replicate (baseIndent + 2) " "
  let indent4 = String.replicate (baseIndent + 4) " "

  // Generate NULL check variables for each extension
  let nullChecks =
    extensions
    |> List.mapi (fun i ext ->
      let firstExtCol =
        ext.table.columns
        |> List.filter (fun col -> col.name <> ext.fkColumn)
        |> List.head

      let colIndex =
        baseTable.columns.Length
        + (extensions |> List.take i |> List.sumBy (fun e -> e.table.columns.Length - 1))

      $"let has{TypeGenerator.toPascalCase ext.aspectName} = not (reader.IsDBNull {colIndex})")
    |> String.concat $"\n{indent}"

  // Generate base field reads
  let baseFields = generateBaseFieldReads baseTable 0 |> String.concat $",\n{indent}"

  // Generate match patterns for each extension
  let matchPatterns =
    extensions
    |> List.mapi (fun i ext ->
      let caseName = TypeGenerator.toPascalCase ext.aspectName

      let pattern =
        extensions
        |> List.mapi (fun j _ -> if i = j then "true" else "false")
        |> String.concat ", "

      let allFields =
        generateBaseFieldReads baseTable 0
        @ generateExtensionFieldReads
            ext
            (baseTable.columns.Length
             + (extensions |> List.take i |> List.sumBy (fun e -> e.table.columns.Length - 1)))
        |> String.concat $",\n{indent}"

      $"{indent}| {pattern} ->\n{indent4}{typeName}.With{caseName} ({allFields})")
    |> String.concat "\n"

  // Base case pattern (all false)
  let basePattern = extensions |> List.map (fun _ -> "false") |> String.concat ", "

  let baseCaseMatch =
    $"{indent}| {basePattern} ->\n{indent4}{typeName}.Base ({baseFields})"

  // Default case only needed when there are 2+ extensions (to handle unexpected combinations like true, true)
  // With a single extension, true/false already covers all cases
  let defaultCase =
    if extensions.Length > 1 then
      $"\n{indent}| _ ->\n{indent4}// Multiple extensions active - choosing Base case\n{indent4}{typeName}.Base ({baseFields})"
    else
      ""

  $"""{indent}{nullChecks}

{indent}match {extensions
               |> List.map (fun ext -> $"has{TypeGenerator.toPascalCase ext.aspectName}")
               |> String.concat ", "} with
{matchPatterns}
{baseCaseMatch}{defaultCase}"""

/// Generate SelectAll method for normalized table
let generateGetAll (normalized: NormalizedTable) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let selectColumns = generateSelectColumns normalized.baseTable normalized.extensions

  let leftJoins =
    if normalized.extensions.IsEmpty then
      ""
    else
      "\n         " + generateLeftJoins normalized.baseTable normalized.extensions

  let getSql =
    $"SELECT {selectColumns}\n         FROM {normalized.baseTable.name}{leftJoins}"

  let caseSelection =
    generateCaseSelection 14 normalized.baseTable normalized.extensions typeName

  $"""  static member SelectAll (tx: SqliteTransaction) : Task<Result<{typeName} list, SqliteException>> =
    task {{
      try
        use cmd = new SqliteCommand("{getSql}", tx.Connection, tx)
        use! reader = cmd.ExecuteReaderAsync()
        let results = ResizeArray<{typeName}>()
        let mutable hasMore = true
        while hasMore do
          let! next = reader.ReadAsync()
          hasMore <- next
          if hasMore then
            let item =
{caseSelection}
            results.Add item
        return Ok(results |> Seq.toList)
      with
      | :? SqliteException as ex -> return Error ex
    }}"""

/// Generate SelectById method for normalized table
let generateGetById (normalized: NormalizedTable) : string option =
  let pkCols = getPrimaryKeyColumns normalized.baseTable

  match pkCols with
  | [] -> None
  | pks ->
    let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
    let selectColumns = generateSelectColumns normalized.baseTable normalized.extensions

    let leftJoins =
      if normalized.extensions.IsEmpty then
        ""
      else
        "\n         " + generateLeftJoins normalized.baseTable normalized.extensions

    let whereClause =
      pks
      |> List.map (fun pk -> $"{normalized.baseTable.name}.{pk.name} = @{pk.name}")
      |> String.concat " AND "

    let getSql =
      $"SELECT {selectColumns}\n         FROM {normalized.baseTable.name}{leftJoins}\n         WHERE {whereClause}"

    let paramList =
      pks
      |> List.map (fun pk ->
        let pkType = TypeGenerator.mapSqlType pk.columnType false
        $"({pk.name}: {pkType})")
      |> String.concat " "

    let asyncParamBindings =
      pks
      |> List.map (fun pk -> $"cmd.Parameters.AddWithValue(\"@{pk.name}\", {pk.name}) |> ignore")
      |> String.concat "\n        "

    let caseSelection =
      generateCaseSelection 12 normalized.baseTable normalized.extensions typeName

    Some
      $"""  static member SelectById {paramList} (tx: SqliteTransaction) : Task<Result<{typeName} option, SqliteException>> =
    task {{
      try
        use cmd = new SqliteCommand("{getSql}", tx.Connection, tx)
        {asyncParamBindings}
        use! reader = cmd.ExecuteReaderAsync()
        let! hasRow = reader.ReadAsync()
        if hasRow then
          let item =
{caseSelection}
          return Ok(Some item)
        else
          return Ok None
      with
      | :? SqliteException as ex -> return Error ex
    }}"""

/// Generate SelectOne method for normalized table
let generateGetOne (normalized: NormalizedTable) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let selectColumns = generateSelectColumns normalized.baseTable normalized.extensions

  let leftJoins =
    if normalized.extensions.IsEmpty then
      ""
    else
      "\n         " + generateLeftJoins normalized.baseTable normalized.extensions

  let getSql =
    $"SELECT {selectColumns}\n         FROM {normalized.baseTable.name}{leftJoins}\n         LIMIT 1"

  let caseSelection =
    generateCaseSelection 12 normalized.baseTable normalized.extensions typeName

  $"""  static member SelectOne (tx: SqliteTransaction) : Task<Result<{typeName} option, SqliteException>> =
    task {{
      try
        use cmd = new SqliteCommand("{getSql}", tx.Connection, tx)
        use! reader = cmd.ExecuteReaderAsync()
        let! hasRow = reader.ReadAsync()
        if hasRow then
          let item =
{caseSelection}
          return Ok(Some item)
        else
          return Ok None
      with
      | :? SqliteException as ex -> return Error ex
    }}"""

/// Generate UPDATE SQL for base table
let private generateUpdateBaseSql (baseTable: CreateTable) : string =
  let pkCols =
    getPrimaryKeyColumns baseTable |> List.map (fun c -> c.name) |> Set.ofList

  let updateCols =
    baseTable.columns |> List.filter (fun col -> not (Set.contains col.name pkCols))

  let setClauses =
    updateCols |> List.map (fun c -> $"{c.name} = @{c.name}") |> String.concat ", "

  let whereClause =
    pkCols
    |> Set.toList
    |> List.map (fun pk -> $"{pk} = @{pk}")
    |> String.concat " AND "

  $"UPDATE {baseTable.name} SET {setClauses} WHERE {whereClause}"

/// Generate Base case update (UPDATE base, DELETE extensions)
let private generateUpdateBaseCase
  (baseTable: CreateTable)
  (extensions: ExtensionTable list)
  (typeName: string)
  : string =
  let updateSql = generateUpdateBaseSql baseTable
  let fieldPattern = generateFieldPattern baseTable.columns

  // Get the Id variable name for delete statements
  let idCol =
    baseTable.columns
    |> List.find (fun col ->
      col.constraints
      |> List.exists (fun c ->
        match c with
        | PrimaryKey _ -> true
        | _ -> false))

  let idFieldName = TypeGenerator.toPascalCase idCol.name

  let idVarName =
    idFieldName.ToLower().[0..0]
    + (if idFieldName.Length > 1 then idFieldName.[1..] else "")

  let asyncParamBindings =
    generateParamBindings baseTable.columns "cmd" |> String.concat "\n          "

  let deleteStatements =
    extensions
    |> List.map (fun ext ->
      $"          use delCmd{ext.aspectName} = new SqliteCommand(\"DELETE FROM {ext.table.name} WHERE {ext.fkColumn} = @id\", tx.Connection, tx)\n          delCmd{ext.aspectName}.Parameters.AddWithValue(\"@id\", {idVarName}) |> ignore\n          MigrationLog.ensureWriteAllowed tx\n          let! _ = delCmd{ext.aspectName}.ExecuteNonQueryAsync()")
    |> String.concat "\n"

  $"""        | {typeName}.Base({fieldPattern}) ->
          // Update base table, delete all extensions
          use cmd = new SqliteCommand("{updateSql}", tx.Connection, tx)
          {asyncParamBindings}
          MigrationLog.ensureWriteAllowed tx
          let! _ = cmd.ExecuteNonQueryAsync()

{deleteStatements}
          return Ok()"""

/// Generate extension case update (UPDATE base, INSERT OR REPLACE extension)
let private generateUpdateExtensionCase
  (baseTable: CreateTable)
  (extension: ExtensionTable)
  (allExtensions: ExtensionTable list)
  (typeName: string)
  : string =
  let caseName = TypeGenerator.toPascalCase extension.aspectName
  let updateSql = generateUpdateBaseSql baseTable

  // Extension columns excluding FK
  let extensionInsertColumns =
    extension.table.columns
    |> List.filter (fun col -> col.name <> extension.fkColumn)

  let extensionColumnNames =
    extensionInsertColumns |> List.map (fun c -> c.name) |> String.concat ", "

  let extensionParamNames =
    extensionInsertColumns |> List.map (fun c -> $"@{c.name}") |> String.concat ", "

  let insertOrReplaceSql =
    $"INSERT OR REPLACE INTO {extension.table.name} ({extension.fkColumn}, {extensionColumnNames}) VALUES (@{extension.fkColumn}, {extensionParamNames})"

  // Combine all columns for the field pattern
  let allColumns = baseTable.columns @ extensionInsertColumns
  let fieldPattern = generateFieldPattern allColumns

  // Get the Id variable name for delete statements
  let idCol =
    baseTable.columns
    |> List.find (fun col ->
      col.constraints
      |> List.exists (fun c ->
        match c with
        | PrimaryKey _ -> true
        | _ -> false))

  let idFieldName = TypeGenerator.toPascalCase idCol.name

  let idVarName =
    idFieldName.ToLower().[0..0]
    + (if idFieldName.Length > 1 then idFieldName.[1..] else "")

  let asyncBaseParamBindings =
    generateParamBindings baseTable.columns "cmd1" |> String.concat "\n          "

  let asyncExtensionParamBindings =
    generateParamBindings extensionInsertColumns "cmd2"
    |> String.concat "\n          "

  // Delete other extensions
  let deleteOtherExtensions =
    allExtensions
    |> List.filter (fun e -> e.table.name <> extension.table.name)
    |> List.map (fun ext ->
      $"          use delCmd{ext.aspectName} = new SqliteCommand(\"DELETE FROM {ext.table.name} WHERE {ext.fkColumn} = @id\", tx.Connection, tx)\n          delCmd{ext.aspectName}.Parameters.AddWithValue(\"@id\", {idVarName}) |> ignore\n          MigrationLog.ensureWriteAllowed tx\n          let! _ = delCmd{ext.aspectName}.ExecuteNonQueryAsync()")
    |> String.concat "\n"

  $"""        | {typeName}.With{caseName}({fieldPattern}) ->
          // Update base, INSERT OR REPLACE extension
          use cmd1 = new SqliteCommand("{updateSql}", tx.Connection, tx)
          {asyncBaseParamBindings}
          MigrationLog.ensureWriteAllowed tx
          let! _ = cmd1.ExecuteNonQueryAsync()

          use cmd2 = new SqliteCommand("{insertOrReplaceSql}", tx.Connection, tx)
          cmd2.Parameters.AddWithValue("@{extension.fkColumn}", {idVarName}) |> ignore
          {asyncExtensionParamBindings}
          MigrationLog.ensureWriteAllowed tx
          let! _ = cmd2.ExecuteNonQueryAsync()

{deleteOtherExtensions}
          return Ok()"""

/// Generate Update method for normalized table
let generateUpdate (normalized: NormalizedTable) : string option =
  let pkCols = getPrimaryKeyColumns normalized.baseTable

  match pkCols with
  | [] -> None
  | _ ->
    let typeName = TypeGenerator.toPascalCase normalized.baseTable.name

    // Generate Base case
    let baseCase =
      generateUpdateBaseCase normalized.baseTable normalized.extensions typeName

    // Generate extension cases
    let extensionCases =
      normalized.extensions
      |> List.map (fun ext -> generateUpdateExtensionCase normalized.baseTable ext normalized.extensions typeName)
      |> String.concat "\n\n"

    let allCases =
      if normalized.extensions.IsEmpty then
        baseCase
      else
        $"{baseCase}\n\n{extensionCases}"

    Some
      $"""  static member Update (item: {typeName}) (tx: SqliteTransaction)
    : Task<Result<unit, SqliteException>> =
    task {{
      try
        match item with
{allCases}
      with
      | :? SqliteException as ex -> return Error ex
    }}"""

/// Generate Delete method for normalized table
let generateDelete (normalized: NormalizedTable) : string option =
  let pkCols = getPrimaryKeyColumns normalized.baseTable

  match pkCols with
  | [] -> None
  | pks ->
    let typeName = TypeGenerator.toPascalCase normalized.baseTable.name

    let whereClause =
      pks |> List.map (fun pk -> $"{pk.name} = @{pk.name}") |> String.concat " AND "

    let deleteSql = $"DELETE FROM {normalized.baseTable.name} WHERE {whereClause}"

    let paramList =
      pks
      |> List.map (fun pk ->
        let pkType = TypeGenerator.mapSqlType pk.columnType false
        $"({pk.name}: {pkType})")
      |> String.concat " "

    // Build the async method body using AST with task CE
    let asyncBodyExprs =
      [ OtherExpr $"use cmd = new SqliteCommand(\"{deleteSql}\", tx.Connection, tx)" ]
      @ (pks
         |> List.map (fun pk -> OtherExpr $"cmd.Parameters.AddWithValue(\"@{pk.name}\", {pk.name}) |> ignore"))
      @ [ OtherExpr "MigrationLog.ensureWriteAllowed tx"
          OtherExpr "let! _ = cmd.ExecuteNonQueryAsync()"
          OtherExpr "return Ok()" ]

    let memberName = $"Delete {paramList} (tx: SqliteTransaction)"
    let returnType = "Task<Result<unit, SqliteException>>"
    let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]

    Some(generateStaticMemberCode typeName memberName returnType body)

/// Get all columns from normalized table (base + all extensions)
let private getAllNormalizedColumns (normalized: NormalizedTable) : (string * ColumnDef) list =
  let baseColumns = normalized.baseTable.columns |> List.map (fun c -> ("Base", c))

  let extensionColumns =
    normalized.extensions
    |> List.collect (fun ext -> ext.table.columns |> List.map (fun c -> (ext.aspectName, c)))

  baseColumns @ extensionColumns

/// Validate QueryBy annotation for normalized table references existing columns (case-insensitive)
let private validateNormalizedQueryByAnnotation
  (normalized: NormalizedTable)
  (annotation: QueryByAnnotation)
  : Result<unit, string> =
  let allColumns = getAllNormalizedColumns normalized

  let columnNames =
    allColumns |> List.map (fun (_, c) -> c.name.ToLowerInvariant()) |> Set.ofList

  annotation.columns
  |> List.tryFind (fun col -> not (columnNames.Contains(col.ToLowerInvariant())))
  |> function
    | Some invalidCol ->
      let availableCols =
        allColumns |> List.map (fun (_, c) -> c.name) |> String.concat ", "

      Error
        $"QueryBy annotation references non-existent column '{invalidCol}' in normalized table '{normalized.baseTable.name}'. Available columns: {availableCols}"
    | None -> Ok()

/// Validate QueryLike annotation for normalized table references one existing column (case-insensitive)
let private validateNormalizedQueryLikeAnnotation
  (normalized: NormalizedTable)
  (annotation: QueryLikeAnnotation)
  : Result<unit, string> =
  match annotation.columns with
  | [] -> Error $"QueryLike annotation on normalized table '{normalized.baseTable.name}' requires exactly one column."
  | [ col ] ->
    let allColumns = getAllNormalizedColumns normalized

    let columnNames =
      allColumns |> List.map (fun (_, c) -> c.name.ToLowerInvariant()) |> Set.ofList

    if columnNames.Contains(col.ToLowerInvariant()) then
      Ok()
    else
      let availableCols =
        allColumns |> List.map (fun (_, c) -> c.name) |> String.concat ", "

      Error
        $"QueryLike annotation references non-existent column '{col}' in normalized table '{normalized.baseTable.name}'. Available columns: {availableCols}"
  | _ ->
    let receivedCols = annotation.columns |> String.concat ", "

    Error
      $"QueryLike annotation on normalized table '{normalized.baseTable.name}' supports exactly one column. Received: {receivedCols}"

/// Find column in normalized table by name (case-insensitive)
let private findNormalizedColumn (normalized: NormalizedTable) (colName: string) : (string * ColumnDef) option =
  getAllNormalizedColumns normalized
  |> List.tryFind (fun (_, c) -> c.name.ToLowerInvariant() = colName.ToLowerInvariant())

/// Generate custom QueryBy method for normalized tables with tupled parameters
let private generateNormalizedQueryBy (normalized: NormalizedTable) (annotation: QueryByAnnotation) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name

  // 1. Build method name: SelectByIdName
  let methodName =
    annotation.columns
    |> List.map TypeGenerator.toPascalCase
    |> String.concat ""
    |> sprintf "SelectBy%s"

  // 2. Build tupled parameters with types (case-insensitive column lookup)
  let parameters =
    annotation.columns
    |> List.map (fun col ->
      let _, columnDef = findNormalizedColumn normalized col |> Option.get
      let isNullable = TypeGenerator.isColumnNullable columnDef
      let fsharpType = TypeGenerator.mapSqlType columnDef.columnType isNullable
      $"{col}: {fsharpType}")
    |> String.concat ", "

  // 3. Build WHERE clause: id = @id AND name = @name
  let whereClause =
    annotation.columns
    |> List.map (fun col -> $"{col} = @{col}")
    |> String.concat " AND "

  // 4. Generate LEFT JOIN SQL and column selections (same as generateGetAll)
  let baseColumns = normalized.baseTable.columns |> List.map (fun c -> $"b.{c.name}")

  let extensionSelects =
    normalized.extensions
    |> List.collect (fun ext -> ext.table.columns |> List.map (fun c -> $"e{ext.aspectName}.{c.name}"))

  let allSelects = (baseColumns @ extensionSelects) |> String.concat ", "

  let joins =
    normalized.extensions
    |> List.map (fun ext ->
      let pk = getPrimaryKeyColumns normalized.baseTable |> List.head
      $"LEFT JOIN {ext.table.name} AS e{ext.aspectName} ON b.{pk.name} = e{ext.aspectName}.{ext.fkColumn}")
    |> String.concat "\n        "

  let sql =
    if normalized.extensions.IsEmpty then
      $"SELECT {allSelects} FROM {normalized.baseTable.name} AS b WHERE {whereClause}"
    else
      $"""SELECT {allSelects}
        FROM {normalized.baseTable.name} AS b
        {joins}
        WHERE {whereClause}"""

  // 5. Build async parameter bindings
  let asyncParamBindings =
    annotation.columns
    |> List.map (fun col ->
      let _, columnDef = findNormalizedColumn normalized col |> Option.get
      let isNullable = TypeGenerator.isColumnNullable columnDef

      if isNullable then
        $"cmd.Parameters.AddWithValue(\"@{col}\", match {col} with Some v -> box v | None -> box DBNull.Value) |> ignore"
      else
        $"cmd.Parameters.AddWithValue(\"@{col}\", {col}) |> ignore")
    |> String.concat "\n        "

  // 6. Generate case selection logic (async needs 14-space indent)
  let caseSelection =
    generateCaseSelection 14 normalized.baseTable normalized.extensions typeName

  $"""  static member {methodName} ({parameters}) (tx: SqliteTransaction) : Task<Result<{typeName} list, SqliteException>> =
    task {{
      try
        use cmd = new SqliteCommand("{sql}", tx.Connection, tx)
        {asyncParamBindings}
        use! reader = cmd.ExecuteReaderAsync()
        let results = ResizeArray<{typeName}>()
        let mutable hasMore = true
        while hasMore do
          let! next = reader.ReadAsync()
          hasMore <- next
          if hasMore then
            let record =
{caseSelection}
            results.Add(record)
        return Ok(results |> Seq.toList)
      with
      | :? SqliteException as ex -> return Error ex
    }}"""

/// Generate custom QueryLike method for normalized tables with SQL LIKE '%value%' semantics
let private generateNormalizedQueryLike (normalized: NormalizedTable) (annotation: QueryLikeAnnotation) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let col = annotation.columns |> List.head

  let methodName = $"Select{TypeGenerator.toPascalCase col}Like"

  let parameters =
    let _, columnDef = findNormalizedColumn normalized col |> Option.get
    let isNullable = TypeGenerator.isColumnNullable columnDef
    let fsharpType = TypeGenerator.mapSqlType columnDef.columnType isNullable
    $"{col}: {fsharpType}"

  let whereClause = $"{col} LIKE '%%' || @{col} || '%%'"

  let baseColumns = normalized.baseTable.columns |> List.map (fun c -> $"b.{c.name}")

  let extensionSelects =
    normalized.extensions
    |> List.collect (fun ext -> ext.table.columns |> List.map (fun c -> $"e{ext.aspectName}.{c.name}"))

  let allSelects = (baseColumns @ extensionSelects) |> String.concat ", "

  let joins =
    normalized.extensions
    |> List.map (fun ext ->
      let pk = getPrimaryKeyColumns normalized.baseTable |> List.head
      $"LEFT JOIN {ext.table.name} AS e{ext.aspectName} ON b.{pk.name} = e{ext.aspectName}.{ext.fkColumn}")
    |> String.concat "\n        "

  let sql =
    if normalized.extensions.IsEmpty then
      $"SELECT {allSelects} FROM {normalized.baseTable.name} AS b WHERE {whereClause}"
    else
      $"""SELECT {allSelects}
        FROM {normalized.baseTable.name} AS b
        {joins}
        WHERE {whereClause}"""

  let asyncParamBindings =
    let _, columnDef = findNormalizedColumn normalized col |> Option.get
    let isNullable = TypeGenerator.isColumnNullable columnDef

    if isNullable then
      $"cmd.Parameters.AddWithValue(\"@{col}\", match {col} with Some v -> box v | None -> box DBNull.Value) |> ignore"
    else
      $"cmd.Parameters.AddWithValue(\"@{col}\", {col}) |> ignore"

  let caseSelection =
    generateCaseSelection 14 normalized.baseTable normalized.extensions typeName

  $"""  static member {methodName} ({parameters}) (tx: SqliteTransaction) : Task<Result<{typeName} list, SqliteException>> =
    task {{
      try
        use cmd = new SqliteCommand("{sql}", tx.Connection, tx)
        {asyncParamBindings}
        use! reader = cmd.ExecuteReaderAsync()
        let results = ResizeArray<{typeName}>()
        let mutable hasMore = true
        while hasMore do
          let! next = reader.ReadAsync()
          hasMore <- next
          if hasMore then
            let record =
{caseSelection}
            results.Add(record)
        return Ok(results |> Seq.toList)
      with
      | :? SqliteException as ex -> return Error ex
    }}"""

/// Validate QueryByOrCreate annotation for normalized table references existing columns (case-insensitive)
let private validateNormalizedQueryByOrCreateAnnotation
  (normalized: NormalizedTable)
  (annotation: QueryByOrCreateAnnotation)
  : Result<unit, string> =
  let allColumns = getAllNormalizedColumns normalized

  let allColumnNames =
    allColumns |> List.map (fun (_, c) -> c.name.ToLowerInvariant()) |> Set.ofList

  // Check if all columns exist somewhere (in base or any extension)
  annotation.columns
  |> List.tryFind (fun col -> not (allColumnNames.Contains(col.ToLowerInvariant())))
  |> function
    | Some invalidCol ->
      let availableCols =
        allColumns |> List.map (fun (_, c) -> c.name) |> String.concat ", "

      Error
        $"QueryByOrCreate annotation references non-existent column '{invalidCol}' in normalized table '{normalized.baseTable.name}'. Available columns: {availableCols}"
    | None -> Ok()

/// Check if a DU case (specified by its columns) has all the query columns
let private caseHasAllQueryColumns (caseColumns: ColumnDef list) (queryColumns: string list) : bool =
  let caseColumnNames =
    caseColumns |> List.map (fun c -> c.name.ToLowerInvariant()) |> Set.ofList

  queryColumns
  |> List.forall (fun col -> caseColumnNames.Contains(col.ToLowerInvariant()))

/// Generate custom QueryByOrCreate method for normalized tables that extracts query values from NewType DU
let private generateNormalizedQueryByOrCreate
  (normalized: NormalizedTable)
  (annotation: QueryByOrCreateAnnotation)
  : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let newTypeName = $"New{typeName}"

  let basePkColumn =
    getPrimaryKeyColumns normalized.baseTable
    |> List.tryHead
    |> Option.map (fun pk -> pk.name)
    |> Option.defaultValue "id"

  // 1. Build method name: SelectByNameOrInsert
  let methodName =
    annotation.columns
    |> List.map TypeGenerator.toPascalCase
    |> String.concat ""
    |> sprintf "SelectBy%sOrInsert"

  // 2. Generate value extraction from NewType DU (pattern match on all cases)
  // Cases that don't have all query columns will throw an exception
  // Get base insert columns (excluding auto-increment PK)
  let baseInsertColumns = getInsertColumns normalized.baseTable

  // Check which cases have all query columns
  let baseHasAllColumns = caseHasAllQueryColumns baseInsertColumns annotation.columns

  // Generate match arms for value extraction with configurable indentation
  // If a case doesn't have all query columns, generate an exception arm
  let generateBaseMatch (indent: string) =
    if baseHasAllColumns then
      // Generate extraction pattern for all query columns
      let extractions =
        annotation.columns
        |> List.map (fun col ->
          let _, varName = generateSingleFieldPattern baseInsertColumns col
          varName)
        |> String.concat ", "

      let pattern = generateFieldPattern baseInsertColumns
      $"{indent}| {newTypeName}.Base({pattern}) -> ({extractions})"
    else
      let pattern = generateFieldPattern baseInsertColumns
      let missingCols = annotation.columns |> String.concat ", "
      $"{indent}| {newTypeName}.Base({pattern}) -> invalidArg \"newItem\" \"Base case does not have the required fields ({missingCols}) for this QueryByOrCreate operation\""

  let generateExtensionMatches (indent: string) =
    normalized.extensions
    |> List.map (fun ext ->
      let caseName = $"With{TypeGenerator.toPascalCase ext.aspectName}"
      // Extension columns = base insert columns + extension columns (excluding FK)
      let extensionCols =
        ext.table.columns |> List.filter (fun c -> c.name <> ext.fkColumn)

      let allCols = baseInsertColumns @ extensionCols
      let extHasAllColumns = caseHasAllQueryColumns allCols annotation.columns

      if extHasAllColumns then
        let extractions =
          annotation.columns
          |> List.map (fun col ->
            let _, varName = generateSingleFieldPattern allCols col
            varName)
          |> String.concat ", "

        let pattern = generateFieldPattern allCols
        $"{indent}| {newTypeName}.{caseName}({pattern}) -> ({extractions})"
      else
        let pattern = generateFieldPattern allCols
        let missingCols = annotation.columns |> String.concat ", "
        $"{indent}| {newTypeName}.{caseName}({pattern}) -> invalidArg \"newItem\" \"{caseName} case does not have the required fields ({missingCols}) for this QueryByOrCreate operation\"")
    |> String.concat "\n"

  // Generate variable bindings for extracted query columns
  let varBindings =
    annotation.columns |> List.map (fun col -> col) |> String.concat ", "

  // Generate value extractions with proper indentation
  let generateValueExtractions (letIndent: string) (matchIndent: string) =
    let baseMatch = generateBaseMatch matchIndent
    let extensionMatches = generateExtensionMatches matchIndent

    let allMatches =
      if extensionMatches = "" then
        baseMatch
      else
        $"{baseMatch}\n{extensionMatches}"

    $"{letIndent}let ({varBindings}) = \n{matchIndent}match newItem with\n{allMatches}"

  // 3. Build WHERE clause
  let whereClause =
    annotation.columns
    |> List.map (fun col -> $"{col} = @{col}")
    |> String.concat " AND "

  // 4. Generate LEFT JOIN SQL and column selections (same as generateGetAll)
  let baseColumns = normalized.baseTable.columns |> List.map (fun c -> $"b.{c.name}")

  let extensionSelects =
    normalized.extensions
    |> List.collect (fun ext -> ext.table.columns |> List.map (fun c -> $"e{ext.aspectName}.{c.name}"))

  let allSelects = (baseColumns @ extensionSelects) |> String.concat ", "

  let joins =
    normalized.extensions
    |> List.map (fun ext ->
      $"LEFT JOIN {ext.table.name} e{ext.aspectName} ON b.{basePkColumn} = e{ext.aspectName}.{ext.fkColumn}")
    |> String.concat "\n      "

  let selectSql =
    if normalized.extensions.IsEmpty then
      $"SELECT {allSelects} FROM {normalized.baseTable.name} b WHERE {whereClause} LIMIT 1"
    else
      $"SELECT {allSelects} FROM {normalized.baseTable.name} b\n      {joins}\n      WHERE {whereClause} LIMIT 1"

  // 5. Build async parameter bindings
  let generateAsyncParamBindings (cmdVarName: string) =
    annotation.columns
    |> List.map (fun col ->
      let _, columnDef = findNormalizedColumn normalized col |> Option.get
      let isNullable = TypeGenerator.isColumnNullable columnDef

      if isNullable then
        $"{cmdVarName}.Parameters.AddWithValue(\"@{col}\", match {col} with Some v -> box v | None -> box DBNull.Value) |> ignore"
      else
        $"{cmdVarName}.Parameters.AddWithValue(\"@{col}\", {col}) |> ignore")
    |> String.concat "\n        "

  let asyncParamBindings = generateAsyncParamBindings "cmd"
  let asyncRequeryParamBindings = generateAsyncParamBindings "cmd2"

  // 6. Generate mapping logic (async needs 12-space indent)
  let caseSelection =
    generateCaseSelection 12 normalized.baseTable normalized.extensions typeName

  let nestedCaseSelection =
    generateCaseSelection 16 normalized.baseTable normalized.extensions typeName

  // Async: 8-space let indent, 10-space match arm indent
  let valueExtractions = generateValueExtractions "        " "          "

  $"""  static member {methodName} (newItem: {newTypeName}) (tx: SqliteTransaction) : Task<Result<{typeName}, SqliteException>> =
    task {{
      try
        // Extract query values from NewType DU
{valueExtractions}
        // Try to find existing record
        use cmd = new SqliteCommand("{selectSql}", tx.Connection, tx)
        {asyncParamBindings}
        use! reader = cmd.ExecuteReaderAsync()
        let! hasRow = reader.ReadAsync()
        if hasRow then
          // Found existing record - return it
          let item =
{caseSelection}
          return Ok item
        else
          // Not found - insert and fetch
          reader.Close()
          let! insertResult = {typeName}.Insert newItem tx
          match insertResult with
          | Ok _ ->
            use cmd2 = new SqliteCommand("{selectSql}", tx.Connection, tx)
            {asyncRequeryParamBindings}
            use! reader = cmd2.ExecuteReaderAsync()
            let! hasInsertedRow = reader.ReadAsync()
            if hasInsertedRow then
              let insertedItem =
{nestedCaseSelection}
              return Ok insertedItem
            else
              return Error (SqliteException("Failed to retrieve inserted record", 0))
          | Error ex -> return Error ex
      with
      | :? SqliteException as ex -> return Error ex
    }}"""

/// Generate all methods for a normalized table and format with Fantomas
let generateNormalizedTableCode (normalized: NormalizedTable) : Result<string, string> =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name

  // Validate all QueryBy annotations
  let queryByValidationResults =
    normalized.baseTable.queryByAnnotations
    |> List.map (validateNormalizedQueryByAnnotation normalized)

  // Validate all QueryLike annotations
  let queryLikeValidationResults =
    normalized.baseTable.queryLikeAnnotations
    |> List.map (validateNormalizedQueryLikeAnnotation normalized)

  // Validate all QueryByOrCreate annotations
  let queryByOrCreateValidationResults =
    normalized.baseTable.queryByOrCreateAnnotations
    |> List.map (validateNormalizedQueryByOrCreateAnnotation normalized)

  let firstError =
    (queryByValidationResults
     @ queryLikeValidationResults
     @ queryByOrCreateValidationResults)
    |> List.tryFind (fun r ->
      match r with
      | Error _ -> true
      | _ -> false)

  match firstError with
  | Some(Error msg) -> Error msg
  | _ ->
    // Generate all method strings
    let insertMethod = generateInsert normalized

    let insertOrIgnoreMethod =
      if normalized.baseTable.insertOrIgnoreAnnotations.IsEmpty then
        None
      else
        Some(generateInsertOrIgnore normalized)

    let getAllMethod = generateGetAll normalized
    let getByIdMethod = generateGetById normalized
    let getOneMethod = generateGetOne normalized
    let updateMethod = generateUpdate normalized
    let deleteMethod = generateDelete normalized

    // Generate QueryBy methods
    let queryByMethods =
      normalized.baseTable.queryByAnnotations
      |> List.map (generateNormalizedQueryBy normalized)

    // Generate QueryLike methods
    let queryLikeMethods =
      normalized.baseTable.queryLikeAnnotations
      |> List.map (generateNormalizedQueryLike normalized)

    // Generate QueryByOrCreate methods
    let queryByOrCreateMethods =
      normalized.baseTable.queryByOrCreateAnnotations
      |> List.map (generateNormalizedQueryByOrCreate normalized)

    // Collect all methods
    let allMethods =
      [ Some insertMethod
        insertOrIgnoreMethod
        Some getAllMethod
        getByIdMethod
        Some getOneMethod
        updateMethod
        deleteMethod ]
      @ (queryByMethods |> List.map Some)
      @ (queryLikeMethods |> List.map Some)
      @ (queryByOrCreateMethods |> List.map Some)
      |> List.choose id
      |> String.concat "\n\n"

    // Build the type extension and format with Fantomas
    let code = $"type {typeName} with\n{allMethods}"
    Ok(FabulousAstHelpers.formatCode code)
