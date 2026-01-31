module internal migrate.CodeGen.QueryGenerator

open migrate.DeclarativeMigrations.Types
open migrate.CodeGen.ViewIntrospection
open migrate.CodeGen.AstExprBuilders
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

/// Convert snake_case to PascalCase for F# naming conventions
let capitalize (s: string) =
  if System.String.IsNullOrWhiteSpace s then
    s
  else
    s.Split '_'
    |> Array.map (fun part ->
      if part.Length > 0 then
        (string part.[0]).ToUpper() + part.[1..].ToLower()
      else
        part)
    |> System.String.Concat

/// Helper to get reader method name from F# type
let readerMethod (t: string) =
  t.Replace("int64", "Int64").Replace("string", "String").Replace("float", "Double").Replace("DateTime", "DateTime")

/// Generate INSERT method using Fabulous.AST for sync version
let generateInsert (useAsync: bool) (table: CreateTable) : string =
  let typeName = capitalize table.name
  let pkCols = getPrimaryKey table

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

  if useAsync then
    // Keep async version as string template for now (task CE is complex)
    let asyncParamBindings =
      insertCols
      |> List.map (fun col ->
        let fieldName = capitalize col.name
        let isNullable = TypeGenerator.isColumnNullable col

        if isNullable then
          $"cmd.Parameters.AddWithValue(\"@{col.name}\", match item.{fieldName} with Some v -> box v | None -> box DBNull.Value) |> ignore"
        else
          $"cmd.Parameters.AddWithValue(\"@{col.name}\", item.{fieldName}) |> ignore")
      |> String.concat "\n        "

    $"""  static member Insert (item: {typeName}) (tx: SqliteTransaction) : Task<Result<int64, SqliteException>> =
    task {{
      try
        use cmd = new SqliteCommand("{insertSql}", tx.Connection, tx)
        {asyncParamBindings}
        let! _ = cmd.ExecuteNonQueryAsync()
        use lastIdCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
        let! lastId = lastIdCmd.ExecuteScalarAsync()
        return Ok(lastId |> unbox<int64>)
      with
      | :? SqliteException as ex -> return Error ex
    }}"""
  else
    // Build the sync method body using AST
    let paramBindingStmts =
      insertCols
      |> List.map (fun col ->
        let fieldName = capitalize col.name
        let isNullable = TypeGenerator.isColumnNullable col

        if isNullable then
          let matchExpr =
            ParenExpr(
              MatchExpr(
                ConstantExpr $"item.{fieldName}",
                [ MatchClauseExpr("Some v", "box v")
                  MatchClauseExpr("None", "box DBNull.Value") ]
              )
            )

          let addWithValue =
            AppExpr("cmd.Parameters.AddWithValue", [ ConstantExpr $"\"@{col.name}\""; matchExpr ])

          pipeIgnore addWithValue
        else
          let addWithValue =
            AppExpr(
              "cmd.Parameters.AddWithValue",
              [ ConstantExpr $"\"@{col.name}\""; ConstantExpr $"item.{fieldName}" ]
            )

          pipeIgnore addWithValue)

    let bodyExprs =
      ConstantExpr $"use cmd = new SqliteCommand(\"{insertSql}\", tx.Connection, tx)"
      :: paramBindingStmts
      @ [ pipeIgnore (ConstantExpr "cmd.ExecuteNonQuery()")
          ConstantExpr "use lastIdCmd = new SqliteCommand(\"SELECT last_insert_rowid()\", tx.Connection, tx)"
          ConstantExpr "let lastId = lastIdCmd.ExecuteScalar() |> unbox<int64>"
          ConstantExpr "Ok lastId" ]

    let memberName = $"Insert (item: {typeName}) (tx: SqliteTransaction)"
    let returnType = "Result<int64, SqliteException>"
    let body = trySqliteException bodyExprs

    generateStaticMemberCode typeName memberName returnType body

