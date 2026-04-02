module internal Mig.CodeGen.QueryGeneratorTableQueryExtensions

open Mig.DeclarativeMigrations.Types
open Fabulous.AST
open type Fabulous.AST.Ast
open Mig.CodeGen.AstExprBuilders
open Mig.CodeGen.QueryGeneratorCommon
open Mig.CodeGen.SqlParamBindings

let private readerRecordLambda (fieldMappings: (string * string) list) =
  fieldMappings
  |> List.map (fun (fieldName, expr) -> RecordFieldExpr(fieldName, expr))
  |> RecordExpr
  |> lambdaExpr "reader"

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

let generateQueryBy (table: CreateTable) (annotation: QueryByAnnotation) =
  let typeName = capitalizeName table.name

  let methodName =
    annotation.columns
    |> List.map capitalizeName
    |> String.concat ""
    |> sprintf "SelectBy%s"

  let parameters =
    annotation.columns
    |> List.map (fun col ->
      let columnDef = findColumn table col |> Option.get
      let fsharpType = TypeGenerator.mapColumnType columnDef
      col, fsharpType)

  let whereClause =
    annotation.columns
    |> List.map (fun col -> $"{col} = @{col}")
    |> String.concat " AND "

  let columnNames, fieldMappings =
    buildRecordProjection (fun (c: ColumnDef) -> c.name) TypeGenerator.readColumnExpr table.columns

  let asyncParamBindings =
    annotation.columns
    |> List.map (fun col ->
      let columnDef = findColumn table col |> Option.get
      addColumnBinding "cmd" columnDef col)

  let configureExpr =
    asyncParamBindings
    |> joinBindings "        "
    |> fun bindings -> $"(fun cmd ->\n        {bindings})"

  renderSelectMember
    methodName
    [ typedTupledOrSingleParam parameters; txParam ]
    $"Task<Result<{typeName} list, SqliteException>>"
    "queryList"
    $"SELECT {columnNames} FROM {table.name} WHERE {whereClause}"
    configureExpr
    fieldMappings

let generateQueryLike (table: CreateTable) (annotation: QueryLikeAnnotation) =
  let typeName = capitalizeName table.name
  let col = annotation.columns |> List.head
  let methodName = $"Select{capitalizeName col}Like"
  let columnDef = findColumn table col |> Option.get
  let isNullable = TypeGenerator.isColumnNullable columnDef
  let fsharpType = TypeGenerator.mapSqlType columnDef.columnType isNullable
  let whereClause = $"{col} LIKE '%%' || @{col} || '%%'"

  let columnNames, fieldMappings =
    buildRecordProjection (fun (c: ColumnDef) -> c.name) TypeGenerator.readColumnExpr table.columns

  let asyncParamBindingExpr =
    if isNullable then
      addOptionalPlainBinding "cmd" col col
    else
      addPlainBinding "cmd" col col

  renderSelectMember
    methodName
    [ typedParenParam col fsharpType; txParam ]
    $"Task<Result<{typeName} list, SqliteException>>"
    "queryList"
    $"SELECT {columnNames} FROM {table.name} WHERE {whereClause}"
    $"(fun cmd ->\n        {asyncParamBindingExpr})"
    fieldMappings

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

let generateQueryByOrCreate (table: CreateTable) (annotation: QueryByOrCreateAnnotation) =
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

  let columnNames, fieldMappings =
    buildRecordProjection (fun (c: ColumnDef) -> c.name) TypeGenerator.readColumnExpr table.columns

  let extractionBindings =
    annotation.columns
    |> List.map (fun col ->
      let fieldName = capitalizeName col
      LetOrUseExpr(Value(col, rawExpr $"newItem.{fieldName}")))

  let paramBindings =
    annotation.columns
    |> List.map (fun col ->
      let columnDef = findColumn table col |> Option.get
      addColumnBinding "cmd" columnDef col)

  let selectExpr =
    AppExpr(
      "querySingle",
      [ ConstantExpr(String($"SELECT {columnNames} FROM {table.name} WHERE {whereClause} LIMIT 1"))
        lambdaStatementsExpr "cmd" paramBindings
        readerRecordLambda fieldMappings
        rawExpr "tx" ]
    )

  let body =
    CompExprBodyExpr(
      seq {
        yield! extractionBindings
        yield LetOrUseExpr(Function("select", UnitPat(), selectExpr))

        yield
          OtherExpr(
            AppExpr(
              "querySingleOrInsert",
              [ rawExpr "select"
                lambdaRawExpr "()" $"{typeName}.Insert newItem tx" ]
            )
          )
      }
    )

  staticMember
    methodName
    [ typedParenParam "newItem" typeName; txParam ]
    body
    $"Task<Result<{typeName}, SqliteException>>"
