module internal Mig.CodeGen.QueryGeneratorTableCrud

open Mig.DeclarativeMigrations.Types
open Fabulous.AST
open type Fabulous.AST.Ast
open Microsoft.Data.Sqlite
open Mig.CodeGen.AstExprBuilders
open Mig.CodeGen.QueryGeneratorCommon
open Mig.CodeGen.SqlParamBindings

let generateInsert (table: CreateTable) : string =
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

  let asyncParamBindings =
    insertCols
    |> List.map (fun col -> paramBindingExprForItem "cmd" "item" col)
    |> joinBindings "        "

  let rowDataPairs =
    (insertCols |> List.map (rowDataPairExprForItem "item"))
    @ (match autoPkColName with
       | Some colName -> [ $"\"{colName}\", box newId" ]
       | None -> [])
    |> String.concat "; "
    |> fun pairs -> $"[{pairs}]"

  $"""  static member Insert (item: {typeName}) (tx: SqliteTransaction) : Task<Result<int64, SqliteException>> =
    executeInsert
      "{insertSql}"
      (fun cmd ->
        {asyncParamBindings})
      tx
      (fun newId ->
        task {{
          MigrationLog.recordInsert tx "{table.name}" {rowDataPairs}
          return Ok newId
        }})"""

let generateInsertOrIgnore (table: CreateTable) : string =
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

  let asyncParamBindings =
    insertCols
    |> List.map (fun col -> paramBindingExprForItem "cmd" "item" col)
    |> joinBindings "        "

  let rowDataPairs =
    (insertCols |> List.map (rowDataPairExprForItem "item"))
    @ (match autoPkColName with
       | Some colName -> [ $"\"{colName}\", box newId" ]
       | None -> [])
    |> String.concat "; "
    |> fun pairs -> $"[{pairs}]"

  $"""  static member InsertOrIgnore (item: {typeName}) (tx: SqliteTransaction) : Task<Result<int64 option, SqliteException>> =
    executeInsertOrIgnore
      "{insertSql}"
      (fun cmd ->
        {asyncParamBindings})
      tx
      (fun newId ->
        task {{
          match newId with
          | None -> return Ok None
          | Some newId ->
            MigrationLog.recordInsert tx "{table.name}" {rowDataPairs}
            return Ok (Some newId)
        }})"""

let generateGet (table: CreateTable) : string option =
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

    let paramList =
      pks
      |> List.map (fun pk -> let pkType = TypeGenerator.mapColumnType pk in $"({pk.name}: {pkType})")
      |> String.concat " "

    let asyncParamBindings =
      pks
      |> List.map (fun pk -> paramBindingExprForColumnVar "cmd" pk pk.name)
      |> joinBindings "        "

    Some(
      renderSelectMember
        $"SelectById {paramList} (tx: SqliteTransaction) : Task<Result<{typeName} option, SqliteException>>"
        "querySingle"
        getSql
        $"(fun cmd ->\n        {asyncParamBindings})"
        fieldMappings
    )

let generateGetAll (table: CreateTable) : string =
  let typeName = capitalizeName table.name

  let columnNames, fieldMappings =
    buildRecordProjection (fun (c: ColumnDef) -> c.name) TypeGenerator.readColumnExpr table.columns

  let getSql = $"SELECT {columnNames} FROM {table.name}"

  renderSelectMember
    $"SelectAll (tx: SqliteTransaction) : Task<Result<{typeName} list, SqliteException>>"
    "queryList"
    getSql
    "(fun _ -> ())"
    fieldMappings

let generateGetOne (table: CreateTable) : string =
  let typeName = capitalizeName table.name

  let columnNames, fieldMappings =
    buildRecordProjection (fun (c: ColumnDef) -> c.name) TypeGenerator.readColumnExpr table.columns

  let getSql = $"SELECT {columnNames} FROM {table.name} LIMIT 1"

  renderSelectMember
    $"SelectOne (tx: SqliteTransaction) : Task<Result<{typeName} option, SqliteException>>"
    "querySingle"
    getSql
    "(fun _ -> ())"
    fieldMappings

let generateUpdate (table: CreateTable) : string option =
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

    let asyncParamBindings =
      table.columns
      |> List.map (fun col -> paramBindingExprForItem "cmd" "item" col)
      |> joinBindings "            "

    Some
      $"""  static member Update (item: {typeName}) (tx: SqliteTransaction) : Task<Result<unit, SqliteException>> =
    task {{
      let! updateResult =
        executeWriteUnit
          "{updateSql}"
          (fun cmd ->
            {asyncParamBindings})
          tx

      match updateResult with
      | Error ex -> return Error ex
      | Ok () ->
        MigrationLog.recordUpdate tx "{table.name}" {rowDataExpr}
        return Ok()
    }}"""

let generateDelete (table: CreateTable) : string option =
  let typeName = capitalizeName table.name
  let pkCols = getPrimaryKey table
  let rowDataExpr = rowDataListExprForParams pkCols

  match pkCols with
  | [] -> None
  | pks ->
    let whereClause =
      pks |> List.map (fun pk -> $"{pk.name} = @{pk.name}") |> String.concat " AND "

    let deleteSql = $"DELETE FROM {table.name} WHERE {whereClause}"

    let paramList =
      pks
      |> List.map (fun pk -> let pkType = TypeGenerator.mapColumnType pk in $"({pk.name}: {pkType})")
      |> String.concat " "

    let asyncParamBindings =
      pks
      |> List.map (fun pk -> paramBindingExprForColumnVar "cmd" pk pk.name)
      |> joinBindings "            "

    Some
      $"""  static member Delete {paramList} (tx: SqliteTransaction) : Task<Result<unit, SqliteException>> =
    task {{
      let! deleteResult =
        executeWriteUnit
          "{deleteSql}"
          (fun cmd ->
            {asyncParamBindings})
          tx

      match deleteResult with
      | Error ex -> return Error ex
      | Ok () ->
        MigrationLog.recordDelete tx "{table.name}" {rowDataExpr}
        return Ok()
    }}"""

let generateUpsert (table: CreateTable) : string option =
  let typeName = capitalizeName table.name
  let pkCols = getPrimaryKey table

  match pkCols with
  | [] -> None
  | pks ->
    let selectByIdArgs =
      pks
      |> List.map (fun pk -> $"item.{capitalizeName pk.name}")
      |> String.concat " "

    Some
      $"""  static member Upsert (item: {typeName}) (tx: SqliteTransaction) : Task<Result<unit, SqliteException>> =
    upsertByExisting
      (fun () -> {typeName}.SelectById {selectByIdArgs} tx)
      (fun () -> {typeName}.Update item tx)
      (fun () -> {typeName}.Insert item tx)"""

let validateUpsertAnnotation (table: CreateTable) : Result<unit, string> =
  if table.upsertAnnotations.IsEmpty then
    Ok()
  else
    match getPrimaryKey table with
    | [] -> Error $"Upsert annotation requires a primary key on table '{table.name}'."
    | _ -> Ok()
