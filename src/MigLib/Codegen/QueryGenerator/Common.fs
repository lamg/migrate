module internal MigLib.Codegen.QueryGeneratorCommon

open Fabulous.AST
open MigLib.Codegen
open MigLib.Schema.Types
open MigLib.Codegen.AstExprBuilders
open MigLib.Codegen.SqlFragments
open MigLib.Codegen.SqlParamBindings

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
      | ForeignKey fk when fk.columns.Length = 1 -> Some(fk.columns[0], fk.refTable)
      | _ -> None)

  columnFks @ tableFks |> List.distinct

let private capitalize = TypeGenerator.toPascalCase

let capitalizeName = capitalize

let findColumn (table: CreateTable) (colName: string) : ColumnDef option =
  table.columns
  |> List.tryFind (fun col -> col.name.ToLowerInvariant() = colName.ToLowerInvariant())

let findViewColumn (columns: ViewColumn list) (colName: string) : ViewColumn option =
  columns
  |> List.tryFind (fun col -> col.name.ToLowerInvariant() = colName.ToLowerInvariant())

let paramBindingExprForItem (cmdVarName: string) (itemExpr: string) (column: ColumnDef) =
  let fieldName = capitalize column.name
  addColumnBinding cmdVarName column $"{itemExpr}.{fieldName}"

let paramBindingExprForColumnVar (cmdVarName: string) (column: ColumnDef) (varExpr: string) =
  addColumnBinding cmdVarName column varExpr

type SelectableColumn =
  { name: string
    fsharpType: string
    readExpr: int -> string
    bindingExpr: string -> string -> string }

let selectableColumnFromColumnDef (column: ColumnDef) : SelectableColumn =
  { name = column.name
    fsharpType = TypeGenerator.mapColumnType column
    readExpr = TypeGenerator.readColumnExpr column
    bindingExpr = fun cmdVarName valueExpr -> addColumnBinding cmdVarName column valueExpr }

let selectableColumnFromViewColumn (column: ViewColumn) : SelectableColumn =
  { name = column.name
    fsharpType = TypeGenerator.mapViewColumnType column
    readExpr = TypeGenerator.readViewColumnExpr column
    bindingExpr = fun cmdVarName valueExpr -> addViewBinding cmdVarName column valueExpr }

let private findSelectableColumn (columns: SelectableColumn list) (columnName: string) =
  columns
  |> List.find (fun column -> column.name.ToLowerInvariant() = columnName.ToLowerInvariant())

let private orderByMethodSuffix (orderBy: string option) =
  match orderBy with
  | Some raw when not (System.String.IsNullOrWhiteSpace raw) ->
    raw.Replace(",", " ").Split([| ' ' |], System.StringSplitOptions.RemoveEmptyEntries)
    |> Array.toList
    |> List.map capitalizeName
    |> String.concat ""
    |> fun suffix -> $"OrderBy{suffix}"
  | _ -> ""

let buildRecordProjection
  (getName: 'a -> string)
  (readExpr: 'a -> int -> string)
  (columns: 'a list)
  : string * (string * string) list =
  let columnNames = columns |> List.map getName |> String.concat ", "

  let fieldMappings =
    columns
    |> List.mapi (fun index column ->
      let fieldName = capitalizeName (getName column)
      fieldName, readExpr column index)

  columnNames, fieldMappings

let renderSelectMember
  (memberName: string)
  parameters
  (returnType: string)
  (queryHelper: string)
  (sql: string)
  (configureExpr: string)
  (fieldMappings: (string * string) list)
  =
  let readerLambda =
    fieldMappings
    |> List.map (fun (fieldName, expr) -> Ast.RecordFieldExpr(fieldName, expr))
    |> Ast.RecordExpr
    |> lambdaExpr "reader"

  let body =
    Ast.AppExpr(
      queryHelper,
      [ Ast.ConstantExpr(Ast.String sql)
        rawExpr configureExpr
        readerLambda
        rawExpr "tx" ]
    )

  staticMember memberName parameters body returnType

let generateSelectOneByMember
  (sourceName: string)
  (typeName: string)
  (columns: SelectableColumn list)
  (annotation: SelectOneByAnnotation)
  =
  let methodName =
    let columnsSuffix =
      annotation.columns |> List.map capitalizeName |> String.concat ""

    $"SelectOneBy{columnsSuffix}{orderByMethodSuffix annotation.orderBy}"

  let parameters =
    annotation.columns
    |> List.map (fun columnName ->
      let column = findSelectableColumn columns columnName
      columnName, column.fsharpType)

  let whereClause =
    annotation.columns
    |> List.map (fun columnName -> $"{columnName} = @{columnName}")
    |> String.concat " AND "

  let columnNames, fieldMappings =
    buildRecordProjection (fun column -> column.name) (fun column index -> column.readExpr index) columns

  let asyncParamBindings =
    annotation.columns
    |> List.map (fun columnName ->
      let column = findSelectableColumn columns columnName
      column.bindingExpr "cmd" columnName)

  let configureExpr =
    asyncParamBindings
    |> joinBindings "        "
    |> fun bindings -> $"(fun cmd ->\n        {bindings})"

  let sql =
    $"SELECT {columnNames} FROM {sourceName} WHERE {whereClause}"
    |> appendOrderBy annotation.orderBy
    |> fun selectSql -> $"{selectSql} LIMIT 1"

  renderSelectMember
    methodName
    [ typedTupledOrSingleParam parameters; txParam ]
    $"Task<Result<{typeName} option, SqliteException>>"
    "querySingle"
    sql
    configureExpr
    fieldMappings
