module internal Mig.CodeGen.NormalizedQueryGeneratorInsertSelect

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

let private executeInsertExpr (sql: string) (bindings: string list) onSuccess =
  AppExpr(
    "executeInsert",
    [ ConstantExpr(Ast.String sql)
      commandLambda bindings
      rawExpr "tx"
      onSuccess ]
  )

let private executeInsertOrIgnoreExpr (sql: string) (bindings: string list) onSuccess =
  AppExpr(
    "executeInsertOrIgnore",
    [ ConstantExpr(Ast.String sql)
      commandLambda bindings
      rawExpr "tx"
      onSuccess ]
  )

let private executeWriteExpr (sql: string) (bindings: string list) onSuccess =
  AppExpr(
    "executeWrite",
    [ ConstantExpr(Ast.String sql)
      commandLambda bindings
      rawExpr "tx"
      onSuccess ]
  )

let private readerLambda (caseSelectionExpr: string) =
  lambdaExpr "reader" (rawExpr caseSelectionExpr)

let private catchSqliteTaskExpr (bodyExpr: WidgetBuilder<Expr>) =
  taskExpr [ OtherExpr(TryWithExpr(bodyExpr, MatchClauseExpr(":? SqliteException as ex", returnExprRaw "Error ex"))) ]

let private baseCaseInsertClause (baseTable: CreateTable) (typeName: string) =
  let insertColumns = getInsertColumns baseTable
  let insertSql = generateInsertSql baseTable.name insertColumns
  let fieldPattern = generateFieldPattern insertColumns

  let onSuccess =
    lambdaExpr $"{baseTable.name}Id" (taskExpr [ OtherExpr(returnExprRaw $"Ok {baseTable.name}Id") ])

  MatchClauseExpr(
    $"New{typeName}.Base({fieldPattern})",
    returnFromExpr (executeInsertExpr insertSql (generateParamBindings insertColumns "cmd") onSuccess)
  )

let private baseCaseInsertOrIgnoreClause (baseTable: CreateTable) (typeName: string) =
  let insertColumns = getInsertColumns baseTable
  let insertSql = generateInsertOrIgnoreSql baseTable.name insertColumns
  let fieldPattern = generateFieldPattern insertColumns

  let onSuccess =
    lambdaExpr $"{baseTable.name}Id" (taskExpr [ OtherExpr(returnExprRaw $"Ok {baseTable.name}Id") ])

  MatchClauseExpr(
    $"New{typeName}.Base({fieldPattern})",
    returnFromExpr (executeInsertOrIgnoreExpr insertSql (generateParamBindings insertColumns "cmd") onSuccess)
  )

let private extensionInsertClause (baseTable: CreateTable) (extension: ExtensionTable) (typeName: string) =
  let caseName = TypeGenerator.toPascalCase extension.aspectName
  let baseInsertColumns = getInsertColumns baseTable
  let baseInsertSql = generateInsertSql baseTable.name baseInsertColumns
  let basePkColumns = getPrimaryKeyColumns baseTable

  let autoIncrementBasePkColumn =
    getSinglePrimaryKeyColumn baseTable |> Option.filter isAutoIncrementPrimaryKey

  let extensionInsertColumns = getExtensionNonKeyColumns extension

  let fieldPattern = generateFieldPattern (baseInsertColumns @ extensionInsertColumns)

  let allInsertColumns =
    extension.fkColumns @ (extensionInsertColumns |> List.map (fun c -> c.name))

  let extensionColumnNames = allInsertColumns |> String.concat ", "

  let extensionParamNames =
    allInsertColumns |> List.map (fun c -> $"@{c}") |> String.concat ", "

  let extensionSql =
    $"INSERT INTO {extension.table.name} ({extensionColumnNames}) VALUES ({extensionParamNames})"

  let baseInsertResultExpr =
    executeInsertExpr
      baseInsertSql
      (generateParamBindings baseInsertColumns "cmd")
      (lambdaExpr $"{baseTable.name}Id" (taskExpr [ OtherExpr(returnExprRaw $"Ok {baseTable.name}Id") ]))

  let extensionBindings =
    match autoIncrementBasePkColumn with
    | Some _ ->
      addPlainBinding "cmd" extension.fkColumns.Head $"{baseTable.name}Id"
      :: generateParamBindings extensionInsertColumns "cmd"
    | None ->
      let fkBindings =
        (extension.fkColumns, basePkColumns)
        ||> List.map2 (fun fkColumn pkColumn -> addPlainBinding "cmd" fkColumn (getColumnVarName pkColumn))

      fkBindings @ generateParamBindings extensionInsertColumns "cmd"

  let extensionWriteExpr =
    executeWriteExpr
      extensionSql
      extensionBindings
      (lambdaExpr "_" (taskExpr [ OtherExpr(returnExprRaw $"Ok {baseTable.name}Id") ]))

  MatchClauseExpr(
    $"New{typeName}.With{caseName}({fieldPattern})",
    CompExprBodyExpr(
      [ LetOrUseBangExpr(NamedPat("baseInsertResult"), baseInsertResultExpr)
        OtherExpr(
          MatchExpr(
            "baseInsertResult",
            [ MatchClauseExpr("Error ex", returnExprRaw "Error ex")
              MatchClauseExpr($"Ok {baseTable.name}Id", returnFromExpr extensionWriteExpr) ]
          )
        ) ]
    )
  )

