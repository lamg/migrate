module internal Mig.CodeGen.QueryGeneratorCommon

open Mig.DeclarativeMigrations.Types
open Mig.CodeGen.ViewIntrospection
open Mig.CodeGen.AstExprBuilders

let indent n = String.replicate n " "

let joinWithIndent (spaces: int) (lines: string list) =
  lines |> String.concat $"\n{indent spaces}"

let getPrimaryKey (table: CreateTable) : ColumnDef list =
  let columnLevelPks =
    table.columns
    |> List.filter (fun col ->
      col.constraints
      |> List.exists (fun c ->
        match c with
        | PrimaryKey _ -> true
        | _ -> false))

  let tableLevelPkCols =
    table.constraints
    |> List.tryPick (fun c ->
      match c with
      | PrimaryKey pk when pk.columns.Length > 0 -> Some pk.columns
      | _ -> None)
    |> Option.defaultValue []
    |> List.choose (fun colName -> table.columns |> List.tryFind (fun col -> col.name = colName))

  if tableLevelPkCols.Length > 0 then
    tableLevelPkCols
  else
    columnLevelPks

let getForeignKeys (table: CreateTable) : (string * string) list =
  let columnFks =
    table.columns
    |> List.collect (fun col ->
      col.constraints
      |> List.choose (fun c ->
        match c with
        | ForeignKey fk -> Some(col.name, fk.refTable)
        | _ -> None))

  let tableFks =
    table.constraints
    |> List.choose (fun c ->
      match c with
      | ForeignKey fk when fk.columns.Length = 1 -> Some(fk.columns.[0], fk.refTable)
      | _ -> None)

  columnFks @ tableFks |> List.distinct

let private capitalize = TypeGenerator.toPascalCase
let capitalizeName = capitalize

let readerMethod (t: string) =
  t.Replace("int64", "Int64").Replace("string", "String").Replace("float", "Double").Replace("DateTime", "DateTime")

let getAutoIncrementPrimaryKeyColumnName (table: CreateTable) : string option =
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

let rowDataPairExprForItem (itemExpr: string) (column: ColumnDef) : string =
  let fieldName = capitalize column.name
  let isNullable = TypeGenerator.isColumnNullable column
  let itemFieldExpr = $"{itemExpr}.{fieldName}"

  if isNullable then
    let storedValueExpr = TypeGenerator.toDbValueExpr column "v"
    $"(\"{column.name}\", match {itemFieldExpr} with Some v -> box ({storedValueExpr}) | None -> null)"
  else
    let storedValueExpr = TypeGenerator.toDbValueExpr column itemFieldExpr
    $"(\"{column.name}\", box ({storedValueExpr}))"

let rowDataListExprForItem (itemExpr: string) (columns: ColumnDef list) : string =
  columns
  |> List.map (rowDataPairExprForItem itemExpr)
  |> String.concat "; "
  |> fun pairs -> $"[{pairs}]"

let rowDataListExprForParams (columns: ColumnDef list) : string =
  columns
  |> List.map (fun column -> $"(\"{column.name}\", box ({TypeGenerator.toDbValueExpr column column.name}))")
  |> String.concat "; "
  |> fun pairs -> $"[{pairs}]"

let findColumn (table: CreateTable) (colName: string) : ColumnDef option =
  table.columns
  |> List.tryFind (fun c -> c.name.ToLowerInvariant() = colName.ToLowerInvariant())

let findViewColumn (columns: ViewColumn list) (colName: string) : ViewColumn option =
  columns
  |> List.tryFind (fun c -> c.name.ToLowerInvariant() = colName.ToLowerInvariant())
