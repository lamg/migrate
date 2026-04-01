module internal Mig.CodeGen.QueryGeneratorTableCrud

open Mig.DeclarativeMigrations.Types
open Fabulous.AST
open type Fabulous.AST.Ast
open Microsoft.Data.Sqlite
open Mig.CodeGen.AstExprBuilders
open Mig.CodeGen.QueryGeneratorCommon

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

  let asyncParamBindingExprs =
    insertCols
    |> List.map (fun col ->
      let fieldName = capitalizeName col.name
      let isNullable = TypeGenerator.isColumnNullable col
      let itemFieldExpr = $"item.{fieldName}"

      if isNullable then
        let dbValueExpr = TypeGenerator.toNullableDbValueExpr col itemFieldExpr
        OtherExpr $"cmd.Parameters.AddWithValue(\"@{col.name}\", {dbValueExpr}) |> ignore"
      else
        let dbValueExpr = TypeGenerator.toDbValueExpr col itemFieldExpr
        OtherExpr $"cmd.Parameters.AddWithValue(\"@{col.name}\", {dbValueExpr}) |> ignore")

  let rowDataPairs =
    (insertCols |> List.map (rowDataPairExprForItem "item"))
    @ (match autoPkColName with
       | Some colName -> [ $"(\"{colName}\", box newId)" ]
       | None -> [])
    |> String.concat "; "
    |> fun pairs -> $"[{pairs}]"

  let asyncBodyExprs =
    [ OtherExpr $"use cmd = new SqliteCommand(\"{insertSql}\", tx.Connection, tx)" ]
    @ asyncParamBindingExprs
    @ [ OtherExpr "MigrationLog.ensureWriteAllowed tx"
        OtherExpr "let! _ = cmd.ExecuteNonQueryAsync()"
        OtherExpr "use lastIdCmd = new SqliteCommand(\"SELECT last_insert_rowid()\", tx.Connection, tx)"
        OtherExpr "let! lastId = lastIdCmd.ExecuteScalarAsync()"
        OtherExpr "let newId = lastId |> unbox<int64>"
        OtherExpr $"MigrationLog.recordInsert tx \"{table.name}\" {rowDataPairs}"
        OtherExpr "return Ok newId" ]

  let memberName = $"Insert (item: {typeName}) (tx: SqliteTransaction)"
  let returnType = "Task<Result<int64, SqliteException>>"
  let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]
  generateStaticMemberCode typeName memberName returnType body

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

  let asyncParamBindingExprs =
    insertCols
    |> List.map (fun col ->
      let fieldName = capitalizeName col.name
      let isNullable = TypeGenerator.isColumnNullable col
      let itemFieldExpr = $"item.{fieldName}"

      if isNullable then
        let dbValueExpr = TypeGenerator.toNullableDbValueExpr col itemFieldExpr
        OtherExpr $"cmd.Parameters.AddWithValue(\"@{col.name}\", {dbValueExpr}) |> ignore"
      else
        let dbValueExpr = TypeGenerator.toDbValueExpr col itemFieldExpr
        OtherExpr $"cmd.Parameters.AddWithValue(\"@{col.name}\", {dbValueExpr}) |> ignore")

  let rowDataPairs =
    (insertCols |> List.map (rowDataPairExprForItem "item"))
    @ (match autoPkColName with
       | Some colName -> [ $"(\"{colName}\", box newId)" ]
       | None -> [])
    |> String.concat "; "
    |> fun pairs -> $"[{pairs}]"

  let asyncBodyExprs =
    [ OtherExpr $"use cmd = new SqliteCommand(\"{insertSql}\", tx.Connection, tx)" ]
    @ asyncParamBindingExprs
    @ [ OtherExpr "MigrationLog.ensureWriteAllowed tx"
        OtherExpr "let! rows = cmd.ExecuteNonQueryAsync()"
        OtherExpr "if rows = 0 then return Ok None else"
        OtherExpr "  use lastIdCmd = new SqliteCommand(\"SELECT last_insert_rowid()\", tx.Connection, tx)"
        OtherExpr "  let! lastId = lastIdCmd.ExecuteScalarAsync()"
        OtherExpr "  let newId = lastId |> unbox<int64>"
        OtherExpr $"  MigrationLog.recordInsert tx \"{table.name}\" {rowDataPairs}"
        OtherExpr "  return Ok (Some newId)" ]

  let memberName = $"InsertOrIgnore (item: {typeName}) (tx: SqliteTransaction)"
  let returnType = "Task<Result<int64 option, SqliteException>>"
  let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]
  generateStaticMemberCode typeName memberName returnType body

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

    let asyncParamBindingExprs =
      pks
      |> List.map (fun pk ->
        OtherExpr $"cmd.Parameters.AddWithValue(\"@{pk.name}\", {TypeGenerator.toDbValueExpr pk pk.name}) |> ignore")

    let asyncBodyExprs =
      [ OtherExpr $"use cmd = new SqliteCommand(\"{getSql}\", tx.Connection, tx)" ]
      @ asyncParamBindingExprs
      @ [ OtherExpr "use! reader = cmd.ExecuteReaderAsync()"
          OtherExpr "let! hasRow = reader.ReadAsync()"
          OtherExpr $"if hasRow then return Ok(Some {{ {fieldMappings} }}) else return Ok None" ]

    let memberName = $"SelectById {paramList} (tx: SqliteTransaction)"
    let returnType = $"Task<Result<{typeName} option, SqliteException>>"
    let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]
    Some(generateStaticMemberCode typeName memberName returnType body)

