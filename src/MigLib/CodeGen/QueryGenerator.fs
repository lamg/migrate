module internal migrate.CodeGen.QueryGenerator

open migrate.DeclarativeMigrations.Types
open Fabulous.AST
open type Fabulous.AST.Ast

/// Get the primary key column(s) from a table
let getPrimaryKey (table: CreateTable) : ColumnDef list =
  table.columns
  |> List.filter (fun col ->
    col.constraints
    |> List.exists (fun c ->
      match c with
      | PrimaryKey _ -> true
      | _ -> false))

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

/// Capitalize first letter for F# naming conventions
let capitalize (s: string) =
  if String.length s > 0 then
    (string s.[0]).ToUpper() + s.[1..]
  else
    s

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
               | _ -> false))))

  let columnNames = insertCols |> List.map (fun c -> c.name) |> String.concat ", "

  let paramNames =
    insertCols |> List.map (fun c -> $"@{c.name}") |> String.concat ", "

  let insertSql = $"INSERT INTO {table.name} ({columnNames}) VALUES ({paramNames})"

  $"""    static member Insert(conn: SqliteConnection, item: {typeName}) : Result<int64, SqliteException> =
        try
            use cmd = new SqliteCommand("{insertSql}", conn)
{insertCols
 |> List.map (fun col ->
   let fieldName = capitalize col.name
   let isNullable = TypeGenerator.isColumnNullable col

   if isNullable then
     $"            cmd.Parameters.AddWithValue(\"@{col.name}\", match item.{fieldName} with Some v -> box v | None -> box DBNull.Value) |> ignore"
   else
     $"            cmd.Parameters.AddWithValue(\"@{col.name}\", item.{fieldName}) |> ignore")
 |> String.concat "\n"}
            cmd.ExecuteNonQuery() |> ignore
            Ok(conn.LastInsertRowId)
        with
        | :? SqliteException as ex -> Error ex"""

/// Generate GET by ID method
let generateGet (table: CreateTable) : string option =
  let typeName = capitalize table.name
  let pkCols = getPrimaryKey table

  match pkCols with
  | [ pk ] ->
    let pkType = TypeGenerator.mapSqlType pk.columnType false
    let columnNames = table.columns |> List.map (fun c -> c.name) |> String.concat ", "
    let getSql = $"SELECT {columnNames} FROM {table.name} WHERE {pk.name} = @id"

    let readerMethod (t: string) =
      t.Replace("int64", "Int64").Replace("string", "String").Replace("float", "Double")

    let fieldMappings =
      table.columns
      |> List.mapi (fun i col ->
        let fieldName = capitalize col.name
        let isNullable = TypeGenerator.isColumnNullable col
        let method = TypeGenerator.mapSqlType col.columnType false |> readerMethod

        if isNullable then
          $"                    {fieldName} = if reader.IsDBNull({i}) then None else Some(reader.Get{method}({i}))"
        else
          $"                    {fieldName} = reader.Get{method}({i})")
      |> String.concat "\n"

    Some
      $"""    static member GetById(conn: SqliteConnection, id: {pkType}) : Result<{typeName} option, SqliteException> =
        try
            use cmd = new SqliteCommand("{getSql}", conn)
            cmd.Parameters.AddWithValue("@id", id) |> ignore
            use reader = cmd.ExecuteReader()
            if reader.Read() then
                Ok(Some {{
{fieldMappings}
                }})
            else
                Ok None
        with
        | :? SqliteException as ex -> Error ex"""
  | _ -> None // Skip composite primary keys for now

/// Generate code for a table
let generateTableCode (table: CreateTable) : string =
  let typeName = capitalize table.name
  let insertMethod = generateInsert table
  let getMethod = generateGet table

  let methods =
    [ Some insertMethod; getMethod ] |> List.choose id |> String.concat "\n\n"

  $"""type {typeName} with
{methods}"""
