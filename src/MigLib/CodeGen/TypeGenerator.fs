module internal migrate.CodeGen.TypeGenerator

open migrate.DeclarativeMigrations.Types
open migrate.CodeGen.ViewIntrospection

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

/// Check if a column is nullable (doesn't have NOT NULL constraint)
let isColumnNullable (column: ColumnDef) : bool =
  column.constraints
  |> List.exists (fun c ->
    match c with
    | NotNull -> true
    | _ -> false)
  |> not

/// Generate a record field from a column definition
let generateField (column: ColumnDef) =
  let isNullable = isColumnNullable column
  let fsharpType = mapSqlType column.columnType isNullable
  // Capitalize first letter of column name for F# convention
  let fieldName =
    if String.length column.name > 0 then
      (string column.name.[0]).ToUpper() + column.name.[1..]
    else
      column.name

  fieldName, fsharpType

/// Generate an F# record type from a table definition
let generateRecordType (table: CreateTable) : string =
  let typeName =
    if String.length table.name > 0 then
      (string table.name.[0]).ToUpper() + table.name.[1..]
    else
      table.name

  let fields =
    table.columns
    |> List.map generateField
    |> List.map (fun (name, typ) -> $"    {name}: {typ}")
    |> String.concat "\n"

  $"type {typeName} = {{\n{fields}\n}}"

/// Generate an F# record type from a view definition
let generateViewRecordType (viewName: string) (columns: ViewColumn list) : string =
  let typeName =
    if String.length viewName > 0 then
      (string viewName.[0]).ToUpper() + viewName.[1..]
    else
      viewName

  let fields =
    columns
    |> List.map (fun col ->
      let fsharpType = mapSqlType col.columnType col.isNullable
      let fieldName =
        if String.length col.name > 0 then
          (string col.name.[0]).ToUpper() + col.name.[1..]
        else
          col.name

      fieldName, fsharpType)
    |> List.map (fun (name, typ) -> $"    {name}: {typ}")
    |> String.concat "\n"

  $"type {typeName} = {{\n{fields}\n}}"
