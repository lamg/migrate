module internal Mig.CodeGen.NormalizedQueryGeneratorQueryExtensions

open Mig.DeclarativeMigrations.Types
open Fabulous.AST
open type Fabulous.AST.Ast
open Mig.CodeGen.AstExprBuilders
open Mig.CodeGen.NormalizedSchema
open Mig.CodeGen.NormalizedQueryGeneratorCommon
open Mig.CodeGen.SqlParamBindings

let private commandLambda (bindings: string list) =
  match bindings with
  | [] -> lambdaExpr "_" unitExpr
  | _ -> lambdaStatementsExpr "cmd" bindings

let private readerLambda (caseSelectionExpr: string) =
  lambdaExpr "reader" (rawExpr caseSelectionExpr)

let private tupledOrSingleNamePattern (names: string list) =
  match names with
  | [ name ] -> NamedPat(name)
  | _ -> names |> List.map NamedPat |> TuplePat |> ParenPat

let private generateAliasedSelectColumns (normalized: NormalizedTable) =
  let baseColumns = normalized.baseTable.columns |> List.map (fun c -> $"b.{c.name}")

  let extensionColumns =
    normalized.extensions
    |> List.collect (fun ext ->
      getExtensionNonKeyColumns ext
      |> List.map (fun c -> $"e{ext.aspectName}.{c.name}"))

  (baseColumns @ extensionColumns) |> String.concat ", "

let generateNormalizedQueryBy (normalized: NormalizedTable) (annotation: QueryByAnnotation) =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name

  let methodName =
    annotation.columns
    |> List.map TypeGenerator.toPascalCase
    |> String.concat ""
    |> sprintf "SelectBy%s"

  let parameters =
    annotation.columns
    |> List.map (fun col ->
      let _, columnDef = findNormalizedColumn normalized col |> Option.get
      let fsharpType = TypeGenerator.mapColumnType columnDef
      col, fsharpType)

  let whereClause =
    annotation.columns
    |> List.map (fun col -> $"{col} = @{col}")
    |> String.concat " AND "

  let allSelects = generateAliasedSelectColumns normalized

  let joins =
    normalized.extensions
    |> List.map (fun ext ->
      let alias = $"e{ext.aspectName}"

      let joinCondition =
        generateExtensionJoinCondition "b" normalized.baseTable ext alias

      $"LEFT JOIN {ext.table.name} AS {alias} ON {joinCondition}")
    |> String.concat "\n        "

  let sql =
    if normalized.extensions.IsEmpty then
      $"SELECT {allSelects} FROM {normalized.baseTable.name} AS b WHERE {whereClause}"
    else
      $"SELECT {allSelects}\n        FROM {normalized.baseTable.name} AS b\n        {joins}\n        WHERE {whereClause}"

  let bindings =
    annotation.columns
    |> List.map (fun col ->
      let _, columnDef = findNormalizedColumn normalized col |> Option.get
      addColumnBinding "cmd" columnDef col)

  let caseSelectionExpr =
    generateCaseSelectionExpr normalized.baseTable normalized.extensions typeName

  staticMember
    methodName
    [ typedTupledOrSingleParam parameters; txParam ]
    (AppExpr(
      "queryList",
      [ ConstantExpr(Ast.String sql)
        commandLambda bindings
        readerLambda caseSelectionExpr
        rawExpr "tx" ]
    ))
    $"Task<Result<{typeName} list, SqliteException>>"

let generateNormalizedQueryLike (normalized: NormalizedTable) (annotation: QueryLikeAnnotation) =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let col = annotation.columns |> List.head
  let methodName = $"Select{TypeGenerator.toPascalCase col}Like"

  let parameterType =
    let _, columnDef = findNormalizedColumn normalized col |> Option.get
    let isNullable = TypeGenerator.isColumnNullable columnDef
    TypeGenerator.mapSqlType columnDef.columnType isNullable

  let whereClause = $"{col} LIKE '%%' || @{col} || '%%'"
  let allSelects = generateAliasedSelectColumns normalized

  let joins =
    normalized.extensions
    |> List.map (fun ext ->
      let alias = $"e{ext.aspectName}"

      let joinCondition =
        generateExtensionJoinCondition "b" normalized.baseTable ext alias

      $"LEFT JOIN {ext.table.name} AS {alias} ON {joinCondition}")
    |> String.concat "\n        "

  let sql =
    if normalized.extensions.IsEmpty then
      $"SELECT {allSelects} FROM {normalized.baseTable.name} AS b WHERE {whereClause}"
    else
      $"SELECT {allSelects}\n        FROM {normalized.baseTable.name} AS b\n        {joins}\n        WHERE {whereClause}"

  let _, columnDef = findNormalizedColumn normalized col |> Option.get

  let caseSelectionExpr =
    generateCaseSelectionExpr normalized.baseTable normalized.extensions typeName

  staticMember
    methodName
    [ typedParenParam col parameterType; txParam ]
    (AppExpr(
      "queryList",
      [ ConstantExpr(Ast.String sql)
        commandLambda [ addColumnBinding "cmd" columnDef col ]
        readerLambda caseSelectionExpr
        rawExpr "tx" ]
    ))
    $"Task<Result<{typeName} list, SqliteException>>"

