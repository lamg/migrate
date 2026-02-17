module internal MigLib.CodeGen.QueryGenerator

open MigLib.DeclarativeMigrations.Types
open MigLib.CodeGen.ViewIntrospection
open MigLib.CodeGen.AstExprBuilders
open Fabulous.AST
open type Fabulous.AST.Ast
open Microsoft.Data.Sqlite

/// Create indentation string with given number of spaces
let indent n = String.replicate n " "

/// Join lines with indentation
let joinWithIndent (spaces: int) (lines: string list) =
  lines |> String.concat $"\n{indent spaces}"

/// Get the primary key column(s) from a table
let getPrimaryKey (table: CreateTable) : ColumnDef list =
  // First check for column-level primary keys
  let columnLevelPks =
    table.columns
    |> List.filter (fun col ->
      col.constraints
      |> List.exists (fun c ->
        match c with
        | PrimaryKey _ -> true
        | _ -> false))

  // Then check for table-level primary keys (composite PKs)
  let tableLevelPkCols =
    table.constraints
    |> List.tryPick (fun c ->
      match c with
      | PrimaryKey pk when pk.columns.Length > 0 -> Some pk.columns
      | _ -> None)
    |> Option.defaultValue []
    |> List.choose (fun colName -> table.columns |> List.tryFind (fun col -> col.name = colName))

  // Prefer table-level if present, otherwise use column-level
  if tableLevelPkCols.Length > 0 then
    tableLevelPkCols
  else
    columnLevelPks

/// Get foreign key columns from a table
let getForeignKeys (table: CreateTable) : (string * string) list =
  // Column-level foreign keys
  let columnFks =
    table.columns
    |> List.collect (fun col ->
      col.constraints
      |> List.choose (fun c ->
        match c with
        | ForeignKey fk -> Some(col.name, fk.refTable)
        | _ -> None))

  // Table-level foreign keys
  let tableFks =
    table.constraints
    |> List.choose (fun c ->
      match c with
      | ForeignKey fk when fk.columns.Length = 1 -> Some(fk.columns.[0], fk.refTable)
      | _ -> None)

  columnFks @ tableFks |> List.distinct

let private capitalize = TypeGenerator.toPascalCase

/// Helper to get reader method name from F# type
let readerMethod (t: string) =
  t.Replace("int64", "Int64").Replace("string", "String").Replace("float", "Double").Replace("DateTime", "DateTime")

let private getAutoIncrementPrimaryKeyColumnName (table: CreateTable) : string option =
  let tableLevelAutoPk =
    table.constraints
    |> List.tryPick (function
      | PrimaryKey pk when pk.isAutoincrement && pk.columns.Length = 1 -> Some pk.columns.Head
      | _ -> None)

  match tableLevelAutoPk with
  | Some name -> Some name
  | None ->
    table.columns
    |> List.tryPick (fun column ->
      column.constraints
      |> List.tryPick (function
        | PrimaryKey pk when pk.isAutoincrement -> Some column.name
        | _ -> None))

let private rowDataPairExprForItem (itemExpr: string) (column: ColumnDef) : string =
  let fieldName = capitalize column.name
  let isNullable = TypeGenerator.isColumnNullable column

  if isNullable then
    $"(\"{column.name}\", match {itemExpr}.{fieldName} with Some v -> box v | None -> null)"
  else
    $"(\"{column.name}\", box {itemExpr}.{fieldName})"

let private rowDataListExprForItem (itemExpr: string) (columns: ColumnDef list) : string =
  columns
  |> List.map (rowDataPairExprForItem itemExpr)
  |> String.concat "; "
  |> fun pairs -> $"[{pairs}]"

let private rowDataListExprForParams (columns: ColumnDef list) : string =
  columns
  |> List.map (fun column -> $"(\"{column.name}\", box {column.name})")
  |> String.concat "; "
  |> fun pairs -> $"[{pairs}]"

/// Generate async INSERT method using Fabulous.AST
let generateInsert (table: CreateTable) : string =
  let typeName = capitalize table.name
  let pkCols = getPrimaryKey table
  let autoPkColName = getAutoIncrementPrimaryKeyColumnName table

  // Exclude auto-increment primary keys from insert
  let insertCols =
    table.columns
    |> List.filter (fun col ->
      not (
        pkCols
        |> List.exists (fun pk ->
          pk.name = col.name
          && pk.constraints
             |> List.exists (fun c ->
               match c with
               | PrimaryKey pk -> pk.isAutoincrement
               | _ -> false))
      ))

  let columnNames = insertCols |> List.map (fun c -> c.name) |> String.concat ", "

  let paramNames =
    insertCols |> List.map (fun c -> $"@{c.name}") |> String.concat ", "

  let insertSql = $"INSERT INTO {table.name} ({columnNames}) VALUES ({paramNames})"

  // Build the async method body using AST with task CE
  let asyncParamBindingExprs =
    insertCols
    |> List.map (fun col ->
      let fieldName = capitalize col.name
      let isNullable = TypeGenerator.isColumnNullable col

      if isNullable then
        OtherExpr
          $"cmd.Parameters.AddWithValue(\"@{col.name}\", match item.{fieldName} with Some v -> box v | None -> box DBNull.Value) |> ignore"
      else
        OtherExpr $"cmd.Parameters.AddWithValue(\"@{col.name}\", item.{fieldName}) |> ignore")

  let rowDataPairs =
    (insertCols |> List.map (rowDataPairExprForItem "item"))
    @ (match autoPkColName with
       | Some colName -> [ $"(\"{colName}\", box newId)" ]
       | None -> [])
    |> String.concat "; "
    |> fun pairs -> $"[{pairs}]"

  let asyncBodyExprs =
    [ OtherExpr $"use cmd = new SqliteCommand(\"{insertSql}\", tx.Connection, tx)" ]
    @ asyncParamBindingExprs
    @ [ OtherExpr "MigrationLog.ensureWriteAllowed tx"
        OtherExpr "let! _ = cmd.ExecuteNonQueryAsync()"
        OtherExpr "use lastIdCmd = new SqliteCommand(\"SELECT last_insert_rowid()\", tx.Connection, tx)"
        OtherExpr "let! lastId = lastIdCmd.ExecuteScalarAsync()"
        OtherExpr "let newId = lastId |> unbox<int64>"
        OtherExpr $"MigrationLog.recordInsert tx \"{table.name}\" {rowDataPairs}"
        OtherExpr "return Ok newId" ]

  let memberName = $"Insert (item: {typeName}) (tx: SqliteTransaction)"
  let returnType = "Task<Result<int64, SqliteException>>"
  let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]

  generateStaticMemberCode typeName memberName returnType body

