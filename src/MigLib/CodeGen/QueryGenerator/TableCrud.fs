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
    executeWrite
      "{insertSql}"
      (fun cmd ->
        {asyncParamBindings})
      tx
      (fun _ ->
        task {{
          let! newId = getLastInsertRowId tx
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
    executeWrite
      "{insertSql}"
      (fun cmd ->
        {asyncParamBindings})
      tx
      (fun rows ->
        task {{
          if rows = 0 then
            return Ok None
          else
            let! newId = getLastInsertRowId tx
            MigrationLog.recordInsert tx "{table.name}" {rowDataPairs}
            return Ok (Some newId)
        }})"""

let generateGet (table: CreateTable) : string option =
  let typeName = capitalizeName table.name
  let pkCols = getPrimaryKey table

  match pkCols with
  | [] -> None
  | pks ->
    let columnNames = table.columns |> List.map (fun c -> c.name) |> String.concat ", "

    let whereClause =
      pks |> List.map (fun pk -> $"{pk.name} = @{pk.name}") |> String.concat " AND "

    let getSql = $"SELECT {columnNames} FROM {table.name} WHERE {whereClause}"

    let paramList =
      pks
      |> List.map (fun pk -> let pkType = TypeGenerator.mapColumnType pk in $"({pk.name}: {pkType})")
      |> String.concat " "

    let fieldMappings =
      table.columns
      |> List.mapi (fun i col ->
        let fieldName = capitalizeName col.name in $"{fieldName} = {TypeGenerator.readColumnExpr col i}")
      |> String.concat "; "

    let asyncParamBindings =
      pks
      |> List.map (fun pk -> paramBindingExprForColumnVar "cmd" pk pk.name)
      |> joinBindings "        "

    Some
      $"""  static member SelectById {paramList} (tx: SqliteTransaction) : Task<Result<{typeName} option, SqliteException>> =
    querySingle
      "{getSql}"
      (fun cmd ->
        {asyncParamBindings})
      (fun reader ->
        {{ {fieldMappings} }})
      tx"""

let generateGetAll (table: CreateTable) : string =
  let typeName = capitalizeName table.name
  let columnNames = table.columns |> List.map (fun c -> c.name) |> String.concat ", "
  let getSql = $"SELECT {columnNames} FROM {table.name}"

  let fieldMappings =
    table.columns
    |> List.mapi (fun i col ->
      let fieldName = capitalizeName col.name in $"{fieldName} = {TypeGenerator.readColumnExpr col i}")
    |> String.concat "; "

  $"""  static member SelectAll (tx: SqliteTransaction) : Task<Result<{typeName} list, SqliteException>> =
    queryList
      "{getSql}"
      (fun _ -> ())
      (fun reader ->
        {{ {fieldMappings} }})
      tx"""

let generateGetOne (table: CreateTable) : string =
  let typeName = capitalizeName table.name
  let columnNames = table.columns |> List.map (fun c -> c.name) |> String.concat ", "
  let getSql = $"SELECT {columnNames} FROM {table.name} LIMIT 1"

  let fieldMappings =
    table.columns
    |> List.mapi (fun i col ->
      let fieldName = capitalizeName col.name in $"{fieldName} = {TypeGenerator.readColumnExpr col i}")
    |> String.concat "; "

  $"""  static member SelectOne (tx: SqliteTransaction) : Task<Result<{typeName} option, SqliteException>> =
    querySingle
      "{getSql}"
      (fun _ -> ())
      (fun reader ->
        {{ {fieldMappings} }})
      tx"""

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
      |> joinBindings "        "

    Some
      $"""  static member Update (item: {typeName}) (tx: SqliteTransaction) : Task<Result<unit, SqliteException>> =
    executeWrite
      "{updateSql}"
      (fun cmd ->
        {asyncParamBindings})
      tx
      (fun _ ->
        task {{
          MigrationLog.recordUpdate tx "{table.name}" {rowDataExpr}
          return Ok()
        }})"""

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
      |> joinBindings "        "

    Some
      $"""  static member Delete {paramList} (tx: SqliteTransaction) : Task<Result<unit, SqliteException>> =
    executeWrite
      "{deleteSql}"
      (fun cmd ->
        {asyncParamBindings})
      tx
      (fun _ ->
        task {{
          MigrationLog.recordDelete tx "{table.name}" {rowDataExpr}
          return Ok()
        }})"""

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
    task {{
      let! existing = {typeName}.SelectById {selectByIdArgs} tx

      match existing with
      | Error ex -> return Error ex
      | Ok(Some _) -> return! {typeName}.Update item tx
      | Ok None ->
        let! inserted = {typeName}.Insert item tx

        match inserted with
        | Ok _ -> return Ok()
        | Error ex -> return Error ex
    }}"""

let validateUpsertAnnotation (table: CreateTable) : Result<unit, string> =
  if table.upsertAnnotations.IsEmpty then
    Ok()
  else
    match getPrimaryKey table with
    | [] -> Error $"Upsert annotation requires a primary key on table '{table.name}'."
    | _ -> Ok()
