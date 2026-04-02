module internal Mig.CodeGen.QueryGeneratorViewQueries

open Mig.DeclarativeMigrations.Types
open Mig.CodeGen.ViewIntrospection
open Fabulous.AST
open type Fabulous.AST.Ast
open Mig.CodeGen.AstExprBuilders
open Mig.CodeGen.QueryGeneratorCommon
open Mig.CodeGen.SqlParamBindings

let generateViewGetAll (viewName: string) (columns: ViewColumn list) : string =
  let typeName = capitalizeName viewName
  let columnNames = columns |> List.map (fun c -> c.name) |> String.concat ", "
  let getSql = $"SELECT {columnNames} FROM {viewName}"

  let fieldMappings =
    columns
    |> List.mapi (fun i col ->
      let fieldName = capitalizeName col.name in $"{fieldName} = {TypeGenerator.readViewColumnExpr col i}")
    |> String.concat "; "

  $"""  static member SelectAll (tx: SqliteTransaction) : Task<Result<{typeName} list, SqliteException>> =
    queryList
      "{getSql}"
      (fun _ -> ())
      (fun reader ->
        {{ {fieldMappings} }})
      tx"""

let generateViewGetOne (viewName: string) (columns: ViewColumn list) : string =
  let typeName = capitalizeName viewName
  let columnNames = columns |> List.map (fun c -> c.name) |> String.concat ", "
  let getSql = $"SELECT {columnNames} FROM {viewName} LIMIT 1"

  let fieldMappings =
    columns
    |> List.mapi (fun i col ->
      let fieldName = capitalizeName col.name in $"{fieldName} = {TypeGenerator.readViewColumnExpr col i}")
    |> String.concat "; "

  $"""  static member SelectOne (tx: SqliteTransaction) : Task<Result<{typeName} option, SqliteException>> =
    querySingle
      "{getSql}"
      (fun _ -> ())
      (fun reader ->
        {{ {fieldMappings} }})
      tx"""

let validateViewQueryByAnnotation
  (viewName: string)
  (columns: ViewColumn list)
  (annotation: QueryByAnnotation)
  : Result<unit, string> =
  let columnNames =
    columns |> List.map (fun c -> c.name.ToLowerInvariant()) |> Set.ofList

  annotation.columns
  |> List.tryFind (fun col -> not (columnNames.Contains(col.ToLowerInvariant())))
  |> function
    | Some invalidCol ->
      let availableCols = columns |> List.map (fun c -> c.name) |> String.concat ", " in

      Error
        $"QueryBy annotation references non-existent column '{invalidCol}' in view '{viewName}'. Available columns: {availableCols}"
    | None -> Ok()

let validateViewQueryLikeAnnotation
  (viewName: string)
  (columns: ViewColumn list)
  (annotation: QueryLikeAnnotation)
  : Result<unit, string> =
  match annotation.columns with
  | [] -> Error $"QueryLike annotation on view '{viewName}' requires exactly one column."
  | [ col ] ->
    let columnNames =
      columns |> List.map (fun c -> c.name.ToLowerInvariant()) |> Set.ofList

    if columnNames.Contains(col.ToLowerInvariant()) then
      Ok()
    else
      let availableCols = columns |> List.map (fun c -> c.name) |> String.concat ", " in

      Error
        $"QueryLike annotation references non-existent column '{col}' in view '{viewName}'. Available columns: {availableCols}"
  | _ ->
    let receivedCols = annotation.columns |> String.concat ", " in
    Error $"QueryLike annotation on view '{viewName}' supports exactly one column. Received: {receivedCols}"

let generateViewQueryBy (viewName: string) (columns: ViewColumn list) (annotation: QueryByAnnotation) : string =
  let typeName = capitalizeName viewName

  let methodName =
    annotation.columns
    |> List.map capitalizeName
    |> String.concat ""
    |> sprintf "SelectBy%s"

  let parameters =
    annotation.columns
    |> List.map (fun col ->
      let columnDef = findViewColumn columns col |> Option.get in
      let fsharpType = TypeGenerator.mapViewColumnType columnDef in
      $"{col}: {fsharpType}")
    |> String.concat ", "

  let whereClause =
    annotation.columns
    |> List.map (fun col -> $"{col} = @{col}")
    |> String.concat " AND "

  let columnNames = columns |> List.map (fun c -> c.name) |> String.concat ", "

  let fieldMappings =
    columns
    |> List.mapi (fun i col ->
      let fieldName = capitalizeName col.name in $"{fieldName} = {TypeGenerator.readViewColumnExpr col i}")
    |> String.concat "; "

  let asyncParamBindings =
    annotation.columns
    |> List.map (fun col ->
      let columnDef = findViewColumn columns col |> Option.get in addViewBinding "cmd" columnDef col)
    |> joinBindings "        "

  $"""  static member {methodName} ({parameters}) (tx: SqliteTransaction) : Task<Result<{typeName} list, SqliteException>> =
    queryList
      "SELECT {columnNames} FROM {viewName} WHERE {whereClause}"
      (fun cmd ->
        {asyncParamBindings})
      (fun reader ->
        {{ {fieldMappings} }})
      tx"""

let generateViewQueryLike (viewName: string) (columns: ViewColumn list) (annotation: QueryLikeAnnotation) : string =
  let typeName = capitalizeName viewName
  let col = annotation.columns |> List.head
  let methodName = $"Select{capitalizeName col}Like"
  let columnDef = findViewColumn columns col |> Option.get
  let fsharpType = TypeGenerator.mapSqlType columnDef.columnType false
  let parameters = $"{col}: {fsharpType}"
  let whereClause = $"{col} LIKE '%%' || @{col} || '%%'"
  let columnNames = columns |> List.map (fun c -> c.name) |> String.concat ", "

  let fieldMappings =
    columns
    |> List.mapi (fun i c ->
      let fieldName = capitalizeName c.name in $"{fieldName} = {TypeGenerator.readViewColumnExpr c i}")
    |> String.concat "; "

  let asyncParamBindingExpr = addPlainBinding "cmd" col col

  $"""  static member {methodName} ({parameters}) (tx: SqliteTransaction) : Task<Result<{typeName} list, SqliteException>> =
    queryList
      "SELECT {columnNames} FROM {viewName} WHERE {whereClause}"
      (fun cmd ->
        {asyncParamBindingExpr})
      (fun reader ->
        {{ {fieldMappings} }})
      tx"""