/// Generate async INSERT OR IGNORE method using Fabulous.AST
let generateInsertOrIgnore (table: CreateTable) : string =
  let typeName = capitalize table.name
  let pkCols = getPrimaryKey table
  let autoPkColName = getAutoIncrementPrimaryKeyColumnName table

  // Exclude auto-increment primary keys from insert
  let insertCols =
    table.columns
    |> List.filter (fun col ->
      not (
        pkCols
        |> List.exists (fun pk ->
          pk.name = col.name
          && pk.constraints
             |> List.exists (fun c ->
               match c with
               | PrimaryKey pk -> pk.isAutoincrement
               | _ -> false))
      ))

  let columnNames = insertCols |> List.map (fun c -> c.name) |> String.concat ", "

  let paramNames =
    insertCols |> List.map (fun c -> $"@{c.name}") |> String.concat ", "

  let insertSql =
    $"INSERT OR IGNORE INTO {table.name} ({columnNames}) VALUES ({paramNames})"

  // Build the async method body using AST with task CE
  let asyncParamBindingExprs =
    insertCols
    |> List.map (fun col ->
      let fieldName = capitalize col.name
      let isNullable = TypeGenerator.isColumnNullable col

      if isNullable then
        OtherExpr
          $"cmd.Parameters.AddWithValue(\"@{col.name}\", match item.{fieldName} with Some v -> box v | None -> box DBNull.Value) |> ignore"
      else
        OtherExpr $"cmd.Parameters.AddWithValue(\"@{col.name}\", item.{fieldName}) |> ignore")

  let rowDataPairs =
    (insertCols |> List.map (rowDataPairExprForItem "item"))
    @ (match autoPkColName with
       | Some colName -> [ $"(\"{colName}\", box newId)" ]
       | None -> [])
    |> String.concat "; "
    |> fun pairs -> $"[{pairs}]"

  let asyncBodyExprs =
    [ OtherExpr $"use cmd = new SqliteCommand(\"{insertSql}\", tx.Connection, tx)" ]
    @ asyncParamBindingExprs
    @ [ OtherExpr "MigrationLog.ensureWriteAllowed tx"
        OtherExpr "let! rows = cmd.ExecuteNonQueryAsync()"
        OtherExpr "if rows = 0 then return Ok None else"
        OtherExpr "  use lastIdCmd = new SqliteCommand(\"SELECT last_insert_rowid()\", tx.Connection, tx)"
        OtherExpr "  let! lastId = lastIdCmd.ExecuteScalarAsync()"
        OtherExpr "  let newId = lastId |> unbox<int64>"
        OtherExpr $"  MigrationLog.recordInsert tx \"{table.name}\" {rowDataPairs}"
        OtherExpr "  return Ok (Some newId)" ]

  let memberName = $"InsertOrIgnore (item: {typeName}) (tx: SqliteTransaction)"
  let returnType = "Task<Result<int64 option, SqliteException>>"
  let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]

  generateStaticMemberCode typeName memberName returnType body

/// Generate async GET by ID method (transaction-only) using Fabulous.AST
let generateGet (table: CreateTable) : string option =
  let typeName = capitalize table.name
  let pkCols = getPrimaryKey table

  match pkCols with
  | [] -> None
  | pks ->
    let columnNames = table.columns |> List.map (fun c -> c.name) |> String.concat ", "

    // Generate WHERE clause for all PK columns
    let whereClause =
      pks |> List.map (fun pk -> $"{pk.name} = @{pk.name}") |> String.concat " AND "

    let getSql = $"SELECT {columnNames} FROM {table.name} WHERE {whereClause}"

    // Generate parameter list for function signature (curried)
    let paramList =
      pks
      |> List.map (fun pk ->
        let pkType = TypeGenerator.mapSqlType pk.columnType false
        $"({pk.name}: {pkType})")
      |> String.concat " "

    // Generate field mappings for record literal: "Field1 = reader.GetType 0; Field2 = ..."
    let fieldMappings =
      table.columns
      |> List.mapi (fun i col ->
        let fieldName = capitalize col.name
        let isNullable = TypeGenerator.isColumnNullable col
        let method = TypeGenerator.mapSqlType col.columnType false |> readerMethod

        if isNullable then
          $"{fieldName} = if reader.IsDBNull {i} then None else Some(reader.Get{method} {i})"
        else
          $"{fieldName} = reader.Get{method} {i}")
      |> String.concat "; "

    // Build the async method body using AST with task CE
    let asyncParamBindingExprs =
      pks
      |> List.map (fun pk -> OtherExpr $"cmd.Parameters.AddWithValue(\"@{pk.name}\", {pk.name}) |> ignore")

    let asyncBodyExprs =
      [ OtherExpr $"use cmd = new SqliteCommand(\"{getSql}\", tx.Connection, tx)" ]
      @ asyncParamBindingExprs
      @ [ OtherExpr "use! reader = cmd.ExecuteReaderAsync()"
          OtherExpr "let! hasRow = reader.ReadAsync()"
          OtherExpr $"if hasRow then return Ok(Some {{ {fieldMappings} }}) else return Ok None" ]

    let memberName = $"SelectById {paramList} (tx: SqliteTransaction)"
    let returnType = $"Task<Result<{typeName} option, SqliteException>>"
    let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]

    Some(generateStaticMemberCode typeName memberName returnType body)

