/// Module for generating CRUD query methods for normalized schemas with discriminated unions.
/// Generates methods with pattern matching for DU cases.
module internal migrate.CodeGen.NormalizedQueryGenerator

open migrate.DeclarativeMigrations.Types

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

/// Generate named field pattern for pattern matching (e.g., "Name = name, Age = age")
let private generateFieldPattern (columns: ColumnDef list) : string =
  columns
  |> List.map (fun col ->
    let fieldName = TypeGenerator.toPascalCase col.name

    let varName =
      fieldName.ToLower().[0..0]
      + (if fieldName.Length > 1 then fieldName.[1..] else "")

    $"{fieldName} = {varName}")
  |> String.concat "; "

/// Generate parameter binding code for columns using variable names
let private generateParamBindings (columns: ColumnDef list) : string list =
  columns
  |> List.map (fun col ->
    let fieldName = TypeGenerator.toPascalCase col.name

    let varName =
      fieldName.ToLower().[0..0]
      + (if fieldName.Length > 1 then fieldName.[1..] else "")

    let isNullable = TypeGenerator.isColumnNullable col

    if isNullable then
      $"cmd.Parameters.AddWithValue(\"@{col.name}\", match {varName} with Some v -> box v | None -> box DBNull.Value) |> ignore"
    else
      $"cmd.Parameters.AddWithValue(\"@{col.name}\", {varName}) |> ignore")

/// Generate the Base case insert (single table)
let private generateBaseCase (baseTable: CreateTable) (typeName: string) : string =
  let insertColumns = getInsertColumns baseTable
  let insertSql = generateInsertSql baseTable.name insertColumns
  let fieldPattern = generateFieldPattern insertColumns

  let paramBindings =
    generateParamBindings insertColumns |> String.concat "\n        "

  $"""      | New{typeName}.Base({fieldPattern}) ->
        // Single INSERT into base table
        use cmd = new SqliteCommand("{insertSql}", tx.Connection, tx)
        {paramBindings}
        cmd.ExecuteNonQuery() |> ignore
        use lastIdCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
        let {baseTable.name}Id = lastIdCmd.ExecuteScalar() |> unbox<int64>
        Ok {baseTable.name}Id"""