/// Generate GET by ID method (transaction-only) using Fabulous.AST for sync version
let generateGet (useAsync: bool) (table: CreateTable) : string option =
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

    if useAsync then
      // Keep async version as string template for now (task CE is complex)
      let asyncParamBindings =
        pks
        |> List.map (fun pk -> $"cmd.Parameters.AddWithValue(\"@{pk.name}\", {pk.name}) |> ignore")
        |> String.concat "\n        "

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
        |> String.concat "\n          "

      Some
        $"""  static member GetById {paramList} (tx: SqliteTransaction) : Task<Result<{typeName} option, SqliteException>> =
    task {{
      try
        use cmd = new SqliteCommand("{getSql}", tx.Connection, tx)
        {asyncParamBindings}
        use! reader = cmd.ExecuteReaderAsync()
        let! hasRow = reader.ReadAsync()
        if hasRow then
          return Ok(Some {{
          {asyncFieldMappings}
          }})
        else
          return Ok None
      with
      | :? SqliteException as ex -> return Error ex
    }}"""
    else
      // Build the sync method body using AST
      // Generate parameter binding statements
      let paramBindingStmts =
        pks
        |> List.map (fun pk -> ConstantExpr $"cmd.Parameters.AddWithValue(\"@{pk.name}\", {pk.name}) |> ignore")

      let bodyExprs =
        [ ConstantExpr $"use cmd = new SqliteCommand(\"{getSql}\", tx.Connection, tx)" ]
        @ paramBindingStmts
        @ [ ConstantExpr "use reader = cmd.ExecuteReader()"
            ConstantExpr $"if reader.Read() then Ok(Some {{ {fieldMappings} }}) else Ok None" ]

      let memberName = $"GetById {paramList} (tx: SqliteTransaction)"
      let returnType = $"Result<{typeName} option, SqliteException>"
      let body = trySqliteException bodyExprs

      Some(generateStaticMemberCode typeName memberName returnType body)

/// Generate GET ALL method (transaction-only) using Fabulous.AST for sync version
let generateGetAll (useAsync: bool) (table: CreateTable) : string =
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

  if useAsync then
    // Keep async version as string template for now (task CE is complex)
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
      |> String.concat "\n              "

    $"""  static member GetAll (tx: SqliteTransaction) : Task<Result<{typeName} list, SqliteException>> =
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
            results.Add({{
              {asyncFieldMappings}
            }})
        return Ok(results |> Seq.toList)
      with
      | :? SqliteException as ex -> return Error ex
    }}"""
  else
    // Build the sync method body using AST
    let bodyExprs =
      [ ConstantExpr $"use cmd = new SqliteCommand(\"{getSql}\", tx.Connection, tx)"
        ConstantExpr "use reader = cmd.ExecuteReader()"
        ConstantExpr $"let results = ResizeArray<{typeName}>()"
        ConstantExpr $"while reader.Read() do results.Add({{ {fieldMappings} }})"
        ConstantExpr "Ok(results |> Seq.toList)" ]

    let memberName = "GetAll (tx: SqliteTransaction)"
    let returnType = $"Result<{typeName} list, SqliteException>"
    let body = trySqliteException bodyExprs

    generateStaticMemberCode typeName memberName returnType body

/// Generate GET ONE method (transaction-only) using Fabulous.AST for sync version
let generateGetOne (useAsync: bool) (table: CreateTable) : string =
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

  if useAsync then
    // Keep async version as string template for now (task CE is complex)
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
      |> String.concat "\n          "

    $"""  static member GetOne (tx: SqliteTransaction) : Task<Result<{typeName} option, SqliteException>> =
    task {{
      try
        use cmd = new SqliteCommand("{getSql}", tx.Connection, tx)
        use! reader = cmd.ExecuteReaderAsync()
        let! hasRow = reader.ReadAsync()
        if hasRow then
          return Ok(Some {{
          {asyncFieldMappings}
          }})
        else
          return Ok None
      with
      | :? SqliteException as ex -> return Error ex
    }}"""
  else
    // Build the sync method body using AST
    let bodyExprs =
      [ ConstantExpr $"use cmd = new SqliteCommand(\"{getSql}\", tx.Connection, tx)"
        ConstantExpr "use reader = cmd.ExecuteReader()"
        ConstantExpr $"if reader.Read() then Ok(Some {{ {fieldMappings} }}) else Ok None" ]

    let memberName = "GetOne (tx: SqliteTransaction)"
    let returnType = $"Result<{typeName} option, SqliteException>"
    let body = trySqliteException bodyExprs

    generateStaticMemberCode typeName memberName returnType body