/// Generate async GET ALL method (transaction-only) using Fabulous.AST
let generateGetAll (table: CreateTable) : string =
  let typeName = capitalize table.name
  let columnNames = table.columns |> List.map (fun c -> c.name) |> String.concat ", "
  let getSql = $"SELECT {columnNames} FROM {table.name}"

  // Generate field mappings for record literal: "Field1 = reader.GetType 0; Field2 = ..."
  let fieldMappings =
    table.columns
    |> List.mapi (fun i col ->
      let fieldName = capitalize col.name
      let isNullable = TypeGenerator.isColumnNullable col
      let method = TypeGenerator.mapSqlType col.columnType false |> readerMethod

      if isNullable then
        $"{fieldName} = if reader.IsDBNull {i} then None else Some(reader.Get{method} {i})"
      else
        $"{fieldName} = reader.Get{method} {i}")
    |> String.concat "; "

  // Build the async method body using AST with task CE
  // The while loop pattern for async reading is embedded as a multi-statement expression
  let whileLoopBody =
    $"let mutable hasMore = true in while hasMore do let! next = reader.ReadAsync() in hasMore <- next; if hasMore then results.Add({{ {fieldMappings} }})"

  let asyncBodyExprs =
    [ OtherExpr $"use cmd = new SqliteCommand(\"{getSql}\", tx.Connection, tx)"
      OtherExpr "use! reader = cmd.ExecuteReaderAsync()"
      OtherExpr $"let results = ResizeArray<{typeName}>()"
      OtherExpr whileLoopBody
      OtherExpr "return Ok(results |> Seq.toList)" ]

  let memberName = "SelectAll (tx: SqliteTransaction)"
  let returnType = $"Task<Result<{typeName} list, SqliteException>>"
  let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]

  generateStaticMemberCode typeName memberName returnType body

/// Generate async GET ONE method (transaction-only) using Fabulous.AST
let generateGetOne (table: CreateTable) : string =
  let typeName = capitalize table.name
  let columnNames = table.columns |> List.map (fun c -> c.name) |> String.concat ", "
  let getSql = $"SELECT {columnNames} FROM {table.name} LIMIT 1"

  // Generate field mappings for record literal: "Field1 = reader.GetType 0; Field2 = ..."
  let fieldMappings =
    table.columns
    |> List.mapi (fun i col ->
      let fieldName = capitalize col.name
      let isNullable = TypeGenerator.isColumnNullable col
      let method = TypeGenerator.mapSqlType col.columnType false |> readerMethod

      if isNullable then
        $"{fieldName} = if reader.IsDBNull {i} then None else Some(reader.Get{method} {i})"
      else
        $"{fieldName} = reader.Get{method} {i}")
    |> String.concat "; "

  // Build the async method body using AST with task CE
  let asyncBodyExprs =
    [ OtherExpr $"use cmd = new SqliteCommand(\"{getSql}\", tx.Connection, tx)"
      OtherExpr "use! reader = cmd.ExecuteReaderAsync()"
      OtherExpr "let! hasRow = reader.ReadAsync()"
      OtherExpr $"if hasRow then return Ok(Some {{ {fieldMappings} }}) else return Ok None" ]

  let memberName = "SelectOne (tx: SqliteTransaction)"
  let returnType = $"Task<Result<{typeName} option, SqliteException>>"
  let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]

  generateStaticMemberCode typeName memberName returnType body

/// Generate async UPDATE method (transaction-only)
let generateUpdate (table: CreateTable) : string option =
  let typeName = capitalize table.name
  let pkCols = getPrimaryKey table
  let rowDataExpr = rowDataListExprForItem "item" table.columns

  match pkCols with
  | [] -> None
  | pks ->
    let pkNames = pks |> List.map (fun pk -> pk.name) |> Set.ofList

    // Exclude all primary key columns from SET clause
    let updateCols =
      table.columns |> List.filter (fun col -> not (Set.contains col.name pkNames))

    let setClauses =
      updateCols |> List.map (fun c -> $"{c.name} = @{c.name}") |> String.concat ", "

    // Generate WHERE clause for all PK columns
    let whereClause =
      pks |> List.map (fun pk -> $"{pk.name} = @{pk.name}") |> String.concat " AND "

    let updateSql = $"UPDATE {table.name} SET {setClauses} WHERE {whereClause}"

    // Build the async method body using AST with task CE
    let asyncParamBindingExprs =
      table.columns
      |> List.map (fun col ->
        let fieldName = capitalize col.name
        let isNullable = TypeGenerator.isColumnNullable col

        if isNullable then
          OtherExpr
            $"cmd.Parameters.AddWithValue(\"@{col.name}\", match item.{fieldName} with Some v -> box v | None -> box DBNull.Value) |> ignore"
        else
          OtherExpr $"cmd.Parameters.AddWithValue(\"@{col.name}\", item.{fieldName}) |> ignore")

    let asyncBodyExprs =
      [ OtherExpr $"use cmd = new SqliteCommand(\"{updateSql}\", tx.Connection, tx)" ]
      @ asyncParamBindingExprs
      @ [ OtherExpr "MigrationLog.ensureWriteAllowed tx"
          OtherExpr "let! _ = cmd.ExecuteNonQueryAsync()"
          OtherExpr $"MigrationLog.recordUpdate tx \"{table.name}\" {rowDataExpr}"
          OtherExpr "return Ok()" ]

    let memberName = $"Update (item: {typeName}) (tx: SqliteTransaction)"
    let returnType = "Task<Result<unit, SqliteException>>"
    let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]

    Some(generateStaticMemberCode typeName memberName returnType body)

