module internal Mig.CodeGen.NormalizedQueryGeneratorUpdateDelete

open Mig.DeclarativeMigrations.Types
open Fabulous.AST
open Fantomas.Core.SyntaxOak
open type Fabulous.AST.Ast
open Mig.CodeGen.AstExprBuilders
open Mig.CodeGen.NormalizedSchema
open Mig.CodeGen.NormalizedQueryGeneratorCommon
open Mig.CodeGen.SqlParamBindings

let private commandLambda (bindings: string list) =
  match bindings with
  | [] -> lambdaExpr "_" unitExpr
  | _ -> lambdaStatementsExpr "cmd" bindings

let private executeWriteUnitExpr (sql: string) (bindings: string list) =
  AppExpr("executeWriteUnit", [ ConstantExpr(Ast.String sql); commandLambda bindings; rawExpr "tx" ])

let private unitThunk (bodyExpr: WidgetBuilder<Expr>) = ParenLambdaExpr([ UnitPat() ], bodyExpr)

let private sequenceUnitResultsExpr (steps: WidgetBuilder<Expr> list) =
  AppExpr("sequenceUnitResults", [ ListExpr(steps) ])

let private catchSqliteTaskExpr (bodyExpr: WidgetBuilder<Expr>) =
  taskExpr [ OtherExpr(TryWithExpr(bodyExpr, MatchClauseExpr(":? SqliteException as ex", returnExprRaw "Error ex"))) ]

let private generateUpdateBaseSql (baseTable: CreateTable) : string =
  let pkCols =
    getPrimaryKeyColumns baseTable |> List.map (fun c -> c.name) |> Set.ofList

  let updateCols =
    baseTable.columns |> List.filter (fun col -> not (Set.contains col.name pkCols))

  let setClauses =
    updateCols |> List.map (fun c -> $"{c.name} = @{c.name}") |> String.concat ", "

  let whereClause =
    pkCols
    |> Set.toList
    |> List.map (fun pk -> $"{pk} = @{pk}")
    |> String.concat " AND "

  $"UPDATE {baseTable.name} SET {setClauses} WHERE {whereClause}"

let private getPrimaryKeyVarName (baseTable: CreateTable) =
  let idCol =
    baseTable.columns
    |> List.find (fun col ->
      col.constraints
      |> List.exists (function
        | PrimaryKey _ -> true
        | _ -> false))

  getColumnVarName idCol

let private baseUpdateClause (baseTable: CreateTable) (extensions: ExtensionTable list) (typeName: string) =
  let updateSql = generateUpdateBaseSql baseTable
  let idVarName = getPrimaryKeyVarName baseTable
  let deleteSteps =
    extensions
    |> List.map (fun ext ->
      unitThunk (executeWriteUnitExpr ($"DELETE FROM {ext.table.name} WHERE {ext.fkColumn} = @id") [ addPlainBinding "cmd" "id" idVarName ]))

  let deleteExtensionsExpr = sequenceUnitResultsExpr deleteSteps
  let updateStepExpr = unitThunk (executeWriteUnitExpr updateSql (generateParamBindings baseTable.columns "cmd"))

  MatchClauseExpr(
    $"{typeName}.Base({generateFieldPattern baseTable.columns})",
    CompExprBodyExpr(
      [ LetOrUseExpr(Function("deleteExtensions", UnitPat(), deleteExtensionsExpr))
        OtherExpr(
          returnFromExpr (
            sequenceUnitResultsExpr
              [ updateStepExpr
                unitThunk (AppExpr("deleteExtensions", unitExpr)) ]
          )
        ) ]
    )
  )

let private extensionUpdateClause
  (baseTable: CreateTable)
  (extension: ExtensionTable)
  (allExtensions: ExtensionTable list)
  (typeName: string)
  =
  let caseName = TypeGenerator.toPascalCase extension.aspectName
  let updateSql = generateUpdateBaseSql baseTable

  let extensionInsertColumns =
    extension.table.columns
    |> List.filter (fun col -> col.name <> extension.fkColumn)

  let extensionColumnNames = extensionInsertColumns |> List.map (fun c -> c.name) |> String.concat ", "
  let extensionParamNames = extensionInsertColumns |> List.map (fun c -> $"@{c.name}") |> String.concat ", "

  let insertOrReplaceSql =
    $"INSERT OR REPLACE INTO {extension.table.name} ({extension.fkColumn}, {extensionColumnNames}) VALUES (@{extension.fkColumn}, {extensionParamNames})"

  let idVarName = getPrimaryKeyVarName baseTable

  let deleteOtherExtensionSteps =
    allExtensions
    |> List.filter (fun ext -> ext.table.name <> extension.table.name)
    |> List.map (fun ext ->
      unitThunk (executeWriteUnitExpr ($"DELETE FROM {ext.table.name} WHERE {ext.fkColumn} = @id") [ addPlainBinding "cmd" "id" idVarName ]))

  let deleteOtherExtensionsExpr = sequenceUnitResultsExpr deleteOtherExtensionSteps
  let updateStepExpr = unitThunk (executeWriteUnitExpr updateSql (generateParamBindings baseTable.columns "cmd"))

  let insertBindings =
    addPlainBinding "cmd" extension.fkColumn idVarName
    :: generateParamBindings extensionInsertColumns "cmd"

  let insertStepExpr = unitThunk (executeWriteUnitExpr insertOrReplaceSql insertBindings)
  let allColumns = baseTable.columns @ extensionInsertColumns

  MatchClauseExpr(
    $"{typeName}.With{caseName}({generateFieldPattern allColumns})",
    CompExprBodyExpr(
      [ LetOrUseExpr(Function("deleteOtherExtensions", UnitPat(), deleteOtherExtensionsExpr))
        OtherExpr(
          returnFromExpr (
            sequenceUnitResultsExpr
              [ updateStepExpr
                insertStepExpr
                unitThunk (AppExpr("deleteOtherExtensions", unitExpr)) ]
          )
        ) ]
    )
  )

let generateUpdate (normalized: NormalizedTable) =
  let pkCols = getPrimaryKeyColumns normalized.baseTable

  match pkCols with
  | [] -> None
  | _ ->
    let typeName = TypeGenerator.toPascalCase normalized.baseTable.name

    let cases =
      baseUpdateClause normalized.baseTable normalized.extensions typeName
      :: (normalized.extensions
          |> List.map (fun ext -> extensionUpdateClause normalized.baseTable ext normalized.extensions typeName))

    Some(
      staticMember
        "Update"
        [ typedParenParam "item" typeName; txParam ]
        (catchSqliteTaskExpr (MatchExpr("item", cases)))
        "Task<Result<unit, SqliteException>>"
    )

let generateDelete (normalized: NormalizedTable) =
  let pkCols = getPrimaryKeyColumns normalized.baseTable

  match pkCols with
  | [] -> None
  | pks ->
    let whereClause =
      pks |> List.map (fun pk -> $"{pk.name} = @{pk.name}") |> String.concat " AND "

    let deleteSql = $"DELETE FROM {normalized.baseTable.name} WHERE {whereClause}"

    let parameters =
      pks
      |> List.map (fun pk ->
        let pkType = TypeGenerator.mapColumnType pk
        typedParenParam pk.name pkType)

    let bindings = pks |> List.map (fun pk -> addColumnBinding "cmd" pk pk.name)

    Some(
      staticMember
        "Delete"
        (parameters @ [ txParam ])
        (executeWriteUnitExpr deleteSql bindings)
        "Task<Result<unit, SqliteException>>"
    )