let generateGetAll (table: CreateTable) : string =
  let typeName = capitalizeName table.name
  let columnNames = table.columns |> List.map (fun c -> c.name) |> String.concat ", "
  let getSql = $"SELECT {columnNames} FROM {table.name}"

  let fieldMappings =
    table.columns
    |> List.mapi (fun i col ->
      let fieldName = capitalizeName col.name in $"{fieldName} = {TypeGenerator.readColumnExpr col i}")
    |> String.concat "; "

  let whileLoopBody =
    $"let mutable hasMore = true in while hasMore do let! next = reader.ReadAsync() in hasMore <- next; if hasMore then results.Add({{ {fieldMappings} }})"

  let asyncBodyExprs =
    [ OtherExpr $"use cmd = new SqliteCommand(\"{getSql}\", tx.Connection, tx)"
      OtherExpr "use! reader = cmd.ExecuteReaderAsync()"
      OtherExpr $"let results = ResizeArray<{typeName}>()"
      OtherExpr whileLoopBody
      OtherExpr "return Ok(results |> Seq.toList)" ]

  let memberName = "SelectAll (tx: SqliteTransaction)"
  let returnType = $"Task<Result<{typeName} list, SqliteException>>"
  let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]
  generateStaticMemberCode typeName memberName returnType body

let generateGetOne (table: CreateTable) : string =
  let typeName = capitalizeName table.name
  let columnNames = table.columns |> List.map (fun c -> c.name) |> String.concat ", "
  let getSql = $"SELECT {columnNames} FROM {table.name} LIMIT 1"

  let fieldMappings =
    table.columns
    |> List.mapi (fun i col ->
      let fieldName = capitalizeName col.name in $"{fieldName} = {TypeGenerator.readColumnExpr col i}")
    |> String.concat "; "

  let asyncBodyExprs =
    [ OtherExpr $"use cmd = new SqliteCommand(\"{getSql}\", tx.Connection, tx)"
      OtherExpr "use! reader = cmd.ExecuteReaderAsync()"
      OtherExpr "let! hasRow = reader.ReadAsync()"
      OtherExpr $"if hasRow then return Ok(Some {{ {fieldMappings} }}) else return Ok None" ]

  let memberName = "SelectOne (tx: SqliteTransaction)"
  let returnType = $"Task<Result<{typeName} option, SqliteException>>"
  let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]
  generateStaticMemberCode typeName memberName returnType body

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

    let asyncParamBindingExprs =
      table.columns
      |> List.map (fun col ->
        let fieldName = capitalizeName col.name
        let isNullable = TypeGenerator.isColumnNullable col
        let itemFieldExpr = $"item.{fieldName}"

        if isNullable then
          let dbValueExpr = TypeGenerator.toNullableDbValueExpr col itemFieldExpr in
          OtherExpr $"cmd.Parameters.AddWithValue(\"@{col.name}\", {dbValueExpr}) |> ignore"
        else
          let dbValueExpr = TypeGenerator.toDbValueExpr col itemFieldExpr in
          OtherExpr $"cmd.Parameters.AddWithValue(\"@{col.name}\", {dbValueExpr}) |> ignore")

    let asyncBodyExprs =
      [ OtherExpr $"use cmd = new SqliteCommand(\"{updateSql}\", tx.Connection, tx)" ]
      @ asyncParamBindingExprs
      @ [ OtherExpr "MigrationLog.ensureWriteAllowed tx"
          OtherExpr "let! _ = cmd.ExecuteNonQueryAsync()"
          OtherExpr $"MigrationLog.recordUpdate tx \"{table.name}\" {rowDataExpr}"
          OtherExpr "return Ok()" ]

    let memberName = $"Update (item: {typeName}) (tx: SqliteTransaction)"
    let returnType = "Task<Result<unit, SqliteException>>"
    let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]
    Some(generateStaticMemberCode typeName memberName returnType body)

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

    let asyncBodyExprs =
      [ OtherExpr $"use cmd = new SqliteCommand(\"{deleteSql}\", tx.Connection, tx)" ]
      @ (pks
         |> List.map (fun pk ->
           OtherExpr $"cmd.Parameters.AddWithValue(\"@{pk.name}\", {TypeGenerator.toDbValueExpr pk pk.name}) |> ignore"))
      @ [ OtherExpr "MigrationLog.ensureWriteAllowed tx"
          OtherExpr "let! _ = cmd.ExecuteNonQueryAsync()"
          OtherExpr $"MigrationLog.recordDelete tx \"{table.name}\" {rowDataExpr}"
          OtherExpr "return Ok()" ]

    let memberName = $"Delete {paramList} (tx: SqliteTransaction)"
    let returnType = "Task<Result<unit, SqliteException>>"
    let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]
    Some(generateStaticMemberCode typeName memberName returnType body)

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