/// Generate async DELETE method (transaction-only) using Fabulous.AST
let generateDelete (table: CreateTable) : string option =
  let typeName = capitalize table.name
  let pkCols = getPrimaryKey table
  let rowDataExpr = rowDataListExprForParams pkCols

  match pkCols with
  | [] -> None
  | pks ->
    // Generate WHERE clause for all PK columns
    let whereClause =
      pks |> List.map (fun pk -> $"{pk.name} = @{pk.name}") |> String.concat " AND "

    let deleteSql = $"DELETE FROM {table.name} WHERE {whereClause}"

    // Generate parameter list for function signature (curried)
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
          OtherExpr $"MigrationLog.recordDelete tx \"{table.name}\" {rowDataExpr}"
          OtherExpr "return Ok()" ]

    let memberName = $"Delete {paramList} (tx: SqliteTransaction)"
    let returnType = "Task<Result<unit, SqliteException>>"
    let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]

    Some(generateStaticMemberCode typeName memberName returnType body)

/// Validate QueryBy annotation references existing columns (case-insensitive)
let validateQueryByAnnotation (table: CreateTable) (annotation: QueryByAnnotation) : Result<unit, string> =
  let columnNames =
    table.columns |> List.map (fun c -> c.name.ToLowerInvariant()) |> Set.ofList

  annotation.columns
  |> List.tryFind (fun col -> not (columnNames.Contains(col.ToLowerInvariant())))
  |> function
    | Some invalidCol ->
      let availableCols =
        table.columns |> List.map (fun c -> c.name) |> String.concat ", "

      Error
        $"QueryBy annotation references non-existent column '{invalidCol}' in table '{table.name}'. Available columns: {availableCols}"
    | None -> Ok()

/// Validate QueryLike annotation references one existing column (case-insensitive)
let validateQueryLikeAnnotation (table: CreateTable) (annotation: QueryLikeAnnotation) : Result<unit, string> =
  match annotation.columns with
  | [] -> Error $"QueryLike annotation on table '{table.name}' requires exactly one column."
  | [ col ] ->
    let columnNames =
      table.columns |> List.map (fun c -> c.name.ToLowerInvariant()) |> Set.ofList

    if columnNames.Contains(col.ToLowerInvariant()) then
      Ok()
    else
      let availableCols =
        table.columns |> List.map (fun c -> c.name) |> String.concat ", "

      Error
        $"QueryLike annotation references non-existent column '{col}' in table '{table.name}'. Available columns: {availableCols}"
  | _ ->
    let receivedCols = annotation.columns |> String.concat ", "

    Error $"QueryLike annotation on table '{table.name}' supports exactly one column. Received: {receivedCols}"

/// Find column by name (case-insensitive)
let findColumn (table: CreateTable) (colName: string) : ColumnDef option =
  table.columns
  |> List.tryFind (fun c -> c.name.ToLowerInvariant() = colName.ToLowerInvariant())

/// Generate async custom QueryBy method with tupled parameters using Fabulous.AST
let generateQueryBy (table: CreateTable) (annotation: QueryByAnnotation) : string =
  let typeName = capitalize table.name

  // 1. Build method name: SelectByIdName
  let methodName =
    annotation.columns
    |> List.map (fun col -> capitalize col)
    |> String.concat ""
    |> sprintf "SelectBy%s"

  // 2. Build tupled parameters with types (case-insensitive column lookup)
  let parameters =
    annotation.columns
    |> List.map (fun col ->
      let columnDef = findColumn table col |> Option.get
      let isNullable = TypeGenerator.isColumnNullable columnDef
      let fsharpType = TypeGenerator.mapSqlType columnDef.columnType isNullable
      $"{col}: {fsharpType}")
    |> String.concat ", "

  // 3. Build WHERE clause: id = @id AND name = @name
  let whereClause =
    annotation.columns
    |> List.map (fun col -> $"{col} = @{col}")
    |> String.concat " AND "

  // 5. Get column names for SELECT
  let columnNames = table.columns |> List.map (fun c -> c.name) |> String.concat ", "

  // 6. Generate field mappings for reader
  let fieldMappings =
    table.columns
    |> List.mapi (fun i col ->
      let fieldName = capitalize col.name
      let isNullable = TypeGenerator.isColumnNullable col
      let method = TypeGenerator.mapSqlType col.columnType false |> readerMethod

      if isNullable then
        $"{fieldName} = if reader.IsDBNull {i} then None else Some(reader.Get{method} {i})"
      else
        $"{fieldName} = reader.Get{method} {i}")
    |> String.concat "; "

  // 7. Generate full method with tupled parameters
  // Build the async method body using AST with task CE
  let asyncParamBindingExprs =
    annotation.columns
    |> List.map (fun col ->
      let columnDef = findColumn table col |> Option.get
      let isNullable = TypeGenerator.isColumnNullable columnDef

      if isNullable then
        OtherExpr
          $"cmd.Parameters.AddWithValue(\"@{col}\", match {col} with Some v -> box v | None -> box DBNull.Value) |> ignore"
      else
        OtherExpr $"cmd.Parameters.AddWithValue(\"@{col}\", {col}) |> ignore")

  let whileLoopBody =
    $"let mutable hasMore = true in while hasMore do let! next = reader.ReadAsync() in hasMore <- next; if hasMore then results.Add({{ {fieldMappings} }})"

  let asyncBodyExprs =
    [ OtherExpr
        $"use cmd = new SqliteCommand(\"SELECT {columnNames} FROM {table.name} WHERE {whereClause}\", tx.Connection, tx)" ]
    @ asyncParamBindingExprs
    @ [ OtherExpr "use! reader = cmd.ExecuteReaderAsync()"
        OtherExpr $"let results = ResizeArray<{typeName}>()"
        OtherExpr whileLoopBody
        OtherExpr "return Ok(results |> Seq.toList)" ]

  let memberName = $"{methodName} ({parameters}) (tx: SqliteTransaction)"
  let returnType = $"Task<Result<{typeName} list, SqliteException>>"
  let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]

  generateStaticMemberCode typeName memberName returnType body