let private extensionInsertOrIgnoreClause (baseTable: CreateTable) (extension: ExtensionTable) (typeName: string) =
  let caseName = TypeGenerator.toPascalCase extension.aspectName
  let baseInsertColumns = getInsertColumns baseTable
  let baseInsertSql = generateInsertOrIgnoreSql baseTable.name baseInsertColumns
  let basePkColumns = getPrimaryKeyColumns baseTable

  let autoIncrementBasePkColumn =
    getSinglePrimaryKeyColumn baseTable |> Option.filter isAutoIncrementPrimaryKey

  let extensionInsertColumns = getExtensionNonKeyColumns extension

  let fieldPattern = generateFieldPattern (baseInsertColumns @ extensionInsertColumns)

  let allInsertColumns =
    extension.fkColumns @ (extensionInsertColumns |> List.map (fun c -> c.name))

  let extensionColumnNames = allInsertColumns |> String.concat ", "

  let extensionParamNames =
    allInsertColumns |> List.map (fun c -> $"@{c}") |> String.concat ", "

  let extensionSql =
    $"INSERT INTO {extension.table.name} ({extensionColumnNames}) VALUES ({extensionParamNames})"

  let baseInsertResultExpr =
    executeInsertOrIgnoreExpr
      baseInsertSql
      (generateParamBindings baseInsertColumns "cmd")
      (lambdaExpr $"{baseTable.name}Id" (taskExpr [ OtherExpr(returnExprRaw $"Ok {baseTable.name}Id") ]))

  let extensionBindings =
    match autoIncrementBasePkColumn with
    | Some _ ->
      addPlainBinding "cmd" extension.fkColumns.Head $"{baseTable.name}Id"
      :: generateParamBindings extensionInsertColumns "cmd"
    | None ->
      let fkBindings =
        (extension.fkColumns, basePkColumns)
        ||> List.map2 (fun fkColumn pkColumn -> addPlainBinding "cmd" fkColumn (getColumnVarName pkColumn))

      fkBindings @ generateParamBindings extensionInsertColumns "cmd"

  let extensionWriteExpr =
    executeWriteExpr
      extensionSql
      extensionBindings
      (lambdaExpr "_" (taskExpr [ OtherExpr(returnExprRaw $"Ok (Some {baseTable.name}Id)") ]))

  MatchClauseExpr(
    $"New{typeName}.With{caseName}({fieldPattern})",
    CompExprBodyExpr(
      [ LetOrUseBangExpr(NamedPat("baseInsertResult"), baseInsertResultExpr)
        OtherExpr(
          MatchExpr(
            "baseInsertResult",
            [ MatchClauseExpr("Error ex", returnExprRaw "Error ex")
              MatchClauseExpr("Ok None", returnExprRaw "Ok None")
              MatchClauseExpr($"Ok(Some {baseTable.name}Id)", returnFromExpr extensionWriteExpr) ]
          )
        ) ]
    )
  )

