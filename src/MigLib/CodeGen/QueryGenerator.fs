module internal migrate.CodeGen.QueryGenerator

open migrate.DeclarativeMigrations.Types
open migrate.CodeGen.ViewIntrospection
open Fabulous.AST
open type Fabulous.AST.Ast

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
    s.Split('_')
    |> Array.map (fun part ->
      if part.Length > 0 then
        (string part.[0]).ToUpper() + part.[1..].ToLower()
      else
        part)
    |> System.String.Concat

/// Helper to get reader method name from F# type
let readerMethod (t: string) =
  t.Replace("int64", "Int64").Replace("string", "String").Replace("float", "Double").Replace("DateTime", "DateTime")

/// Generate INSERT method
let generateInsert (table: CreateTable) : string =
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

  $"""  static member Insert (item: {typeName}) (tx: SqliteTransaction) : Result<int64, SqliteException> =
    try
      use cmd = new SqliteCommand("{insertSql}", tx.Connection, tx)
{insertCols
 |> List.map (fun col ->
   let fieldName = capitalize col.name
   let isNullable = TypeGenerator.isColumnNullable col

   if isNullable then
     $"      cmd.Parameters.AddWithValue(\"@{col.name}\", match item.{fieldName} with Some v -> box v | None -> box DBNull.Value) |> ignore"
   else
     $"      cmd.Parameters.AddWithValue(\"@{col.name}\", item.{fieldName}) |> ignore")
 |> String.concat "\n"}
      cmd.ExecuteNonQuery() |> ignore
      use lastIdCmd = new SqliteCommand("SELECT last_insert_rowid()", tx.Connection, tx)
      let lastId = lastIdCmd.ExecuteScalar() |> unbox<int64>
      Ok lastId
    with
    | :? SqliteException as ex -> Error ex"""

/// Generate GET by ID method (transaction-only)
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

    // Generate parameter bindings
    let paramBindings =
      pks
      |> List.map (fun pk -> $"      cmd.Parameters.AddWithValue(\"@{pk.name}\", {pk.name}) |> ignore")
      |> String.concat "\n"

    let fieldMappings =
      table.columns
      |> List.mapi (fun i col ->
        let fieldName = capitalize col.name
        let isNullable = TypeGenerator.isColumnNullable col
        let method = TypeGenerator.mapSqlType col.columnType false |> readerMethod

        if isNullable then
          $"        {fieldName} = if reader.IsDBNull {i} then None else Some(reader.Get{method} {i})"
        else
          $"        {fieldName} = reader.Get{method} {i}")
      |> String.concat "\n"

    Some
      $"""  static member GetById {paramList} (tx: SqliteTransaction) : Result<{typeName} option, SqliteException> =
    try
      use cmd = new SqliteCommand("{getSql}", tx.Connection, tx)
{paramBindings}
      use reader = cmd.ExecuteReader()
      if reader.Read() then
        Ok(Some {{
{fieldMappings}
        }})
      else
        Ok None
    with
    | :? SqliteException as ex -> Error ex"""

/// Generate GET ALL method (transaction-only)
let generateGetAll (table: CreateTable) : string =
  let typeName = capitalize table.name
  let columnNames = table.columns |> List.map (fun c -> c.name) |> String.concat ", "
  let getSql = $"SELECT {columnNames} FROM {table.name}"

  let fieldMappings =
    table.columns
    |> List.mapi (fun i col ->
      let fieldName = capitalize col.name
      let isNullable = TypeGenerator.isColumnNullable col
      let method = TypeGenerator.mapSqlType col.columnType false |> readerMethod

      if isNullable then
        $"          {fieldName} = if reader.IsDBNull {i} then None else Some(reader.Get{method} {i})"
      else
        $"          {fieldName} = reader.Get{method} {i}")
    |> String.concat "\n"

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

/// Generate GET ONE method (transaction-only)
let generateGetOne (table: CreateTable) : string =
  let typeName = capitalize table.name
  let columnNames = table.columns |> List.map (fun c -> c.name) |> String.concat ", "
  let getSql = $"SELECT {columnNames} FROM {table.name} LIMIT 1"

  let fieldMappings =
    table.columns
    |> List.mapi (fun i col ->
      let fieldName = capitalize col.name
      let isNullable = TypeGenerator.isColumnNullable col
      let method = TypeGenerator.mapSqlType col.columnType false |> readerMethod

      if isNullable then
        $"        {fieldName} = if reader.IsDBNull {i} then None else Some(reader.Get{method} {i})"
      else
        $"        {fieldName} = reader.Get{method} {i}")
    |> String.concat "\n"

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