/// Generate async custom QueryLike method with one parameter and SQL LIKE '%value%' semantics
let generateQueryLike (table: CreateTable) (annotation: QueryLikeAnnotation) : string =
  let typeName = capitalize table.name
  let col = annotation.columns |> List.head

  let methodName = $"Select{capitalize col}Like"
  let columnDef = findColumn table col |> Option.get
  let isNullable = TypeGenerator.isColumnNullable columnDef
  let fsharpType = TypeGenerator.mapSqlType columnDef.columnType isNullable
  let parameters = $"{col}: {fsharpType}"
  let whereClause = $"{col} LIKE '%%' || @{col} || '%%'"
  let columnNames = table.columns |> List.map (fun c -> c.name) |> String.concat ", "

  let fieldMappings =
    table.columns
    |> List.mapi (fun i c ->
      let fieldName = capitalize c.name
      let isNullableField = TypeGenerator.isColumnNullable c
      let method = TypeGenerator.mapSqlType c.columnType false |> readerMethod

      if isNullableField then
        $"{fieldName} = if reader.IsDBNull {i} then None else Some(reader.Get{method} {i})"
      else
        $"{fieldName} = reader.Get{method} {i}")
    |> String.concat "; "

  let asyncParamBindingExpr =
    if isNullable then
      OtherExpr
        $"cmd.Parameters.AddWithValue(\"@{col}\", match {col} with Some v -> box v | None -> box DBNull.Value) |> ignore"
    else
      OtherExpr $"cmd.Parameters.AddWithValue(\"@{col}\", {col}) |> ignore"

  let whileLoopBody =
    $"let mutable hasMore = true in while hasMore do let! next = reader.ReadAsync() in hasMore <- next; if hasMore then results.Add({{ {fieldMappings} }})"

  let asyncBodyExprs =
    [ OtherExpr
        $"use cmd = new SqliteCommand(\"SELECT {columnNames} FROM {table.name} WHERE {whereClause}\", tx.Connection, tx)"
      asyncParamBindingExpr
      OtherExpr "use! reader = cmd.ExecuteReaderAsync()"
      OtherExpr $"let results = ResizeArray<{typeName}>()"
      OtherExpr whileLoopBody
      OtherExpr "return Ok(results |> Seq.toList)" ]

  let memberName = $"{methodName} ({parameters}) (tx: SqliteTransaction)"
  let returnType = $"Task<Result<{typeName} list, SqliteException>>"
  let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]

  generateStaticMemberCode typeName memberName returnType body

/// Validate QueryByOrCreate annotation references existing columns (case-insensitive)
let validateQueryByOrCreateAnnotation
  (table: CreateTable)
  (annotation: QueryByOrCreateAnnotation)
  : Result<unit, string> =
  // Same validation as QueryBy
  let availableColumns = table.columns |> List.map (fun c -> c.name.ToLower())

  let invalidColumns =
    annotation.columns
    |> List.filter (fun col -> not (availableColumns |> List.contains (col.ToLower())))

  match invalidColumns with
  | [] -> Ok()
  | invalidCol :: _ ->
    let availableCols =
      table.columns |> List.map (fun c -> c.name) |> String.concat ", "

    Error
      $"QueryByOrCreate annotation references non-existent column '{invalidCol}' in table '{table.name}'. Available columns: {availableCols}"