/// Generate UPDATE method (transaction-only)
let generateUpdate (useAsync: bool) (table: CreateTable) : string option =
  let typeName = capitalize table.name
  let pkCols = getPrimaryKey table

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

    let paramBindings =
      table.columns
      |> List.map (fun col ->
        let fieldName = capitalize col.name
        let isNullable = TypeGenerator.isColumnNullable col

        if isNullable then
          let matchExpr =
            ParenExpr(
              MatchExpr(
                ConstantExpr $"item.{fieldName}",
                [ MatchClauseExpr("Some v", "box v")
                  MatchClauseExpr("None", "box DBNull.Value") ]
              )
            )

          let addWithValue =
            AppExpr("cmd.Parameters.AddWithValue", [ ConstantExpr $"\"@{col.name}\""; matchExpr ])

          pipeIgnore addWithValue
        else
          let addWithValue =
            AppExpr(
              "cmd.Parameters.AddWithValue",
              [ ConstantExpr $"\"@{col.name}\""; ConstantExpr $"item.{fieldName}" ]
            )

          pipeIgnore addWithValue)

    if useAsync then
      let asyncParamBindings =
        table.columns
        |> List.map (fun col ->
          let fieldName = capitalize col.name
          let isNullable = TypeGenerator.isColumnNullable col

          if isNullable then
            $"cmd.Parameters.AddWithValue(\"@{col.name}\", match item.{fieldName} with Some v -> box v | None -> box DBNull.Value) |> ignore"
          else
            $"cmd.Parameters.AddWithValue(\"@{col.name}\", item.{fieldName}) |> ignore")
        |> String.concat "\n        "

      Some
        $"""  static member Update (item: {typeName}) (tx: SqliteTransaction) : Task<Result<unit, SqliteException>> =
    task {{
      try
        use cmd = new SqliteCommand("{updateSql}", tx.Connection, tx)
        {asyncParamBindings}
        let! _ = cmd.ExecuteNonQueryAsync()
        return Ok()
      with
      | :? SqliteException as ex -> return Error ex
    }}"""
    else
      let bodyExprs =
        ConstantExpr $"use cmd = new SqliteCommand(\"{updateSql}\", tx.Connection, tx)"
        :: paramBindings
        @ returnOk (ConstantExpr "cmd.ExecuteNonQuery()")

      let memberName = $"Update (item: {typeName}) (tx: SqliteTransaction)"
      let returnType = "Result<unit, SqliteException>"
      let body = trySqliteException bodyExprs
      Some(generateStaticMemberCode typeName memberName returnType body)

/// Generate DELETE method (transaction-only) using Fabulous.AST
let generateDelete (useAsync: bool) (table: CreateTable) : string option =
  let typeName = capitalize table.name
  let pkCols = getPrimaryKey table

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

    // Generate parameter binding statements
    let paramBindingStmts =
      pks
      |> List.map (fun pk -> ConstantExpr $"cmd.Parameters.AddWithValue(\"@{pk.name}\", {pk.name}) |> ignore")

    if useAsync then
      // Keep async version as string template for now (task CE with try/with is complex)
      let asyncParamBindings =
        pks
        |> List.map (fun pk -> $"cmd.Parameters.AddWithValue(\"@{pk.name}\", {pk.name}) |> ignore")
        |> String.concat "\n        "

      Some
        $"""  static member Delete {paramList} (tx: SqliteTransaction) : Task<Result<unit, SqliteException>> =
    task {{
      try
        use cmd = new SqliteCommand("{deleteSql}", tx.Connection, tx)
        {asyncParamBindings}
        let! _ = cmd.ExecuteNonQueryAsync()
        return Ok()
      with
      | :? SqliteException as ex -> return Error ex
    }}"""
    else
      // Build the sync method body using AST
      let bodyExprs =
        ConstantExpr $"use cmd = new SqliteCommand(\"{deleteSql}\", tx.Connection, tx)"
        :: paramBindingStmts
        @ returnOk (ConstantExpr "cmd.ExecuteNonQuery()")

      let memberName = $"Delete {paramList} (tx: SqliteTransaction)"
      let returnType = "Result<unit, SqliteException>"
      let body = trySqliteException bodyExprs

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