let generateNormalizedQueryByOrCreate (normalized: NormalizedTable) (annotation: QueryByOrCreateAnnotation) =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let newTypeName = $"New{typeName}"

  let methodName =
    annotation.columns
    |> List.map TypeGenerator.toPascalCase
    |> String.concat ""
    |> sprintf "SelectBy%sOrInsert"

  let baseInsertColumns = getInsertColumns normalized.baseTable
  let baseHasAllColumns = caseHasAllQueryColumns baseInsertColumns annotation.columns

  let generateBaseMatch () =
    if baseHasAllColumns then
      let extractedValues =
        annotation.columns
        |> List.map (fun col ->
          let _, varName = generateSingleFieldPattern baseInsertColumns col
          varName)

      let extractionExpr =
        match extractedValues with
        | [ value ] -> value
        | _ -> extractedValues |> String.concat ", " |> sprintf "(%s)"

      $"| {newTypeName}.Base({generateFieldPattern baseInsertColumns}) -> {extractionExpr}"
    else
      let missingCols = annotation.columns |> String.concat ", "
      $"| {newTypeName}.Base({generateFieldPattern baseInsertColumns}) -> invalidArg \"newItem\" \"Base case does not have the required fields ({missingCols}) for this QueryByOrCreate operation\""

  let generateExtensionMatches () =
    normalized.extensions
    |> List.map (fun ext ->
      let caseName = $"With{TypeGenerator.toPascalCase ext.aspectName}"
      let extensionCols = getExtensionNonKeyColumns ext
      let allCols = baseInsertColumns @ extensionCols
      let extHasAllColumns = caseHasAllQueryColumns allCols annotation.columns

      if extHasAllColumns then
        let extractedValues =
          annotation.columns
          |> List.map (fun col ->
            let _, varName = generateSingleFieldPattern allCols col
            varName)

        let extractionExpr =
          match extractedValues with
          | [ value ] -> value
          | _ -> extractedValues |> String.concat ", " |> sprintf "(%s)"

        $"| {newTypeName}.{caseName}({generateFieldPattern allCols}) -> {extractionExpr}"
      else
        let missingCols = annotation.columns |> String.concat ", "
        $"| {newTypeName}.{caseName}({generateFieldPattern allCols}) -> invalidArg \"newItem\" \"{caseName} case does not have the required fields ({missingCols}) for this QueryByOrCreate operation\"")
    |> String.concat " "

  let extractionExpr =
    let extensionMatches = generateExtensionMatches ()

    let matches =
      if extensionMatches = "" then
        generateBaseMatch ()
      else
        String.concat " " [ generateBaseMatch (); extensionMatches ]

    $"match newItem with {matches}"

  let whereClause =
    annotation.columns
    |> List.map (fun col -> $"{col} = @{col}")
    |> String.concat " AND "

  let allSelects = generateAliasedSelectColumns normalized

  let joins =
    normalized.extensions
    |> List.map (fun ext ->
      let alias = $"e{ext.aspectName}"

      let joinCondition =
        generateExtensionJoinCondition "b" normalized.baseTable ext alias

      $"LEFT JOIN {ext.table.name} {alias} ON {joinCondition}")
    |> String.concat "\n      "

  let selectSql =
    if normalized.extensions.IsEmpty then
      $"SELECT {allSelects} FROM {normalized.baseTable.name} b WHERE {whereClause} LIMIT 1"
    else
      $"SELECT {allSelects} FROM {normalized.baseTable.name} b\n      {joins}\n      WHERE {whereClause} LIMIT 1"

  let paramBindings =
    annotation.columns
    |> List.map (fun col ->
      let _, columnDef = findNormalizedColumn normalized col |> Option.get
      let isNullable = TypeGenerator.isColumnNullable columnDef

      if isNullable then
        $"cmd.Parameters.AddWithValue(\"@{col}\", {TypeGenerator.toNullableDbValueExpr columnDef col}) |> ignore"
      else
        $"cmd.Parameters.AddWithValue(\"@{col}\", {TypeGenerator.toDbValueExpr columnDef col}) |> ignore")

  let caseSelectionExpr =
    generateCaseSelectionExpr normalized.baseTable normalized.extensions typeName

  let selectExpr =
    AppExpr(
      "querySingle",
      [ ConstantExpr(Ast.String selectSql)
        commandLambda paramBindings
        readerLambda caseSelectionExpr
        rawExpr "tx" ]
    )

  let body =
    CompExprBodyExpr(
      [ LetOrUseExpr(Value(tupledOrSingleNamePattern annotation.columns, rawExpr extractionExpr))
        LetOrUseExpr(Function("select", UnitPat(), selectExpr))
        OtherExpr(
          AppExpr("querySingleOrInsert", [ rawExpr "select"; lambdaRawExpr "()" $"{typeName}.Insert newItem tx" ])
        ) ]
    )

  staticMember
    methodName
    [ typedParenParam "newItem" newTypeName; txParam ]
    body
    $"Task<Result<{typeName}, SqliteException>>"