/// Generate async custom QueryByOrCreate method that extracts query values from newItem using Fabulous.AST
let generateQueryByOrCreate (table: CreateTable) (annotation: QueryByOrCreateAnnotation) : string =
  let typeName = capitalize table.name

  // 1. Build method name: SelectByStatusOrInsert
  let methodName =
    annotation.columns
    |> List.map (fun col -> capitalize col)
    |> String.concat ""
    |> sprintf "SelectBy%sOrInsert"

  // 2. Build WHERE clause
  let whereClause =
    annotation.columns
    |> List.map (fun col -> $"{col} = @{col}")
    |> String.concat " AND "

  // 6. Get column names for SELECT
  let columnNames = table.columns |> List.map (fun c -> c.name) |> String.concat ", "

  // 7. Generate field mappings for reader (semicolon-separated for inline record)
  let fieldMappings =
    table.columns
    |> List.mapi (fun i col ->
      let fieldName = capitalize col.name
      let isNullable = TypeGenerator.isColumnNullable col
      let method = TypeGenerator.mapSqlType col.columnType false |> readerMethod

      if isNullable then
        $"{fieldName} = if reader.IsDBNull {i} then None else Some(reader.Get{method} {i})"
      else
        $"{fieldName} = reader.Get{method} {i}")
    |> String.concat "; "

  // Keep async version as string template for now (task CE is complex)
  let asyncValueExtractions =
    annotation.columns
    |> List.map (fun col ->
      let fieldName = capitalize col
      $"let {col} = newItem.{fieldName}")
    |> String.concat "\n        "

  let generateAsyncParamBindings (cmdVarName: string) =
    annotation.columns
    |> List.map (fun col ->
      let columnDef = findColumn table col |> Option.get
      let isNullable = TypeGenerator.isColumnNullable columnDef

      if isNullable then
        $"{cmdVarName}.Parameters.AddWithValue(\"@{col}\", match {col} with Some v -> box v | None -> box DBNull.Value) |> ignore"
      else
        $"{cmdVarName}.Parameters.AddWithValue(\"@{col}\", {col}) |> ignore")
    |> String.concat "\n        "

  let asyncParamBindings = generateAsyncParamBindings "cmd"
  let asyncRequeryParamBindings = generateAsyncParamBindings "cmd2"

  let asyncFieldMappings =
    table.columns
    |> List.mapi (fun i col ->
      let fieldName = capitalize col.name
      let isNullable = TypeGenerator.isColumnNullable col
      let method = TypeGenerator.mapSqlType col.columnType false |> readerMethod

      if isNullable then
        $"{fieldName} = if reader.IsDBNull {i} then None else Some(reader.Get{method} {i})"
      else
        $"{fieldName} = reader.Get{method} {i}")
    |> String.concat "\n            "

  let requeryAfterInsertAsync =
    $"""
            // Re-query to get inserted record
            use cmd2 = new SqliteCommand("SELECT {columnNames} FROM {table.name} WHERE {whereClause} LIMIT 1", tx.Connection, tx)
            {asyncRequeryParamBindings}
            use! reader = cmd2.ExecuteReaderAsync()
            let! hasInsertedRow = reader.ReadAsync()
            if hasInsertedRow then
              return Ok {{
                {asyncFieldMappings}
              }}
            else
              return Error (SqliteException("Failed to retrieve inserted record", 0))"""

  // Generate full method - async version
  $"""  static member {methodName} (newItem: {typeName}) (tx: SqliteTransaction) : Task<Result<{typeName}, SqliteException>> =
    task {{
      try
        // Extract query values from newItem
        {asyncValueExtractions}
        // Try to find existing record
        use cmd = new SqliteCommand("SELECT {columnNames} FROM {table.name} WHERE {whereClause} LIMIT 1", tx.Connection, tx)
        {asyncParamBindings}
        use! reader = cmd.ExecuteReaderAsync()
        let! hasRow = reader.ReadAsync()
        if hasRow then
          // Found existing record - return it
          return Ok {{
            {asyncFieldMappings}
          }}
        else
          // Not found - insert and fetch
          reader.Close()
          let! insertResult = {typeName}.Insert newItem tx
          match insertResult with
          | Ok _ ->{requeryAfterInsertAsync}
          | Error ex -> return Error ex
      with
      | :? SqliteException as ex -> return Error ex
    }}"""

/// Generate code for a table
let generateTableCode (table: CreateTable) : Result<string, string> =
  let typeName = capitalize table.name

  // Validate all QueryBy annotations
  let queryByValidationResults =
    table.queryByAnnotations |> List.map (validateQueryByAnnotation table)

  // Validate all QueryLike annotations
  let queryLikeValidationResults =
    table.queryLikeAnnotations |> List.map (validateQueryLikeAnnotation table)

  // Validate all QueryByOrCreate annotations
  let queryByOrCreateValidationResults =
    table.queryByOrCreateAnnotations
    |> List.map (validateQueryByOrCreateAnnotation table)

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
    // CRUD methods with curried signatures
    let insertMethod = generateInsert table

    let insertOrIgnoreMethod =
      if table.insertOrIgnoreAnnotations.IsEmpty then
        None
      else
        Some(generateInsertOrIgnore table)

    let getMethod = generateGet table
    let getAllMethod = generateGetAll table
    let getOneMethod = generateGetOne table
    let updateMethod = generateUpdate table
    let deleteMethod = generateDelete table

    // Generate QueryBy methods
    let queryByMethods = table.queryByAnnotations |> List.map (generateQueryBy table)

    // Generate QueryLike methods
    let queryLikeMethods =
      table.queryLikeAnnotations |> List.map (generateQueryLike table)

    // Generate QueryByOrCreate methods
    let queryByOrCreateMethods =
      table.queryByOrCreateAnnotations |> List.map (generateQueryByOrCreate table)

    let allMethods =
      [ Some insertMethod
        insertOrIgnoreMethod
        getMethod
        Some getAllMethod
        Some getOneMethod
        updateMethod
        deleteMethod ]
      @ (queryByMethods |> List.map Some)
      @ (queryLikeMethods |> List.map Some)
      @ (queryByOrCreateMethods |> List.map Some)
      |> List.choose id
      |> String.concat "\n\n"

    Ok
      $"""type {typeName} with
{allMethods}"""