/// Generate an extension case insert (multi-table)
let private generateExtensionCase (baseTable: CreateTable) (extension: ExtensionTable) (typeName: string) : string =
  let caseName = TypeGenerator.toPascalCase extension.aspectName
  let baseInsertColumns = getInsertColumns baseTable
  let baseInsertSql = generateInsertSql baseTable.name baseInsertColumns

  // Extension columns excluding the FK column
  let extensionInsertColumns =
    extension.table.columns
    |> List.filter (fun col -> col.name <> extension.fkColumn)

  let extensionInsertSql =
    generateInsertSql extension.table.name extensionInsertColumns

  // Combine all columns for the field pattern
  let allColumns = baseInsertColumns @ extensionInsertColumns
  let fieldPattern = generateFieldPattern allColumns

  let baseParamBindings =
    generateParamBindings baseInsertColumns |> String.concat "\n        "

  let extensionParamBindings =
    generateParamBindings extensionInsertColumns |> String.concat "\n        "

  $"""      | New{typeName}.With{caseName}({fieldPattern}) ->
        // Two inserts in same transaction (atomic)
        use cmd1 = new SqliteCommand("{baseInsertSql}", tx.Connection, tx)
        {baseParamBindings}
        cmd1.ExecuteNonQuery() |> ignore

        use lastIdCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
        let {baseTable.name}Id = lastIdCmd.ExecuteScalar() |> unbox<int64>

        use cmd2 = new SqliteCommand("INSERT INTO {extension.table.name} ({extension.fkColumn}, {extensionInsertColumns |> List.map (fun c -> c.name) |> String.concat ", "}) VALUES (@{extension.fkColumn}, {extensionInsertColumns
                                                                                                                                                                                                              |> List.map (fun c -> $"@{{c.name}}")
                                                                                                                                                                                                              |> String.concat ", "})", tx.Connection, tx)
        cmd2.Parameters.AddWithValue("@{extension.fkColumn}", {baseTable.name}Id) |> ignore
        {extensionParamBindings}
        cmd2.ExecuteNonQuery() |> ignore
        Ok {baseTable.name}Id"""

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
    : Result<int64, SqliteException> =
    try
      match item with
{allCases}
    with
    | :? SqliteException as ex -> Error ex"""

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
  extensions
  |> List.mapi (fun i ext ->
    let alias = $"ext{i}"
    $"LEFT JOIN {ext.table.name} {alias} ON {baseTable.name}.id = {alias}.{ext.fkColumn}")
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
let private generateCaseSelection
  (baseTable: CreateTable)
  (extensions: ExtensionTable list)
  (typeName: string)
  : string =
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
    |> String.concat "\n          "

  // Generate base field reads
  let baseFields = generateBaseFieldReads baseTable 0 |> String.concat "\n          "

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
        |> String.concat "\n          "

      $"          | {pattern} ->
            {typeName}.With{caseName} {{|
          {allFields}
            |}}")
    |> String.concat "\n"

  // Base case pattern (all false)
  let basePattern = extensions |> List.map (fun _ -> "false") |> String.concat ", "

  let baseCaseMatch =
    $"          | {basePattern} ->
            {typeName}.Base {{|
          {baseFields}
            |}}"

  // Default case (multiple extensions - choose first)
  let defaultCase =
    $"          | _ ->
            // Multiple extensions active - choosing Base case
            {typeName}.Base {{|
          {baseFields}
            |}}"

  $"""          {nullChecks}

          match {extensions
                 |> List.map (fun ext -> $"has{TypeGenerator.toPascalCase ext.aspectName}")
                 |> String.concat ", "} with
{matchPatterns}
{baseCaseMatch}
{defaultCase}"""

/// Generate GetAll method for normalized table
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
    generateCaseSelection normalized.baseTable normalized.extensions typeName

  $"""  static member GetAll (tx: SqliteTransaction) : Result<{typeName} list, SqliteException> =
    try
      use cmd = new SqliteCommand("{getSql}", tx.Connection, tx)
      use reader = cmd.ExecuteReader()
      let results = ResizeArray<{typeName}>()
      while reader.Read() do
        let item =
{caseSelection}
        results.Add item
      Ok(results |> Seq.toList)
    with
    | :? SqliteException as ex -> Error ex"""

/// Generate GetById method for normalized table
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

    let paramBindings =
      pks
      |> List.map (fun pk -> $"cmd.Parameters.AddWithValue(\"@{pk.name}\", {pk.name}) |> ignore")
      |> String.concat "\n      "

    let caseSelection =
      generateCaseSelection normalized.baseTable normalized.extensions typeName

    Some
      $"""  static member GetById {paramList} (tx: SqliteTransaction) : Result<{typeName} option, SqliteException> =
    try
      use cmd = new SqliteCommand("{getSql}", tx.Connection, tx)
      {paramBindings}
      use reader = cmd.ExecuteReader()
      if reader.Read() then
        let item =
{caseSelection}
        Ok(Some item)
      else
        Ok None
    with
    | :? SqliteException as ex -> Error ex"""

/// Generate GetOne method for normalized table
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
    generateCaseSelection normalized.baseTable normalized.extensions typeName

  $"""  static member GetOne (tx: SqliteTransaction) : Result<{typeName} option, SqliteException> =
    try
      use cmd = new SqliteCommand("{getSql}", tx.Connection, tx)
      use reader = cmd.ExecuteReader()
      if reader.Read() then
        let item =
{caseSelection}
        Ok(Some item)
      else
        Ok None
    with
    | :? SqliteException as ex -> Error ex"""

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

  let paramBindings =
    generateParamBindings baseTable.columns |> String.concat "\n        "

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

  let deleteStatements =
    extensions
    |> List.map (fun ext ->
      $"        use delCmd{ext.aspectName} = new SqliteCommand(\"DELETE FROM {ext.table.name} WHERE {ext.fkColumn} = @id\", tx.Connection, tx)\n        delCmd{ext.aspectName}.Parameters.AddWithValue(\"@id\", {idVarName}) |> ignore\n        delCmd{ext.aspectName}.ExecuteNonQuery() |> ignore")
    |> String.concat "\n"

  $"""      | {typeName}.Base({fieldPattern}) ->
        // Update base table, delete all extensions
        use cmd = new SqliteCommand("{updateSql}", tx.Connection, tx)
        {paramBindings}
        cmd.ExecuteNonQuery() |> ignore

