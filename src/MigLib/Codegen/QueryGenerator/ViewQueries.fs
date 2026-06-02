module internal MigLib.Codegen.QueryGeneratorViewQueries

open MigLib.Schema.Types
open Fabulous.AST
open type Fabulous.AST.Ast
open MigLib.Codegen
open MigLib.Codegen.AstExprBuilders
open MigLib.Codegen.QueryGeneratorCommon
open MigLib.Codegen.SqlFragments
open MigLib.Codegen.SqlParamBindings
open MigLib.Codegen.Validation

let generateViewGetAll (viewName: string) (columns: ViewColumn list) =
  let typeName = capitalizeName viewName

  let columnNames, fieldMappings =
    buildRecordProjection (fun (column: ViewColumn) -> column.name) TypeGenerator.readViewColumnExpr columns

  renderSelectMember
    "SelectAll"
    [ txParam ]
    $"Task<Result<{typeName} list, SqliteException>>"
    "queryList"
    $"SELECT {columnNames} FROM {viewName}"
    "(fun _ -> ())"
    fieldMappings

let generateViewGetOne (viewName: string) (columns: ViewColumn list) =
  let typeName = capitalizeName viewName

  let columnNames, fieldMappings =
    buildRecordProjection (fun (column: ViewColumn) -> column.name) TypeGenerator.readViewColumnExpr columns

  renderSelectMember
    "SelectOne"
    [ txParam ]
    $"Task<Result<{typeName} option, SqliteException>>"
    "querySingle"
    $"SELECT {columnNames} FROM {viewName} LIMIT 1"
    "(fun _ -> ())"
    fieldMappings

let validateViewQueryByAnnotation
  (viewName: string)
  (columns: ViewColumn list)
  (annotation: QueryByAnnotation)
  : Result<unit, string> =
  validateAnnotationColumns "QueryBy" "view" viewName (columns |> List.map _.name) annotation.columns

let validateViewSelectOneByAnnotation
  (viewName: string)
  (columns: ViewColumn list)
  (annotation: SelectOneByAnnotation)
  : Result<unit, string> =
  validateAnnotationColumns "SelectOneBy" "view" viewName (columns |> List.map _.name) annotation.columns

let validateViewQueryLikeAnnotation
  (viewName: string)
  (columns: ViewColumn list)
  (annotation: QueryLikeAnnotation)
  : Result<unit, string> =
  match annotation.columns with
  | [] -> Error $"QueryLike annotation on view '{viewName}' requires exactly one column."
  | [ col ] ->
    let columnNames =
      columns |> List.map (fun column -> column.name.ToLowerInvariant()) |> Set.ofList

    if columnNames.Contains(col.ToLowerInvariant()) then
      Ok()
    else
      let availableCols =
        columns |> List.map (fun column -> column.name) |> String.concat ", "

      Error
        $"QueryLike annotation references non-existent column '{col}' in view '{viewName}'. Available columns: {availableCols}"
  | _ ->
    let receivedCols = annotation.columns |> String.concat ", "
    Error $"QueryLike annotation on view '{viewName}' supports exactly one column. Received: {receivedCols}"

let validateViewQueryWhereAnnotation
  (viewName: string)
  (columns: ViewColumn list)
  (annotation: QueryWhereAnnotation)
  : Result<unit, string> =
  validateAnnotationColumns "QueryWhere" "view" viewName (columns |> List.map _.name) annotation.columns

let generateViewQueryBy (viewName: string) (columns: ViewColumn list) (annotation: QueryByAnnotation) =
  let typeName = capitalizeName viewName

  let methodName =
    annotation.columns
    |> List.map capitalizeName
    |> String.concat ""
    |> sprintf "SelectBy%s"

  let parameters =
    annotation.columns
    |> List.map (fun col ->
      let columnDef = findViewColumn columns col |> Option.get
      let fsharpType = TypeGenerator.mapViewColumnType columnDef
      col, fsharpType)

  let whereClause =
    annotation.columns
    |> List.map (fun col -> $"{col} = @{col}")
    |> String.concat " AND "

  let columnNames, fieldMappings =
    buildRecordProjection (fun (column: ViewColumn) -> column.name) TypeGenerator.readViewColumnExpr columns

  let asyncParamBindings =
    annotation.columns
    |> List.map (fun col ->
      let columnDef = findViewColumn columns col |> Option.get
      addViewBinding "cmd" columnDef col)
    |> joinBindings "        "

  renderSelectMember
    methodName
    [ typedTupledOrSingleParam parameters; txParam ]
    $"Task<Result<{typeName} list, SqliteException>>"
    "queryList"
    ($"SELECT {columnNames} FROM {viewName} WHERE {whereClause}"
     |> appendOrderBy annotation.orderBy)
    $"(fun cmd ->\n        {asyncParamBindings})"
    fieldMappings

let generateViewSelectOneBy (viewName: string) (columns: ViewColumn list) (annotation: SelectOneByAnnotation) =
  columns
  |> List.map selectableColumnFromViewColumn
  |> fun columns -> generateSelectOneByMember viewName (capitalizeName viewName) columns annotation

let generateViewQueryLike (viewName: string) (columns: ViewColumn list) (annotation: QueryLikeAnnotation) =
  let typeName = capitalizeName viewName
  let col = annotation.columns |> List.head
  let methodName = $"Select{capitalizeName col}Like"
  let columnDef = findViewColumn columns col |> Option.get
  let fsharpType = TypeGenerator.mapSqlType columnDef.columnType false
  let whereClause = $"{col} LIKE '%%' || @{col} || '%%'"

  let columnNames, fieldMappings =
    buildRecordProjection (fun (column: ViewColumn) -> column.name) TypeGenerator.readViewColumnExpr columns

  let asyncParamBindingExpr = addPlainBinding "cmd" col col

  renderSelectMember
    methodName
    [ typedParenParam col fsharpType; txParam ]
    $"Task<Result<{typeName} list, SqliteException>>"
    "queryList"
    $"SELECT {columnNames} FROM {viewName} WHERE {whereClause}"
    $"(fun cmd ->\n        {asyncParamBindingExpr})"
    fieldMappings

let generateViewQueryWhere (viewName: string) (columns: ViewColumn list) (annotation: QueryWhereAnnotation) =
  let typeName = capitalizeName viewName
  let methodName = $"Select{capitalizeName annotation.name}"

  let parameters =
    annotation.columns
    |> List.map (fun col ->
      let columnDef = findViewColumn columns col |> Option.get
      let fsharpType = TypeGenerator.mapViewColumnType columnDef
      col, fsharpType)

  let columnNames, fieldMappings =
    buildRecordProjection (fun (column: ViewColumn) -> column.name) TypeGenerator.readViewColumnExpr columns

  let asyncParamBindings =
    annotation.columns
    |> List.map (fun col ->
      let columnDef = findViewColumn columns col |> Option.get
      addViewBinding "cmd" columnDef col)

  let configureExpr =
    match asyncParamBindings with
    | [] -> "(fun _ -> ())"
    | bindings ->
      bindings
      |> joinBindings "        "
      |> fun bindings -> $"(fun cmd ->\n        {bindings})"

  let methodParameters =
    match parameters with
    | [] -> [ txParam ]
    | _ -> [ typedTupledOrSingleParam parameters; txParam ]

  renderSelectMember
    methodName
    methodParameters
    $"Task<Result<{typeName} list, SqliteException>>"
    "queryList"
    ($"SELECT {columnNames} FROM {viewName} WHERE {annotation.whereSql}"
     |> appendOrderBy annotation.orderBy)
    configureExpr
    fieldMappings