/// Generate async SelectAll method for a view (read-only) using Fabulous.AST
let generateViewGetAll (viewName: string) (columns: ViewColumn list) : string =
  let typeName = capitalize viewName
  let columnNames = columns |> List.map (fun c -> c.name) |> String.concat ", "
  let getSql = $"SELECT {columnNames} FROM {viewName}"

  // Generate field mappings for record literal (semicolon-separated for inline)
  let fieldMappings =
    columns
    |> List.mapi (fun i col ->
      let fieldName = capitalize col.name
      let method = TypeGenerator.mapSqlType col.columnType false |> readerMethod

      $"{fieldName} = reader.Get{method} {i}")
    |> String.concat "; "

  // Build the async method body using AST with task CE
  let whileLoopBody =
    $"let mutable hasMore = true in while hasMore do let! next = reader.ReadAsync() in hasMore <- next; if hasMore then results.Add({{ {fieldMappings} }})"

  let asyncBodyExprs =
    [ OtherExpr $"use cmd = new SqliteCommand(\"{getSql}\", tx.Connection, tx)"
      OtherExpr "use! reader = cmd.ExecuteReaderAsync()"
      OtherExpr $"let results = ResizeArray<{typeName}>()"
      OtherExpr whileLoopBody
      OtherExpr "return Ok(results |> Seq.toList)" ]

  let memberName = "SelectAll (tx: SqliteTransaction)"
  let returnType = $"Task<Result<{typeName} list, SqliteException>>"
  let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]

  generateStaticMemberCode typeName memberName returnType body

/// Generate async GET ONE method for a view (read-only) using Fabulous.AST
let generateViewGetOne (viewName: string) (columns: ViewColumn list) : string =
  let typeName = capitalize viewName
  let columnNames = columns |> List.map (fun c -> c.name) |> String.concat ", "
  let getSql = $"SELECT {columnNames} FROM {viewName} LIMIT 1"

  // Generate field mappings for record literal (semicolon-separated for inline)
  let fieldMappings =
    columns
    |> List.mapi (fun i col ->
      let fieldName = capitalize col.name
      let method = TypeGenerator.mapSqlType col.columnType false |> readerMethod
      $"{fieldName} = reader.Get{method} {i}")
    |> String.concat "; "

  // Build the async method body using AST with task CE
  let asyncBodyExprs =
    [ OtherExpr $"use cmd = new SqliteCommand(\"{getSql}\", tx.Connection, tx)"
      OtherExpr "use! reader = cmd.ExecuteReaderAsync()"
      OtherExpr "let! hasRow = reader.ReadAsync()"
      OtherExpr $"if hasRow then return Ok(Some {{ {fieldMappings} }}) else return Ok None" ]

  let memberName = "SelectOne (tx: SqliteTransaction)"
  let returnType = $"Task<Result<{typeName} option, SqliteException>>"
  let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]

  generateStaticMemberCode typeName memberName returnType body

/// Validate QueryBy annotation for view references existing columns (case-insensitive)
let validateViewQueryByAnnotation
  (viewName: string)
  (columns: ViewColumn list)
  (annotation: QueryByAnnotation)
  : Result<unit, string> =
  let columnNames =
    columns |> List.map (fun c -> c.name.ToLowerInvariant()) |> Set.ofList

  annotation.columns
  |> List.tryFind (fun col -> not (columnNames.Contains(col.ToLowerInvariant())))
  |> function
    | Some invalidCol ->
      let availableCols = columns |> List.map (fun c -> c.name) |> String.concat ", "

      Error
        $"QueryBy annotation references non-existent column '{invalidCol}' in view '{viewName}'. Available columns: {availableCols}"
    | None -> Ok()

/// Validate QueryLike annotation for view references one existing column (case-insensitive)
let validateViewQueryLikeAnnotation
  (viewName: string)
  (columns: ViewColumn list)
  (annotation: QueryLikeAnnotation)
  : Result<unit, string> =
  match annotation.columns with
  | [] -> Error $"QueryLike annotation on view '{viewName}' requires exactly one column."
  | [ col ] ->
    let columnNames =
      columns |> List.map (fun c -> c.name.ToLowerInvariant()) |> Set.ofList

    if columnNames.Contains(col.ToLowerInvariant()) then
      Ok()
    else
      let availableCols = columns |> List.map (fun c -> c.name) |> String.concat ", "

      Error
        $"QueryLike annotation references non-existent column '{col}' in view '{viewName}'. Available columns: {availableCols}"
  | _ ->
    let receivedCols = annotation.columns |> String.concat ", "

    Error $"QueryLike annotation on view '{viewName}' supports exactly one column. Received: {receivedCols}"

/// Find view column by name (case-insensitive)
let findViewColumn (columns: ViewColumn list) (colName: string) : ViewColumn option =
  columns
  |> List.tryFind (fun c -> c.name.ToLowerInvariant() = colName.ToLowerInvariant())

