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

  let asyncParamBindingExprs =
    annotation.columns
    |> List.map (fun col ->
      let columnDef = findColumn table col |> Option.get
      let isNullable = TypeGenerator.isColumnNullable columnDef

      if isNullable then
        OtherExpr
          $"cmd.Parameters.AddWithValue(\"@{col}\", {TypeGenerator.toNullableDbValueExpr columnDef col}) |> ignore"
      else
        OtherExpr $"cmd.Parameters.AddWithValue(\"@{col}\", {TypeGenerator.toDbValueExpr columnDef col}) |> ignore")

  let whileLoopBody =
    $"let mutable hasMore = true in while hasMore do let! next = reader.ReadAsync() in hasMore <- next; if hasMore then results.Add({{ {fieldMappings} }})"

  let asyncBodyExprs =
    [ OtherExpr
        $"use cmd = new SqliteCommand(\"SELECT {columnNames} FROM {table.name} WHERE {whereClause}\", tx.Connection, tx)" ]
    @ asyncParamBindingExprs
    @ [ OtherExpr "use! reader = cmd.ExecuteReaderAsync()"
        OtherExpr $"let results = ResizeArray<{typeName}>()"
        OtherExpr whileLoopBody
        OtherExpr "return Ok(results |> Seq.toList)" ]

  let memberName = $"{methodName} ({parameters}) (tx: SqliteTransaction)"
  let returnType = $"Task<Result<{typeName} list, SqliteException>>"
  let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]
  generateStaticMemberCode typeName memberName returnType body

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
      OtherExpr
        $"cmd.Parameters.AddWithValue(\"@{col}\", match {col} with Some v -> box v | None -> box DBNull.Value) |> ignore"
    else
      OtherExpr $"cmd.Parameters.AddWithValue(\"@{col}\", {col}) |> ignore"

  let whileLoopBody =
    $"let mutable hasMore = true in while hasMore do let! next = reader.ReadAsync() in hasMore <- next; if hasMore then results.Add({{ {fieldMappings} }})"

  let asyncBodyExprs =
    [ OtherExpr
        $"use cmd = new SqliteCommand(\"SELECT {columnNames} FROM {table.name} WHERE {whereClause}\", tx.Connection, tx)"
      asyncParamBindingExpr
      OtherExpr "use! reader = cmd.ExecuteReaderAsync()"
      OtherExpr $"let results = ResizeArray<{typeName}>()"
      OtherExpr whileLoopBody
      OtherExpr "return Ok(results |> Seq.toList)" ]

  let memberName = $"{methodName} ({parameters}) (tx: SqliteTransaction)"
  let returnType = $"Task<Result<{typeName} list, SqliteException>>"
  let body = taskExpr [ OtherExpr(trySqliteExceptionAsync asyncBodyExprs) ]
  generateStaticMemberCode typeName memberName returnType body

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
    |> String.concat "\n        "

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

  let asyncParamBindings = generateAsyncParamBindings "cmd" "        "
  let asyncRequeryParamBindings = generateAsyncParamBindings "cmd2" "            "

  let requeryAfterInsertAsync =
    $"""
            // Re-query to get inserted record
            use cmd2 = new SqliteCommand("SELECT {columnNames} FROM {table.name} WHERE {whereClause} LIMIT 1", tx.Connection, tx)
            {asyncRequeryParamBindings}
            use! reader = cmd2.ExecuteReaderAsync()
            let! hasInsertedRow = reader.ReadAsync()
            if hasInsertedRow then
              return Ok {{ {fieldMappings} }}
            else
              return Error (SqliteException("Failed to retrieve inserted record", 0))"""

  $"""  static member {methodName} (newItem: {typeName}) (tx: SqliteTransaction) : Task<Result<{typeName}, SqliteException>> =
    task {{
      try
        // Extract query values from newItem
        {asyncValueExtractions}
        // Try to find existing record
        use cmd = new SqliteCommand("SELECT {columnNames} FROM {table.name} WHERE {whereClause} LIMIT 1", tx.Connection, tx)
        {asyncParamBindings}
        use! reader = cmd.ExecuteReaderAsync()
        let! hasRow = reader.ReadAsync()
        if hasRow then
          // Found existing record - return it
          return Ok {{ {fieldMappings} }}
        else
          // Not found - insert and fetch
          reader.Close()
          let! insertResult = {typeName}.Insert newItem tx
          match insertResult with
          | Ok _ ->{requeryAfterInsertAsync}
          | Error ex -> return Error ex
      with
      | :? SqliteException as ex -> return Error ex
    }}"""
