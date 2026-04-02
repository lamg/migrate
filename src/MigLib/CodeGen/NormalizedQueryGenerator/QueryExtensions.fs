module internal Mig.CodeGen.NormalizedQueryGeneratorQueryExtensions

open Mig.DeclarativeMigrations.Types
open Mig.CodeGen.NormalizedSchema
open Mig.CodeGen.NormalizedQueryGeneratorCommon
open Mig.CodeGen.SqlParamBindings

let generateNormalizedQueryBy (normalized: NormalizedTable) (annotation: QueryByAnnotation) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name

  let methodName =
    annotation.columns
    |> List.map TypeGenerator.toPascalCase
    |> String.concat ""
    |> sprintf "SelectBy%s"

  let parameters =
    annotation.columns
    |> List.map (fun col ->
      let _, columnDef = findNormalizedColumn normalized col |> Option.get in
      let fsharpType = TypeGenerator.mapColumnType columnDef in
      $"{col}: {fsharpType}")
    |> String.concat ", "

  let whereClause =
    annotation.columns
    |> List.map (fun col -> $"{col} = @{col}")
    |> String.concat " AND "

  let baseColumns = normalized.baseTable.columns |> List.map (fun c -> $"b.{c.name}")

  let extensionSelects =
    normalized.extensions
    |> List.collect (fun ext -> ext.table.columns |> List.map (fun c -> $"e{ext.aspectName}.{c.name}"))

  let allSelects = (baseColumns @ extensionSelects) |> String.concat ", "

  let joins =
    normalized.extensions
    |> List.map (fun ext ->
      let pk = getPrimaryKeyColumns normalized.baseTable |> List.head in
      $"LEFT JOIN {ext.table.name} AS e{ext.aspectName} ON b.{pk.name} = e{ext.aspectName}.{ext.fkColumn}")
    |> String.concat "\n        "

  let sql =
    if normalized.extensions.IsEmpty then
      $"SELECT {allSelects} FROM {normalized.baseTable.name} AS b WHERE {whereClause}"
    else
      $"""SELECT {allSelects}
        FROM {normalized.baseTable.name} AS b
        {joins}
        WHERE {whereClause}"""

  let asyncParamBindings =
    annotation.columns
    |> List.map (fun col ->
      let _, columnDef = findNormalizedColumn normalized col |> Option.get in addColumnBinding "cmd" columnDef col)
    |> joinBindings "        "

  let caseSelection =
    generateCaseSelection 14 normalized.baseTable normalized.extensions typeName

  $"""  static member {methodName} ({parameters}) (tx: SqliteTransaction) : Task<Result<{typeName} list, SqliteException>> =
    queryList
      "{sql}"
      (fun cmd ->
        {asyncParamBindings})
      (fun reader ->
        let record =
{caseSelection}
        record)
      tx"""

let generateNormalizedQueryLike (normalized: NormalizedTable) (annotation: QueryLikeAnnotation) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let col = annotation.columns |> List.head
  let methodName = $"Select{TypeGenerator.toPascalCase col}Like"

  let parameters =
    let _, columnDef = findNormalizedColumn normalized col |> Option.get in
    let isNullable = TypeGenerator.isColumnNullable columnDef in
    let fsharpType = TypeGenerator.mapSqlType columnDef.columnType isNullable in
    $"{col}: {fsharpType}"

  let whereClause = $"{col} LIKE '%%' || @{col} || '%%'"
  let baseColumns = normalized.baseTable.columns |> List.map (fun c -> $"b.{c.name}")

  let extensionSelects =
    normalized.extensions
    |> List.collect (fun ext -> ext.table.columns |> List.map (fun c -> $"e{ext.aspectName}.{c.name}"))

  let allSelects = (baseColumns @ extensionSelects) |> String.concat ", "

  let joins =
    normalized.extensions
    |> List.map (fun ext ->
      let pk = getPrimaryKeyColumns normalized.baseTable |> List.head in
      $"LEFT JOIN {ext.table.name} AS e{ext.aspectName} ON b.{pk.name} = e{ext.aspectName}.{ext.fkColumn}")
    |> String.concat "\n        "

  let sql =
    if normalized.extensions.IsEmpty then
      $"SELECT {allSelects} FROM {normalized.baseTable.name} AS b WHERE {whereClause}"
    else
      $"""SELECT {allSelects}
        FROM {normalized.baseTable.name} AS b
        {joins}
        WHERE {whereClause}"""

  let asyncParamBindings =
    let _, columnDef = findNormalizedColumn normalized col |> Option.get in addColumnBinding "cmd" columnDef col

  let caseSelection =
    generateCaseSelection 14 normalized.baseTable normalized.extensions typeName

  $"""  static member {methodName} ({parameters}) (tx: SqliteTransaction) : Task<Result<{typeName} list, SqliteException>> =
    queryList
      "{sql}"
      (fun cmd ->
        {asyncParamBindings})
      (fun reader ->
        let record =
{caseSelection}
        record)
      tx"""

