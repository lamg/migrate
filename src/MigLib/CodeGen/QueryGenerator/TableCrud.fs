module internal Mig.CodeGen.QueryGeneratorTableCrud

open Mig.DeclarativeMigrations.Types
open Fabulous.AST
open type Fabulous.AST.Ast
open Microsoft.Data.Sqlite
open Mig.CodeGen.AstExprBuilders
open Mig.CodeGen.QueryGeneratorCommon
open Mig.CodeGen.SqlParamBindings

let private commandLambda (bindings: string list) =
  match bindings with
  | [] -> lambdaExpr "_" unitExpr
  | _ -> lambdaStatementsExpr "cmd" bindings

let private readerRecordLambda (fieldMappings: (string * string) list) =
  fieldMappings
  |> List.map (fun (fieldName, expr) -> RecordFieldExpr(fieldName, expr))
  |> RecordExpr
  |> lambdaExpr "reader"

let private executeInsertExpr (sql: string) (bindings: string list) onSuccess =
  AppExpr("executeInsert", [ ConstantExpr(String(sql)); commandLambda bindings; rawExpr "tx"; onSuccess ])

let private executeInsertOrIgnoreExpr (sql: string) (bindings: string list) onSuccess =
  AppExpr("executeInsertOrIgnore", [ ConstantExpr(String(sql)); commandLambda bindings; rawExpr "tx"; onSuccess ])

let private executeWriteUnitExpr (sql: string) (bindings: string list) =
  AppExpr("executeWriteUnit", [ ConstantExpr(String(sql)); commandLambda bindings; rawExpr "tx" ])

let generateInsert (table: CreateTable) =
  let typeName = capitalizeName table.name
  let pkCols = getPrimaryKey table
  let autoPkColName = getAutoIncrementPrimaryKeyColumnName table

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

  let paramBindings =
    insertCols
    |> List.map (fun col -> paramBindingExprForItem "cmd" "item" col)

  let rowDataPairs =
    (insertCols |> List.map (rowDataPairExprForItem "item"))
    @ (match autoPkColName with
       | Some colName -> [ $"\"{colName}\", box newId" ]
       | None -> [])
    |> String.concat "; "
    |> fun pairs -> $"[{pairs}]"

  let onSuccess =
    lambdaExpr
      "newId"
      (taskExpr
        [ OtherExpr($"MigrationLog.recordInsert tx \"{table.name}\" {rowDataPairs}")
          OtherExpr(returnExprRaw "Ok newId") ])

  staticMember
    "Insert"
    [ typedParenParam "item" typeName; txParam ]
    (executeInsertExpr insertSql paramBindings onSuccess)
    "Task<Result<int64, SqliteException>>"

let generateInsertOrIgnore (table: CreateTable) =
  let typeName = capitalizeName table.name
  let pkCols = getPrimaryKey table
  let autoPkColName = getAutoIncrementPrimaryKeyColumnName table

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

  let paramBindings =
    insertCols
    |> List.map (fun col -> paramBindingExprForItem "cmd" "item" col)

  let rowDataPairs =
    (insertCols |> List.map (rowDataPairExprForItem "item"))
    @ (match autoPkColName with
       | Some colName -> [ $"\"{colName}\", box newId" ]
       | None -> [])
    |> String.concat "; "
    |> fun pairs -> $"[{pairs}]"

  let onSuccess =
    lambdaExpr
      "newId"
      (taskExpr
        [ OtherExpr(
            MatchExpr(
              "newId",
              [ MatchClauseExpr("None", returnExprRaw "Ok None")
                MatchClauseExpr(
                  "Some newId",
                  rawStatementsExpr
                    [ $"MigrationLog.recordInsert tx \"{table.name}\" {rowDataPairs}"
                      "return Ok (Some newId)" ]
                ) ]
            )
          ) ])

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
      buildRecordProjection (fun (c: ColumnDef) -> c.name) TypeGenerator.readColumnExpr table.columns

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
      |> joinBindings "        "

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
    buildRecordProjection (fun (c: ColumnDef) -> c.name) TypeGenerator.readColumnExpr table.columns

  let getSql = $"SELECT {columnNames} FROM {table.name}"

  renderSelectMember
    "SelectAll"
    [ txParam ]
    $"Task<Result<{typeName} list, SqliteException>>"
    "queryList"
    getSql
    "(fun _ -> ())"
    fieldMappings