{deleteStatements}        Ok()"""

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

  let baseParamBindings =
    generateParamBindings baseTable.columns |> String.concat "\n        "

  let extensionParamBindings =
    generateParamBindings extensionInsertColumns |> String.concat "\n        "

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

  // Delete other extensions
  let deleteOtherExtensions =
    allExtensions
    |> List.filter (fun e -> e.table.name <> extension.table.name)
    |> List.map (fun ext ->
      $"        use delCmd{ext.aspectName} = new SqliteCommand(\"DELETE FROM {ext.table.name} WHERE {ext.fkColumn} = @id\", tx.Connection, tx)\n        delCmd{ext.aspectName}.Parameters.AddWithValue(\"@id\", {idVarName}) |> ignore\n        delCmd{ext.aspectName}.ExecuteNonQuery() |> ignore")
    |> String.concat "\n"

  $"""      | {typeName}.With{caseName}({fieldPattern}) ->
        // Update base, INSERT OR REPLACE extension
        use cmd1 = new SqliteCommand("{updateSql}", tx.Connection, tx)
        {baseParamBindings}
        cmd1.ExecuteNonQuery() |> ignore

        use cmd2 = new SqliteCommand("{insertOrReplaceSql}", tx.Connection, tx)
        cmd2.Parameters.AddWithValue("@{extension.fkColumn}", {idVarName}) |> ignore
        {extensionParamBindings}
        cmd2.ExecuteNonQuery() |> ignore

{deleteOtherExtensions}        Ok()"""

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
    : Result<unit, SqliteException> =
    try
      match item with
{allCases}
    with
    | :? SqliteException as ex -> Error ex"""

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

    let paramBindings =
      pks
      |> List.map (fun pk -> $"cmd.Parameters.AddWithValue(\"@{pk.name}\", {pk.name}) |> ignore")
      |> String.concat "\n      "

    Some
      $"""  static member Delete {paramList} (tx: SqliteTransaction)
    : Result<unit, SqliteException> =
    try
      use cmd = new SqliteCommand("{deleteSql}", tx.Connection, tx)
      {paramBindings}
      cmd.ExecuteNonQuery() |> ignore
      Ok()
    with
    | :? SqliteException as ex -> Error ex"""

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

/// Find column in normalized table by name (case-insensitive)
let private findNormalizedColumn (normalized: NormalizedTable) (colName: string) : (string * ColumnDef) option =
  getAllNormalizedColumns normalized
  |> List.tryFind (fun (_, c) -> c.name.ToLowerInvariant() = colName.ToLowerInvariant())

/// Generate custom QueryBy method for normalized tables with tupled parameters
let private generateNormalizedQueryBy (normalized: NormalizedTable) (annotation: QueryByAnnotation) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name

  // 1. Build method name: GetByIdName
  let methodName =
    annotation.columns
    |> List.map TypeGenerator.toPascalCase
    |> String.concat ""
    |> sprintf "GetBy%s"

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

  // 4. Build parameter bindings
  let paramBindings =
    annotation.columns
    |> List.map (fun col ->
      let _, columnDef = findNormalizedColumn normalized col |> Option.get
      let isNullable = TypeGenerator.isColumnNullable columnDef

      if isNullable then
        $"cmd.Parameters.AddWithValue(\"@{col}\", match {col} with Some v -> box v | None -> box DBNull.Value) |> ignore"
      else
        $"cmd.Parameters.AddWithValue(\"@{col}\", {col}) |> ignore")
    |> String.concat "\n      "

  // 5. Generate LEFT JOIN SQL and column selections (same as generateGetAll)
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

  // 6. Generate case selection logic
  let caseSelection =
    generateCaseSelection normalized.baseTable normalized.extensions typeName

  // 7. Generate full method with tupled parameters
  $"""  static member {methodName} ({parameters}) (tx: SqliteTransaction) : Result<{typeName} list, SqliteException> =
    try
      use cmd = new SqliteCommand("{sql}", tx.Connection, tx)
      {paramBindings}
      use reader = cmd.ExecuteReader()
      let results = ResizeArray<{typeName}>()
      while reader.Read() do
        let record =
{caseSelection}
        results.Add(record)
      Ok(results |> Seq.toList)
    with
    | :? SqliteException as ex -> Error ex"""