let generateNormalizedQueryByOrCreate (normalized: NormalizedTable) (annotation: QueryByOrCreateAnnotation) : string =
  let typeName = TypeGenerator.toPascalCase normalized.baseTable.name
  let newTypeName = $"New{typeName}"

  let basePkColumn =
    getPrimaryKeyColumns normalized.baseTable
    |> List.tryHead
    |> Option.map (fun pk -> pk.name)
    |> Option.defaultValue "id"

  let methodName =
    annotation.columns
    |> List.map TypeGenerator.toPascalCase
    |> String.concat ""
    |> sprintf "SelectBy%sOrInsert"

  let baseInsertColumns = getInsertColumns normalized.baseTable
  let baseHasAllColumns = caseHasAllQueryColumns baseInsertColumns annotation.columns

  let generateBaseMatch (indent: string) =
    if baseHasAllColumns then
      let extractions =
        annotation.columns
        |> List.map (fun col -> let _, varName = generateSingleFieldPattern baseInsertColumns col in varName)
        |> String.concat ", "

      let pattern = generateFieldPattern baseInsertColumns
      $"{indent}| {newTypeName}.Base({pattern}) -> ({extractions})"
    else
      let pattern = generateFieldPattern baseInsertColumns
      let missingCols = annotation.columns |> String.concat ", "
      $"{indent}| {newTypeName}.Base({pattern}) -> invalidArg \"newItem\" \"Base case does not have the required fields ({missingCols}) for this QueryByOrCreate operation\""

  let generateExtensionMatches (indent: string) =
    normalized.extensions
    |> List.map (fun ext ->
      let caseName = $"With{TypeGenerator.toPascalCase ext.aspectName}"

      let extensionCols =
        ext.table.columns |> List.filter (fun c -> c.name <> ext.fkColumn)

      let allCols = baseInsertColumns @ extensionCols
      let extHasAllColumns = caseHasAllQueryColumns allCols annotation.columns

      if extHasAllColumns then
        let extractions =
          annotation.columns
          |> List.map (fun col -> let _, varName = generateSingleFieldPattern allCols col in varName)
          |> String.concat ", "

        let pattern = generateFieldPattern allCols
        $"{indent}| {newTypeName}.{caseName}({pattern}) -> ({extractions})"
      else
        let pattern = generateFieldPattern allCols
        let missingCols = annotation.columns |> String.concat ", "
        $"{indent}| {newTypeName}.{caseName}({pattern}) -> invalidArg \"newItem\" \"{caseName} case does not have the required fields ({missingCols}) for this QueryByOrCreate operation\"")
    |> String.concat "\n"

  let varBindings = annotation.columns |> List.map id |> String.concat ", "

  let generateValueExtractions (letIndent: string) (matchIndent: string) =
    let baseMatch = generateBaseMatch matchIndent
    let extensionMatches = generateExtensionMatches matchIndent

    let allMatches =
      if extensionMatches = "" then
        baseMatch
      else
        $"{baseMatch}\n{extensionMatches}"

    $"{letIndent}let ({varBindings}) = \n{matchIndent}match newItem with\n{allMatches}"

  let whereClause =
    annotation.columns
    |> List.map (fun col -> $"{col} = @{col}")
    |> String.concat " AND "

  let baseColumns = normalized.baseTable.columns |> List.map (fun c -> $"b.{c.name}")

  let extensionSelects =
    normalized.extensions
    |> List.collect (fun ext -> ext.table.columns |> List.map (fun c -> $"e{ext.aspectName}.{c.name}"))

  let allSelects = (baseColumns @ extensionSelects) |> String.concat ", "

  let joins =
    normalized.extensions
    |> List.map (fun ext ->
      $"LEFT JOIN {ext.table.name} e{ext.aspectName} ON b.{basePkColumn} = e{ext.aspectName}.{ext.fkColumn}")
    |> String.concat "\n      "

  let selectSql =
    if normalized.extensions.IsEmpty then
      $"SELECT {allSelects} FROM {normalized.baseTable.name} b WHERE {whereClause} LIMIT 1"
    else
      $"SELECT {allSelects} FROM {normalized.baseTable.name} b\n      {joins}\n      WHERE {whereClause} LIMIT 1"

  let generateAsyncParamBindings (cmdVarName: string) (lineIndent: string) =
    annotation.columns
    |> List.map (fun col ->
      let _, columnDef = findNormalizedColumn normalized col |> Option.get
      let isNullable = TypeGenerator.isColumnNullable columnDef

      if isNullable then
        $"{cmdVarName}.Parameters.AddWithValue(\"@{col}\", {TypeGenerator.toNullableDbValueExpr columnDef col}) |> ignore"
      else
        $"{cmdVarName}.Parameters.AddWithValue(\"@{col}\", {TypeGenerator.toDbValueExpr columnDef col}) |> ignore")
    |> String.concat $"\n{lineIndent}"

  let asyncParamBindings = generateAsyncParamBindings "cmd" "            "
  let asyncRequeryParamBindings = generateAsyncParamBindings "cmd" "                "

  let caseSelection =
    generateCaseSelection 14 normalized.baseTable normalized.extensions typeName

  let nestedCaseSelection =
    generateCaseSelection 18 normalized.baseTable normalized.extensions typeName

  let valueExtractions = generateValueExtractions "      " "        "

  $"""  static member {methodName} (newItem: {newTypeName}) (tx: SqliteTransaction) : Task<Result<{typeName}, SqliteException>> =
    task {{
      // Extract query values from NewType DU
{valueExtractions}
      // Try to find existing record
      let! existingResult =
        (querySingle
          "{selectSql}"
          (fun cmd ->
            {asyncParamBindings})
          (fun reader ->
            let item =
{caseSelection}
            item)
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
                let insertedItem =
{nestedCaseSelection}
                insertedItem)
              tx)

          return
            match insertedResult with
            | Ok(Some item) -> Ok item
            | Ok None -> Error (SqliteException("Failed to retrieve inserted record", 0))
            | Error ex -> Error ex
        | Error ex -> return Error ex
      | Error ex -> return Error ex
    }}"""
