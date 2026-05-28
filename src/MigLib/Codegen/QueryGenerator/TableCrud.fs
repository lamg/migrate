module internal MigLib.Codegen.QueryGeneratorTableCrud

open MigLib.Schema.Types
open Fabulous.AST
open type Fabulous.AST.Ast
open MigLib.Codegen
open MigLib.Codegen.AstExprBuilders
open MigLib.Codegen.QueryGeneratorCommon

let private commandLambda (bindings: string list) =
  match bindings with
  | [] -> lambdaExpr "_" unitExpr
  | _ -> lambdaStatementsExpr "cmd" bindings

let private executeInsertExpr (sql: string) (bindings: string list) onSuccess =
  AppExpr("executeInsert", [ ConstantExpr(String(sql)); commandLambda bindings; rawExpr "tx"; onSuccess ])

let private executeInsertOrIgnoreExpr (sql: string) (bindings: string list) onSuccess =
  AppExpr("executeInsertOrIgnore", [ ConstantExpr(String(sql)); commandLambda bindings; rawExpr "tx"; onSuccess ])

let private executeWriteUnitExpr (sql: string) (bindings: string list) =
  AppExpr("executeWriteUnit", [ ConstantExpr(String(sql)); commandLambda bindings; rawExpr "tx" ])

let generateInsert (table: CreateTable) =
  let typeName = capitalizeName table.name
  let pkCols = getPrimaryKey table

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

  let columnNames = insertCols |> List.map (fun col -> col.name) |> String.concat ", "

  let paramNames =
    insertCols |> List.map (fun col -> $"@{col.name}") |> String.concat ", "

  let insertSql = $"INSERT INTO {table.name} ({columnNames}) VALUES ({paramNames})"

  let paramBindings =
    insertCols |> List.map (fun col -> paramBindingExprForItem "cmd" "item" col)

  let onSuccess = lambdaRawExpr "newId" "Task.FromResult(Ok newId)"

  staticMember
    "Insert"
    [ typedParenParam "item" typeName; txParam ]
    (executeInsertExpr insertSql paramBindings onSuccess)
    "Task<Result<int64, SqliteException>>"

let generateInsertOrIgnore (table: CreateTable) =
  let typeName = capitalizeName table.name
  let pkCols = getPrimaryKey table

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

  let columnNames = insertCols |> List.map (fun col -> col.name) |> String.concat ", "

  let paramNames =
    insertCols |> List.map (fun col -> $"@{col.name}") |> String.concat ", "

  let insertSql =
    $"INSERT OR IGNORE INTO {table.name} ({columnNames}) VALUES ({paramNames})"

  let paramBindings =
    insertCols |> List.map (fun col -> paramBindingExprForItem "cmd" "item" col)

  let onSuccess =
    lambdaExpr "newId" (rawExpr "Task.FromResult(match newId with | None -> Ok None | Some newId -> Ok(Some newId))")

  staticMember
    "InsertOrIgnore"
    [ typedParenParam "item" typeName; txParam ]
    (executeInsertOrIgnoreExpr insertSql paramBindings onSuccess)
    "Task<Result<int64 option, SqliteException>>"

let generateGet (table: CreateTable) =
  let typeName = capitalizeName table.name
  let pkCols = getPrimaryKey table

  match pkCols with
  | [] -> None
  | pks ->
    let columnNames, fieldMappings =
      buildRecordProjection (fun (column: ColumnDef) -> column.name) TypeGenerator.readColumnExpr table.columns

    let whereClause =
      pks |> List.map (fun pk -> $"{pk.name} = @{pk.name}") |> String.concat " AND "

    let getSql = $"SELECT {columnNames} FROM {table.name} WHERE {whereClause}"

    let parameters =
      pks
      |> List.map (fun pk ->
        let pkType = TypeGenerator.mapColumnType pk
        typedParenParam pk.name pkType)

    let asyncParamBindings =
      pks
      |> List.map (fun pk -> paramBindingExprForColumnVar "cmd" pk pk.name)
      |> String.concat "\n        "

    Some(
      renderSelectMember
        "SelectById"
        (parameters @ [ txParam ])
        $"Task<Result<{typeName} option, SqliteException>>"
        "querySingle"
        getSql
        $"(fun cmd ->\n        {asyncParamBindings})"
        fieldMappings
    )