let generateInsert (normalized: NormalizedTable) =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name

  let cases =
    baseCaseInsertClause normalized.baseTable typeName
    :: (normalized.extensions
        |> List.map (fun ext -> extensionInsertClause normalized.baseTable ext typeName))

  staticMember
    "Insert"
    [ typedParenParam "item" $"New{typeName}"; txParam ]
    (catchSqliteTaskExpr (MatchExpr("item", cases)))
    "Task<Result<int64, SqliteException>>"

let generateInsertOrIgnore (normalized: NormalizedTable) =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name

  let cases =
    baseCaseInsertOrIgnoreClause normalized.baseTable typeName
    :: (normalized.extensions
        |> List.map (fun ext -> extensionInsertOrIgnoreClause normalized.baseTable ext typeName))

  staticMember
    "InsertOrIgnore"
    [ typedParenParam "item" $"New{typeName}"; txParam ]
    (catchSqliteTaskExpr (MatchExpr("item", cases)))
    "Task<Result<int64 option, SqliteException>>"

let generateGetAll (normalized: NormalizedTable) =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let selectColumns = generateSelectColumns normalized.baseTable normalized.extensions

  let leftJoins =
    if normalized.extensions.IsEmpty then
      ""
    else
      "\n         " + generateLeftJoins normalized.baseTable normalized.extensions

  let sql =
    $"SELECT {selectColumns}\n         FROM {normalized.baseTable.name}{leftJoins}"

  let caseSelectionExpr =
    generateCaseSelectionExpr normalized.baseTable normalized.extensions typeName

  staticMember
    "SelectAll"
    [ txParam ]
    (AppExpr(
      "queryList",
      [ ConstantExpr(Ast.String sql)
        lambdaExpr "_" unitExpr
        readerLambda caseSelectionExpr
        rawExpr "tx" ]
    ))
    $"Task<Result<{typeName} list, SqliteException>>"

let generateGetById (normalized: NormalizedTable) =
  let pkCols = getPrimaryKeyColumns normalized.baseTable

  match pkCols with
  | [] -> None
  | pks ->
    let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
    let selectColumns = generateSelectColumns normalized.baseTable normalized.extensions

    let leftJoins =
      if normalized.extensions.IsEmpty then
        ""
      else
        "\n         " + generateLeftJoins normalized.baseTable normalized.extensions

    let whereClause =
      pks
      |> List.map (fun pk -> $"{normalized.baseTable.name}.{pk.name} = @{pk.name}")
      |> String.concat " AND "

    let sql =
      $"SELECT {selectColumns}\n         FROM {normalized.baseTable.name}{leftJoins}\n         WHERE {whereClause}"

    let caseSelectionExpr =
      generateCaseSelectionExpr normalized.baseTable normalized.extensions typeName

    let parameters =
      pks
      |> List.map (fun pk ->
        let pkType = TypeGenerator.mapColumnType pk
        typedParenParam pk.name pkType)

    let bindings = pks |> List.map (fun pk -> addColumnBinding "cmd" pk pk.name)

    Some(
      staticMember
        "SelectById"
        (parameters @ [ txParam ])
        (AppExpr(
          "querySingle",
          [ ConstantExpr(Ast.String sql)
            commandLambda bindings
            readerLambda caseSelectionExpr
            rawExpr "tx" ]
        ))
        $"Task<Result<{typeName} option, SqliteException>>"
    )

let generateGetOne (normalized: NormalizedTable) =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let selectColumns = generateSelectColumns normalized.baseTable normalized.extensions

  let leftJoins =
    if normalized.extensions.IsEmpty then
      ""
    else
      "\n         " + generateLeftJoins normalized.baseTable normalized.extensions

  let sql =
    $"SELECT {selectColumns}\n         FROM {normalized.baseTable.name}{leftJoins}\n         LIMIT 1"

  let caseSelectionExpr =
    generateCaseSelectionExpr normalized.baseTable normalized.extensions typeName

  staticMember
    "SelectOne"
    [ txParam ]
    (AppExpr(
      "querySingle",
      [ ConstantExpr(Ast.String sql)
        lambdaExpr "_" unitExpr
        readerLambda caseSelectionExpr
        rawExpr "tx" ]
    ))
    $"Task<Result<{typeName} option, SqliteException>>"
