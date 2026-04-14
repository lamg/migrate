module internal Mig.CodeGen.TypeGenerator

open System
open Mig.DeclarativeMigrations.Types
open Fabulous.AST
open type Fabulous.AST.Ast

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

  if isNullable then $"{baseType} option" else baseType

let private readerMethod (t: string) =
  t.Replace("int64", "Int64").Replace("string", "String").Replace("float", "Double").Replace("DateTime", "DateTime")

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

let mapColumnType (column: ColumnDef) : string =
  let isNullable = isColumnNullable column

  let baseType =
    match column.enumLikeDu, column.unitOfMeasure with
    | Some enumLikeDu, _ -> enumLikeDu.typeName
    | None, Some unitOfMeasure -> $"{mapSqlType column.columnType false}<{unitOfMeasure}>"
    | None, None -> mapSqlType column.columnType false

  if isNullable then $"{baseType} option" else baseType

let mapViewColumnType (column: ViewColumn) : string =
  match column.enumLikeDu, column.unitOfMeasure with
  | Some enumLikeDu, _ -> enumLikeDu.typeName
  | None, Some unitOfMeasure -> $"{mapSqlType column.columnType false}<{unitOfMeasure}>"
  | None, None -> mapSqlType column.columnType false

let enumCaseToDbValueExpr (enumLikeDu: EnumLikeDu) (valueExpr: string) : string =
  let cases =
    enumLikeDu.cases
    |> List.map (fun caseName -> $"| {enumLikeDu.typeName}.{caseName} -> \"{caseName}\"")
    |> String.concat " "

  $"(match {valueExpr} with {cases})"

let enumFromDbValueExpr (enumLikeDu: EnumLikeDu) (columnName: string) (valueExpr: string) : string =
  let cases =
    enumLikeDu.cases
    |> List.map (fun caseName -> $"| \"{caseName}\" -> {enumLikeDu.typeName}.{caseName}")
    |> String.concat " "

  $"(match {valueExpr} with {cases} | other -> raise (SqliteException($\"Invalid value '{{other}}' for {enumLikeDu.typeName} column '{columnName}'\", 0)))"

let toDbValueExpr (column: ColumnDef) (valueExpr: string) : string =
  match column.enumLikeDu with
  | Some enumLikeDu -> enumCaseToDbValueExpr enumLikeDu valueExpr
  | None -> valueExpr

let toViewDbValueExpr (column: ViewColumn) (valueExpr: string) : string =
  match column.enumLikeDu with
  | Some enumLikeDu -> enumCaseToDbValueExpr enumLikeDu valueExpr
  | None -> valueExpr

let toNullableDbValueExpr (column: ColumnDef) (valueExpr: string) : string =
  match column.enumLikeDu with
  | Some _ ->
    let innerExpr = toDbValueExpr column "v"
    $"match {valueExpr} with Some v -> box ({innerExpr}) | None -> box DBNull.Value"
  | None -> $"match {valueExpr} with Some v -> box v | None -> box DBNull.Value"

let readColumnExpr (column: ColumnDef) (index: int) : string =
  let readerExpr =
    match column.enumLikeDu with
    | Some enumLikeDu -> enumFromDbValueExpr enumLikeDu column.name $"reader.GetString {index}"
    | None ->
      match column.unitOfMeasure, column.columnType with
      | Some unitOfMeasure, SqlInteger ->
        $"LanguagePrimitives.Int64WithMeasure<{unitOfMeasure}> (reader.GetInt64 {index})"
      | Some unitOfMeasure, SqlReal ->
        $"LanguagePrimitives.FloatWithMeasure<{unitOfMeasure}> (reader.GetDouble {index})"
      | Some _, _ ->
        let method = mapSqlType column.columnType false |> readerMethod
        $"reader.Get{method} {index}"
      | None, _ ->
        let method = mapSqlType column.columnType false |> readerMethod
        $"reader.Get{method} {index}"

  if isColumnNullable column then
    $"if reader.IsDBNull {index} then None else Some({readerExpr})"
  else
    readerExpr

let readViewColumnExpr (column: ViewColumn) (index: int) : string =
  match column.enumLikeDu with
  | Some enumLikeDu -> enumFromDbValueExpr enumLikeDu column.name $"reader.GetString {index}"
  | None ->
    match column.unitOfMeasure, column.columnType with
    | Some unitOfMeasure, SqlInteger ->
      $"LanguagePrimitives.Int64WithMeasure<{unitOfMeasure}> (reader.GetInt64 {index})"
    | Some unitOfMeasure, SqlReal -> $"LanguagePrimitives.FloatWithMeasure<{unitOfMeasure}> (reader.GetDouble {index})"
    | Some _, _ ->
      let method = mapSqlType column.columnType false |> readerMethod
      $"reader.Get{method} {index}"
    | None, _ ->
      let method = mapSqlType column.columnType false |> readerMethod
      $"reader.Get{method} {index}"

let collectEnumLikeDusFromColumns (columns: #seq<ColumnDef>) : EnumLikeDu list =
  columns
  |> Seq.choose _.enumLikeDu
  |> Seq.distinctBy (fun enumLikeDu -> enumLikeDu.typeName, enumLikeDu.cases)
  |> Seq.toList

let collectEnumLikeDusFromViewColumns (columns: #seq<ViewColumn>) : EnumLikeDu list =
  columns
  |> Seq.choose _.enumLikeDu
  |> Seq.distinctBy (fun enumLikeDu -> enumLikeDu.typeName, enumLikeDu.cases)
  |> Seq.toList

let generateEnumType (enumLikeDu: EnumLikeDu) : string =
  Oak() {
    AnonymousModule() {
      Union enumLikeDu.typeName {
        for caseName in enumLikeDu.cases do
          UnionCase caseName
      }
    }
  }
  |> Gen.mkOak
  |> Gen.run

let generateMeasureType (measureType: string) =
  Oak() { AnonymousModule() { Measure measureType } } |> Gen.mkOak |> Gen.run

/// Generate a record field from a column definition
let generateField (column: ColumnDef) =
  let fsharpType = mapColumnType column
  let fieldName = toPascalCase column.name
  fieldName, fsharpType

/// Generate an F# record type from a table definition
let generateRecordType (table: CreateTable) : string =
  let typeName = toPascalCase table.name

  Oak() {
    AnonymousModule() {
      Record typeName {
        for column in table.columns do
          let fieldName, fieldType = generateField column
          Field(fieldName, fieldType)
      }
    }
  }
  |> Gen.mkOak
  |> Gen.run

/// Generate an F# record type from a view definition
let generateViewRecordType (viewName: string) (columns: ViewColumn list) : string =
  let typeName = toPascalCase viewName

  Oak() {
    AnonymousModule() {
      Record typeName {
        for col in columns do
          let fsharpType = mapViewColumnType col
          let fieldName = toPascalCase col.name
          Field(fieldName, fsharpType)
      }
    }
  }
  |> Gen.mkOak
  |> Gen.run