/// Generate UPDATE method (transaction-only)
let generateUpdate (table: CreateTable) : string option =
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
          $"      cmd.Parameters.AddWithValue(\"@{col.name}\", match item.{fieldName} with Some v -> box v | None -> box DBNull.Value) |> ignore"
        else
          $"      cmd.Parameters.AddWithValue(\"@{col.name}\", item.{fieldName}) |> ignore")
      |> String.concat "\n"

    Some
      $"""  static member Update (item: {typeName}) (tx: SqliteTransaction) : Result<unit, SqliteException> =
    try
      use cmd = new SqliteCommand("{updateSql}", tx.Connection, tx)
{paramBindings}
      cmd.ExecuteNonQuery() |> ignore
      Ok()
    with
    | :? SqliteException as ex -> Error ex"""

/// Generate DELETE method (transaction-only)
let generateDelete (table: CreateTable) : string option =
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

    // Generate parameter bindings
    let paramBindings =
      pks
      |> List.map (fun pk -> $"      cmd.Parameters.AddWithValue(\"@{pk.name}\", {pk.name}) |> ignore")
      |> String.concat "\n"

    Some
      $"""  static member Delete {paramList} (tx: SqliteTransaction) : Result<unit, SqliteException> =
    try
      use cmd = new SqliteCommand("{deleteSql}", tx.Connection, tx)
{paramBindings}
      cmd.ExecuteNonQuery() |> ignore
      Ok()
    with
    | :? SqliteException as ex -> Error ex"""

/// Generate code for a table
let generateTableCode (table: CreateTable) : string =
  let typeName = capitalize table.name

  // CRUD methods with curried signatures
  let insertMethod = generateInsert table
  let getMethod = generateGet table
  let getAllMethod = generateGetAll table
  let getOneMethod = generateGetOne table
  let updateMethod = generateUpdate table
  let deleteMethod = generateDelete table

  let methods =
    [ Some insertMethod
      getMethod
      Some getAllMethod
      Some getOneMethod
      updateMethod
      deleteMethod ]
    |> List.choose id
    |> String.concat "\n\n"

  $"""type {typeName} with
{methods}"""

/// Generate GetAll method for a view (read-only)
let generateViewGetAll (viewName: string) (columns: ViewColumn list) : string =
  let typeName = capitalize viewName
  let columnNames = columns |> List.map (fun c -> c.name) |> String.concat ", "
  let getSql = $"SELECT {columnNames} FROM {viewName}"

  let fieldMappings =
    columns
    |> List.mapi (fun i col ->
      let fieldName = capitalize col.name
      let method = TypeGenerator.mapSqlType col.columnType false |> readerMethod

      if col.isNullable then
        $"          {fieldName} = if reader.IsDBNull {i} then None else Some(reader.Get{method} {i})"
      else
        $"          {fieldName} = reader.Get{method} {i}")
    |> String.concat "\n"

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
let generateViewGetOne (viewName: string) (columns: ViewColumn list) : string =
  let typeName = capitalize viewName
  let columnNames = columns |> List.map (fun c -> c.name) |> String.concat ", "
  let getSql = $"SELECT {columnNames} FROM {viewName} LIMIT 1"

  let fieldMappings =
    columns
    |> List.mapi (fun i col ->
      let fieldName = capitalize col.name
      let method = TypeGenerator.mapSqlType col.columnType false |> readerMethod

      if col.isNullable then
        $"        {fieldName} = if reader.IsDBNull {i} then None else Some(reader.Get{method} {i})"
      else
        $"        {fieldName} = reader.Get{method} {i}")
    |> String.concat "\n"

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

/// Generate code for a view (read-only queries)
let generateViewCode (viewName: string) (columns: ViewColumn list) : string =
  let typeName = capitalize viewName
  let getAllMethod = generateViewGetAll viewName columns
  let getOneMethod = generateViewGetOne viewName columns

  $"""type {typeName} with
{getAllMethod}

{getOneMethod}"""