/// Validate QueryByOrCreate annotation for normalized table references existing columns (case-insensitive)
/// AND ensures all query columns are in the base table (not extension-only)
let private validateNormalizedQueryByOrCreateAnnotation
  (normalized: NormalizedTable)
  (annotation: QueryByOrCreateAnnotation)
  : Result<unit, string> =
  let allColumns = getAllNormalizedColumns normalized

  let baseColumnNames =
    normalized.baseTable.columns
    |> List.map (fun c -> c.name.ToLowerInvariant())
    |> Set.ofList

  let allColumnNames =
    allColumns |> List.map (fun (_, c) -> c.name.ToLowerInvariant()) |> Set.ofList

  // First check if all columns exist somewhere
  annotation.columns
  |> List.tryFind (fun col -> not (allColumnNames.Contains(col.ToLowerInvariant())))
  |> function
    | Some invalidCol ->
      let availableCols =
        allColumns |> List.map (fun (_, c) -> c.name) |> String.concat ", "

      Error
        $"QueryByOrCreate annotation references non-existent column '{invalidCol}' in normalized table '{normalized.baseTable.name}'. Available columns: {availableCols}"
    | None ->
      // Then check if all query columns are in the base table
      annotation.columns
      |> List.tryFind (fun col -> not (baseColumnNames.Contains(col.ToLowerInvariant())))
      |> function
        | Some extensionCol ->
          let baseCols =
            normalized.baseTable.columns |> List.map (fun c -> c.name) |> String.concat ", "

          Error
            $"QueryByOrCreate annotation on normalized table '{normalized.baseTable.name}' requires field '{extensionCol}' to be in base table. Extension-only fields are not supported. Base table columns: {baseCols}"
        | None -> Ok()