/// Find column by name (case-insensitive)
let findColumn (table: CreateTable) (colName: string) : ColumnDef option =
  table.columns
  |> List.tryFind (fun c -> c.name.ToLowerInvariant() = colName.ToLowerInvariant())

/// Generate custom QueryBy method with tupled parameters using Fabulous.AST for sync version
let generateQueryBy (useAsync: bool) (table: CreateTable) (annotation: QueryByAnnotation) : string =
  let typeName = capitalize table.name

  // 1. Build method name: GetByIdName
  let methodName =
    annotation.columns
    |> List.map (fun col -> capitalize col)
    |> String.concat ""
    |> sprintf "GetBy%s"

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
  if useAsync then
    // Keep async version as string template for now (task CE is complex)
    let asyncParamBindings =
      annotation.columns
      |> List.map (fun col ->
        let columnDef = findColumn table col |> Option.get
        let isNullable = TypeGenerator.isColumnNullable columnDef

        if isNullable then
          $"cmd.Parameters.AddWithValue(\"@{col}\", match {col} with Some v -> box v | None -> box DBNull.Value) |> ignore"
        else
          $"cmd.Parameters.AddWithValue(\"@{col}\", {col}) |> ignore")
      |> String.concat "\n        "

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
      |> String.concat "\n              "

    $"""  static member {methodName} ({parameters}) (tx: SqliteTransaction) : Task<Result<{typeName} list, SqliteException>> =
    task {{
      try
        use cmd = new SqliteCommand("SELECT {columnNames} FROM {table.name} WHERE {whereClause}", tx.Connection, tx)
        {asyncParamBindings}
        use! reader = cmd.ExecuteReaderAsync()
        let results = ResizeArray<{typeName}>()
        let mutable hasMore = true
        while hasMore do
          let! next = reader.ReadAsync()
          hasMore <- next
          if hasMore then
            results.Add({{
              {asyncFieldMappings}
            }})
        return Ok(results |> Seq.toList)
      with
      | :? SqliteException as ex -> return Error ex
    }}"""
  else
    // Build the sync method body using AST
    let paramBindingStmts =
      annotation.columns
      |> List.map (fun col ->
        let columnDef = findColumn table col |> Option.get
        let isNullable = TypeGenerator.isColumnNullable columnDef

        if isNullable then
          let matchExpr =
            ParenExpr(
              MatchExpr(
                ConstantExpr col,
                [ MatchClauseExpr("Some v", "box v")
                  MatchClauseExpr("None", "box DBNull.Value") ]
              )
            )

          let addWithValue =
            AppExpr("cmd.Parameters.AddWithValue", [ ConstantExpr $"\"@{col}\""; matchExpr ])

          pipeIgnore addWithValue
        else
          let addWithValue =
            AppExpr("cmd.Parameters.AddWithValue", [ ConstantExpr $"\"@{col}\""; ConstantExpr col ])

          pipeIgnore addWithValue)

    let bodyExprs =
      ConstantExpr
        $"use cmd = new SqliteCommand(\"SELECT {columnNames} FROM {table.name} WHERE {whereClause}\", tx.Connection, tx)"
      :: paramBindingStmts
      @ [ ConstantExpr "use reader = cmd.ExecuteReader()"
          ConstantExpr $"let results = ResizeArray<{typeName}>()"
          ConstantExpr $"while reader.Read() do results.Add({{ {fieldMappings} }})"
          ConstantExpr "Ok(results |> Seq.toList)" ]

    let memberName = $"{methodName} ({parameters}) (tx: SqliteTransaction)"
    let returnType = $"Result<{typeName} list, SqliteException>"
    let body = trySqliteException bodyExprs

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

