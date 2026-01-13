module internal migrate.CodeGen.TypeGenerator

open System
open migrate.DeclarativeMigrations.Types
open migrate.CodeGen.ViewIntrospection

/// Convert snake_case to PascalCase for F# naming conventions
let toPascalCase (s: string) =
  if String.IsNullOrWhiteSpace s then
    s
  else
    s.Split '_'
    |> Array.map (fun part ->
      if String.length part > 0 then
        (string part.[0]).ToUpper() + part.[1..].ToLower()
      else
        part)
    |> String.concat ""

/// Map SQL types to F# types
let mapSqlType (sqlType: SqlType) (isNullable: bool) : string =
  let baseType =
    match sqlType with
    | SqlInteger -> "int64"
    | SqlText -> "string"
    | SqlReal -> "float"
    | SqlTimestamp -> "DateTime"
    | SqlString -> "string"
    | SqlFlexible -> "obj"

  if isNullable then $"{baseType} option" else baseType

/// Check if a column is nullable (doesn't have NOT NULL constraint or PRIMARY KEY)
/// PRIMARY KEY columns are implicitly NOT NULL in SQLite
let isColumnNullable (column: ColumnDef) : bool =
  column.constraints
  |> List.exists (fun c ->
    match c with
    | NotNull -> true
    | PrimaryKey _ -> true
    | _ -> false)
  |> not

/// Generate a record field from a column definition
let generateField (column: ColumnDef) =
  let isNullable = isColumnNullable column
  let fsharpType = mapSqlType column.columnType isNullable
  let fieldName = toPascalCase column.name
  fieldName, fsharpType

/// Generate an F# record type from a table definition
let generateRecordType (table: CreateTable) : string =
  let typeName = toPascalCase table.name

  let fields =
    table.columns
    |> List.map generateField
    |> List.map (fun (name, typ) -> $"    {name}: {typ}")
    |> String.concat "\n"

  $"type {typeName} = {{\n{fields}\n}}"

/// Generate an F# record type from a view definition
let generateViewRecordType (viewName: string) (columns: ViewColumn list) : string =
  let typeName = toPascalCase viewName

  let fields =
    columns
    |> List.map (fun col ->
      let fsharpType = mapSqlType col.columnType col.isNullable
      let fieldName = toPascalCase col.name
      fieldName, fsharpType)
    |> List.map (fun (name, typ) -> $"    {name}: {typ}")
    |> String.concat "\n"

  $"type {typeName} = {{\n{fields}\n}}"