let generateGetAll (table: CreateTable) =
  let typeName = capitalizeName table.name

  let columnNames, fieldMappings =
    buildRecordProjection (fun (column: ColumnDef) -> column.name) TypeGenerator.readColumnExpr table.columns

  renderSelectMember
    "SelectAll"
    [ txParam ]
    $"Task<Result<{typeName} list, SqliteException>>"
    "queryList"
    $"SELECT {columnNames} FROM {table.name}"
    "(fun _ -> ())"
    fieldMappings

let generateGetOne (table: CreateTable) =
  let typeName = capitalizeName table.name

  let columnNames, fieldMappings =
    buildRecordProjection (fun (column: ColumnDef) -> column.name) TypeGenerator.readColumnExpr table.columns

  renderSelectMember
    "SelectOne"
    [ txParam ]
    $"Task<Result<{typeName} option, SqliteException>>"
    "querySingle"
    $"SELECT {columnNames} FROM {table.name} LIMIT 1"
    "(fun _ -> ())"
    fieldMappings

let generateUpdate (table: CreateTable) =
  let typeName = capitalizeName table.name
  let pkCols = getPrimaryKey table

  match pkCols with
  | [] -> None
  | pks ->
    let pkNames = pks |> List.map (fun pk -> pk.name) |> Set.ofList

    let updateCols =
      table.columns |> List.filter (fun col -> not (Set.contains col.name pkNames))

    let setClauses =
      updateCols
      |> List.map (fun col -> $"{col.name} = @{col.name}")
      |> String.concat ", "

    let whereClause =
      pks |> List.map (fun pk -> $"{pk.name} = @{pk.name}") |> String.concat " AND "

    let updateSql = $"UPDATE {table.name} SET {setClauses} WHERE {whereClause}"

    let paramBindings =
      table.columns |> List.map (fun col -> paramBindingExprForItem "cmd" "item" col)

    Some(
      staticMember
        "Update"
        [ typedParenParam "item" typeName; txParam ]
        (executeWriteUnitExpr updateSql paramBindings)
        "Task<Result<unit, SqliteException>>"
    )

let generateDelete (table: CreateTable) =
  let pkCols = getPrimaryKey table

  match pkCols with
  | [] -> None
  | pks ->
    let whereClause =
      pks |> List.map (fun pk -> $"{pk.name} = @{pk.name}") |> String.concat " AND "

    let deleteSql = $"DELETE FROM {table.name} WHERE {whereClause}"

    let parameters =
      pks
      |> List.map (fun pk ->
        let pkType = TypeGenerator.mapColumnType pk
        typedParenParam pk.name pkType)

    let paramBindings =
      pks |> List.map (fun pk -> paramBindingExprForColumnVar "cmd" pk pk.name)

    Some(
      staticMember
        "Delete"
        (parameters @ [ txParam ])
        (executeWriteUnitExpr deleteSql paramBindings)
        "Task<Result<unit, SqliteException>>"
    )

let generateDeleteAll (table: CreateTable) =
  Some(
    staticMember
      "DeleteAll"
      [ txParam ]
      (executeWriteUnitExpr $"DELETE FROM {table.name}" [])
      "Task<Result<unit, SqliteException>>"
  )

let generateUpsert (table: CreateTable) =
  let typeName = capitalizeName table.name
  let pkCols = getPrimaryKey table

  match pkCols with
  | [] -> None
  | pks ->
    let selectByIdArgs =
      pks
      |> List.map (fun pk -> $"item.{capitalizeName pk.name}")
      |> String.concat " "

    let body =
      AppExpr(
        "upsertByExisting",
        [
          lambdaRawExpr "()" $"{typeName}.SelectById {selectByIdArgs} tx"
          lambdaRawExpr "()" $"{typeName}.Update item tx"
          lambdaRawExpr "()" $"{typeName}.Insert item tx"
        ]
      )

    Some(staticMember "Upsert" [ typedParenParam "item" typeName; txParam ] body "Task<Result<unit, SqliteException>>")

let validateUpsertAnnotation (table: CreateTable) : Result<unit, string> =
  if table.upsertAnnotations.IsEmpty then
    Ok()
  else
    match getPrimaryKey table with
    | [] -> Error $"Upsert annotation requires a primary key on table '{table.name}'."
    | _ -> Ok()
