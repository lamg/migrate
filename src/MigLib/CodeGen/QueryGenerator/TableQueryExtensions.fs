module internal Mig.CodeGen.QueryGeneratorTableQueryExtensions

open Mig.DeclarativeMigrations.Types
open Fabulous.AST
open type Fabulous.AST.Ast
open Mig.CodeGen.AstExprBuilders
open Mig.CodeGen.QueryGeneratorCommon

let validateQueryByAnnotation (table: CreateTable) (annotation: QueryByAnnotation) : Result<unit, string> =
  let columnNames =
    table.columns |> List.map (fun c -> c.name.ToLowerInvariant()) |> Set.ofList

  annotation.columns
  |> List.tryFind (fun col -> not (columnNames.Contains(col.ToLowerInvariant())))
  |> function
    | Some invalidCol ->
      let availableCols =
        table.columns |> List.map (fun c -> c.name) |> String.concat ", "

      Error
        $"QueryBy annotation references non-existent column '{invalidCol}' in table '{table.name}'. Available columns: {availableCols}"
    | None -> Ok()

let validateQueryLikeAnnotation (table: CreateTable) (annotation: QueryLikeAnnotation) : Result<unit, string> =
  match annotation.columns with
  | [] -> Error $"QueryLike annotation on table '{table.name}' requires exactly one column."
  | [ col ] ->
    let columnNames =
      table.columns |> List.map (fun c -> c.name.ToLowerInvariant()) |> Set.ofList

    if columnNames.Contains(col.ToLowerInvariant()) then
      Ok()
    else
      let availableCols =
        table.columns |> List.map (fun c -> c.name) |> String.concat ", " in

      Error
        $"QueryLike annotation references non-existent column '{col}' in table '{table.name}'. Available columns: {availableCols}"
  | _ ->
    let receivedCols = annotation.columns |> String.concat ", " in
    Error $"QueryLike annotation on table '{table.name}' supports exactly one column. Received: {receivedCols}"

let generateQueryBy (table: CreateTable) (annotation: QueryByAnnotation) : string =
  let typeName = capitalizeName table.name

  let methodName =
    annotation.columns
    |> List.map capitalizeName
    |> String.concat ""
    |> sprintf "SelectBy%s"

  let parameters =
    annotation.columns
    |> List.map (fun col ->
      let columnDef = findColumn table col |> Option.get in
      let fsharpType = TypeGenerator.mapColumnType columnDef in
      $"{col}: {fsharpType}")
    |> String.concat ", "

  let whereClause =
    annotation.columns
    |> List.map (fun col -> $"{col} = @{col}")
    |> String.concat " AND "

  let columnNames = table.columns |> List.map (fun c -> c.name) |> String.concat ", "

  let fieldMappings =
    table.columns
    |> List.mapi (fun i col ->
      let fieldName = capitalizeName col.name in $"{fieldName} = {TypeGenerator.readColumnExpr col i}")
    |> String.concat "; "

  let asyncParamBindings =
    annotation.columns
    |> List.map (fun col ->
      let columnDef = findColumn table col |> Option.get
      let isNullable = TypeGenerator.isColumnNullable columnDef

      if isNullable then
        $"cmd.Parameters.AddWithValue(\"@{col}\", {TypeGenerator.toNullableDbValueExpr columnDef col}) |> ignore"
      else
        $"cmd.Parameters.AddWithValue(\"@{col}\", {TypeGenerator.toDbValueExpr columnDef col}) |> ignore")
    |> String.concat "\n        "

  $"""  static member {methodName} ({parameters}) (tx: SqliteTransaction) : Task<Result<{typeName} list, SqliteException>> =
    queryList
      "SELECT {columnNames} FROM {table.name} WHERE {whereClause}"
      (fun cmd ->
        {asyncParamBindings})
      (fun reader ->
        {{ {fieldMappings} }})
      tx"""