/// Generate custom QueryByOrCreate method for normalized tables that extracts query values from NewType DU
let private generateNormalizedQueryByOrCreate
  (normalized: NormalizedTable)
  (annotation: QueryByOrCreateAnnotation)
  : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let newTypeName = $"New{typeName}"
  let pkCol = normalized.baseTable.columns |> List.find (fun c -> c.name = "id")

  // 1. Build method name: GetByNameOrCreate
  let methodName =
    annotation.columns
    |> List.map TypeGenerator.toPascalCase
    |> String.concat ""
    |> sprintf "GetBy%sOrCreate"

  // 2. Generate value extraction from NewType DU (pattern match on all cases)
  // All query fields must be in base table (validated earlier), so they exist in all DU cases
  let valueExtractions =
    annotation.columns
    |> List.map (fun col ->
      let fieldName = TypeGenerator.toPascalCase col

      // Generate pattern match to extract field from all DU cases
      let baseCaseName = "Base"

      let extensionCases =
        normalized.extensions
        |> List.map (fun ext -> $"With{TypeGenerator.toPascalCase ext.aspectName}")

      let allCases = baseCaseName :: extensionCases

      let varName =
        fieldName.ToLower().[0..0]
        + (if fieldName.Length > 1 then fieldName.[1..] else "")

      let caseMatches =
        allCases
        |> List.map (fun caseName -> $"        | {newTypeName}.{caseName}({fieldName} = {varName}) -> {varName}")
        |> String.concat "\n"

      $"      let {col} = \n        match newItem with\n{caseMatches}")
    |> String.concat "\n"

  // 3. Build WHERE clause
  let whereClause =
    annotation.columns
    |> List.map (fun col -> $"{col} = @{col}")
    |> String.concat " AND "

  // 4. Build parameter bindings (using extracted variables)
  let paramBindings =
    annotation.columns
    |> List.map (fun col ->
      let _, columnDef = findNormalizedColumn normalized col |> Option.get
      let isNullable = TypeGenerator.isColumnNullable columnDef

      if isNullable then
        $"cmd.Parameters.AddWithValue(\"@{col}\", match {col} with Some v -> box v | None -> box DBNull.Value) |> ignore"
      else
        $"cmd.Parameters.AddWithValue(\"@{col}\", {col}) |> ignore")
    |> String.concat "\n      "

  // 6. Generate LEFT JOIN SQL and column selections (same as generateGetAll)
  let baseColumns = normalized.baseTable.columns |> List.map (fun c -> $"b.{c.name}")

  let extensionSelects =
    normalized.extensions
    |> List.collect (fun ext -> ext.table.columns |> List.map (fun c -> $"e{ext.aspectName}.{c.name}"))

  let allSelects = (baseColumns @ extensionSelects) |> String.concat ", "

  let joins =
    normalized.extensions
    |> List.map (fun ext -> $"LEFT JOIN {ext.table.name} e{ext.aspectName} ON b.id = e{ext.aspectName}.{ext.fkColumn}")
    |> String.concat "\n      "

  let selectSql =
    if normalized.extensions.IsEmpty then
      $"SELECT {allSelects} FROM {normalized.baseTable.name} b WHERE {whereClause} LIMIT 1"
    else
      $"SELECT {allSelects} FROM {normalized.baseTable.name} b\n      {joins}\n      WHERE {whereClause} LIMIT 1"

  // 7. Generate mapping logic (same as GetById)
  let caseSelection =
    generateCaseSelection normalized.baseTable normalized.extensions typeName

  // 5. Generate full method
  $"""  static member {methodName} (newItem: {newTypeName}) (tx: SqliteTransaction) : Result<{typeName}, SqliteException> =
    try
      // Extract query values from NewType DU
{valueExtractions}
      // Try to find existing record
      use cmd = new SqliteCommand("{selectSql}", tx.Connection, tx)
      {paramBindings}
      use reader = cmd.ExecuteReader()
      if reader.Read() then
        // Found existing record - return it
        let item =
{caseSelection}
        Ok item
      else
        // Not found - insert and fetch
        reader.Close()
        match {typeName}.Insert newItem tx with
        | Ok newId ->
          match {typeName}.GetById newId tx with
          | Ok (Some item) -> Ok item
          | Ok None -> Error (SqliteException("Failed to retrieve inserted record", 0))
          | Error ex -> Error ex
        | Error ex -> Error ex
    with
    | :? SqliteException as ex -> Error ex"""

/// Generate all methods for a normalized table
let generateNormalizedTableCode (normalized: NormalizedTable) : Result<string, string> =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name

  // Validate all QueryBy annotations
  let queryByValidationResults =
    normalized.baseTable.queryByAnnotations
    |> List.map (validateNormalizedQueryByAnnotation normalized)

  // Validate all QueryByOrCreate annotations
  let queryByOrCreateValidationResults =
    normalized.baseTable.queryByOrCreateAnnotations
    |> List.map (validateNormalizedQueryByOrCreateAnnotation normalized)

  let firstError =
    (queryByValidationResults @ queryByOrCreateValidationResults)
    |> List.tryFind (fun r ->
      match r with
      | Error _ -> true
      | _ -> false)

  match firstError with
  | Some(Error msg) -> Error msg
  | _ ->
    let insertMethod = generateInsert normalized
    let getAllMethod = generateGetAll normalized
    let getByIdMethod = generateGetById normalized
    let getOneMethod = generateGetOne normalized
    let updateMethod = generateUpdate normalized
    let deleteMethod = generateDelete normalized

    // Generate QueryBy methods
    let queryByMethods =
      normalized.baseTable.queryByAnnotations
      |> List.map (generateNormalizedQueryBy normalized)

    // Generate QueryByOrCreate methods
    let queryByOrCreateMethods =
      normalized.baseTable.queryByOrCreateAnnotations
      |> List.map (generateNormalizedQueryByOrCreate normalized)

    let methods =
      [ Some insertMethod
        Some getAllMethod
        getByIdMethod
        Some getOneMethod
        updateMethod
        deleteMethod ]
      @ (queryByMethods |> List.map Some)
      @ (queryByOrCreateMethods |> List.map Some)
      |> List.choose id
      |> String.concat "\n\n"

    Ok
      $"""type {typeName} with
{methods}"""