/// Generate async custom QueryBy method for views with tupled parameters using Fabulous.AST
let generateViewQueryBy (viewName: string) (columns: ViewColumn list) (annotation: QueryByAnnotation) : string =
  let typeName = capitalize viewName

  // 1. Build method name: SelectByIdName
  let methodName =
    annotation.columns
    |> List.map (fun col -> capitalize col)
    |> String.concat ""
    |> sprintf "SelectBy%s"

  // 2. Build tupled parameters with types (case-insensitive column lookup)
  let parameters =
    annotation.columns
    |> List.map (fun col ->
      let columnDef = findViewColumn columns col |> Option.get
      let fsharpType = TypeGenerator.mapSqlType columnDef.columnType false
      $"{col}: {fsharpType}")
    |> String.concat ", "

  // 3. Build WHERE clause: id = @id AND name = @name
  let whereClause =
    annotation.columns
    |> List.map (fun col -> $"{col} = @{col}")
    |> String.concat " AND "

  // 5. Get column names for SELECT
  let columnNames = columns |> List.map (fun c -> c.name) |> String.concat ", "

  // 6. Generate field mappings for reader (semicolon-separated for inline)
  let fieldMappings =
    columns
    |> List.mapi (fun i col ->
      let fieldName = capitalize col.name
      let method = TypeGenerator.mapSqlType col.columnType false |> readerMethod
      $"{fieldName} = reader.Get{method} {i}")
    |> String.concat "; "

  // 7. Generate full method with tupled parameters
  // Build the async method body using AST with task CE
  let asyncParamBindingExprs =
    annotation.columns
    |> List.map (fun col -> OtherExpr $"cmd.Parameters.AddWithValue(\"@{col}\", {col}) |> ignore")

  let whileLoopBody =
    $"let mutable hasMore = true in while hasMore do let! next = reader.ReadAsync() in hasMore <- next; if hasMore then results.Add({{ {fieldMappings} }})"

  let asyncBodyExprs =
    [ OtherExpr
        $"use cmd = new SqliteCommand(\"SELECT {columnNames} FROM {viewName} WHERE {whereClause}\", tx.Connection, tx)" ]
    @ asyncParamBindingExprs
    @ [ OtherExpr "use! reader = cmd.ExecuteReaderAsync()"
        OtherExpr $"let results = ResizeArray<{typeName}>()"
        OtherExpr whileLoopBody
        OtherExpr "return Ok(results |> Seq.toList)" ]

  let memberName = $"{methodName} ({parameters}) (tx: SqliteTransaction)"
  let returnType = $"Task<Result<{typeName} list, SqliteException>>"
  let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]

  generateStaticMemberCode typeName memberName returnType body

/// Generate async custom QueryLike method for views with SQL LIKE '%value%' semantics
let generateViewQueryLike (viewName: string) (columns: ViewColumn list) (annotation: QueryLikeAnnotation) : string =
  let typeName = capitalize viewName
  let col = annotation.columns |> List.head
  let methodName = $"Select{capitalize col}Like"
  let columnDef = findViewColumn columns col |> Option.get
  let fsharpType = TypeGenerator.mapSqlType columnDef.columnType false
  let parameters = $"{col}: {fsharpType}"
  let whereClause = $"{col} LIKE '%%' || @{col} || '%%'"
  let columnNames = columns |> List.map (fun c -> c.name) |> String.concat ", "

  let fieldMappings =
    columns
    |> List.mapi (fun i c ->
      let fieldName = capitalize c.name
      let method = TypeGenerator.mapSqlType c.columnType false |> readerMethod
      $"{fieldName} = reader.Get{method} {i}")
    |> String.concat "; "

  let asyncParamBindingExpr =
    OtherExpr $"cmd.Parameters.AddWithValue(\"@{col}\", {col}) |> ignore"

  let whileLoopBody =
    $"let mutable hasMore = true in while hasMore do let! next = reader.ReadAsync() in hasMore <- next; if hasMore then results.Add({{ {fieldMappings} }})"

  let asyncBodyExprs =
    [ OtherExpr
        $"use cmd = new SqliteCommand(\"SELECT {columnNames} FROM {viewName} WHERE {whereClause}\", tx.Connection, tx)"
      asyncParamBindingExpr
      OtherExpr "use! reader = cmd.ExecuteReaderAsync()"
      OtherExpr $"let results = ResizeArray<{typeName}>()"
      OtherExpr whileLoopBody
      OtherExpr "return Ok(results |> Seq.toList)" ]

  let memberName = $"{methodName} ({parameters}) (tx: SqliteTransaction)"
  let returnType = $"Task<Result<{typeName} list, SqliteException>>"
  let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]

  generateStaticMemberCode typeName memberName returnType body

/// Generate code for a view (read-only queries)
let generateViewCode (view: CreateView) (columns: ViewColumn list) : Result<string, string> =
  let typeName = capitalize view.name

  // Reject QueryByOrCreate and InsertOrIgnore annotations on views (views are read-only)
  match view.queryByOrCreateAnnotations, view.insertOrIgnoreAnnotations with
  | [], [] ->
    // Validate all QueryBy annotations
    let queryByValidationResults =
      view.queryByAnnotations
      |> List.map (validateViewQueryByAnnotation view.name columns)

    // Validate all QueryLike annotations
    let queryLikeValidationResults =
      view.queryLikeAnnotations
      |> List.map (validateViewQueryLikeAnnotation view.name columns)

    let validationResults = queryByValidationResults @ queryLikeValidationResults

    let firstError =
      validationResults
      |> List.tryFind (fun r ->
        match r with
        | Error _ -> true
        | _ -> false)

    match firstError with
    | Some(Error msg) -> Error msg
    | _ ->
      let getAllMethod = generateViewGetAll view.name columns
      let getOneMethod = generateViewGetOne view.name columns

      // Generate QueryBy methods
      let queryByMethods =
        view.queryByAnnotations |> List.map (generateViewQueryBy view.name columns)

      // Generate QueryLike methods
      let queryLikeMethods =
        view.queryLikeAnnotations |> List.map (generateViewQueryLike view.name columns)

      let allMethods =
        [ getAllMethod; getOneMethod ] @ queryByMethods @ queryLikeMethods
        |> String.concat "\n\n"

      Ok
        $"""type {typeName} with
{allMethods}"""
  | _ :: _, _ ->
    Error
      $"QueryByOrCreate annotation is not supported on views (view '{view.name}' is read-only). Use QueryBy instead."
  | [], _ :: _ -> Error $"InsertOrIgnore annotation is not supported on views (view '{view.name}' is read-only)."