let generateQueryLike (table: CreateTable) (annotation: QueryLikeAnnotation) : string =
  let typeName = capitalizeName table.name
  let col = annotation.columns |> List.head
  let methodName = $"Select{capitalizeName col}Like"
  let columnDef = findColumn table col |> Option.get
  let isNullable = TypeGenerator.isColumnNullable columnDef
  let fsharpType = TypeGenerator.mapSqlType columnDef.columnType isNullable
  let parameters = $"{col}: {fsharpType}"
  let whereClause = $"{col} LIKE '%%' || @{col} || '%%'"
  let columnNames = table.columns |> List.map (fun c -> c.name) |> String.concat ", "

  let fieldMappings =
    table.columns
    |> List.mapi (fun i c ->
      let fieldName = capitalizeName c.name in $"{fieldName} = {TypeGenerator.readColumnExpr c i}")
    |> String.concat "; "

  let asyncParamBindingExpr =
    if isNullable then
      $"cmd.Parameters.AddWithValue(\"@{col}\", match {col} with Some v -> box v | None -> box DBNull.Value) |> ignore"
    else
      $"cmd.Parameters.AddWithValue(\"@{col}\", {col}) |> ignore"

  $"""  static member {methodName} ({parameters}) (tx: SqliteTransaction) : Task<Result<{typeName} list, SqliteException>> =
    queryList
      "SELECT {columnNames} FROM {table.name} WHERE {whereClause}"
      (fun cmd ->
        {asyncParamBindingExpr})
      (fun reader ->
        {{ {fieldMappings} }})
      tx"""

let validateQueryByOrCreateAnnotation
  (table: CreateTable)
  (annotation: QueryByOrCreateAnnotation)
  : Result<unit, string> =
  let availableColumns = table.columns |> List.map (fun c -> c.name.ToLower())

  let invalidColumns =
    annotation.columns
    |> List.filter (fun col -> not (availableColumns |> List.contains (col.ToLower())))

  match invalidColumns with
  | [] -> Ok()
  | invalidCol :: _ ->
    let availableCols =
      table.columns |> List.map (fun c -> c.name) |> String.concat ", " in

    Error
      $"QueryByOrCreate annotation references non-existent column '{invalidCol}' in table '{table.name}'. Available columns: {availableCols}"

let generateQueryByOrCreate (table: CreateTable) (annotation: QueryByOrCreateAnnotation) : string =
  let typeName = capitalizeName table.name

  let methodName =
    annotation.columns
    |> List.map capitalizeName
    |> String.concat ""
    |> sprintf "SelectBy%sOrInsert"

  let whereClause =
    annotation.columns
    |> List.map (fun col -> $"{col} = @{col}")
    |> String.concat " AND "

  let columnNames = table.columns |> List.map (fun c -> c.name) |> String.concat ", "

  let fieldMappings =
    table.columns
    |> List.mapi (fun i col ->
      let fieldName = capitalizeName col.name in $"{fieldName} = {TypeGenerator.readColumnExpr col i}")
    |> String.concat "; "

  let asyncValueExtractions =
    annotation.columns
    |> List.map (fun col -> let fieldName = capitalizeName col in $"let {col} = newItem.{fieldName}")
    |> String.concat "\n      "

  let generateAsyncParamBindings (cmdVarName: string) (lineIndent: string) =
    annotation.columns
    |> List.map (fun col ->
      let columnDef = findColumn table col |> Option.get
      let isNullable = TypeGenerator.isColumnNullable columnDef

      if isNullable then
        $"{cmdVarName}.Parameters.AddWithValue(\"@{col}\", {TypeGenerator.toNullableDbValueExpr columnDef col}) |> ignore"
      else
        $"{cmdVarName}.Parameters.AddWithValue(\"@{col}\", {TypeGenerator.toDbValueExpr columnDef col}) |> ignore")
    |> String.concat $"\n{lineIndent}"

  let selectSql =
    $"SELECT {columnNames} FROM {table.name} WHERE {whereClause} LIMIT 1"

  let asyncParamBindings = generateAsyncParamBindings "cmd" "            "
  let asyncRequeryParamBindings = generateAsyncParamBindings "cmd" "                "

  $"""  static member {methodName} (newItem: {typeName}) (tx: SqliteTransaction) : Task<Result<{typeName}, SqliteException>> =
    task {{
      // Extract query values from newItem
      {asyncValueExtractions}
      // Try to find existing record
      let! existingResult =
        (querySingle
          "{selectSql}"
          (fun cmd ->
            {asyncParamBindings})
          (fun reader ->
            {{ {fieldMappings} }})
          tx)

      match existingResult with
      | Ok(Some item) ->
        return Ok item
      | Ok None ->
        // Not found - insert and fetch
        let! insertResult = {typeName}.Insert newItem tx

        match insertResult with
        | Ok _ ->
          let! insertedResult =
            (querySingle
              "{selectSql}"
              (fun cmd ->
                {asyncRequeryParamBindings})
              (fun reader ->
                {{ {fieldMappings} }})
              tx)

          return
            match insertedResult with
            | Ok(Some item) -> Ok item
            | Ok None -> Error (SqliteException("Failed to retrieve inserted record", 0))
            | Error ex -> Error ex
        | Error ex -> return Error ex
      | Error ex -> return Error ex
    }}"""