/// Generate custom QueryByOrCreate method that extracts query values from newItem using Fabulous.AST for sync version
let generateQueryByOrCreate (useAsync: bool) (table: CreateTable) (annotation: QueryByOrCreateAnnotation) : string =
  let typeName = capitalize table.name
  let pkCols = getPrimaryKey table

  // 1. Build method name: GetByStatusOrCreate
  let methodName =
    annotation.columns
    |> List.map (fun col -> capitalize col)
    |> String.concat ""
    |> sprintf "GetBy%sOrCreate"

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

  if useAsync then
    // Keep async version as string template for now (task CE is complex)
    let asyncValueExtractions =
      annotation.columns
      |> List.map (fun col ->
        let fieldName = capitalize col
        $"let {col} = newItem.{fieldName}")
      |> String.concat "\n        "

    let asyncParamBindings =
      annotation.columns
      |> List.map (fun col ->
        let columnDef = findColumn table col |> Option.get
        let isNullable = TypeGenerator.isColumnNullable columnDef

        if isNullable then
          $"cmd.Parameters.AddWithValue(\"@{col}\", match {col} with Some v -> box v | None -> box DBNull.Value) |> ignore"
        else
          $"cmd.Parameters.AddWithValue(\"@{col}\", {col}) |> ignore")
      |> String.concat "\n        "

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

    // Build GetById call (handle composite PK or no PK) - async version
    let getByIdCallAsync =
      match pkCols with
      | [] ->
        // No primary key - re-query with WHERE clause
        $"""
            // No primary key - re-query to get inserted record
            use cmd = new SqliteCommand("SELECT {columnNames} FROM {table.name} WHERE {whereClause} LIMIT 1", tx.Connection, tx)
            {asyncParamBindings}
            use! reader = cmd.ExecuteReaderAsync()
            let! hasRow = reader.ReadAsync()
            if hasRow then
              return Ok {{
                {asyncFieldMappings}
              }}
            else
              return Error (SqliteException("Failed to retrieve inserted record", 0))"""
      | pks ->
        // Has primary key - use GetById
        let getByIdParams = pks |> List.map (fun pk -> "newId") |> String.concat " "

        $"""
            // Fetch newly inserted record by ID
            let! getResult = {typeName}.GetById {getByIdParams} tx
            match getResult with
            | Ok (Some item) -> return Ok item
            | Ok None -> return Error (SqliteException("Failed to retrieve inserted record", 0))
            | Error ex -> return Error ex"""

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
          | Ok newId ->{getByIdCallAsync}
          | Error ex -> return Error ex
      with
      | :? SqliteException as ex -> return Error ex
    }}"""
  else
    // Build the sync method body using AST
    // Value extractions from newItem
    let valueExtractionStmts =
      annotation.columns
      |> List.map (fun col ->
        let fieldName = capitalize col
        ConstantExpr $"let {col} = newItem.{fieldName}")

    // Parameter bindings with match expressions for nullable columns
    let paramBindingStmts =
      annotation.columns
      |> List.map (fun col ->
        let columnDef = findColumn table col |> Option.get
        let isNullable = TypeGenerator.isColumnNullable columnDef

        if isNullable then
          let matchExpr =
            ParenExpr(
              MatchExpr(
                ConstantExpr col,
                [ MatchClauseExpr("Some v", "box v")
                  MatchClauseExpr("None", "box DBNull.Value") ]
              )
            )

          let addWithValue =
            AppExpr("cmd.Parameters.AddWithValue", [ ConstantExpr $"\"@{col}\""; matchExpr ])

          pipeIgnore addWithValue
        else
          let addWithValue =
            AppExpr("cmd.Parameters.AddWithValue", [ ConstantExpr $"\"@{col}\""; ConstantExpr col ])

          pipeIgnore addWithValue)

    // Build the if/else logic for found vs insert
    let ifElseLogic =
      match pkCols with
      | [] ->
        // No primary key - re-query with WHERE clause
        let nestedParamBindings =
          annotation.columns
          |> List.map (fun col ->
            let columnDef = findColumn table col |> Option.get
            let isNullable = TypeGenerator.isColumnNullable columnDef

            if isNullable then
              $"cmd.Parameters.AddWithValue(\"@{col}\", match {col} with Some v -> box v | None -> box DBNull.Value) |> ignore"
            else
              $"cmd.Parameters.AddWithValue(\"@{col}\", {col}) |> ignore")
          |> String.concat "; "

        $"if reader.Read() then Ok {{ {fieldMappings} }} else (reader.Close(); match {typeName}.Insert newItem tx with | Ok newId -> (use cmd = new SqliteCommand(\"SELECT {columnNames} FROM {table.name} WHERE {whereClause} LIMIT 1\", tx.Connection, tx); {nestedParamBindings}; use reader = cmd.ExecuteReader(); if reader.Read() then Ok {{ {fieldMappings} }} else Error (SqliteException(\"Failed to retrieve inserted record\", 0))) | Error ex -> Error ex)"
      | pks ->
        // Has primary key - use GetById
        let getByIdParams = pks |> List.map (fun pk -> "newId") |> String.concat " "
        $"if reader.Read() then Ok {{ {fieldMappings} }} else (reader.Close(); match {typeName}.Insert newItem tx with | Ok newId -> (match {typeName}.GetById {getByIdParams} tx with | Ok (Some item) -> Ok item | Ok None -> Error (SqliteException(\"Failed to retrieve inserted record\", 0)) | Error ex -> Error ex) | Error ex -> Error ex)"

    let bodyExprs =
      valueExtractionStmts
      @ [ ConstantExpr
            $"use cmd = new SqliteCommand(\"SELECT {columnNames} FROM {table.name} WHERE {whereClause} LIMIT 1\", tx.Connection, tx)" ]
      @ paramBindingStmts
      @ [ ConstantExpr "use reader = cmd.ExecuteReader()"; ConstantExpr ifElseLogic ]

    let memberName = $"{methodName} (newItem: {typeName}) (tx: SqliteTransaction)"
    let returnType = $"Result<{typeName}, SqliteException>"
    let body = trySqliteException bodyExprs

    generateStaticMemberCode typeName memberName returnType body

/// Generate code for a table
let generateTableCode (useAsync: bool) (table: CreateTable) : Result<string, string> =
  let typeName = capitalize table.name

  // Validate all QueryBy annotations
  let queryByValidationResults =
    table.queryByAnnotations |> List.map (validateQueryByAnnotation table)

  // Validate all QueryByOrCreate annotations
  let queryByOrCreateValidationResults =
    table.queryByOrCreateAnnotations
    |> List.map (validateQueryByOrCreateAnnotation table)

  let firstError =
    (queryByValidationResults @ queryByOrCreateValidationResults)
    |> List.tryFind (fun r ->
      match r with
      | Error _ -> true
      | _ -> false)

  match firstError with
  | Some(Error msg) -> Error msg
  | _ ->
    // CRUD methods with curried signatures
    let insertMethod = generateInsert useAsync table
    let getMethod = generateGet useAsync table
    let getAllMethod = generateGetAll useAsync table
    let getOneMethod = generateGetOne useAsync table
    let updateMethod = generateUpdate useAsync table
    let deleteMethod = generateDelete useAsync table

    // Generate QueryBy methods
    let queryByMethods =
      table.queryByAnnotations |> List.map (generateQueryBy useAsync table)

    // Generate QueryByOrCreate methods
    let queryByOrCreateMethods =
      table.queryByOrCreateAnnotations
      |> List.map (generateQueryByOrCreate useAsync table)

    let allMethods =
      [ Some insertMethod
        getMethod
        Some getAllMethod
        Some getOneMethod
        updateMethod
        deleteMethod ]
      @ (queryByMethods |> List.map Some)
      @ (queryByOrCreateMethods |> List.map Some)
      |> List.choose id
      |> String.concat "\n\n"

    Ok
      $"""type {typeName} with
{allMethods}"""

/// Generate GetAll method for a view (read-only)
let generateViewGetAll (useAsync: bool) (viewName: string) (columns: ViewColumn list) : string =
  let typeName = capitalize viewName
  let columnNames = columns |> List.map (fun c -> c.name) |> String.concat ", "
  let getSql = $"SELECT {columnNames} FROM {viewName}"

  let fieldMappings =
    columns
    |> List.mapi (fun i col ->
      let fieldName = capitalize col.name
      let method = TypeGenerator.mapSqlType col.columnType false |> readerMethod

      if col.isNullable then
        $"{fieldName} = if reader.IsDBNull {i} then None else Some(reader.Get{method} {i})"
      else
        $"{fieldName} = reader.Get{method} {i}")
    |> String.concat "\n          "

  if useAsync then
    let asyncFieldMappings =
      columns
      |> List.mapi (fun i col ->
        let fieldName = capitalize col.name
        let method = TypeGenerator.mapSqlType col.columnType false |> readerMethod

        if col.isNullable then
          $"{fieldName} = if reader.IsDBNull {i} then None else Some(reader.Get{method} {i})"
        else
          $"{fieldName} = reader.Get{method} {i}")
      |> String.concat "\n              "

    $"""  static member GetAll (tx: SqliteTransaction) : Task<Result<{typeName} list, SqliteException>> =
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
            results.Add({{
              {asyncFieldMappings}
            }})
        return Ok(results |> Seq.toList)
      with
      | :? SqliteException as ex -> return Error ex
    }}"""
  else
    $"""  static member GetAll (tx: SqliteTransaction) : Result<{typeName} list, SqliteException> =
    try
      use cmd = new SqliteCommand("{getSql}", tx.Connection, tx)
      use reader = cmd.ExecuteReader()
      let results = ResizeArray<{typeName}>()
      while reader.Read() do
        results.Add({{
          {fieldMappings}
        }})
      Ok(results |> Seq.toList)
    with
    | :? SqliteException as ex -> Error ex"""

/// Generate GET ONE method for a view (read-only)
let generateViewGetOne (useAsync: bool) (viewName: string) (columns: ViewColumn list) : string =
  let typeName = capitalize viewName
  let columnNames = columns |> List.map (fun c -> c.name) |> String.concat ", "
  let getSql = $"SELECT {columnNames} FROM {viewName} LIMIT 1"

  let fieldMappings =
    columns
    |> List.mapi (fun i col ->
      let fieldName = capitalize col.name
      let method = TypeGenerator.mapSqlType col.columnType false |> readerMethod

      if col.isNullable then
        $"{fieldName} = if reader.IsDBNull {i} then None else Some(reader.Get{method} {i})"
      else
        $"{fieldName} = reader.Get{method} {i}")
    |> String.concat "\n        "

  if useAsync then
    let asyncFieldMappings =
      columns
      |> List.mapi (fun i col ->
        let fieldName = capitalize col.name
        let method = TypeGenerator.mapSqlType col.columnType false |> readerMethod

        if col.isNullable then
          $"{fieldName} = if reader.IsDBNull {i} then None else Some(reader.Get{method} {i})"
        else
          $"{fieldName} = reader.Get{method} {i}")
      |> String.concat "\n          "

    $"""  static member GetOne (tx: SqliteTransaction) : Task<Result<{typeName} option, SqliteException>> =
    task {{
      try
        use cmd = new SqliteCommand("{getSql}", tx.Connection, tx)
        use! reader = cmd.ExecuteReaderAsync()
        let! hasRow = reader.ReadAsync()
        if hasRow then
          return Ok(Some {{
          {asyncFieldMappings}
          }})
        else
          return Ok None
      with
      | :? SqliteException as ex -> return Error ex
    }}"""
  else
    $"""  static member GetOne (tx: SqliteTransaction) : Result<{typeName} option, SqliteException> =
    try
      use cmd = new SqliteCommand("{getSql}", tx.Connection, tx)
      use reader = cmd.ExecuteReader()
      if reader.Read() then
        Ok(Some {{
        {fieldMappings}
        }})
      else
        Ok None
    with
    | :? SqliteException as ex -> Error ex"""

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

/// Find view column by name (case-insensitive)
let findViewColumn (columns: ViewColumn list) (colName: string) : ViewColumn option =
  columns
  |> List.tryFind (fun c -> c.name.ToLowerInvariant() = colName.ToLowerInvariant())

/// Generate custom QueryBy method for views with tupled parameters
let generateViewQueryBy
  (useAsync: bool)
  (viewName: string)
  (columns: ViewColumn list)
  (annotation: QueryByAnnotation)
  : string =
  let typeName = capitalize viewName

  // 1. Build method name: GetByIdName
  let methodName =
    annotation.columns
    |> List.map (fun col -> capitalize col)
    |> String.concat ""
    |> sprintf "GetBy%s"

  // 2. Build tupled parameters with types (case-insensitive column lookup)
  let parameters =
    annotation.columns
    |> List.map (fun col ->
      let columnDef = findViewColumn columns col |> Option.get
      let fsharpType = TypeGenerator.mapSqlType columnDef.columnType columnDef.isNullable
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
      let columnDef = findViewColumn columns col |> Option.get

      if columnDef.isNullable then
        $"cmd.Parameters.AddWithValue(\"@{col}\", match {col} with Some v -> box v | None -> box DBNull.Value) |> ignore"
      else
        $"cmd.Parameters.AddWithValue(\"@{col}\", {col}) |> ignore")
    |> String.concat "\n      "

  // 5. Get column names for SELECT
  let columnNames = columns |> List.map (fun c -> c.name) |> String.concat ", "

  // 6. Generate field mappings for reader
  let fieldMappings =
    columns
    |> List.mapi (fun i col ->
      let fieldName = capitalize col.name
      let method = TypeGenerator.mapSqlType col.columnType false |> readerMethod

      if col.isNullable then
        $"{fieldName} = if reader.IsDBNull {i} then None else Some(reader.Get{method} {i})"
      else
        $"{fieldName} = reader.Get{method} {i}")
    |> String.concat "\n          "

  // 7. Generate full method with tupled parameters
  if useAsync then
    let asyncParamBindings =
      annotation.columns
      |> List.map (fun col ->
        let columnDef = findViewColumn columns col |> Option.get

        if columnDef.isNullable then
          $"cmd.Parameters.AddWithValue(\"@{col}\", match {col} with Some v -> box v | None -> box DBNull.Value) |> ignore"
        else
          $"cmd.Parameters.AddWithValue(\"@{col}\", {col}) |> ignore")
      |> String.concat "\n        "

    let asyncFieldMappings =
      columns
      |> List.mapi (fun i col ->
        let fieldName = capitalize col.name
        let method = TypeGenerator.mapSqlType col.columnType false |> readerMethod

        if col.isNullable then
          $"{fieldName} = if reader.IsDBNull {i} then None else Some(reader.Get{method} {i})"
        else
          $"{fieldName} = reader.Get{method} {i}")
      |> String.concat "\n              "

    $"""  static member {methodName} ({parameters}) (tx: SqliteTransaction) : Task<Result<{typeName} list, SqliteException>> =
    task {{
      try
        use cmd = new SqliteCommand("SELECT {columnNames} FROM {viewName} WHERE {whereClause}", tx.Connection, tx)
        {asyncParamBindings}
        use! reader = cmd.ExecuteReaderAsync()
        let results = ResizeArray<{typeName}>()
        let mutable hasMore = true
        while hasMore do
          let! next = reader.ReadAsync()
          hasMore <- next
          if hasMore then
            results.Add({{
              {asyncFieldMappings}
            }})
        return Ok(results |> Seq.toList)
      with
      | :? SqliteException as ex -> return Error ex
    }}"""
  else
    $"""  static member {methodName} ({parameters}) (tx: SqliteTransaction) : Result<{typeName} list, SqliteException> =
    try
      use cmd = new SqliteCommand("SELECT {columnNames} FROM {viewName} WHERE {whereClause}", tx.Connection, tx)
      {paramBindings}
      use reader = cmd.ExecuteReader()
      let results = ResizeArray<{typeName}>()
      while reader.Read() do
        results.Add({{
          {fieldMappings}
        }})
      Ok(results |> Seq.toList)
    with
    | :? SqliteException as ex -> Error ex"""

/// Generate code for a view (read-only queries)
let generateViewCode (useAsync: bool) (view: CreateView) (columns: ViewColumn list) : Result<string, string> =
  let typeName = capitalize view.name

  // Reject QueryByOrCreate annotations on views (views are read-only)
  match view.queryByOrCreateAnnotations with
  | [] ->
    // Validate all QueryBy annotations
    let validationResults =
      view.queryByAnnotations
      |> List.map (validateViewQueryByAnnotation view.name columns)

    let firstError =
      validationResults
      |> List.tryFind (fun r ->
        match r with
        | Error _ -> true
        | _ -> false)

    match firstError with
    | Some(Error msg) -> Error msg
    | _ ->
      let getAllMethod = generateViewGetAll useAsync view.name columns
      let getOneMethod = generateViewGetOne useAsync view.name columns

      // Generate QueryBy methods
      let queryByMethods =
        view.queryByAnnotations
        |> List.map (generateViewQueryBy useAsync view.name columns)

      let allMethods =
        [ getAllMethod; getOneMethod ] @ queryByMethods |> String.concat "\n\n"

      Ok
        $"""type {typeName} with
{allMethods}"""
  | _ ->
    Error
      $"QueryByOrCreate annotation is not supported on views (view '{view.name}' is read-only). Use QueryBy instead."