let generateGetOne (table: CreateTable) =
  let typeName = capitalizeName table.name

  let columnNames, fieldMappings =
    buildRecordProjection (fun (c: ColumnDef) -> c.name) TypeGenerator.readColumnExpr table.columns

  let getSql = $"SELECT {columnNames} FROM {table.name} LIMIT 1"

  renderSelectMember
    "SelectOne"
    [ txParam ]
    $"Task<Result<{typeName} option, SqliteException>>"
    "querySingle"
    getSql
    "(fun _ -> ())"
    fieldMappings

let generateUpdate (table: CreateTable) =
  let typeName = capitalizeName table.name
  let pkCols = getPrimaryKey table
  let rowDataExpr = rowDataListExprForItem "item" table.columns

  match pkCols with
  | [] -> None
  | pks ->
    let pkNames = pks |> List.map (fun pk -> pk.name) |> Set.ofList

    let updateCols =
      table.columns |> List.filter (fun col -> not (Set.contains col.name pkNames))

    let setClauses =
      updateCols |> List.map (fun c -> $"{c.name} = @{c.name}") |> String.concat ", "

    let whereClause =
      pks |> List.map (fun pk -> $"{pk.name} = @{pk.name}") |> String.concat " AND "

    let updateSql = $"UPDATE {table.name} SET {setClauses} WHERE {whereClause}"

    let paramBindings =
      table.columns
      |> List.map (fun col -> paramBindingExprForItem "cmd" "item" col)

    let body =
      taskExpr
        [ LetOrUseBangExpr(NamedPat("updateResult"), executeWriteUnitExpr updateSql paramBindings)
          OtherExpr(
            MatchExpr(
              "updateResult",
              [ MatchClauseExpr("Error ex", returnExprRaw "Error ex")
                MatchClauseExpr(
                  "Ok ()",
                  rawStatementsExpr
                    [ $"MigrationLog.recordUpdate tx \"{table.name}\" {rowDataExpr}"
                      "return Ok()" ]
                ) ]
            )
          ) ]

    Some(
      staticMember
        "Update"
        [ typedParenParam "item" typeName; txParam ]
        body
        "Task<Result<unit, SqliteException>>"
    )

let generateDelete (table: CreateTable) =
  let pkCols = getPrimaryKey table
  let rowDataExpr = rowDataListExprForParams pkCols

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
      pks
      |> List.map (fun pk -> paramBindingExprForColumnVar "cmd" pk pk.name)

    let body =
      taskExpr
        [ LetOrUseBangExpr(NamedPat("deleteResult"), executeWriteUnitExpr deleteSql paramBindings)
          OtherExpr(
            MatchExpr(
              "deleteResult",
              [ MatchClauseExpr("Error ex", returnExprRaw "Error ex")
                MatchClauseExpr(
                  "Ok ()",
                  rawStatementsExpr
                    [ $"MigrationLog.recordDelete tx \"{table.name}\" {rowDataExpr}"
                      "return Ok()" ]
                ) ]
            )
          ) ]

    Some(staticMember "Delete" (parameters @ [ txParam ]) body "Task<Result<unit, SqliteException>>")

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
        [ lambdaRawExpr "()" $"{typeName}.SelectById {selectByIdArgs} tx"
          lambdaRawExpr "()" $"{typeName}.Update item tx"
          lambdaRawExpr "()" $"{typeName}.Insert item tx" ]
      )

    Some(
      staticMember
        "Upsert"
        [ typedParenParam "item" typeName; txParam ]
        body
        "Task<Result<unit, SqliteException>>"
    )

let validateUpsertAnnotation (table: CreateTable) : Result<unit, string> =
  if table.upsertAnnotations.IsEmpty then
    Ok()
  else
    match getPrimaryKey table with
    | [] -> Error $"Upsert annotation requires a primary key on table '{table.name}'."
    | _ -> Ok()
